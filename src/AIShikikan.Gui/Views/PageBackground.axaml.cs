using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
                SwapBackgroundBitmap(null);
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
                SwapBackgroundBitmap(new Bitmap(path));
                BgImage.IsVisible = true;
                return;
            }
            catch
            {
            }
        }

        SwapBackgroundBitmap(null);
        BgImage.IsVisible = false;
    }

    /// <summary>换绑背景位图并归还旧实例。
    /// 背景图整图解码(4K 一张几十 MB), 且三个页面各有一份 PageBackground 实例,
    /// 每次换背景不释放旧位图就是三倍原生内存泄漏。
    /// </summary>
    /// <remarks>
    /// 只释放自己放上去的 <see cref="Bitmap"/>: Source 理论上可能被外部赋成别的 IImage,
    /// 那些实例的所有权不在本类。延后一帧再释放, 避免当前合成帧仍引用旧位图。
    /// </remarks>
    private void SwapBackgroundBitmap(Bitmap? next)
    {
        var old = BgImage.Source;
        if (ReferenceEquals(old, next)) return;

        BgImage.Source = next;
        if (old is not Bitmap oldBitmap) return;

        Dispatcher.UIThread.Post(oldBitmap.Dispose, DispatcherPriority.Background);
    }
}
