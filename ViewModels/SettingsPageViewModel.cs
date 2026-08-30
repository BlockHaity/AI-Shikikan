using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Mcp;
using AIShikikan.Gui.Resources;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIShikikan.Gui.ViewModels;

/// <summary>设置页模型条目: 展示模型名 + 启用开关, 变更即持久化。</summary>
public partial class ModelItem : ObservableObject
{
    private readonly LlmSettings _settings;
    private readonly ProviderConfig _provider;
    private readonly Action _onChanged;

    /// <summary>模型管理里可选的思考等级档位(不含"自动", 自动仅在聊天菜单)。</summary>
    public static IReadOnlyList<ThinkingLevel> LevelOptions { get; } =
        [ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh, ThinkingLevel.Max];

    public ModelItem(LlmSettings settings, ProviderConfig provider, string id, Action onChanged)
    {
        _settings = settings;
        _provider = provider;
        _onChanged = onChanged;
        Id = id;
        _isEnabled = provider.EnabledModels.Contains(id, StringComparer.OrdinalIgnoreCase);
        // 直接写 backing field, 避免构造时触发持久化
        _maxThinking = provider.GetMaxThinking(id);
    }

    public string Id { get; }

    /// <summary>该模型配置的最大思考等级。</summary>
    [ObservableProperty]
    private ThinkingLevel _maxThinking;

    partial void OnMaxThinkingChanged(ThinkingLevel value)
    {
        _provider.ModelMaxThinking[Id] = ThinkingLevels.ToConfigString(value);
        ProviderSettingsService.Save(_settings);
        _onChanged();
    }

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (value)
        {
            if (!_provider.EnabledModels.Contains(Id, StringComparer.OrdinalIgnoreCase))
            {
                _provider.EnabledModels.Add(Id);
            }
        }
        else
        {
            _provider.EnabledModels.RemoveAll(m => string.Equals(m, Id, StringComparison.OrdinalIgnoreCase));
        }

        if (string.Equals(_provider.DefaultModel, Id, StringComparison.OrdinalIgnoreCase) && !value)
        {
            _provider.DefaultModel = _provider.EnabledModels.FirstOrDefault() ?? string.Empty;
        }

        ProviderSettingsService.Save(_settings);
        _onChanged();
    }
}

/// <summary>MCP 服务器条目: 列表展示 + 选中后进入编辑区; StatusText 提供连接状态/工具数。</summary>
public partial class McpItemViewModel : ObservableObject
{
    private readonly McpServerDefinition _def;

    public McpItemViewModel(McpServerDefinition def) => _def = def;

    public string Id => _def.Id;
    public string Name => string.IsNullOrWhiteSpace(_def.Name) ? _def.Id : _def.Name;

    /// <summary>底层配置定义(编辑保存时直接修改此实例)。</summary>
    public McpServerDefinition Definition => _def;

    public string CommandSummary => McpHttpClient.IsHttpTransport(_def.Transport)
        ? $"[{_def.Transport}] {_def.Url}"
        : $"{_def.Command} {string.Join(' ', _def.Args)}".Trim();

    public bool IsConnected { get; private set; }

    public string StatusText { get; private set; } = string.Empty;

    /// <summary>读取当前连接状态(供列表重建时刷新)。</summary>
    public void Probe()
    {
        var mcp = AppShell.Instance.Runtime.Mcp;
        IsConnected = mcp.IsConnected(_def.Id);
        var toolCount = mcp.EnumerateTools().Count(t =>
            t.ServerId.Equals(_def.Id, StringComparison.OrdinalIgnoreCase));
        StatusText = IsConnected
            ? string.Format(Strings.Settings_McpToolCount, toolCount)
            : Strings.Settings_McpNotConnected;
    }

    /// <summary>配置被编辑保存后刷新行内展示。</summary>
    public void RefreshDisplay()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(CommandSummary));
    }
}

public partial class SettingsPageViewModel : ViewModelBase
{
    private readonly ThemeService _themeService;
    private readonly int _currentLanguageIndex;

    public ThemeService ThemeService => _themeService;

    public string AppName { get; } = AppInfo.Name;
    public string AppVersion { get; } = AppInfo.Version;

    public IReadOnlyList<string> LanguageOptions { get; } = ["简体中文", "English"];

    public IReadOnlyList<string> ProviderKindOptions { get; } = ["OpenAI 兼容", "Anthropic"];

    // 字体(标准 + 等宽两个可配置项)
    public IReadOnlyList<string> StandardFontOptions { get; } = ["系统默认", "自定义"];

    public IReadOnlyList<string> MonoFontOptions { get; } = ["系统默认", "自定义"];

    [ObservableProperty]
    private int _languageIndex;

    [ObservableProperty]
    private bool _isDarkTheme;

    [ObservableProperty]
    private Bitmap? _backgroundPreview;

    [ObservableProperty]
    private bool _hasBackground;

    [ObservableProperty]
    private bool _isRestartHintVisible;

    // 提供商管理
    [ObservableProperty]
    private LlmSettings _llmSettings;

    /// <summary>提供商列表(可观察集合, 增删即时刷新 UI)。</summary>
    public ObservableCollection<ProviderConfig> ProviderItems { get; } = [];

    [ObservableProperty]
    private ProviderConfig? _selectedProvider;

    [ObservableProperty]
    private string _newProviderName = string.Empty;

    [ObservableProperty]
    private string _newProviderBaseUrl = string.Empty;

    [ObservableProperty]
    private string _newProviderApiKey = string.Empty;

    [ObservableProperty]
    private string _newProviderCustomModel = string.Empty;

    [ObservableProperty]
    private int _newProviderModelSourceIndex;

    [ObservableProperty]
    private string _newProviderModelError = string.Empty;

    [ObservableProperty]
    private bool _isFetchingNewProviderModels;

    public IReadOnlyList<string> NewProviderModelSourceOptions { get; } = ["自定义模型", "从 API 获取"];

    public IReadOnlyList<string> NewProviderFetchedModels { get; private set; } = [];

    [ObservableProperty]
    private int _newProviderKindIndex;

    // 模型管理
    public ObservableCollection<ModelItem> ModelItems { get; } = [];

    [ObservableProperty]
    private bool _isFetchingModels;

    [ObservableProperty]
    private string _modelFetchError = string.Empty;

    [ObservableProperty]
    private ModelItem? _selectedDefaultModelItem;

    // 字体
    [ObservableProperty]
    private int _selectedStandardFontIndex;

    [ObservableProperty]
    private string _standardCustomFontText = string.Empty;

    [ObservableProperty]
    private int _selectedMonoFontIndex;

    [ObservableProperty]
    private string _monoCustomFontText = string.Empty;

    [ObservableProperty]
    private string _fontInfo = string.Empty;

    [ObservableProperty]
    private string _monoFontInfo = string.Empty;

    // Agent 管理
    [ObservableProperty]
    private IReadOnlyList<CliAgentDefinition> _userAgents;

    // MCP 服务器管理
    public ObservableCollection<McpItemViewModel> McpItems { get; } = [];

    public IReadOnlyList<string> McpTransportOptions { get; } = ["stdio", "http", "sse"];

    /// <summary>当前选择的传输方式是否为 stdio(决定新增表单显示命令/参数/环境变量还是 URL)。</summary>
    public bool IsStdioTransport => NewMcpTransportIndex == 0;

    /// <summary>当前选择的传输方式是否为 http/sse(显示 URL 输入)。</summary>
    public bool IsHttpTransport => NewMcpTransportIndex > 0;

    [ObservableProperty]
    private string _newMcpName = string.Empty;

    [ObservableProperty]
    private int _newMcpTransportIndex;

    partial void OnNewMcpTransportIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsStdioTransport));
        OnPropertyChanged(nameof(IsHttpTransport));
    }

    [ObservableProperty]
    private string _newMcpUrl = string.Empty;

    [ObservableProperty]
    private string _newMcpCommand = string.Empty;

    [ObservableProperty]
    private string _newMcpArgs = string.Empty;

    [ObservableProperty]
    private string _newMcpEnv = string.Empty;

    [ObservableProperty]
    private string _mcpStatus = string.Empty;

    [RelayCommand]
    private void AddMcpServer()
    {
        if (string.IsNullOrWhiteSpace(NewMcpName)) return;

        var transport = McpTransportOptions[Math.Min(NewMcpTransportIndex, McpTransportOptions.Count - 1)];
        if (transport == "stdio" && string.IsNullOrWhiteSpace(NewMcpCommand)) return;
        if (transport != "stdio" && string.IsNullOrWhiteSpace(NewMcpUrl)) return;

        var name = NewMcpName.Trim();
        var id = name.ToLowerInvariant().Replace(" ", "-");
        var def = new McpServerDefinition
        {
            Id = id,
            Name = name,
            Transport = transport,
            Url = NewMcpUrl.Trim(),
            Command = NewMcpCommand.Trim(),
            Args = ParseArgs(NewMcpArgs),
            Env = ParseEnv(NewMcpEnv),
            Enabled = true
        };

        McpConfigService.Upsert(def);
        ReloadMcpItems();
        _ = ReconnectAsync();

        NewMcpName = string.Empty;
        NewMcpTransportIndex = 0;
        NewMcpUrl = string.Empty;
        NewMcpCommand = string.Empty;
        NewMcpArgs = string.Empty;
        NewMcpEnv = string.Empty;
    }

    [ObservableProperty]
    private McpItemViewModel? _selectedMcpItem;

    [ObservableProperty]
    private int _mcpEditTransportIndex;

    [ObservableProperty]
    private string _mcpEditUrl = string.Empty;

    [ObservableProperty]
    private string _mcpEditCommand = string.Empty;

    [ObservableProperty]
    private string _mcpEditArgs = string.Empty;

    [ObservableProperty]
    private string _mcpEditEnv = string.Empty;

    [ObservableProperty]
    private bool _mcpEditEnabled;

    public bool IsMcpEditStdio => McpEditTransportIndex == 0;

    public bool IsMcpEditHttp => McpEditTransportIndex > 0;

    partial void OnMcpEditTransportIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsMcpEditStdio));
        OnPropertyChanged(nameof(IsMcpEditHttp));
    }

    partial void OnSelectedMcpItemChanged(McpItemViewModel? value)
    {
        var def = value?.Definition;
        var transport = (def?.Transport ?? "stdio").Trim().ToLowerInvariant();
        McpEditTransportIndex = transport switch
        {
            "http" or "streamable-http" or "streamable_http" => 1,
            "sse" => 2,
            _ => 0
        };
        McpEditUrl = def?.Url ?? string.Empty;
        McpEditCommand = def?.Command ?? string.Empty;
        McpEditArgs = def is { Args.Count: > 0 } ? string.Join(" ", def.Args) : string.Empty;
        McpEditEnv = def?.Env is { Count: > 0 }
            ? string.Join("\n", def.Env.Select(kv => $"{kv.Key}={kv.Value}"))
            : string.Empty;
        McpEditEnabled = def?.Enabled ?? false;
    }

    /// <summary>保存选中 MCP 服务器的编辑(含启用开关), 随后后台重连。</summary>
    [RelayCommand]
    private void SaveMcpServer()
    {
        var item = SelectedMcpItem;
        if (item is null) return;

        var def = item.Definition;
        var transport = McpTransportOptions[Math.Min(McpEditTransportIndex, McpTransportOptions.Count - 1)];
        if (transport == "stdio" && string.IsNullOrWhiteSpace(McpEditCommand)) return;
        if (transport != "stdio" && string.IsNullOrWhiteSpace(McpEditUrl)) return;

        def.Transport = transport;
        def.Url = McpEditUrl.Trim();
        def.Command = McpEditCommand.Trim();
        def.Args = ParseArgs(McpEditArgs);
        def.Env = ParseEnv(McpEditEnv);
        def.Enabled = McpEditEnabled;

        McpConfigService.Upsert(def);
        item.RefreshDisplay();
        _ = ReconnectAsync();
    }

    /// <summary>移除选中的 MCP 服务器并重连。</summary>
    [RelayCommand]
    private void RemoveSelectedMcpServer()
    {
        var item = SelectedMcpItem;
        if (item is null) return;

        McpConfigService.Remove(item.Id);
        SelectedMcpItem = null;
        ReloadMcpItems();
        _ = ReconnectAsync();
    }

    [RelayCommand]
    private async Task RefreshMcp() => await ReconnectAsync().ConfigureAwait(true);

    [ObservableProperty]
    private string _resetStatus = string.Empty;

    /// <summary>一键还原默认设置: 弹窗二次确认后, 重置界面偏好与 providers/agents/mcp-servers/roster.prompt,
    /// 并让运行时即时生效; 人格/模板示例与会话/统计数据不受影响。</summary>
    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        var owner = GetMainWindow();
        if (owner is null) return;

        var confirmed = await Views.ConfirmDialog.ShowAsync(
            owner,
            Strings.Settings_ResetTitle,
            Strings.Settings_ResetMessage,
            Strings.Settings_ResetConfirm,
            Strings.Settings_Cancel);
        if (!confirmed) return;

        // 界面偏好恢复默认(事件触发后主题/字体/背景即时跟随)
        _themeService.ResetToDefault();
        InitFonts();
        BackgroundPreview = null;
        HasBackground = false;
        LanguageIndex = 0;

        // 配置文件还原默认, Llm.Settings 已替换为默认实例
        var runtime = AppShell.Instance.Runtime;
        var messages = runtime.ResetSettingsToDefault();

        // 设置页跟随新的 LlmSettings 实例
        LlmSettings = runtime.Llm.Settings;
        ProviderItems.Clear();
        foreach (var p in LlmSettings.Providers)
        {
            ProviderItems.Add(p);
        }

        SelectedProvider = null;
        SyncDefaultModelSelection();

        // Agent 列表与 MCP 桥接工具重建
        AppShell.Instance.ReloadAgents();
        UserAgents = AgentConfigService.LoadAll().ToList();
        await ReconnectAsync().ConfigureAwait(true);
        AppShell.Instance.NotifyDataChanged();

        ResetStatus = string.Join("\n", messages) + "\n[偏好] 已还原默认界面设置";
    }

    /// <summary>重连全部启用服务器并展示结果消息。</summary>
    private async Task ReconnectAsync()
    {
        McpStatus = "连接中…";
        var messages = await AppShell.Instance.Runtime.RefreshMcpToolsAsync().ConfigureAwait(true);
        McpStatus = string.Join("\n", messages);
        ReloadMcpItems();
    }

    /// <summary>按最新配置重建 MCP 条目列表(刷新状态列)。</summary>
    public void ReloadMcpItems()
    {
        McpItems.Clear();
        foreach (var def in McpConfigService.LoadAll())
        {
            var item = new McpItemViewModel(def);
            item.Probe();
            McpItems.Add(item);
        }
    }

    /// <summary>解析 KEY=VALUE 行序列为环境变量表。</summary>
    private static Dictionary<string, string> ParseEnv(string input) =>
        input.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Length > 0)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim());

    // Agent 管理(其余)

    [ObservableProperty]
    private CliAgentDefinition? _selectedAgent;

    [ObservableProperty]
    private string _newAgentName = string.Empty;

    [ObservableProperty]
    private string _newAgentExecutable = string.Empty;

    [ObservableProperty]
    private string _agentExecutableEdit = string.Empty;

    [ObservableProperty]
    private string _agentArgsEdit = string.Empty;

    [ObservableProperty]
    private string _agentPlanArgsEdit = string.Empty;

    [ObservableProperty]
    private string _newAgentArgs = string.Empty;

    [ObservableProperty]
    private string _newAgentPlanArgs = string.Empty;

    public SettingsPageViewModel(ThemeService themeService)
    {
        _themeService = themeService;
        _isDarkTheme = themeService.IsDarkTheme;
        _currentLanguageIndex = themeService.Language == "zh-CN" ? 0 : 1;
        _languageIndex = _currentLanguageIndex;
        LlmSettings = AppShell.Instance.Runtime.Llm.Settings;
        UserAgents = AgentConfigService.LoadAll().ToList();
        foreach (var p in LlmSettings.Providers)
        {
            ProviderItems.Add(p);
        }

        InitFonts();
        SyncDefaultModelSelection();
        ReloadMcpItems();

        _themeService.ThemeChanged += (_, isDark) => IsDarkTheme = isDark;
    }

    /// <summary>根据已保存的字体偏好初始化标准/等宽两个下拉框状态。</summary>
    private void InitFonts()
    {
        var std = _themeService.CustomFont;
        if (string.IsNullOrWhiteSpace(std))
        {
            SelectedStandardFontIndex = 0;
            FontInfo = "HarmonyOS Sans SC";
        }
        else if (std == "系统默认")
        {
            SelectedStandardFontIndex = 0;
            FontInfo = "系统默认";
        }
        else
        {
            StandardCustomFontText = std;
            SelectedStandardFontIndex = 1;
            FontInfo = std;
        }

        var mono = _themeService.MonoFont;
        if (string.IsNullOrWhiteSpace(mono))
        {
            SelectedMonoFontIndex = 0;
            MonoFontInfo = "CaskaydiaCove Nerd Font Mono";
        }
        else if (mono == "系统默认")
        {
            SelectedMonoFontIndex = 0;
            MonoFontInfo = "系统默认";
        }
        else
        {
            MonoCustomFontText = mono;
            SelectedMonoFontIndex = 1;
            MonoFontInfo = mono;
        }
    }

    private void SyncDefaultModelSelection()
    {
        if (SelectedProvider is null) return;
        SelectedDefaultModelItem = ModelItems.FirstOrDefault(m =>
            string.Equals(m.Id, SelectedProvider.DefaultModel, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnIsDarkThemeChanged(bool value)
    {
        _themeService.IsDarkTheme = value;
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = value
                ? Avalonia.Styling.ThemeVariant.Dark
                : Avalonia.Styling.ThemeVariant.Light;
        }
    }

    partial void OnLanguageIndexChanged(int value)
    {
        var lang = value == 0 ? "zh-CN" : "en-US";
        _themeService.Language = lang;
        App.I18nService.SetLanguage(lang);
        IsRestartHintVisible = value != _currentLanguageIndex;
    }

    partial void OnSelectedStandardFontIndexChanged(int value)
    {
        if (value == 1) return; // 自定义: 由文本输入即时应用

        _themeService.CustomFont = string.Empty;
        FontInfo = "HarmonyOS Sans SC";
    }

    partial void OnStandardCustomFontTextChanged(string value)
    {
        if (SelectedStandardFontIndex != 1 || string.IsNullOrWhiteSpace(value)) return;

        var font = value.Trim();
        _themeService.CustomFont = font;
        FontInfo = font;
    }

    partial void OnSelectedMonoFontIndexChanged(int value)
    {
        if (value == 1) return; // 自定义: 由文本输入即时应用

        _themeService.MonoFont = string.Empty;
        MonoFontInfo = "CaskaydiaCove Nerd Font Mono";
    }

    partial void OnMonoCustomFontTextChanged(string value)
    {
        if (SelectedMonoFontIndex != 1 || string.IsNullOrWhiteSpace(value)) return;

        var font = value.Trim();
        _themeService.MonoFont = font;
        MonoFontInfo = font;
    }

    partial void OnSelectedProviderChanged(ProviderConfig? value)
    {
        ModelItems.Clear();
        ModelFetchError = string.Empty;

        if (value is null)
        {
            SelectedDefaultModelItem = null;
            return;
        }

        foreach (var id in value.EnabledModels)
        {
            ModelItems.Add(new ModelItem(LlmSettings, value, id, OnModelConfigChanged));
        }

        SyncDefaultModelSelection();
    }

    partial void OnSelectedDefaultModelItemChanged(ModelItem? value)
    {
        if (SelectedProvider is null || value is null) return;
        if (string.Equals(SelectedProvider.DefaultModel, value.Id, StringComparison.OrdinalIgnoreCase)) return;

        SelectedProvider.DefaultModel = value.Id;
        ProviderSettingsService.Save(LlmSettings);
        AppShell.Instance.NotifyDataChanged();
    }

    partial void OnSelectedAgentChanged(CliAgentDefinition? value)
    {
        AgentExecutableEdit = value?.Executable ?? string.Empty;
        AgentArgsEdit = value is { Args.Count: > 0 } ? string.Join(" ", value.Args) : string.Empty;
        AgentPlanArgsEdit = value is { PlanArgs.Count: > 0 } ? string.Join(" ", value.PlanArgs) : string.Empty;
    }

    private void OnModelConfigChanged()
    {
        SyncDefaultModelSelection();
        AppShell.Instance.NotifyDataChanged();
    }

    [RelayCommand]
    private async Task SelectBackground()
    {
        var topLevel = GetMainWindow();
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择背景图片",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] }
            ]
        });

        if (files.Count > 0)
        {
            _themeService.BackgroundImagePath = files[0].Path.LocalPath;
            UpdateBackgroundPreview(_themeService.BackgroundImagePath);
        }
    }

    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            return lifetime.MainWindow;
        }

        return null;
    }

    [RelayCommand]
    private void ClearBackground()
    {
        _themeService.BackgroundImagePath = null;
        BackgroundPreview = null;
        HasBackground = false;
    }

    // 提供商管理
    [RelayCommand]
    private void AddProvider()
    {
        if (string.IsNullOrWhiteSpace(NewProviderName)) return;

        var name = NewProviderName.Trim();
        var id = name.ToLowerInvariant().Replace(" ", "-");
        var kind = NewProviderKindIndex == 0 ? ProviderKind.OpenAi : ProviderKind.Anthropic;
        var defaultModel = NewProviderModelSourceIndex == 0
            ? NewProviderCustomModel.Trim()
            : (NewProviderFetchedModels.FirstOrDefault() ?? string.Empty);
        var provider = new ProviderConfig
        {
            Id = id,
            Name = name,
            Kind = kind,
            BaseUrl = NewProviderBaseUrl.Trim(),
            ApiKey = NewProviderApiKey.Trim(),
            DefaultModel = defaultModel
        };

        if (provider.EnabledModels.Count == 0 && !string.IsNullOrWhiteSpace(provider.DefaultModel))
        {
            provider.EnabledModels.Add(provider.DefaultModel);
        }

        LlmSettings.Providers.Add(provider);
        ProviderItems.Add(provider);
        ProviderSettingsService.Save(LlmSettings);
        AppShell.Instance.NotifyDataChanged();
        SelectedProvider = provider;

        NewProviderName = string.Empty;
        NewProviderBaseUrl = string.Empty;
        NewProviderApiKey = string.Empty;
        NewProviderCustomModel = string.Empty;
        NewProviderModelSourceIndex = 0;
        NewProviderFetchedModels = [];
        NewProviderModelError = string.Empty;
        NewProviderKindIndex = 0;
    }

    [RelayCommand]
    private void RemoveProvider(ProviderConfig provider)
    {
        if (string.Equals(LlmSettings.ActiveProviderId, provider.Id, StringComparison.OrdinalIgnoreCase))
        {
            // 删除的是默认 Provider: 清空标记, 避免指向不存在的 Provider
            LlmSettings.ActiveProviderId = string.Empty;
            LlmSettings.ActiveModel = null;
        }

        LlmSettings.Providers.Remove(provider);
        ProviderItems.Remove(provider);
        SelectedProvider = null;
        ProviderSettingsService.Save(LlmSettings);
        AppShell.Instance.NotifyDataChanged();
    }

    // 模型管理
    partial void OnNewProviderModelSourceIndexChanged(int value)
    {
        NewProviderCustomModel = string.Empty;
        NewProviderFetchedModels = [];
        NewProviderModelError = string.Empty;
        if (value == 1)
        {
            FetchNewProviderModelsCommand.Execute(null);
        }
    }

    [RelayCommand]
    private async Task FetchNewProviderModels()
    {
        if (string.IsNullOrWhiteSpace(NewProviderBaseUrl) || string.IsNullOrWhiteSpace(NewProviderApiKey))
        {
            NewProviderModelError = "请先填写基础 URL 和 API Key";
            return;
        }

        IsFetchingNewProviderModels = true;
        NewProviderModelError = string.Empty;
        try
        {
            var kind = NewProviderKindIndex == 0 ? ProviderKind.OpenAi : ProviderKind.Anthropic;
            var tempProvider = new ProviderConfig
            {
                Kind = kind,
                BaseUrl = NewProviderBaseUrl.Trim(),
                ApiKey = NewProviderApiKey.Trim()
            };
            NewProviderFetchedModels = (await ModelListService.FetchModelsAsync(tempProvider)).ToList();
            if (NewProviderFetchedModels.Count == 0)
            {
                NewProviderModelError = "未获取到任何模型";
            }
        }
        catch (Exception ex)
        {
            NewProviderModelError = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsFetchingNewProviderModels = false;
        }
    }

    [RelayCommand]
    private async Task FetchModels()
    {
        if (SelectedProvider is null || IsFetchingModels) return;

        IsFetchingModels = true;
        ModelFetchError = string.Empty;
        try
        {
            var models = await ModelListService.FetchModelsAsync(SelectedProvider);
            SelectedProvider.EnabledModels = models.ToList();

            // 通过 API 拉取模型列表时, 对尚未配置过的模型自动探测其最大思考等级
            foreach (var m in SelectedProvider.EnabledModels)
            {
                if (!SelectedProvider.ModelMaxThinking.ContainsKey(m))
                {
                    SelectedProvider.ModelMaxThinking[m] =
                        ThinkingLevels.ToConfigString(ThinkingLevels.DetectMaxLevel(m));
                }
            }

            if (SelectedProvider.EnabledModels.Count == 0)
            {
                ModelFetchError = "未获取到任何模型";
            }

            if (string.IsNullOrWhiteSpace(SelectedProvider.DefaultModel) ||
                !SelectedProvider.EnabledModels.Contains(SelectedProvider.DefaultModel, StringComparer.OrdinalIgnoreCase))
            {
                SelectedProvider.DefaultModel = SelectedProvider.EnabledModels.FirstOrDefault() ?? SelectedProvider.DefaultModel;
            }

            ProviderSettingsService.Save(LlmSettings);
            AppShell.Instance.NotifyDataChanged();
            OnSelectedProviderChanged(SelectedProvider);
            SyncDefaultModelSelection();
        }
        catch (Exception ex)
        {
            ModelFetchError = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsFetchingModels = false;
        }
    }

    [RelayCommand]
    private void EnableAllModels()
    {
        if (SelectedProvider is null) return;
        foreach (var item in ModelItems.Where(i => !i.IsEnabled))
        {
            item.IsEnabled = true;
        }
    }

    [RelayCommand]
    private void DisableAllModels()
    {
        if (SelectedProvider is null) return;
        foreach (var item in ModelItems.Where(i => i.IsEnabled))
        {
            item.IsEnabled = false;
        }
    }

    /// <summary>自动为选中的 Provider 各模型探测并写入最大思考等级。</summary>
    [RelayCommand]
    private void DetectThinkingLevels()
    {
        if (SelectedProvider is null) return;

        foreach (var m in SelectedProvider.EnabledModels)
        {
            SelectedProvider.ModelMaxThinking[m] =
                ThinkingLevels.ToConfigString(ThinkingLevels.DetectMaxLevel(m));
        }

        ProviderSettingsService.Save(LlmSettings);
        AppShell.Instance.NotifyDataChanged();
        OnSelectedProviderChanged(SelectedProvider); // 重建 ModelItems, 读取新上限
    }

    // Agent 管理
    [RelayCommand]
    private void AddAgent()
    {
        if (string.IsNullOrWhiteSpace(NewAgentName)) return;

        var name = NewAgentName.Trim();
        var id = name.Replace(" ", "-").ToLowerInvariant();

        var agent = new CliAgentDefinition
        {
            Id = id,
            Name = name,
            Executable = string.IsNullOrWhiteSpace(NewAgentExecutable) ? id : NewAgentExecutable.Trim(),
            Args = ParseArgs(NewAgentArgs),
            PlanArgs = ParseArgs(NewAgentPlanArgs),
            DefaultMode = "sync",
            MaxConcurrent = 1,
            RequireApproval = true,
            TimeoutMinutes = 30,
            Description = "GUI 创建的自定义 Agent"
        };

        AgentConfigService.SaveUserAgent(agent);
        UserAgents = AgentConfigService.LoadAll().ToList();
        AppShell.Instance.NotifyDataChanged();
        NewAgentName = string.Empty;
        NewAgentExecutable = string.Empty;
        NewAgentArgs = string.Empty;
        NewAgentPlanArgs = string.Empty;
    }

    /// <summary>把空格分隔的参数模板解析为参数列表(支持 {prompt} 占位符)。</summary>
    private static List<string> ParseArgs(string input)
    {
        var parts = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts.ToList() : [];
    }

    [RelayCommand]
    private void SaveAgentExecutable()
    {
        if (SelectedAgent is null) return;

        SelectedAgent.Executable = string.IsNullOrWhiteSpace(AgentExecutableEdit)
            ? SelectedAgent.Id
            : AgentExecutableEdit.Trim();
        SelectedAgent.Args = ParseArgs(AgentArgsEdit);
        SelectedAgent.PlanArgs = ParseArgs(AgentPlanArgsEdit);

        AgentConfigService.SaveUserAgent(SelectedAgent);
        UserAgents = AgentConfigService.LoadAll().ToList();
        AppShell.Instance.NotifyDataChanged();
    }

    [RelayCommand]
    private void RemoveAgent(CliAgentDefinition agent)
    {
        AgentConfigService.RemoveUserAgent(agent.Id);
        UserAgents = AgentConfigService.LoadAll().ToList();
        SelectedAgent = null;
        AppShell.Instance.NotifyDataChanged();
    }

    public void UpdateBackgroundPreview(string? path)
    {
        if (path is null)
        {
            BackgroundPreview = null;
            HasBackground = false;
            return;
        }

        try
        {
            BackgroundPreview = new Bitmap(path);
            HasBackground = true;
        }
        catch
        {
            BackgroundPreview = null;
            HasBackground = false;
        }
    }
}
