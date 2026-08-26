using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace AIShikikan.Cli.Tui.Ui;

/// <summary>聊天日志视图: 自绘多行带色滚动日志(替代 TextView, 规避其 cells 渲染缺陷)。
/// 支持追加 markup 文本、鼠标滚轮滚动、自动贴底; 行数超限时丢弃最旧内容。</summary>
public class ChatLogView : View
{
    private const int MaxLines = 3000;

    private readonly List<List<Cell>> _lines = [];
    private bool _bottomPinned = true;
    private int _scrollOffset;

    public ChatLogView()
    {
        CanFocus = false;
        MouseEvent += OnMouseEvent;
    }

    public IReadOnlyList<List<Cell>> Lines => _lines;

    /// <summary>追加一行 markup 文本(可含换行, 自动拆分为多行)。</summary>
    public void AppendMarkup(string markup)
    {
        foreach (var line in MarkupConverter.ParseLines(markup))
        {
            AppendRunLine(line);
        }

        PinToBottomIfPinned();
    }

    /// <summary>追加一行纯文本(按默认色渲染)。</summary>
    public void AppendText(string text) => AppendMarkup(MarkupEscape.Escape(text));

    /// <summary>追加一行带属性的纯文本。</summary>
    public void AppendLine(string text, Terminal.Gui.Drawing.Attribute attribute)
    {
        _lines.Add(text.Length == 0 ? [] : Cell.ToCellList(text, attribute).ToList());
        Trim();
        PinToBottomIfPinned();
    }

    /// <summary>追加一行已构造好的 cells(用于带色边框、进度条等自定义行)。</summary>
    public void AppendCells(List<Cell> cells)
    {
        _lines.Add(cells.Count == 0 ? [] : cells);
        Trim();
        PinToBottomIfPinned();
    }

    private void AppendRunLine(List<MarkupRun> runs)
    {
        if (runs.Count == 0)
        {
            _lines.Add([]);
            return;
        }

        var cells = new List<Cell>();
        foreach (var run in runs)
        {
            if (run.Text.Length == 0) continue;
            cells.AddRange(Cell.ToCellList(run.Text, run.Attribute));
        }

        _lines.Add(cells);
        Trim();
        SetNeedsDraw();
    }

    private void Trim()
    {
        if (_lines.Count > MaxLines)
        {
            _lines.RemoveRange(0, _lines.Count - MaxLines);
        }
    }

    /// <summary>滚动到日志底部(重新贴底)。</summary>
    public void ScrollToBottom()
    {
        _bottomPinned = true;
        _scrollOffset = 0;
        SetNeedsDraw();
    }

    public void Clear()
    {
        _lines.Clear();
        _bottomPinned = true;
        _scrollOffset = 0;
        SetNeedsDraw();
    }

    private void OnMouseEvent(object? sender, Mouse mouse)
    {
        if (!mouse.IsWheel)
        {
            return;
        }

        var delta = mouse.Flags.HasFlag(MouseFlags.WheeledUp)
            ? 3
            : mouse.Flags.HasFlag(MouseFlags.WheeledDown) ? -3 : 0;
        if (delta == 0)
        {
            return;
        }

        var maxOffset = Math.Max(0, WrappedCount(Viewport.Width) - Viewport.Height);
        _scrollOffset = Math.Clamp(_scrollOffset + delta, 0, maxOffset);
        _bottomPinned = _scrollOffset >= maxOffset;
        mouse.Handled = true;
        SetNeedsDraw();
    }

    private void PinToBottomIfPinned()
    {
        if (_bottomPinned)
        {
            _scrollOffset = 0;
        }

        SetNeedsDraw();
    }

    /// <summary>按当前宽度预计算换行后的可视行数。</summary>
    private int WrappedCount(int width) => BuildWrapped(width).Count;

    protected override bool OnDrawingContent(DrawContext context)
    {
        var width = Viewport.Width;
        var height = Viewport.Height;
        if (width <= 0 || height <= 0)
        {
            return true;
        }

        var wrapped = BuildWrapped(width);
        var maxOffset = Math.Max(0, wrapped.Count - height);
        if (_bottomPinned)
        {
            _scrollOffset = maxOffset;
        }
        else
        {
            _scrollOffset = Math.Min(_scrollOffset, maxOffset);
        }

        var start = Math.Max(0, wrapped.Count - height - _scrollOffset);
        for (var i = start; i < wrapped.Count; i++)
        {
            var y = i - start;
            if (y >= height) break;

            var x = 0;
            foreach (var cell in wrapped[i])
            {
                if (x + CellWidth(cell) > width) break;
                SetAttribute(cell.Attribute ?? MarkupConverter.DefaultAttribute);
                AddStr(x, y, cell.Grapheme);
                x += CellWidth(cell);
            }
        }

        return true;
    }

    /// <summary>将源行按 viewport 宽度换行(单元格感知, 宽字符按 2 列计)。</summary>
    private List<List<Cell>> BuildWrapped(int width)
    {
        var result = new List<List<Cell>>();
        var current = new List<Cell>();
        var used = 0;

        foreach (var line in _lines)
        {
            current.Clear();
            used = 0;

            foreach (var cell in line)
            {
                var w = CellWidth(cell);
                if (used + w > width && current.Count > 0)
                {
                    result.Add(current);
                    current = new List<Cell>();
                    used = 0;
                }

                current.Add(cell);
                used += w;
            }

            result.Add(current);
            current = new List<Cell>();
        }

        return result;
    }

    /// <summary>计算单元格显示宽度(东亚宽字符与 emoji 按 2 列)。</summary>
    private static int CellWidth(Cell cell)
    {
        if (cell.Runes.Count > 0)
        {
            return GraphemeWidth(cell.Runes[0]);
        }

        return cell.Grapheme.Length == 0 ? 0 : 1;
    }

    /// <summary>East Asian Wide / Fullwidth / emoji 子集(近似 wcwidth)。</summary>
    private static int GraphemeWidth(Rune r)
    {
        var v = r.Value;
        if (v >= 0x1100 && (v <= 0x115F || v == 0x2329 || v == 0x232A
            || (v >= 0x2E80 && v <= 0xA4CF && v != 0x303F)
            || (v >= 0xAC00 && v <= 0xD7A3)
            || (v >= 0xF900 && v <= 0xFAFF)
            || (v >= 0xFE30 && v <= 0xFE4F)
            || (v >= 0xFF00 && v <= 0xFF60)
            || (v >= 0xFFE0 && v <= 0xFFE6)
            || (v >= 0x1F300 && v <= 0x1F64F)
            || (v >= 0x1F900 && v <= 0x1F9FF)
            || (v >= 0x20000 && v <= 0x3FFFD)))
        {
            return 2;
        }

        return 1;
    }
}

/// <summary>对用户内容做 markup 转义([[ 与 ]] 转义), 与 Spectre 的 Markup.Escape 语义一致。</summary>
public static class MarkupEscape
{
    public static string Escape(string text) =>
        text.Replace("[", "[[").Replace("]", "]]");
}
