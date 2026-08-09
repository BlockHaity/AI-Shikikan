using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using AIShikikan.Core.Services;
using AIShikikan.Gui.Services;
using AIShikikan.Gui.ViewModels;

namespace AIShikikan.Gui.Views;

public partial class MainWindow : Window
{
    private DynamicThemeService? _dynamicThemeService;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainWindowViewModel vm)
        {
            _dynamicThemeService = new DynamicThemeService();
            LoadBackgroundAndApplyPalette(vm.ThemeService);
            vm.ThemeService.BackgroundChanged += OnBackgroundChanged;
        }
    }

    private void OnBackgroundChanged(object? sender, string? path)
    {
        LoadBackgroundAndApplyPalette(sender as ThemeService);
    }

    private void LoadBackgroundAndApplyPalette(ThemeService? themeService)
    {
        if (themeService is null) return;

        var path = themeService.BackgroundImagePath;
        if (path is not null && File.Exists(path))
        {
            ApplyDynamicPalette(path);
        }
        else
        {
            _dynamicThemeService?.ResetToDefault();
        }
    }

    private void ApplyDynamicPalette(string imagePath)
    {
        try
        {
            using var bitmap = new Bitmap(imagePath);
            var pixelSize = bitmap.PixelSize;
            var width = pixelSize.Width;
            var height = pixelSize.Height;

            var stride = width * 4;
            var bufferSize = height * stride;
            var pixels = new byte[bufferSize];
            var bufferPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(bufferSize);

            try
            {
                bitmap.CopyPixels(new PixelRect(0, 0, width, height), bufferPtr, bufferSize, stride);
                System.Runtime.InteropServices.Marshal.Copy(bufferPtr, pixels, 0, bufferSize);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(bufferPtr);
            }

            var uintPixels = new uint[width * height];
            for (var i = 0; i < uintPixels.Length; i++)
            {
                var offset = i * 4;
                var b = pixels[offset];
                var g = pixels[offset + 1];
                var r = pixels[offset + 2];
                var a = pixels[offset + 3];
                uintPixels[i] = (uint)(a << 24 | b << 16 | g << 8 | r);
            }

            var colorService = new ColorExtractionService();
            var palette = colorService.ExtractFromPixels(uintPixels, width, height);
            _dynamicThemeService?.ApplyPalette(palette);
        }
        catch
        {
            _dynamicThemeService?.ResetToDefault();
        }
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
        if (DataContext is not MainWindowViewModel vm) return;
        var goingLight = Application.Current?.RequestedThemeVariant != ThemeVariant.Light;
        var variant = goingLight ? ThemeVariant.Light : ThemeVariant.Dark;
        SetTheme(variant);
        vm.ThemeService.IsDarkTheme = !goingLight;
    }

    private void OnToggleRail(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.IsRail = !vm.IsRail;
            Shell.DrawerLength = vm.IsRail ? 80 : 220;
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
