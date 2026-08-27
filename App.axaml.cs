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

        // 恢复 Compact Subagent 开关(子Agent输出 LLM 压缩)
        Core.Services.Engine.SubagentCompactService.Restore(ThemeService);

        RequestedThemeVariant = ThemeService.IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        I18nService.SetLanguage(ThemeService.Language);
        AIShikikan.Gui.Resources.Strings.Culture = I18nService.CurrentCulture;

        ThemeService.FontChanged += (_, font) => ApplyCustomFont(font);
        ThemeService.MonoFontChanged += (_, font) => ApplyMonoFont(font);
        ApplyCustomFont(ThemeService.CustomFont);
        ApplyMonoFont(ThemeService.MonoFont);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(ThemeService),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>应用自定义字体(标准字体): 以逗号分隔的 fallback 列表, 保证中文字形回退到 HarmonyOS Sans SC。</summary>
    private static void ApplyCustomFont(string? font)
    {
        if (Application.Current is not { } app) return;

        var family = string.IsNullOrWhiteSpace(font) || font == "系统默认"
            ? "avares://AIShikikan.Gui/Assets/Fonts/#HarmonyOS Sans SC"
            : $"{font}, HarmonyOS Sans SC";

        app.Resources["ContentControlThemeFontFamily"] = new FontFamily(family);
    }

    /// <summary>应用自定义等宽字体: 默认使用内置 CaskaydiaCove Nerd Font Mono, 中文字形回退 HarmonyOS Sans SC。</summary>
    private static void ApplyMonoFont(string? font)
    {
        if (Application.Current is not { } app) return;

        var family = string.IsNullOrWhiteSpace(font) || font == "系统默认"
            ? "avares://AIShikikan.Gui/Assets/Fonts/#CaskaydiaCove Nerd Font Mono"
            : $"{font}, HarmonyOS Sans SC, monospace";

        app.Resources["MonoThemeFontFamily"] = new FontFamily(family);
    }
}
