using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using AgentCommander.Core;
using AgentCommander.Core.Services;
using AgentCommander.Gui.ViewModels;
using AgentCommander.Gui.Views;

namespace AgentCommander.Gui;

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

        RequestedThemeVariant = ThemeService.IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        I18nService.SetLanguage(ThemeService.Language);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(ThemeService),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
