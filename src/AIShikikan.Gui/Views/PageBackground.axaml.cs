using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using AIShikikan.Core.Services;

namespace AIShikikan.Gui.Views;

/// <summary>页面背景层: 渲染背景图 + 半透明遮罩, 置于页面内容之下。</summary>
public partial class PageBackground : UserControl
{
    public static readonly StyledProperty<ThemeService?> ThemeServiceProperty =
        AvaloniaProperty.Register<PageBackground, ThemeService?>(nameof(ThemeService));

    public static readonly StyledProperty<double> ScrimOpacityProperty =
        AvaloniaProperty.Register<PageBackground, double>(nameof(ScrimOpacity), 0.6);

    private ThemeService? _themeService;

    public ThemeService? ThemeService
    {
        get => GetValue(ThemeServiceProperty);
        set => SetValue(ThemeServiceProperty, value);
    }

    public double ScrimOpacity
    {
        get => GetValue(ScrimOpacityProperty);
        set => SetValue(ScrimOpacityProperty, value);
    }

    public PageBackground()
    {
        InitializeComponent();
        Scrim.Opacity = ScrimOpacity;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ThemeServiceProperty)
        {
            if (_themeService is not null)
            {
                _themeService.BackgroundChanged -= OnBackgroundChanged;
            }

            _themeService = change.GetNewValue<ThemeService?>();
            if (_themeService is not null)
            {
                _themeService.BackgroundChanged += OnBackgroundChanged;
                ReloadBackground(_themeService.BackgroundImagePath);
            }
            else
            {
                BgImage.Source = null;
                BgImage.IsVisible = false;
            }
        }
        else if (change.Property == ScrimOpacityProperty)
        {
            Scrim.Opacity = change.GetNewValue<double>();
        }
    }

    private void OnBackgroundChanged(object? sender, string? path)
    {
        if (sender is ThemeService themeService)
        {
            ReloadBackground(themeService.BackgroundImagePath);
        }
    }

    private void ReloadBackground(string? path)
    {
        if (path is not null && File.Exists(path))
        {
            try
            {
                BgImage.Source = new Bitmap(path);
                BgImage.IsVisible = true;
                return;
            }
            catch
            {
            }
        }

        BgImage.Source = null;
        BgImage.IsVisible = false;
    }
}
