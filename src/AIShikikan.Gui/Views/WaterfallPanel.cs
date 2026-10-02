using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace AIShikikan.Gui.Views;

/// <summary>
/// 自适应瀑布流面板: 根据可用宽度决定列数, 子元素按顺序放入当前最矮的列,
/// 适合高度不一的设置卡片布局。
/// </summary>
/// <remarks>
/// 列分配算法(布局的核心, 测量与排列必须复用同一套结果):
/// 1. <see cref="ComputeColumns"/> 由可用宽度推出列数与列宽(至少一列, 列宽取整);
/// 2. 每个子元素以 (列宽, ∞) 测量, 得到自身高度;
/// 3. 线性扫描各列累计高度, 放入当前最矮的一列(平手取更靠左的列, 保证顺序稳定);
/// 4. 分配结果记入 _columnOfChild, Arrange 阶段直接复用。
/// </remarks>
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
        var columnCounts = new int[columns];
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
            // 行间距只在该列已有元素时计入。这里必须看"本列"而不是全局已放元素数:
            // 全局计数会让第 2..n 列的首个元素也带上行间距, 使该列累计高度凭空多一个
            // RowSpacing, 最终 desiredHeight 比实际内容高出一截(卡片下方多出空白)。
            columnHeights[target] += h + (columnCounts[target] > 0 ? RowSpacing : 0);
            columnCounts[target]++;
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
            // columnY[col] 是"下一个元素该放的 y": 累加含 RowSpacing 是正确的行距语义,
            // 末位元素多出的那个 RowSpacing 只是累加器的尾数, 不参与任何布局(面板高度取 finalSize)。
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
