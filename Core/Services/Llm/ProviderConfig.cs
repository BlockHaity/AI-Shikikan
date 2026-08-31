using System.Text.Json;
using System.Text.Json.Serialization;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Llm;

public class ProviderConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ProviderKind Kind { get; set; } = ProviderKind.OpenAi;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string DefaultModel { get; set; } = string.Empty;

    /// <summary>启用的模型列表; 为空表示全部允许。</summary>
    public List<string> EnabledModels { get; set; } = [];

    /// <summary>各模型可用的最大思考等级(键为模型 ID, 值为 low/medium/high/xhigh/max); 未配置表示不限。</summary>
    public Dictionary<string, string> ModelMaxThinking { get; set; } = [];

    /// <summary>各模型手动配置的上下文窗口大小(token); 未配置时回退模型档案/默认值。</summary>
    public Dictionary<string, long> ModelContextTokens { get; set; } = [];

    /// <summary>返回指定模型的最大思考等级; 未配置时默认允许全部等级(Max)。</summary>
    public ThinkingLevel GetMaxThinking(string? modelId)
    {
        if (modelId is not null && ModelMaxThinking.TryGetValue(modelId, out var s)
            && ThinkingLevels.TryParse(s, out var level) && level is not ThinkingLevel.Auto)
        {
            return level;
        }

        return ThinkingLevel.Max;
    }

    /// <summary>返回指定模型手动配置的上下文窗口 token 数; 未配置或非法时返回 null。</summary>
    public long? GetContextTokens(string? modelId)
    {
        if (modelId is not null &&
            ModelContextTokens.TryGetValue(modelId, out var tokens) && tokens > 0)
        {
            return tokens;
        }

        return null;
    }

    [JsonIgnore]
    public string EnvKey => Kind == ProviderKind.Anthropic ? "ANTHROPIC_API_KEY" : "OPENAI_API_KEY";
}

public class LlmSettings
{
    public string ActiveProviderId { get; set; } = string.Empty;
    public string? ActiveModel { get; set; }
    public List<ProviderConfig> Providers { get; set; } = [];

    [JsonIgnore]
    public ProviderConfig? ActiveProvider =>
        Providers.FirstOrDefault(p => p.Id == ActiveProviderId);

    [JsonIgnore]
    public string ResolvedModel =>
        !string.IsNullOrEmpty(ActiveModel) ? ActiveModel : ActiveProvider?.DefaultModel ?? string.Empty;
}

public static class ProviderSettingsService
{
    /// <summary>首次启动时生成默认提供商配置(仅当用户文件不存在)。</summary>
    public static void EnsureDefaultExists()
    {
        DefaultConfig.WriteIfMissing(AppPaths.ProvidersPath, DefaultConfig.ProvidersResource);
    }

    public static LlmSettings Load()
    {
        if (File.Exists(AppPaths.ProvidersPath))
        {
            try
            {
                var content = File.ReadAllText(AppPaths.ProvidersPath);
                var settings = TomlBridge.Deserialize<LlmSettings>(content);
                if (settings is not null)
                {
                    // 用户文件为准: 即使删光了 Provider 也保持原样, 不恢复默认
                    return settings;
                }
            }
            catch
            {
            }

            // 文件存在但解析失败: 不覆盖用户文件, 按空配置运行
            return new LlmSettings();
        }

        // 兼容旧 JSON 配置: 若 TOML 不存在, 回退读取 providers.json
        var legacyPath = Path.ChangeExtension(AppPaths.ProvidersPath, ".json");
        if (File.Exists(legacyPath))
        {
            try
            {
                var settings = JsonSerializer.Deserialize(
                    File.ReadAllText(legacyPath), AppJsonContext.Default.LlmSettings);
                if (settings is not null)
                {
                    return settings;
                }
            }
            catch
            {
            }

            return new LlmSettings();
        }

        // 首次启动: 生成默认配置文件后再读取
        EnsureDefaultExists();
        try
        {
            var content = File.ReadAllText(AppPaths.ProvidersPath);
            return TomlBridge.Deserialize<LlmSettings>(content) ?? new LlmSettings();
        }
        catch
        {
            return new LlmSettings();
        }
    }

    public static void Save(LlmSettings settings)
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllText(AppPaths.ProvidersPath, TomlBridge.Serialize(settings));
    }
}
