namespace AIShikikan.Core.Services.Llm;

public class LlmService
{
    private readonly Dictionary<string, IChatCompletionsClient> _clients = [];
    private LlmSettings _settings;

    public LlmService(LlmSettings? settings = null)
    {
        _settings = settings ?? ProviderSettingsService.Load();
    }

    public LlmSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            _clients.Clear();
        }
    }

    public ProviderConfig? GetProvider(string? id = null)
    {
        if (id is null)
        {
            return _settings.ActiveProvider;
        }

        return _settings.Providers.FirstOrDefault(p => p.Id == id);
    }

    public IChatCompletionsClient GetClient(string? providerId = null)
    {
        var provider = GetProvider(providerId)
            ?? throw new LlmApiException($"未找到 Provider: {providerId}，请检查 providers.json");

        if (_clients.TryGetValue(provider.Id, out var existing))
        {
            return existing;
        }

        var key = provider.ApiKey;
        if (string.IsNullOrEmpty(key))
        {
            key = Environment.GetEnvironmentVariable(provider.EnvKey) ?? string.Empty;
        }

        // 不为空才写入（避免覆盖环境变量退路）
        var effective = new ProviderConfig
        {
            Id = provider.Id,
            Name = provider.Name,
            Kind = provider.Kind,
            BaseUrl = provider.BaseUrl,
            ApiKey = key,
            DefaultModel = provider.DefaultModel
        };

        IChatCompletionsClient client = effective.Kind == ProviderKind.Anthropic
            ? new AnthropicChatCompletionsClient(effective)
            : new OpenAiChatCompletionsClient(effective);

        _clients[provider.Id] = client;
        return client;
    }

    public string ResolveModel(string? model = null, string? providerId = null)
    {
        if (!string.IsNullOrEmpty(model))
        {
            return model;
        }

        var provider = GetProvider(providerId);
        return !string.IsNullOrEmpty(_settings.ActiveModel)
            ? _settings.ActiveModel
            : provider?.DefaultModel ?? string.Empty;
    }
}