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

public static class ProviderDefaults
{
    public static LlmSettings CreateDefaultSettings() => new()
    {
        ActiveProviderId = "openai",
        Providers =
        [
            new ProviderConfig
            {
                Id = "openai",
                Name = "OpenAI",
                Kind = ProviderKind.OpenAi,
                BaseUrl = "https://api.openai.com/v1",
                DefaultModel = "gpt-4o"
            },
            new ProviderConfig
            {
                Id = "anthropic",
                Name = "Anthropic",
                Kind = ProviderKind.Anthropic,
                BaseUrl = "https://api.anthropic.com",
                DefaultModel = "claude-sonnet-4-20250514"
            },
            new ProviderConfig
            {
                Id = "deepseek",
                Name = "DeepSeek",
                Kind = ProviderKind.OpenAi,
                BaseUrl = "https://api.deepseek.com/v1",
                DefaultModel = "deepseek-chat"
            }
        ]
    };
}

public static class ProviderSettingsService
{
    public static LlmSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.ProvidersPath))
            {
                var content = File.ReadAllText(AppPaths.ProvidersPath);
                var settings = TomlBridge.Deserialize<LlmSettings>(content);
                if (settings is { Providers.Count: > 0 })
                {
                    return settings;
                }
            }

            // 兼容旧 JSON 配置: 若 TOML 不存在, 回退读取 providers.json
            var legacyPath = Path.ChangeExtension(AppPaths.ProvidersPath, ".json");
            if (File.Exists(legacyPath))
            {
                var settings = JsonSerializer.Deserialize(
                    File.ReadAllText(legacyPath), AppJsonContext.Default.LlmSettings);
                if (settings is { Providers.Count: > 0 })
                {
                    return settings;
                }
            }
        }
        catch
        {
        }

        return ProviderDefaults.CreateDefaultSettings();
    }

    public static void Save(LlmSettings settings)
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllText(AppPaths.ProvidersPath, TomlBridge.Serialize(settings));
    }
}
