using System.Collections.Concurrent;

namespace AIShikikan.Core.Services.Llm;

public class LlmService
{
    // 引擎侧存在真实并发写: 多个会话引擎可并行(同 worktree 同分支允许多会话),
    // run_subagents 内部 Task.WhenAll 并发跑子代理也会各自调 GetClient, 而 Settings
    // setter 又在 UI 线程 Clear。普通 Dictionary 并发写会损坏内部桶链(甚至死循环),
    // 故用 ConcurrentDictionary。
    private readonly ConcurrentDictionary<string, IChatCompletionsClient> _clients = new();

    // ConcurrentDictionary 只保证单次操作原子, Clear 与「写回缓存」之间没有先后保证:
    // A 线程按旧配置构造完 client 尚未入缓存, B 线程换配置并 Clear, A 随后入缓存 →
    // 换完配置仍返回旧 client。这把锁把「换配置」与「建 client 并入缓存」串起来。
    private readonly object _clientsLock = new();

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
            lock (_clientsLock)
            {
                _settings = value;
                _clients.Clear();
            }
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
            ?? throw new LlmApiException($"未找到 Provider: {providerId}，请检查 providers.toml");

        // 快速路径: 命中缓存直接返回(引擎每轮都要调), 不进锁
        var key = BuildCacheKey(provider);
        if (_clients.TryGetValue(key, out var existing))
        {
            return existing;
        }

        lock (_clientsLock)
        {
            // 重新解析 provider: Settings 可能在本方法进锁之前已被换掉, 重新解析避免
            // 把「按旧配置构造的 client」写回缓存。
            provider = GetProvider(providerId)
                ?? throw new LlmApiException($"未找到 Provider: {providerId}，请检查 providers.toml");
            key = BuildCacheKey(provider);

            // 双检: 等锁期间可能已被其他线程填好, 不检查会重复构造 client 顶掉已有实例
            if (_clients.TryGetValue(key, out existing))
            {
                return existing;
            }

            var client = CreateClient(provider);
            _clients[key] = client;
            return client;
        }
    }

    /// <summary>
    /// client 缓存键。只用 Id 的话, 把 provider 的 kind(OpenAI↔Anthropic)或 base_url 改掉后
    /// 仍会命中按旧协议/旧端点构造的 client(设置页就地改 ProviderConfig 实例时连 Settings
    /// setter 都收不到, 清缓存救不了), 只能重启进程。带上 Kind/BaseUrl 让这类变更自动失效。
    /// </summary>
    private static string BuildCacheKey(ProviderConfig provider)
        => $"{provider.Id}|{provider.Kind}|{provider.BaseUrl}";

    private static IChatCompletionsClient CreateClient(ProviderConfig provider)
    {
        var key = provider.ApiKey;
        if (string.IsNullOrEmpty(key))
        {
            key = Environment.GetEnvironmentVariable(provider.EnvKey) ?? string.Empty;
        }

        // 刻意只拷贝 client 真正用到的 6 个字段: EnabledModels / ModelMaxThinking /
        // ModelContextTokens 是给上层(ResolveThinking、上下文窗口解析)用的, client 侧不需要,
        // 不带进来可避免缓存里常驻的 ProviderConfig 随配置膨胀。
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

        return client;
    }

    public string ResolveModel(string? model = null, string? providerId = null)
    {
        if (!string.IsNullOrEmpty(model))
        {
            return model;
        }

        var provider = GetProvider(providerId);
        var preferred = !string.IsNullOrEmpty(_settings.ActiveModel)
            ? _settings.ActiveModel
            : provider?.DefaultModel ?? string.Empty;

        if (provider?.EnabledModels is not { Count: > 0 })
        {
            return preferred;
        }

        if (provider.EnabledModels.Contains(preferred, StringComparer.OrdinalIgnoreCase))
        {
            return preferred;
        }

        return provider.EnabledModels.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
            ?? provider.DefaultModel;
    }
}