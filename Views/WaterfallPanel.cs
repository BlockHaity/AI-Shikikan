using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace AIShikikan.Gui.Views;

/// <summary>
/// 自适应瀑布流面板: 根据可用宽度决定列数, 子元素按顺序放入当前最矮的列,
/// 适合高度不一的设置卡片布局。
/// </summary>
public class WaterfallPanel : Panel
{
    /// <summary>单列最小宽度(窗口足够宽时列数自动增加)。</summary>
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<WaterfallPanel, double>(nameof(MinColumnWidth), 430d);

    /// <summary>列间距。</summary>
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<WaterfallPanel, double>(nameof(ColumnSpacing), 16d);

    /// <summary>同行内的垂直间距。</summary>
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<WaterfallPanel, double>(nameof(RowSpacing), 12d);

    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>测量阶段记录的子元素→列分配(Arrange 复用)。</summary>
    private readonly List<int> _columnOfChild = [];

    private double _columnWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = availableSize.Width;
        var hasFiniteWidth = !double.IsInfinity(width) && !double.IsNaN(width) && width > 0;

        ComputeColumns(hasFiniteWidth ? width : MinColumnWidth,
            out var columns, out _columnWidth);

        var columnHeights = new double[columns];
        _columnOfChild.Clear();

        foreach (var child in Children)
        {
            child.Measure(new Size(_columnWidth, double.PositiveInfinity));
            var h = child.DesiredSize.Height;

            // 放入当前最矮的列
            var target = 0;
            for (var i = 1; i < columns; i++)
            {
                if (columnHeights[i] < columnHeights[target])
                {
                    target = i;
                }
            }

            _columnOfChild.Add(target);
            columnHeights[target] += h +
                (_columnOfChild.Count > 1 ? RowSpacing : 0);
        }

        var desiredHeight = 0d;
        foreach (var ch in columnHeights)
        {
            desiredHeight = Math.Max(desiredHeight, ch);
        }

        return new Size(
            hasFiniteWidth ? width : _columnWidth * columns + ColumnSpacing * (columns - 1),
            desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ComputeColumns(finalSize.Width, out var columns, out var columnWidth);

        // 子元素数量与测量时不一致时退化为逐列轮转(防御异常布局时序)
        if (_columnOfChild.Count != Children.Count)
        {
            _columnOfChild.Clear();
            for (var i = 0; i < Children.Count; i++)
            {
                _columnOfChild.Add(i % columns);
            }
        }

        var columnY = new double[columns];
        for (var i = 0; i < Children.Count; i++)
        {
            var col = _columnOfChild[i];
            if (col >= columns)
            {
                col %= columns;
            }

            var child = Children[i];
            child.Arrange(new Rect(
                col * (columnWidth + ColumnSpacing),
                columnY[col],
                columnWidth,
                child.DesiredSize.Height));
            columnY[col] += child.DesiredSize.Height + RowSpacing;
        }

        return finalSize;
    }

    /// <summary>由可用宽度推导列数与列宽: 至少一列, 列宽不小于单列最小宽度的一半下限。</summary>
    private void ComputeColumns(double availableWidth, out int columns, out double columnWidth)
    {
        var spacing = Math.Max(0, ColumnSpacing);
        var minW = Math.Max(120, MinColumnWidth);

        columns = Math.Max(1, (int)((availableWidth + spacing) / (minW + spacing)));
        columnWidth = (availableWidth - spacing * (columns - 1)) / columns;

        if (columnWidth <= 0)
        {
            columns = 1;
            columnWidth = Math.Max(availableWidth, minW);
        }
        else
        {
            columnWidth = Math.Floor(columnWidth);
        }
    }
}
