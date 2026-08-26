using System.Globalization;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;

namespace AIShikikan.Cli.Tui.Ui;

/// <summary>一段文本及其样式, 用于在 ChatLogView 中按颜色渲染。</summary>
public readonly record struct MarkupRun(string Text, Attribute Attribute);

/// <summary>将 Spectre.Console 风格 markup(`[red]...[/]`、`[bold]`、`[[` 转义)转换为
/// Terminal.Gui 的 <see cref="MarkupRun"/> 序列, 供带色日志渲染。</summary>
public static class MarkupConverter
{
    private static readonly Dictionary<string, ColorName16> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = Color.Black,
        ["red"] = Color.Red,
        ["green"] = Color.Green,
        ["yellow"] = Color.Yellow,
        ["blue"] = Color.Blue,
        ["magenta"] = Color.Magenta,
        ["cyan"] = Color.Cyan,
        ["white"] = Color.White,
        ["gray"] = Color.Gray,
        ["grey"] = Color.Gray,
        ["darkgray"] = Color.DarkGray,
        ["darkgrey"] = Color.DarkGray,
        ["silver"] = Color.Gray,
        ["brightred"] = Color.BrightRed,
        ["brightgreen"] = Color.BrightGreen,
        ["brightyellow"] = Color.BrightYellow,
        ["brightblue"] = Color.BrightBlue,
        ["brightmagenta"] = Color.BrightMagenta,
        ["brightcyan"] = Color.BrightCyan,
        ["brightwhite"] = Color.White,
        // Spectre 常用别名
        ["olive"] = Color.Yellow,
        ["maroon"] = Color.Red,
        ["lime"] = Color.BrightGreen,
        ["aqua"] = Color.BrightCyan,
        ["navy"] = Color.Blue,
        ["teal"] = Color.Cyan,
        ["purple"] = Color.Magenta,
        ["fuchsia"] = Color.BrightMagenta,
        ["orange"] = Color.BrightYellow,
        ["cornflowerblue"] = Color.BrightBlue,
        ["lightgreen"] = Color.BrightGreen,
        ["gold1"] = Color.BrightYellow,
        ["deepskyblue1"] = Color.BrightCyan,
        ["default"] = Color.Gray
    };

    private static readonly Dictionary<string, TextStyle> DecorationStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bold"] = TextStyle.Bold,
        ["faint"] = TextStyle.Faint,
        ["dim"] = TextStyle.Faint,
        ["italic"] = TextStyle.Italic,
        ["underline"] = TextStyle.Underline,
        ["reverse"] = TextStyle.Reverse,
        ["blink"] = TextStyle.Blink,
        ["strikethrough"] = TextStyle.Strikethrough
    };

    private static Color _defaultForeground = Color.Gray;
    private static Color _defaultBackground = Color.Black;

    /// <summary>默认前景色(由应用初始化时从驱动读取)。</summary>
    public static Color DefaultForeground
    {
        get => _defaultForeground;
        set => _defaultForeground = value;
    }

    /// <summary>默认背景色(由应用初始化时从驱动读取)。</summary>
    public static Color DefaultBackground
    {
        get => _defaultBackground;
        set => _defaultBackground = value;
    }

    public static Attribute DefaultAttribute => new(_defaultForeground, _defaultBackground);

    /// <summary>解析 markup 为 runs(不换行)。</summary>
    public static List<MarkupRun> Parse(string markup)
    {
        var runs = new List<MarkupRun>();
        var sb = new System.Text.StringBuilder();
        var fg = _defaultForeground;
        var bg = _defaultBackground;
        var styles = TextStyle.None;

        void Flush()
        {
            if (sb.Length == 0) return;
            runs.Add(new MarkupRun(sb.ToString(), new Attribute(fg, bg, styles)));
            sb.Clear();
        }

        var i = 0;
        while (i < markup.Length)
        {
            var ch = markup[i];

            if (ch == '[' && i + 1 < markup.Length)
            {
                // 转义: [[ 与 ]]
                if (markup[i + 1] == '[')
                {
                    sb.Append('[');
                    i += 2;
                    continue;
                }

                var close = markup.IndexOf(']', i + 1);
                if (close < 0)
                {
                    sb.Append(ch);
                    i++;
                    continue;
                }

                var tag = markup[(i + 1)..close];

                if (tag.Length == 0)
                {
                    // 空标签按字面处理
                    sb.Append(ch);
                    i++;
                    continue;
                }

                if (tag == "/" || tag == "/]")
                {
                    Flush();
                    fg = _defaultForeground;
                    bg = _defaultBackground;
                    styles = TextStyle.None;
                    i = close + 1;
                    continue;
                }

                if (TryApplyTag(tag, ref fg, ref bg, ref styles))
                {
                    Flush();
                    i = close + 1;
                    continue;
                }

                // 无法识别的标签: 按字面输出
                sb.Append(ch);
                i++;
                continue;
            }

            if (ch == ']' && i + 1 < markup.Length && markup[i + 1] == ']')
            {
                sb.Append(']');
                i += 2;
                continue;
            }

            sb.Append(ch);
            i++;
        }

        Flush();
        return runs;
    }

    /// <summary>解析单行 markup 并切分为多行 runs(支持 \n)。</summary>
    public static List<List<MarkupRun>> ParseLines(string markup)
    {
        var runs = Parse(markup);
        var lines = new List<List<MarkupRun>>();
        var current = new List<MarkupRun>();

        foreach (var run in runs)
        {
            var parts = run.Text.Split('\n');
            for (var p = 0; p < parts.Length; p++)
            {
                if (p > 0)
                {
                    lines.Add(current);
                    current = new List<MarkupRun>();
                }

                if (parts[p].Length > 0)
                {
                    current.Add(run with { Text = parts[p] });
                }
            }
        }

        lines.Add(current);
        return lines;
    }

    private static bool TryApplyTag(string tag, ref Color fg, ref Color bg, ref TextStyle styles)
    {
        var found = false;

        // 支持 "bold red on blue" 这类组合
        foreach (var token in tag.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (token.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                fg = _defaultForeground;
                bg = _defaultBackground;
                found = true;
                continue;
            }

            if (NamedColors.TryGetValue(token, out var color))
            {
                // "on" 前的颜色为前景, 之后的为背景
                fg = color;
                bg = _defaultBackground;
                found = true;
                continue;
            }

            if (token.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // on 后的颜色作为背景
            if (tag.Contains(" on ", StringComparison.OrdinalIgnoreCase) && NamedColors.TryGetValue(token, out var bgColor))
            {
                bg = bgColor;
                found = true;
                continue;
            }

            if (Deco(token) is { } deco)
            {
                styles |= deco;
                found = true;
                continue;
            }

            if (token.StartsWith('#') && TryParseHexColor(token, out var hex))
            {
                fg = hex;
                found = true;
            }
        }

        return found;
    }

    private static TextStyle? Deco(string token) =>
        DecorationStyles.TryGetValue(token, out var s) ? s : null;

    private static bool TryParseHexColor(string token, out Color color)
    {
        color = default;
        if (token.Length is not (4 or 7)) return false;

        var hex = token[1..];
        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        }

        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        var r = (byte)((value >> 16) & 0xFF);
        var g = (byte)((value >> 8) & 0xFF);
        var b = (byte)(value & 0xFF);
        color = new Color(r, g, b);
        return true;
    }
}
