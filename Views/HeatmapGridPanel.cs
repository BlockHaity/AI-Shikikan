using System;
using Avalonia;
using Avalonia.Controls;

namespace AIShikikan.Gui.Views;

/// <summary>
/// 活跃热力图网格面板: 固定 7 行(周一→周日)、Columns 列(周)。
/// 前 7 个子元素为星期纵栏标签(占首列), 其余为格子(行优先: 第 r 行 = 各周的第 r 天)。
/// 格子保持正方形并按可用宽度自动放大(不超过 MaxCellSize), 整体水平居中;
/// 空间不足时缩小至 MinCellSize 并撑出横向滚动, 避免卡片右侧留大片空白。
/// </summary>
public class HeatmapGridPanel : Panel
{
    /// <summary>列数(周数)。</summary>
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<HeatmapGridPanel, int>(nameof(Columns), 1);

    /// <summary>格子间距。</summary>
    public static readonly StyledProperty<double> CellSpacingProperty =
        AvaloniaProperty.Register<HeatmapGridPanel, double>(nameof(CellSpacing), 3d);

    /// <summary>格子边长上限(宽裕时拉伸到此值后居中)。</summary>
    public static readonly StyledProperty<double> MaxCellSizeProperty =
        AvaloniaProperty.Register<HeatmapGridPanel, double>(nameof(MaxCellSize), 30d);

    /// <summary>格子边长下限(空间不足时允许缩小到此值)。</summary>
    public static readonly StyledProperty<double> MinCellSizeProperty =
        AvaloniaProperty.Register<HeatmapGridPanel, double>(nameof(MinCellSize), 8d);

    /// <summary>星期纵栏宽度。</summary>
    public static readonly StyledProperty<double> GutterWidthProperty =
        AvaloniaProperty.Register<HeatmapGridPanel, double>(nameof(GutterWidth), 18d);

    private const int RowCount = 7;

    static HeatmapGridPanel()
    {
        AffectsMeasure<HeatmapGridPanel>(ColumnsProperty, CellSpacingProperty,
            MaxCellSizeProperty, MinCellSizeProperty, GutterWidthProperty);
    }

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public double CellSpacing
    {
        get => GetValue(CellSpacingProperty);
        set => SetValue(CellSpacingProperty, value);
    }

    public double MaxCellSize
    {
        get => GetValue(MaxCellSizeProperty);
        set => SetValue(MaxCellSizeProperty, value);
    }

    public double MinCellSize
    {
        get => GetValue(MinCellSizeProperty);
        set => SetValue(MinCellSizeProperty, value);
    }

    public double GutterWidth
    {
        get => GetValue(GutterWidthProperty);
        set => SetValue(GutterWidthProperty, value);
    }

    /// <summary>当前布局使用的格子边长(供外部同步行高)。</summary>
    public double CurrentCellSize { get; private set; }

    /// <summary>格子缩到下限时整网格所需的最小总宽(供外部同步 Width 以启用横向滚动)。</summary>
    public double MinTotalWidth
    {
        get
        {
            var cols = Math.Max(1, Columns);
            var spacing = Math.Max(0, CellSpacing);
            return GutterWidth + spacing + cols * (MinCellSize + spacing) - spacing;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var cols = Math.Max(1, Columns);
        var spacing = Math.Max(0, CellSpacing);
        var minTotal = MinTotalWidth;

        // 外部绑定 Width=ScrollViewer 视口宽并设 MinWidth=MinTotalWidth:
        // 视口宽裕时格子拉伸填满, 紧张时 MinWidth 撑出横向滚动
        var width = availableSize.Width;
        if (double.IsInfinity(width) || double.IsNaN(width) || width <= 0)
        {
            width = Math.Max(0, MinWidth);
        }

        var cell = CellSizeFor(width - GutterWidth - spacing, cols, spacing);
        CurrentCellSize = cell;

        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, cell));
        }

        return new Size(
            Math.Max(width, minTotal),
            RowCount * (cell + spacing) - spacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cols = Math.Max(1, Columns);
        var spacing = Math.Max(0, CellSpacing);
        var cell = CellSizeFor(Math.Max(0, finalSize.Width - GutterWidth - spacing), cols, spacing);
        CurrentCellSize = cell;

        var gridW = cols * (cell + spacing) - spacing;
        var totalW = GutterWidth + spacing + gridW;
        var offsetX = Math.Max(0, (finalSize.Width - totalW) / 2);
        var rowPitch = cell + spacing;

        // 前 7 个子元素: 星期纵栏(首列, 与各行居中对齐)
        for (var r = 0; r < RowCount && r < Children.Count; r++)
        {
            Children[r].Arrange(new Rect(offsetX, r * rowPitch, GutterWidth, cell));
        }

        // 其余子元素: 格子, 行优先
        var cellCount = Children.Count - RowCount;
        for (var i = 0; i < cellCount; i++)
        {
            var row = i / cols;
            var col = i % cols;
            Children[RowCount + i].Arrange(new Rect(
                offsetX + GutterWidth + spacing + col * rowPitch,
                row * rowPitch,
                cell,
                cell));
        }

        return new Size(finalSize.Width, RowCount * rowPitch - spacing);
    }

    /// <summary>由网格可用宽度推导正方形格子边长: 拉伸不超过上限, 收缩不低于下限。</summary>
    private double CellSizeFor(double gridWidth, int cols, double spacing)
    {
        var cell = (gridWidth + spacing) / cols - spacing;
        return Math.Clamp(cell, MinCellSize, MaxCellSize);
    }
}
