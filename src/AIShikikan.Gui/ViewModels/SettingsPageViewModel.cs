using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Llm;
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

    public ModelItem(LlmSettings settings, ProviderConfig provider, string id, Action onChanged)
    {
        _settings = settings;
        _provider = provider;
        _onChanged = onChanged;
        Id = id;
        _isEnabled = provider.EnabledModels.Contains(id, StringComparer.OrdinalIgnoreCase);
    }

    public string Id { get; }

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
    private string _newAgentArgs = string.Empty;

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
        if (string.Equals(SelectedAgent.Executable, AgentExecutableEdit, StringComparison.Ordinal)) return;

        SelectedAgent.Executable = string.IsNullOrWhiteSpace(AgentExecutableEdit)
            ? SelectedAgent.Id
            : AgentExecutableEdit.Trim();
        SelectedAgent.Args = ParseArgs(AgentArgsEdit);

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
