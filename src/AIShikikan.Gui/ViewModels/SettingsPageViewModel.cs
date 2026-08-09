using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Llm;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIShikikan.Gui.ViewModels;

public partial class SettingsPageViewModel : ViewModelBase
{
    private readonly ThemeService _themeService;

    public string AppName { get; } = AppInfo.Name;
    public string AppVersion { get; } = AppInfo.Version;
    public string FontInfo { get; } = "HarmonyOS Sans SC";

    public IReadOnlyList<string> LanguageOptions { get; } = ["简体中文", "English"];

    public IReadOnlyList<string> ProviderKindOptions { get; } = ["OpenAI 兼容", "Anthropic"];

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

    [ObservableProperty]
    private ProviderConfig? _selectedProvider;

    [ObservableProperty]
    private string _newProviderId = string.Empty;

    [ObservableProperty]
    private string _newProviderName = string.Empty;

    [ObservableProperty]
    private string _newProviderBaseUrl = string.Empty;

    [ObservableProperty]
    private string _newProviderApiKey = string.Empty;

    [ObservableProperty]
    private string _newProviderDefaultModel = string.Empty;

    [ObservableProperty]
    private int _newProviderKindIndex;

    // Agent 管理
    [ObservableProperty]
    private IReadOnlyList<CliAgentDefinition> _userAgents;

    [ObservableProperty]
    private string _newAgentName = string.Empty;

    [ObservableProperty]
    private string _newAgentExecutable = string.Empty;

    private readonly int _currentLanguageIndex;

    public SettingsPageViewModel(ThemeService themeService)
    {
        _themeService = themeService;
        _isDarkTheme = themeService.IsDarkTheme;
        _currentLanguageIndex = themeService.Language == "zh-CN" ? 0 : 1;
        _languageIndex = _currentLanguageIndex;
        LlmSettings = ProviderSettingsService.Load();
        UserAgents = AgentConfigService.LoadUserFile().Agents.ToList();

        _themeService.ThemeChanged += (_, isDark) => IsDarkTheme = isDark;
    }
    partial void OnIsDarkThemeChanged(bool value)
    {
        _themeService.IsDarkTheme = value;
        if (Avalonia.Application.Current is { } app)
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

    [RelayCommand]
    private async Task SelectBackground()
    {
        var topLevel = TopLevel.GetTopLevel(null);
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

        var id = string.IsNullOrWhiteSpace(NewProviderId)
            ? NewProviderName.Trim().ToLowerInvariant().Replace(" ", "-")
            : NewProviderId.Trim();
        var kind = NewProviderKindIndex == 0 ? ProviderKind.OpenAi : ProviderKind.Anthropic;
        var provider = new ProviderConfig
        {
            Id = id,
            Name = NewProviderName.Trim(),
            Kind = kind,
            BaseUrl = NewProviderBaseUrl.Trim(),
            ApiKey = NewProviderApiKey.Trim(),
            DefaultModel = NewProviderDefaultModel.Trim()
        };

        LlmSettings.Providers.Add(provider);
        ProviderSettingsService.Save(LlmSettings);

        NewProviderId = string.Empty;
        NewProviderName = string.Empty;
        NewProviderBaseUrl = string.Empty;
        NewProviderApiKey = string.Empty;
        NewProviderDefaultModel = string.Empty;
        NewProviderKindIndex = 0;
    }

    [RelayCommand]
    private void RemoveProvider(ProviderConfig provider)
    {
        LlmSettings.Providers.Remove(provider);
        ProviderSettingsService.Save(LlmSettings);
    }

    [RelayCommand]
    private void SetActiveProvider(ProviderConfig provider)
    {
        LlmSettings.ActiveProviderId = provider.Id;
        ProviderSettingsService.Save(LlmSettings);
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
            DefaultMode = "sync",
            MaxConcurrent = 1,
            RequireApproval = true,
            TimeoutMinutes = 30,
            Description = "GUI 创建的自定义 Agent"
        };

        AgentConfigService.SaveUserAgent(agent);
        UserAgents = AgentConfigService.LoadUserFile().Agents.ToList();
        NewAgentName = string.Empty;
        NewAgentExecutable = string.Empty;
    }

    [RelayCommand]
    private void RemoveAgent(CliAgentDefinition agent)
    {
        AgentConfigService.RemoveUserAgent(agent.Id);
        UserAgents = AgentConfigService.LoadUserFile().Agents.ToList();
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