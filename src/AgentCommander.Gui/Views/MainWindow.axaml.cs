using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using AgentCommander.Gui.ViewModels;

namespace AgentCommander.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is ListBox listBox)
        {
            vm.SelectedIndex = listBox.SelectedIndex;
        }
    }

    private void OnThemeToggleClick(object? sender, RoutedEventArgs e)
    {
        var goingLight = Application.Current?.RequestedThemeVariant != ThemeVariant.Light;
        SetTheme(goingLight ? ThemeVariant.Light : ThemeVariant.Dark);
    }

    private void OnToggleRail(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.IsRail = !vm.IsRail;
            Shell.DrawerLength = vm.IsRail ? 80 : 360;
            CollapseChevron.RenderTransform = new RotateTransform(vm.IsRail ? 180 : 0);
            RailToggleBtn.HorizontalAlignment = vm.IsRail
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Right;
        }
    }

    private void SetTheme(ThemeVariant variant)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = variant;
        ThemeIcon.Data = (Geometry)this.FindResource(variant == ThemeVariant.Light ? "IconSun" : "IconMoon")!;
    }
}
