using System.Collections.Generic;
using AgentCommander.Core;
using AgentCommander.Core.Services;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentCommander.Gui.ViewModels;

public partial class SettingsPageViewModel : ViewModelBase
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

    public SettingsPageViewModel(ThemeService themeService)
    {
        _themeService = themeService;
        _isDarkTheme = themeService.IsDarkTheme;
        _languageIndex = themeService.Language == "zh-CN" ? 0 : 1;

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
    }

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
