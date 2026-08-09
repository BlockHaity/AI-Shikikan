using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIShikikan.Core.Serialization;
using AIShikikan.Core.Services.Llm;

namespace AIShikikan.Core.Services.Usage;

/// <summary>模型档案来源。</summary>
public enum ProfileSource
{
    /// <summary>用户手动配置(models.toml)。</summary>
    Manual,

    /// <summary>从 Provider API 获取(/v1/models)。</summary>
    Api,

    /// <summary>未收录: 无上下文窗口与价格信息。</summary>
    Unknown
}

/// <summary>模型档案: 上下文窗口大小 + 计价(USD / 每百万 token)。</summary>
public sealed class ModelProfile
{
    public long ContextTokens { get; set; }
    public double InputPricePer1M { get; set; }
    public double OutputPricePer1M { get; set; }

    /// <summary>缓存输入单价; 为 null 时按输入价的 10% 估算。</summary>
    public double? CachedInputPricePer1M { get; set; }

    public ProfileSource Source { get; set; } = ProfileSource.Unknown;
}

/// <summary>models.toml 手动配置模型条目。</summary>
public sealed class ModelPriceConfig
{
    public long ContextTokens { get; set; }

    [JsonPropertyName("input_price_per_1m")]
    public double InputPricePer1M { get; set; }

    [JsonPropertyName("output_price_per_1m")]
    public double OutputPricePer1M { get; set; }

    [JsonPropertyName("cached_input_price_per_1m")]
    public double? CachedInputPricePer1M { get; set; }
}

/// <summary>models.toml 手动配置: [model."模式"] 条目, 键支持精确 id 或 "前缀*" 通配。</summary>
public sealed class ModelConfigFile
{
    public Dictionary<string, ModelPriceConfig> Model { get; set; } = [];
}

/// <summary>API 拉取的单个模型档案缓存条目。</summary>
public sealed class ApiModelProfile
{
    public string Model { get; set; } = string.Empty;
    public long ContextTokens { get; set; }
    public double InputPricePer1M { get; set; }
    public double OutputPricePer1M { get; set; }
}

/// <summary>API 拉取结果缓存(models-cache.json)。</summary>
public sealed class ModelProfileCache
{
    public DateTime FetchedAt { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public List<ApiModelProfile> Profiles { get; set; } = [];
}

/// <summary>模型档案服务: 合并 手动配置(models.toml) > API 拉取(/v1/models) > 未知 三档来源,
/// 提供上下文窗口大小与成本计算。价格统一以 USD 计。</summary>
public static class ModelProfileService
{
    /// <summary>未收录模型时假设的上下文窗口大小。</summary>
    public const long DefaultContextTokens = 128_000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly object Sync = new();
    private static ModelConfigFile? _manualConfig;
    private static ModelProfileCache? _apiCache;

    public static string ModelsPath => Path.Combine(AppPaths.ConfigDir, "models.toml");

    public static string CachePath => Path.Combine(AppPaths.DataDir, "models-cache.json");

    /// <summary>加载用户手动配置(models.toml); 文件缺失或解析失败时返回空配置。</summary>
    public static ModelConfigFile LoadManualConfig()
    {
        lock (Sync)
        {
            if (_manualConfig is not null) return _manualConfig;

            var config = new ModelConfigFile();
            try
            {
                if (File.Exists(ModelsPath))
                {
                    var file = TomlBridge.Deserialize<ModelConfigFile>(File.ReadAllText(ModelsPath));
                    if (file is not null) config = file;
                }
            }
            catch
            {
            }

            _manualConfig = config;
            return config;
        }
    }

    /// <summary>加载 API 拉取缓存(内存优先, 其次读取 models-cache.json)。</summary>
    public static ModelProfileCache LoadApiCache()
    {
        lock (Sync)
        {
            if (_apiCache is not null) return _apiCache;

            var cache = new ModelProfileCache();
            try
            {
                if (File.Exists(CachePath))
                {
                    var loaded = JsonSerializer.Deserialize(
                        File.ReadAllText(CachePath), AppJsonContext.Default.ModelProfileCache);
                    if (loaded is not null) cache = loaded;
                }
            }
            catch
            {
            }

            _apiCache = cache;
            return cache;
        }
    }

    /// <summary>从 Provider 的 OpenAI 兼容 /v1/models 端点拉取模型档案(OpenRouter 风格的
    /// pricing 字段 + OpenAI 风格的 context_window / context_length)。Anthropic 官方 API
    /// 不提供计价与上下文信息, 直接跳过。结果持久化到 models-cache.json。</summary>
    public static async Task<ModelProfileCache> FetchFromApiAsync(
        ProviderConfig provider, CancellationToken ct = default)
    {
        if (provider.Kind == ProviderKind.Anthropic ||
            string.IsNullOrWhiteSpace(provider.ApiKey) ||
            string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            return new ModelProfileCache { ProviderId = provider.Id };
        }

        var url = provider.BaseUrl.TrimEnd('/') + "/v1/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", provider.ApiKey);

        var profiles = new List<ApiModelProfile>();
        using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return new ModelProfileCache { ProviderId = provider.Id };
            }

            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl) || string.IsNullOrWhiteSpace(idEl.GetString()))
                {
                    continue;
                }

                var id = idEl.GetString()!;
                long context = 0;
                if (item.TryGetProperty("context_window", out var cw) && cw.TryGetInt64(out var cwVal))
                {
                    context = cwVal;
                }
                else if (item.TryGetProperty("context_length", out var cl) && cl.TryGetInt64(out var clVal))
                {
                    context = clVal;
                }

                double inPrice = 0, outPrice = 0;
                if (item.TryGetProperty("pricing", out var pricing) && pricing.ValueKind == JsonValueKind.Object)
                {
                    inPrice = ReadPrice(pricing, "prompt");
                    outPrice = ReadPrice(pricing, "completion");
                }

                // 无上下文窗口且无价格信息的模型条目没有意义, 丢弃
                if (context <= 0 && inPrice <= 0 && outPrice <= 0) continue;

                profiles.Add(new ApiModelProfile
                {
                    Model = id,
                    ContextTokens = context,
                    InputPricePer1M = inPrice,
                    OutputPricePer1M = outPrice
                });
            }
        }

        var cache = new ModelProfileCache
        {
            FetchedAt = DateTime.Now,
            ProviderId = provider.Id,
            Profiles = profiles
        };

        lock (Sync)
        {
            _apiCache = cache;
        }

        TrySaveCache(cache);
        return cache;
    }

    /// <summary>解析模型档案: 手动配置(models.toml 精确/通配匹配)优先, 其次 API 缓存, 最后未知。</summary>
    public static ModelProfile Resolve(string model, string? providerId = null)
    {
        if (string.IsNullOrWhiteSpace(model)) return new ModelProfile();

        var manual = LoadManualConfig().Model;
        if (manual.Count > 0)
        {
            // 通配键按长度降序匹配, 精确键等效于最长通配
            var matched = manual
                .OrderByDescending(kv => kv.Key.Length)
                .FirstOrDefault(kv => MatchesPattern(kv.Key, model));
            if (!string.IsNullOrEmpty(matched.Key))
            {
                var cfg = matched.Value;
                return new ModelProfile
                {
                    ContextTokens = cfg.ContextTokens,
                    InputPricePer1M = cfg.InputPricePer1M,
                    OutputPricePer1M = cfg.OutputPricePer1M,
                    CachedInputPricePer1M = cfg.CachedInputPricePer1M,
                    Source = ProfileSource.Manual
                };
            }
        }

        var cache = LoadApiCache();
        if (!string.IsNullOrEmpty(providerId) &&
            string.Equals(cache.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
        {
            var api = cache.Profiles.FirstOrDefault(p =>
                string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));
            if (api is not null)
            {
                return new ModelProfile
                {
                    ContextTokens = api.ContextTokens,
                    InputPricePer1M = api.InputPricePer1M,
                    OutputPricePer1M = api.OutputPricePer1M,
                    Source = ProfileSource.Api
                };
            }
        }

        return new ModelProfile { Source = ProfileSource.Unknown };
    }

    /// <summary>计算单次调用成本(USD); 未知来源按 0 计。缓存 token 无单独单价时按输入价 10% 估算。</summary>
    public static double CalcCostUsd(ModelProfile profile, int inputTokens, int outputTokens, int cachedInputTokens = 0)
    {
        if (profile.Source == ProfileSource.Unknown) return 0;

        var cost = inputTokens / 1_000_000.0 * profile.InputPricePer1M
                   + outputTokens / 1_000_000.0 * profile.OutputPricePer1M;
        if (cachedInputTokens > 0)
        {
            var cachedPrice = profile.CachedInputPricePer1M ?? profile.InputPricePer1M * 0.1;
            cost += cachedInputTokens / 1_000_000.0 * cachedPrice;
        }

        return cost;
    }

    /// <summary>格式化成本为 USD 文本, 例如 $0.0034。</summary>
    public static string FormatCost(double usd)
    {
        return $"${usd.ToString("0.####")}";
    }

    private static bool MatchesPattern(string pattern, string model)
    {
        if (pattern.EndsWith('*'))
        {
            return model.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(pattern, model, StringComparison.OrdinalIgnoreCase);
    }

    private static double ReadPrice(JsonElement pricing, string key)
    {
        if (!pricing.TryGetProperty(key, out var el)) return 0;
        return el.ValueKind == JsonValueKind.Number
            ? el.GetDouble()
            : double.TryParse(el.GetString(), out var v) ? v : 0;
    }

    private static void TrySaveCache(ModelProfileCache cache)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(CachePath,
                JsonSerializer.Serialize(cache, AppJsonContext.Default.ModelProfileCache));
        }
        catch
        {
        }
    }
}
