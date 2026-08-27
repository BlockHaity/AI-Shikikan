using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AIShikikan.Gui.Resources;
using AIShikikan.Gui.Services;
using AIShikikan.Gui.ViewModels;
using ScottPlot;

namespace AIShikikan.Gui.Views;

public partial class HomePageView : UserControl
{
    private HomePageViewModel? _vm;
    private bool _interactionDisabled;

    /// <summary>折线图日期对应的数值轴(与 ChartDates 对齐, 悬浮拾取用)。</summary>
    private double[] _chartDateNumbers = [];

    // 显式设定的坐标范围(与 SetLimits 一致, 用于悬浮拾取的线性映射)
    private double _xMin;
    private double _xMax;
    private double _yMax;

    public HomePageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DynamicThemeService.PaletteApplied += OnPaletteChanged;
        DetachedFromVisualTree += (_, _) => DynamicThemeService.PaletteApplied -= OnPaletteChanged;
    }

    private void OnPaletteChanged(object? sender, EventArgs e) => RenderChart();

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.ThemeService.ThemeChanged -= OnThemeChanged;
        }

        _vm = DataContext as HomePageViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.ThemeService.ThemeChanged += OnThemeChanged;
            RenderChart();
        }
    }

    private void OnThemeChanged(object? sender, bool isDark) => RenderChart();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HomePageViewModel.ChartDates)
            or nameof(HomePageViewModel.SelectedRangeIndex))
        {
            RenderChart();
        }
    }

    /// <summary>从应用主题资源解析颜色, 回退 Material 默认值。</summary>
    private static ScottPlot.Color ResolveColor(string key, string fallbackHex)
    {
        if (Application.Current is { } app &&
            app.TryGetResource(key, app.RequestedThemeVariant, out var value) &&
            value is ISolidColorBrush brush)
        {
            var c = brush.Color;
            return new ScottPlot.Color($"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}");
        }

        return new ScottPlot.Color(fallbackHex);
    }

    /// <summary>
    /// 按当前数据重建用量趋势折线图(输入/输出/缓存命中):
    /// 带标尺刻度的固定坐标轴、数据端点标记, 禁用拖拽缩放, 配色跟随应用主题。
    /// </summary>
    private void RenderChart()
    {
        if (_vm is null) return;

        var plot = UsageChart.Plot;
        plot.Clear();

        // 交互在首次渲染时整体禁用: 防止拖拽平移/滚轮缩放让折线"随便移动"
        if (!_interactionDisabled)
        {
            UsageChart.UserInputProcessor.Disable();
            _interactionDisabled = true;
        }

        var axis = ResolveColor("OnSurfaceVariant", "#CAC4D0");
        var grid = ResolveColor("OutlineVariant", "#49454F");

        // 背景透明融入卡片
        plot.FigureBackground.Color = new ScottPlot.Color("#00000000");
        plot.DataBackground.Color = new ScottPlot.Color("#00000000");

        plot.Axes.DateTimeTicksBottom();
        plot.Axes.Color(axis);
        plot.Grid.MajorLineColor = grid;
        plot.Grid.MinorLineWidth = 0;

        // 标尺刻度: 主刻度长线 + 次刻度短线, 仅保留左/下坐标轴
        ApplyRulerStyle(plot.Axes.Bottom, axis);
        ApplyRulerStyle(plot.Axes.Left, axis);

        // Y 轴大数简化为 K/M 缩写
        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = FormatTokensAxis
        };

        plot.Axes.Right.IsVisible = false;
        plot.Axes.Top.IsVisible = false;

        var dates = _vm.ChartDates;
        _chartDateNumbers = Array.ConvertAll(dates, d => NumericConversion.ToNumber(d));
        if (dates.Length > 0)
        {
            var series = new[]
            {
                (Data: _vm.ChartInputTokens, Name: Strings.Home_UsageInput,
                 Key: "Primary", Fallback: "#6750A4"),
                (Data: _vm.ChartOutputTokens, Name: Strings.Home_UsageOutput,
                 Key: "Secondary", Fallback: "#625B71"),
                (Data: _vm.ChartCachedTokens, Name: Strings.Home_UsageCacheHit,
                 Key: "Tertiary", Fallback: "#7D5260")
            };

            foreach (var (data, name, key, fallback) in series)
            {
                var scatter = plot.Add.Scatter(dates, data);
                scatter.LegendText = name;
                scatter.Color = ResolveColor(key, fallback);
                scatter.LineWidth = 2;
                // 数据点端点标记
                scatter.MarkerSize = 4;
                scatter.MarkerShape = MarkerShape.FilledCircle;
            }

            // 图例由 Avalonia 自绘(见 XAML), 不使用 SP 白底图例;
            // 显式设定坐标范围供悬浮拾取做线性映射
            var maxVal = 0.0d;
            foreach (var v in _vm.ChartInputTokens) maxVal = Math.Max(maxVal, v);
            foreach (var v in _vm.ChartOutputTokens) maxVal = Math.Max(maxVal, v);
            foreach (var v in _vm.ChartCachedTokens) maxVal = Math.Max(maxVal, v);

            var spanDays = dates.Length > 1 ? _chartDateNumbers[^1] - _chartDateNumbers[0] : 1;
            _xMin = _chartDateNumbers[0] - Math.Max(spanDays * 0.03, 0.5);
            _xMax = _chartDateNumbers[^1] + Math.Max(spanDays * 0.05, 0.5);
            _yMax = Math.Max(maxVal * 1.15, 10);

            plot.Axes.SetLimits(_xMin, _xMax, 0, _yMax);
        }

        HideChartTip();
        UsageChart.Refresh();
    }

    private static string FormatTokensAxis(double v)
    {
        if (Math.Abs(v) >= 1e9) return $"{v / 1e9:0.#}G";
        if (Math.Abs(v) >= 1e6) return $"{v / 1e6:0.#}M";
        if (Math.Abs(v) >= 1e3) return $"{v / 1e3:0.#}K";
        return v.ToString("0.#");
    }

    /// <summary>标尺样式: 主刻度长线 + 次刻度短线。</summary>
    private static void ApplyRulerStyle(ScottPlot.IAxis axisEdge, ScottPlot.Color tickColor)
    {
        axisEdge.MajorTickStyle.Length = 6;
        axisEdge.MajorTickStyle.Width = 1.3f;
        axisEdge.MajorTickStyle.Color = tickColor;
        axisEdge.MinorTickStyle.Length = 3;
        axisEdge.MinorTickStyle.Width = 1f;
        axisEdge.MinorTickStyle.Color = tickColor;
    }

    /// <summary>拾取半径(像素)。</summary>
    private const double PickRadiusPx = 16;

    /// <summary>悬浮时查找最近的折点并显示详情气泡; 远离数据则隐藏。</summary>
    private void OnChartPointerMoved(object? sender, PointerEventArgs e)
    {
        var dates = _vm?.ChartDates;
        var stats = _vm?.ChartStats;
        if (_vm is null || dates is null || stats is null || dates.Length == 0 ||
            dates.Length != stats.Count)
        {
            return;
        }

        var pos = e.GetPosition(UsageChart);
        float mx = (float)pos.X;
        float my = (float)pos.Y;

        // 数据区矩形来自最近一次渲染, 坐标↔像素自行线性映射
        var rect = UsageChart.Plot.RenderManager.LastRender.DataRect;
        var bottomEdge = Math.Max(rect.Top, rect.Bottom);
        var dataW = Math.Max(rect.Right - rect.Left, 1);
        var dataH = Math.Max(rect.Height, 1);
        double X(double num) => rect.Left + (num - _xMin) / (_xMax - _xMin) * dataW;
        double Y(double val) => bottomEdge - val / _yMax * dataH;

        double bestDist = PickRadiusPx * PickRadiusPx;
        int bestIndex = -1;

        // 三条序列共用日期 X 轴, 找出所有折点里离鼠标最近的一个
        var allSeries = new[] { _vm.ChartInputTokens, _vm.ChartOutputTokens, _vm.ChartCachedTokens };
        for (var i = 0; i < dates.Length; i++)
        {
            var px = X(_chartDateNumbers[i]);
            foreach (var data in allSeries)
            {
                if (i >= data.Length) continue;

                var py = Y(data[i]);
                double dx = px - mx;
                double dy = py - my;
                var dist = dx * dx + dy * dy;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIndex = i;
                }
            }
        }

        if (bestIndex < 0)
        {
            HideChartTip();
            return;
        }

        ShowChartTip(dates[bestIndex], stats[bestIndex], pos);
    }

    private void OnChartPointerExited(object? sender, PointerEventArgs e) => HideChartTip();

    /// <summary>在折点旁展示详情: 日期、输入/输出、命中/未命中拆分与合计。</summary>
    private void ShowChartTip(DateTime date, AIShikikan.Core.Services.Usage.DailyUsageStat stat, Point pointerPos)
    {
        var input = stat.InputTokens;
        var output = stat.OutputTokens;
        var cached = Math.Min(stat.CachedTokens, input); // 防御: 命中不应超过输入
        ChartTipText.Text =
            $"{date:yyyy/M/d}\n" +
            $"{Strings.Home_TipInput}: {input:N0} ({Strings.Home_TipCached} {cached:N0} · {Strings.Home_TipMissed} {Math.Max(input - cached, 0):N0})\n" +
            $"{Strings.Home_TipOutput}: {output:N0}\n" +
            $"{Strings.Home_TipTotal}: {(long)(input + output):N0}";

        // 气泡配色随主题(InverseSurface 对比色, 缺资源时按明暗回退)
        var dark = _vm?.ThemeService.IsDarkTheme ?? true;
        ChartTip.Background = HeatmapBrush.ResolveResource(
            "InverseSurface", dark ? "#E6E1E5" : "#313033");
        ChartTipText.Foreground = HeatmapBrush.ResolveResource(
            "InverseOnSurface", dark ? "#313033" : "#F4EFF4");

        // 量取尺寸后用 Canvas 附加属性定位(不参与布局测量, 不撑大卡片):
        // 默认出现在指针右上, 越界时翻转/夹紧
        ChartTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = ChartTip.DesiredSize.Width;
        var h = ChartTip.DesiredSize.Height;
        var boundsW = UsageChart.Bounds.Width;
        var boundsH = UsageChart.Bounds.Height;

        var x = pointerPos.X + 14;
        if (x + w > boundsW - 4)
        {
            x = Math.Max(pointerPos.X - w - 14, 4);
        }

        var y = Math.Max(pointerPos.Y - h - 14, 4);
        if (y + h > boundsH - 4)
        {
            y = Math.Max(boundsH - h - 8, 4);
        }

        Canvas.SetLeft(ChartTip, x);
        Canvas.SetTop(ChartTip, y);
        ChartTip.SetValue(Panel.ZIndexProperty, 10);
        ChartTip.IsVisible = true;
    }

    private void HideChartTip() => ChartTip.IsVisible = false;
}
