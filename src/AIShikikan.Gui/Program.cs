using Avalonia;
using System;

namespace AIShikikan.Gui;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { IconUri = "avares://AIShikikan.Gui/Assets/logo.jpg" })
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}
