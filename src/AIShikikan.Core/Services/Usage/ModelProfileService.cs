using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIShikikan.Core.Logging;
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

    /// <summary>最近一次拉取的失败原因(含请求地址与状态码); 为空表示无错误。供界面展示与排障。</summary>
    public string Error { get; set; } = string.Empty;
}

/// <summary>模型档案服务: 合并 手动配置(models.toml) > API 拉取(/models) > 未知 三档来源,
/// 提供上下文窗口大小与成本计算。价格统一以 USD 计。</summary>
public static class ModelProfileService
{
    /// <summary>模型档案未知(既没配 models.toml, /v1/models 也没收录)时假定的上下文窗口大小。
    ///
    /// <para><b>为什么不能返回 0</b>: 调用方(AgentEngine 的自动压缩)用
    /// <c>total &gt; 0</c> 之类"拿不到窗口就不判断"的守卫, 窗口为 0 会让整条阈值判断
    /// 短路 —— 自动压缩<em>对所有未收录模型彻底静默失效</em>, 而用户恰恰最可能在用刚出的新模型。
    /// 给一个保守偏小的窗口(128K 覆盖绝大多数模型的真实值), 最坏结果只是"压缩早一点",
    /// 而不是"永远不压缩直到请求被服务端拒绝"。</para>
    ///
    /// <para>引擎层另有 <c>TurnOptions.FallbackContextTokens</c> 作为第二层兜底;
    /// 两层都做是对的: 本层保证档案数据结构自洽, 引擎层保证该值可按需关闭/调高。</para>
    /// </summary>
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

            // 走 AtomicFile: validate 判据是"能反序列化", 半截 TOML 会被判为不可用。
            // .bak 实际几乎不存在 —— models.toml 是纯手工编辑的文件, 程序没有保存 API,
            // 从不由 AtomicFile 写盘, 所以没有 .bak 备份可回退。这里要的是 validate:
            // 挡住"解析成功但内容是垃圾"的半截文件, 避免它污染记忆中的 _manualConfig。
            ModelConfigFile? parsed = null;
            if (AtomicFile.TryReadText(ModelsPath, out _, text => TryParseManual(text, out parsed))
                && parsed is not null)
            {
                config = parsed;
            }
            else if (parsed is null && File.Exists(ModelsPath))
            {
                Log.Warn("ModelProfile", $"models.toml 解析失败, 已忽略(按未配置处理): {ModelsPath}");
            }

            _manualConfig = config;
            return config;
        }
    }

    /// <summary>尝试解析 models.toml 内容; 异常视为不可用(触发 <c>.bak</c> 回退)。</summary>
    private static bool TryParseManual(string content, out ModelConfigFile? file)
    {
        try
        {
            file = TomlBridge.Deserialize<ModelConfigFile>(content);
            return file is not null;
        }
        catch (Exception ex)
        {
            file = null;
            Log.Debug("ModelProfile", $"models.toml 解析异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>加载 API 拉取缓存(内存优先, 其次读取 models-cache.json)。</summary>
    public static ModelProfileCache LoadApiCache()
    {
        lock (Sync)
        {
            if (_apiCache is not null) return _apiCache;

            var cache = new ModelProfileCache();

            // 损坏时回退 .bak: 缓存丢了只是多一次 /models 拉取, 但半截 JSON 会让
            // 解析失败被 catch 吞掉, 直接退化成"没有缓存"而毫无痕迹。
            if (AtomicFile.TryReadText(CachePath, out var content, text =>
            {
                try
                {
                    return JsonSerializer.Deserialize(text, AppJsonContext.Default.ModelProfileCache) is not null;
                }
                catch
                {
                    return false;
                }
            }))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize(content, AppJsonContext.Default.ModelProfileCache);
                    if (loaded is not null) cache = loaded;
                }
                catch
                {
                }
            }

            _apiCache = cache;
            return cache;
        }
    }

    /// <summary>从 Provider 的 OpenAI 兼容 /models 端点拉取模型档案(OpenRouter 风格的
    /// pricing 字段 + OpenAI 风格的 context_window / context_length)。Anthropic 官方 API
    /// 不提供计价与上下文信息, 直接跳过。结果持久化到 models-cache.json; 失败时把原因写入
    /// <see cref="ModelProfileCache.Error"/> 并记 Warn 日志(旧档案保留, 不因一次抖动清空)。</summary>
    public static async Task<ModelProfileCache> FetchFromApiAsync(
        ProviderConfig provider, CancellationToken ct = default)
    {
        if (provider.Kind == ProviderKind.Anthropic ||
            string.IsNullOrWhiteSpace(provider.ApiKey) ||
            string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            return new ModelProfileCache { ProviderId = provider.Id };
        }

        // 统一由 ModelListService 从 base_url 推导地址(处理 /v1 前缀、尾部斜杠、Azure 形态),
        // 避免这里硬拼 /v1/models 造成 /v1/v1 双重前缀
        var url = ModelListService.BuildModelsUrl(provider.BaseUrl);
        if (url is null)
        {
            Log.Info("ModelProfile",
                $"跳过 {provider.Id} 的档案拉取: 无法从 base_url 推导模型列表地址({provider.BaseUrl})");
            return new ModelProfileCache { ProviderId = provider.Id };
        }

        List<ApiModelProfile> profiles;
        try
        {
            profiles = await FetchProfilesAsync(url, provider, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // 只放行用户主动取消; 超时/网络异常(HttpClient 超时抛 TaskCanceledException)同样记录为失败
            Log.Warn("ModelProfile", ex, $"拉取模型档案失败({provider.Id})");
            return StoreError(provider.Id, $"GET {url} → {ex.Message}");
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

    /// <summary>实际发起 /models 请求并解析档案条目; 响应结构不符时抛异常交由上层记录失败原因。</summary>
    private static async Task<List<ApiModelProfile>> FetchProfilesAsync(
        string url, ProviderConfig provider, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", provider.ApiKey);

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new LlmApiException($"响应缺少 data 数组(非 OpenAI 兼容 /models 响应): {url}");
        }

        var profiles = new List<ApiModelProfile>();
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

        return profiles;
    }

    /// <summary>记录拉取失败: 保留同一 Provider 的旧档案(避免一次网络抖动清空缓存)与原拉取时间,
    /// 附加错误原因后写回内存与磁盘, 供界面读取展示。</summary>
    private static ModelProfileCache StoreError(string providerId, string reason)
    {
        var cache = new ModelProfileCache
        {
            ProviderId = providerId,
            Error = reason
        };

        lock (Sync)
        {
            var prev = _apiCache;
            if (prev is not null &&
                string.Equals(prev.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            {
                cache.FetchedAt = prev.FetchedAt;
                cache.Profiles = prev.Profiles;
            }

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
                    // 条目命中但没填 context_tokens(用户只配了价格)时同样不能给 0:
                    // 与下方 Unknown 分支同理, 0 会让调用方的阈值判断短路。
                    ContextTokens = cfg.ContextTokens > 0 ? cfg.ContextTokens : DefaultContextTokens,
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
                    // API 条目缺 context_window 时 provider 常只给价格, 同样回退保守默认值
                    ContextTokens = api.ContextTokens > 0 ? api.ContextTokens : DefaultContextTokens,
                    InputPricePer1M = api.InputPricePer1M,
                    OutputPricePer1M = api.OutputPricePer1M,
                    Source = ProfileSource.Api
                };
            }
        }

        // 未知来源: 仍然给出保守上下文窗口(而非 0), 否则调用方的阈值判断会整体短路 ——
        // 自动压缩对未收录模型将永不触发, 而未收录恰恰是新模型的常态。价格保持 0,
        // CalcCostUsd 对 Unknown 直接返回 0, 不受影响。
        return new ModelProfile
        {
            ContextTokens = DefaultContextTokens,
            Source = ProfileSource.Unknown
        };
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
        // 原子写: 缓存损坏影响小(可重新拉取), 但仍不应留下半截 JSON
        AtomicFile.TryWriteAllText(
            CachePath,
            JsonSerializer.Serialize(cache, AppJsonContext.Default.ModelProfileCache),
            "models-cache.json");
    }
}
