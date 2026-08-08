using Spectre.Console;
using IRenderable = Spectre.Console.Rendering.IRenderable;

namespace AgentCommander.Cli.Tui.Renderers;

public static class MarkdownRenderer
{
    public static IRenderable Render(string markdown)
    {
        var rows = new List<IRenderable>();
        var lines = markdown.Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            if (line.StartsWith("```"))
            {
                var lang = line[3..].Trim();
                var codeLines = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].StartsWith("```"))
                {
                    codeLines.Add(lines[i]);
                    i++;
                }

                var code = string.Join("\n", codeLines);
                var panel = new Panel(Markup.Escape(code))
                    .Border(BoxBorder.Double)
                    .BorderColor(Color.Grey)
                    .Header($"[{Color.Grey}]{lang}[/]")
                    .Padding(1, 0, 1, 0);
                rows.Add(panel);
                i++;
            }
            else if (line.StartsWith("# "))
            {
                rows.Add(new Markup($"[bold]{Markup.Escape(line[2..])}[/]"));
                rows.Add(new Rule());
                i++;
            }
            else if (line.StartsWith("## "))
            {
                rows.Add(new Markup($"[bold underline]{Markup.Escape(line[3..])}[/]"));
                i++;
            }
            else if (line.StartsWith("### "))
            {
                rows.Add(new Markup($"[bold]{Markup.Escape(line[4..])}[/]"));
                i++;
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                rows.Add(new Markup($"  [grey]*[/] {Markup.Escape(line[2..])}"));
                i++;
            }
            else if (line.StartsWith("> "))
            {
                rows.Add(new Markup($"[italic grey]{Markup.Escape(line[2..])}[/]"));
                i++;
            }
            else if (line.StartsWith("---"))
            {
                rows.Add(new Rule());
                i++;
            }
            else
            {
                var formatted = FormatInlineMarkdown(line);
                if (!string.IsNullOrWhiteSpace(formatted))
                {
                    rows.Add(new Markup(formatted));
                }
                i++;
            }
        }

        return new Rows(rows);
    }

    private static string FormatInlineMarkdown(string line)
    {
        var result = Markup.Escape(line);

        result = System.Text.RegularExpressions.Regex.Replace(
            result, @"\*\*(.+?)\*\*", "[bold]$1[/]");

        result = System.Text.RegularExpressions.Regex.Replace(
            result, @"\*(.+?)\*", "[italic]$1[/]");

        result = System.Text.RegularExpressions.Regex.Replace(
            result, @"`(.+?)`", "[grey on black]$1[/]");

        return result;
    }
}
