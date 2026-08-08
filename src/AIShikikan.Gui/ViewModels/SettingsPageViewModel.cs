using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Llm;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIShikikan.Gui.ViewModels;

public partial class SettingsPageViewModel : ViewModelBase, INotifyPropertyChanged
{
    private readonly ThemeService _themeService;

    public string AppName { get; } = AppInfo.Name;
    public string AppVersion { get; } = AppInfo.Version;
    public string FontInfo { get; } = "HarmonyOS Sans SC";

    public IReadOnlyList<string> LanguageOptions { get; } = ["简体中文", "English"];

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

    [ObservableProperty]
    private bool _appearanceCardExpanded = true;

    [ObservableProperty]
    private bool _providersCardExpanded = true;

    [ObservableProperty]
    private bool _agentsCardExpanded = false;

    [ObservableProperty]
    private bool _aboutCardExpanded = true;

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
        _llmSettings = ProviderSettingsService.Load();
        _userAgents = AgentConfigService.LoadUserFile().Agents.ToList();

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
        IsRestartHintVisible = value != _currentLanguageIndex;
    }

    [RelayCommand]
    private void ToggleAppearanceCard() => AppearanceCardExpanded = !AppearanceCardExpanded;

    [RelayCommand]
    private void ToggleProvidersCard() => ProvidersCardExpanded = !ProvidersCardExpanded;

    [RelayCommand]
    private void ToggleAgentsCard() => AgentsCardExpanded = !AgentsCardExpanded;

    [RelayCommand]
    private void ToggleAboutCard() => AboutCardExpanded = !AboutCardExpanded;

    [RelayCommand]
    private void SelectBackground()
    {
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
        if (string.IsNullOrWhiteSpace(NewProviderId) || string.IsNullOrWhiteSpace(NewProviderName)) return;

        var kind = NewProviderKindIndex == 0 ? ProviderKind.OpenAi : ProviderKind.Anthropic;
        var provider = new ProviderConfig
        {
            Id = NewProviderId.Trim(),
            Name = NewProviderName.Trim(),
            Kind = kind,
            BaseUrl = NewProviderBaseUrl.Trim(),
            ApiKey = NewProviderApiKey.Trim(),
            DefaultModel = NewProviderDefaultModel.Trim()
        };

        _llmSettings.Providers.Add(provider);
        ProviderSettingsService.Save(_llmSettings);

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
        _llmSettings.Providers.Remove(provider);
        ProviderSettingsService.Save(_llmSettings);
    }

    [RelayCommand]
    private void SetActiveProvider(ProviderConfig provider)
    {
        _llmSettings.ActiveProviderId = provider.Id;
        ProviderSettingsService.Save(_llmSettings);
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
        _userAgents = AgentConfigService.LoadUserFile().Agents.ToList();
        NewAgentName = string.Empty;
        NewAgentExecutable = string.Empty;
    }

    [RelayCommand]
    private void RemoveAgent(CliAgentDefinition agent)
    {
        AgentConfigService.RemoveUserAgent(agent.Id);
        _userAgents = AgentConfigService.LoadUserFile().Agents.ToList();
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

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
