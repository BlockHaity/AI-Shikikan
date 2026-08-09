using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using AIShikikan.Gui.ViewModels;
using AIShikikan.Gui.Views;

namespace AIShikikan.Gui;

public partial class App : Application
{
    public static ThemeService ThemeService { get; private set; } = null!;
    public static I18nService I18nService { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppPaths.EnsureDirectoriesExist();

        ThemeService = new ThemeService();
        I18nService = new I18nService();
        I18nService.LanguageChanged += (_, culture) => AIShikikan.Gui.Resources.Strings.Culture = culture;

        RequestedThemeVariant = ThemeService.IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        I18nService.SetLanguage(ThemeService.Language);
        AIShikikan.Gui.Resources.Strings.Culture = I18nService.CurrentCulture;

        ThemeService.FontChanged += (_, font) => ApplyCustomFont(font);
        ApplyCustomFont(ThemeService.CustomFont);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(ThemeService),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>应用自定义字体: 以逗号分隔的 fallback 列表, 保证中文字形回退到 HarmonyOS Sans SC。</summary>
    private static void ApplyCustomFont(string? font)
    {
        if (Application.Current is not { } app) return;

        var family = string.IsNullOrWhiteSpace(font) || font == "系统默认"
            ? "avares://AIShikikan.Gui/Assets/Fonts/#HarmonyOS Sans SC"
            : $"{font}, HarmonyOS Sans SC";

        app.Resources["ContentControlThemeFontFamily"] = new FontFamily(family);
    }
}
