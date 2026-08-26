using System.Text;
using AIShikikan.Cli.Models;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace AIShikikan.Cli.Tui.Ui;

/// <summary>基于 Terminal.Gui 聊天日志与对话框的 <see cref="IUiOutput"/> 实现。
/// 所有模态交互通过 <see cref="IApplication.Invoke"/> 编组到 UI 线程执行。</summary>
public sealed class TuiUiOutput : IUiOutput
{
    private readonly IApplication _app;
    private readonly ChatLogView _chat;

    public TuiUiOutput(IApplication app, ChatLogView chat)
    {
        _app = app;
        _chat = chat;
    }

    public void Write(string markup) => _chat.AppendMarkup(markup);

    public void Message(MessageRole role, string content)
    {
        var (icon, color) = role switch
        {
            MessageRole.User => ("👤", "brightblue"),
            MessageRole.Assistant => ("🤖", "brightgreen"),
            MessageRole.System => ("ℹ️", "grey"),
            MessageRole.Tool => ("🔧", "yellow"),
            _ => ("?", "white")
        };

        _chat.AppendMarkup($"[bold {color}]{icon} {role}[/]");
        if (!string.IsNullOrEmpty(content))
        {
            _chat.AppendMarkup(MarkupEscape.Escape(content));
        }

        _chat.AppendCells([]);
    }

    public void Ok(string text) => Write($"[green]{MarkupEscape.Escape(text)}[/]");

    public void Error(string text) => Write($"[red]{MarkupEscape.Escape(text)}[/]");

    public void Hint(string text) => Write($"[grey]{MarkupEscape.Escape(text)}[/]");

    public void Clear() => _chat.Clear();

    public void ScrollToBottom() => _chat.ScrollToBottom();

    public void Panel(string title, string bodyMarkup)
    {
        var borderAttr = new Attribute(Color.Gray, MarkupConverter.DefaultBackground);
        var bodyLines = MarkupConverter.ParseLines(bodyMarkup);

        var width = title.Length + 6;
        foreach (var line in bodyLines)
        {
            var len = 0;
            foreach (var run in line) len += run.Text.Length;
            width = Math.Max(width, len + 4);
        }

        width = Math.Min(width, 96);

        // 顶边框: ┌─ 标题 ─...─┐
        var cells = new List<Cell> { new(borderAttr, false, "┌─ ") };
        cells.AddRange(Cell.ToCellList(title, new Attribute(Color.Gray, MarkupConverter.DefaultBackground)));
        cells.Add(new Cell(borderAttr, false, " " + new string('─', Math.Max(1, width - title.Length - 4)) + "─┐"));
        _chat.AppendCells(cells);

        foreach (var line in bodyLines)
        {
            var bodyCells = new List<Cell> { new(borderAttr, false, "│ ") };
            var used = 0;
            foreach (var run in line)
            {
                bodyCells.AddRange(Cell.ToCellList(run.Text, run.Attribute));
                used += run.Text.Length;
            }

            var pad = width - 3 - used;
            if (pad > 0) bodyCells.Add(new Cell(borderAttr, false, new string(' ', pad)));
            bodyCells.Add(new Cell(borderAttr, false, "│"));
            _chat.AppendCells(bodyCells);
        }

        _chat.AppendCells(new List<Cell>
        {
            new(borderAttr, false, "└" + new string('─', Math.Max(1, width - 2)) + "┘")
        });
        _chat.AppendCells([]);
    }

    public void Table(string[] headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var rowsList = rows.Select(r => r.ToArray()).ToList();
        var cols = headers.Length;
        var widths = headers.Select(h => h.Length).ToArray();

        foreach (var row in rowsList)
        {
            for (var c = 0; c < Math.Min(cols, row.Length); c++)
            {
                var plain = StripMarkup(row[c]);
                widths[c] = Math.Max(widths[c], Math.Min(plain.Length, 56));
            }
        }

        var sb = new StringBuilder();
        for (var c = 0; c < cols; c++)
        {
            sb.Append(c == 0 ? "[bold]" : "  [bold]");
            sb.Append(MarkupEscape.Escape(Pad(headers[c], widths[c])));
            sb.Append("[/]");
        }

        Write(sb.ToString());
        Write(new string('─', widths.Sum() + (cols - 1) * 2));

        foreach (var row in rowsList)
        {
            var line = new StringBuilder();
            for (var c = 0; c < cols; c++)
            {
                var cell = c < row.Length ? row[c] : string.Empty;
                var plain = StripMarkup(cell);
                var pad = Pad(plain, widths[c]);
                if (c > 0) line.Append("  ");
                line.Append(PadMarkupCell(cell, pad));
            }

            Write(line.ToString());
        }
    }

    private static string Pad(string s, int width) => s.Length >= width ? s : s + new string(' ', width - s.Length);

    private static string PadMarkupCell(string cellMarkup, string paddedPlain)
    {
        // 简单处理: 纯文本直接补空格; 含 markup 时按纯文本长度补
        if (!cellMarkup.Contains('['))
        {
            return paddedPlain;
        }

        return cellMarkup + new string(' ', Math.Max(0, paddedPlain.Length - StripMarkup(cellMarkup).Length));
    }

    private static string StripMarkup(string s)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '[')
            {
                var close = s.IndexOf(']', i + 1);
                if (close > i)
                {
                    i = close + 1;
                    continue;
                }
            }

            sb.Append(s[i]);
            i++;
        }

        return sb.ToString();
    }

    public bool ConfirmModal(string title, string message, string yesLabel = "是", string noLabel = "否")
    {
        var result = RunOnUi(() => MessageBox.Query(_app, title, message, noLabel, yesLabel));
        return result == 1;
    }

    public int SelectModal(string title, string message, string[] choices)
    {
        if (choices.Length == 0) return -1;
        if (choices.Length == 1)
        {
            RunOnUi(() => MessageBox.Query(_app, title, message, choices[0]));
            return 0;
        }

        var idx = RunOnUi(() => MessageBox.Query(_app, title, message, choices));
        return idx ?? -1;
    }

    public void ShowTextModal(string title, string text)
    {
        RunOnUi(() =>
        {
            var win = new Window
            {
                Title = title,
                X = Pos.Center(),
                Y = Pos.Center(),
                Width = Dim.Percent(80),
                Height = Dim.Percent(80)
            };
            win.SetScheme(UiTheme.Transparent);

            var view = new TextView
            {
                ReadOnly = true,
                Multiline = true,
                WordWrap = true,
                ScrollBars = true,
                Text = text,
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(1)
            };
            view.BorderStyle = LineStyle.None;

            var close = new Button { Text = "关闭", X = Pos.Center(), Y = Pos.AnchorEnd(1) };
            close.Accepted += (_, _) => _app.RequestStop(win);

            win.KeyDown += (_, key) =>
            {
                if (key.KeyCode == KeyCode.Esc)
                {
                    key.Handled = true;
                    _app.RequestStop(win);
                }
            };

            win.Add(view, close);
            _app.Run(win);
        });
    }

    /// <summary>在 UI 线程执行 action 并阻塞等待结果(支持从引擎线程调用)。</summary>
    private T RunOnUi<T>(Func<T> action)
    {
        if (_app.MainThreadId is { } mainId && Thread.CurrentThread.ManagedThreadId == mainId)
        {
            return action();
        }

        var tcs = new TaskCompletionSource<T>();
        _app.Invoke(() => tcs.TrySetResult(action()));
        return tcs.Task.GetAwaiter().GetResult();
    }

    private void RunOnUi(Action action)
    {
        if (_app.MainThreadId is { } mainId && Thread.CurrentThread.ManagedThreadId == mainId)
        {
            action();
            return;
        }

        var tcs = new TaskCompletionSource();
        _app.Invoke(() =>
        {
            action();
            tcs.TrySetResult();
        });
        tcs.Task.GetAwaiter().GetResult();
    }
}
