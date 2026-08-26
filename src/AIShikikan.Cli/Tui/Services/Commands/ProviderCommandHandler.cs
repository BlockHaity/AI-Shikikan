using AIShikikan.Cli.Tui.Ui;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Llm;

namespace AIShikikan.Cli.Tui.Services.Commands;

/// <summary>Provider 与模型管理: 支持查看/新增/删除 Provider、列出/切换模型、从 API 拉取模型列表。</summary>
public sealed class ProviderCommandHandler : ICommandHandler
{
    private readonly CommanderRuntime _runtime;
    private readonly IUiOutput _ui;

    public ProviderCommandHandler(CommanderRuntime runtime, IUiOutput ui)
    {
        _runtime = runtime;
        _ui = ui;
    }

    public bool CanHandle(string command) =>
        command is "providers" or "provider" or "models" or "model";

    public IEnumerable<(string Command, string Help)> HelpRows =>
    [
        ("/providers", "列出 Provider 配置与 API Key 状态"),
        ("/provider add [grey]<名称> <base-url> <模型> [--anthropic][/]", "新增 Provider(从环境变量读 Key)"),
        ("/provider remove [grey]<id>[/]", "删除 Provider"),
        ("/provider use [grey]<id>[/]", "切换活动 Provider"),
        ("/models [grey]<provider-id>[/]", "列出 Provider 的已启用模型"),
        ("/models fetch [grey]<provider-id>[/]", "从 API 拉取模型列表"),
        ("/models enable-all|disable-all [grey]<provider-id>[/]", "启用全部 / 停用全部模型"),
        ("/model [grey]<模型>[/]", "查看/切换活动模型 (模型/provider)")
    ];

    public bool TryHandle(string command, string args)
    {
        switch (command)
        {
            case "providers":
                ListProviders();
                return true;
            case "provider":
                HandleProvider(args);
                return true;
            case "models":
                HandleModels(args);
                return true;
            case "model":
                SetModel(args);
                return true;
            default:
                return false;
        }
    }

    private void HandleProvider(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            ListProviders();
            return;
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "add":
                AddProvider(parts.Skip(1).ToArray());
                return;
            case "remove":
            case "rm":
                RemoveProvider(parts.Length > 1 ? parts[1] : string.Empty);
                return;
            case "use":
            case "switch":
                UseProvider(parts.Length > 1 ? parts[1] : string.Empty);
                return;
            default:
                ListProviders();
                return;
        }
    }

    private void AddProvider(string[] parts)
    {
        var anthropic = parts.Contains("--anthropic", StringComparer.OrdinalIgnoreCase);
        parts = parts.Where(p => !string.Equals(p, "--anthropic", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (parts.Length < 3)
        {
            _ui.Error("用法: /provider add <名称> <base-url> <默认模型> [--anthropic]  (API Key 从环境变量读取)");
            return;
        }

        var name = parts[0];
        var baseUrl = parts[1];
        var model = parts[2];
        var id = name.ToLowerInvariant().Replace(" ", "-");

        var settings = _runtime.Llm.Settings;
        if (settings.Providers.Any(p => p.Id == id))
        {
            _ui.Error($"Provider 已存在: {id}");
            return;
        }

        var provider = new ProviderConfig
        {
            Id = id,
            Name = name,
            Kind = anthropic ? ProviderKind.Anthropic : ProviderKind.OpenAi,
            BaseUrl = baseUrl,
            DefaultModel = model,
        };
        provider.EnabledModels.Add(model);

        settings.Providers.Add(provider);
        ProviderSettingsService.Save(settings);
        _ui.Ok($"已新增 Provider: {name} ({id}) [{provider.Kind}]");
        _ui.Hint(anthropic
            ? "Anthropic 类型使用 ANTHROPIC_API_KEY 环境变量"
            : "OpenAI 兼容类型使用 OPENAI_API_KEY 环境变量(如需自定义请在 providers.toml 中配置)");
    }

    private void RemoveProvider(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _ui.Error("用法: /provider remove <id>");
            return;
        }

        var settings = _runtime.Llm.Settings;
        var provider = settings.Providers.FirstOrDefault(p => p.Id == id);
        if (provider is null)
        {
            _ui.Error($"未找到 Provider: {id}");
            return;
        }

        if (string.Equals(settings.ActiveProviderId, id, StringComparison.OrdinalIgnoreCase))
        {
            settings.ActiveProviderId = string.Empty;
            settings.ActiveModel = null;
        }

        settings.Providers.Remove(provider);
        ProviderSettingsService.Save(settings);
        _ui.Ok($"已删除 Provider: {id}");
    }

    private void UseProvider(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _ui.Error("用法: /provider use <id>");
            return;
        }

        var settings = _runtime.Llm.Settings;
        var provider = settings.Providers.FirstOrDefault(p => p.Id == id);
        if (provider is null)
        {
            _ui.Error($"未找到 Provider: {id}");
            return;
        }

        settings.ActiveProviderId = provider.Id;
        ProviderSettingsService.Save(settings);
        _ui.Ok($"已切换活动 Provider: {provider.Name} ({provider.Id})");
    }

    private void HandleModels(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
        var rest = parts.Length > 1 ? parts[1] : string.Empty;

        if (sub == "fetch")
        {
            FetchModels(rest);
            return;
        }

        if (sub == "enable-all")
        {
            ToggleAllModels(rest, enabled: true);
            return;
        }

        if (sub == "disable-all")
        {
            ToggleAllModels(rest, enabled: false);
            return;
        }

        ListModels(sub);
    }

    private void ToggleAllModels(string providerArg, bool enabled)
    {
        var provider = ResolveProvider(providerArg);
        if (provider is null)
        {
            _ui.Error("未找到 Provider, 用法: /models enable-all|disable-all <provider-id>");
            return;
        }

        var settings = _runtime.Llm.Settings;
        if (enabled)
        {
            // 启用全部: 拉取 Provider 完整模型列表并全部启用
            FetchModels(provider.Id);
            return;
        }

        provider.EnabledModels.Clear();
        ProviderSettingsService.Save(settings);
        _ui.Ok($"已停用 {provider.Name} 的全部模型");
        ListModels(provider.Id);
    }

    private void FetchModels(string providerArg)
    {
        var settings = _runtime.Llm.Settings;
        var provider = ResolveProvider(providerArg);
        if (provider is null)
        {
            _ui.Error("未找到 Provider, 用法: /models fetch <provider-id>");
            return;
        }

        // 从环境变量取 Key(不落盘到 providers.toml)
        var key = string.IsNullOrEmpty(provider.ApiKey)
            ? Environment.GetEnvironmentVariable(provider.EnvKey) ?? string.Empty
            : provider.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            _ui.Error($"缺少 API Key (环境变量 {provider.EnvKey})");
            return;
        }

        var effective = new ProviderConfig
        {
            Id = provider.Id,
            Name = provider.Name,
            Kind = provider.Kind,
            BaseUrl = provider.BaseUrl,
            ApiKey = key,
            DefaultModel = provider.DefaultModel
        };

        try
        {
            _ui.Hint($"正在从 {provider.BaseUrl} 获取模型列表...");
            var models = ModelListService.FetchModelsAsync(effective).GetAwaiter().GetResult();
            if (models.Count == 0)
            {
                _ui.Hint("未获取到任何模型");
                return;
            }

            provider.EnabledModels = models.ToList();
            if (string.IsNullOrWhiteSpace(provider.DefaultModel) ||
                !provider.EnabledModels.Contains(provider.DefaultModel, StringComparer.OrdinalIgnoreCase))
            {
                provider.DefaultModel = provider.EnabledModels.FirstOrDefault() ?? provider.DefaultModel;
            }

            ProviderSettingsService.Save(settings);
            _ui.Ok($"已更新 {provider.Name} 的模型列表 ({models.Count} 个)");
            ListModels(provider.Id);
        }
        catch (Exception ex)
        {
            _ui.Error($"获取失败: {ex.Message}");
        }
    }

    private void ListModels(string providerArg)
    {
        var provider = ResolveProvider(providerArg);
        if (provider is null)
        {
            _ui.Error("未找到 Provider, 用法: /models <provider-id>");
            return;
        }

        var active = _runtime.Llm.Settings.ActiveModel ?? provider.DefaultModel;
        var models = provider.EnabledModels.Count > 0
            ? provider.EnabledModels
            : [provider.DefaultModel];
        var rows = new List<IReadOnlyList<string>>();
        foreach (var m in models.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var isDefault = string.Equals(m, provider.DefaultModel, StringComparison.OrdinalIgnoreCase) ? "[yellow]★[/]" : "";
            var isActive = string.Equals(m, active, StringComparison.OrdinalIgnoreCase) ? "[green]◀[/]" : "";
            rows.Add(new[] { MarkupEscape.Escape(m), isDefault, isActive });
        }

        _ui.Table(["模型", "默认", "活动"], rows);
        _ui.Hint("用法: /model <模型>/<provider-id> 切换活动模型 | /models fetch <provider-id> 拉取模型列表");
        _ui.ScrollToBottom();
    }

    private void SetModel(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            _ui.Write(
                $"[bold]当前模型:[/] [green]{MarkupEscape.Escape(_runtime.Llm.ResolveModel())}[/] " +
                $"(provider: [grey]{_runtime.Llm.GetProvider()?.Id ?? "?"}[/])");
            return;
        }

        var parts = args.Split('/', 2);
        var model = parts[0].Trim();
        var providerId = parts.Length > 1 ? parts[1].Trim() : null;

        var provider = _runtime.Llm.GetProvider(providerId) ?? _runtime.Llm.GetProvider();
        if (provider is null)
        {
            _ui.Error($"找不到 Provider: {providerId ?? "?"}");
            return;
        }

        _runtime.Llm.Settings.ActiveProviderId = provider.Id;
        _runtime.Llm.Settings.ActiveModel = model;
        ProviderSettingsService.Save(_runtime.Llm.Settings);
        _ui.Ok($"已切换: {model} @ {provider.Id}");
    }

    private void ListProviders()
    {
        var settings = _runtime.Llm.Settings;
        if (settings.Providers.Count == 0)
        {
            _ui.Hint("暂无 Provider, 用法: /provider add <名称> <base-url> <模型>");
            return;
        }

        var rows = new List<IReadOnlyList<string>>();
        foreach (var p in settings.Providers)
        {
            var hasKey = !string.IsNullOrEmpty(p.ApiKey) ||
                         !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(p.EnvKey));
            var isActive = string.Equals(p.Id, settings.ActiveProviderId, StringComparison.OrdinalIgnoreCase)
                ? "[green]◀[/]"
                : "";
            rows.Add(new[]
            {
                p.Id,
                MarkupEscape.Escape(p.Name),
                p.Kind == ProviderKind.Anthropic ? "Anthropic" : "OpenAI 兼容",
                MarkupEscape.Escape(p.DefaultModel),
                hasKey ? "[green]✔[/]" : "[red]✘[/]",
                isActive
            });
        }

        _ui.Table(["ID", "名称", "类型", "默认模型", "API Key", "活动"], rows);
        _ui.Hint($"配置文件: {AppPaths.ProvidersPath}");
        _ui.Hint("用法: /provider add|remove|use <参数> | /models [fetch] <provider-id>");
        _ui.ScrollToBottom();
    }

    private ProviderConfig? ResolveProvider(string id)
    {
        var settings = _runtime.Llm.Settings;
        if (string.IsNullOrWhiteSpace(id))
        {
            return settings.ActiveProvider ?? settings.Providers.FirstOrDefault();
        }

        return settings.Providers.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Name, id, StringComparison.OrdinalIgnoreCase));
    }
}
