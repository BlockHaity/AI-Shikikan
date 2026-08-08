using AgentCommander.Core;
using Spectre.Console;

namespace AgentCommander.Cli.Tui.Renderers;

public static class StatusBarRenderer
{
    public static void Render(string? modelInfo = null)
    {
        var left = $"[bold]{AppInfo.Name}[/] v{AppInfo.Version}";
        var right = modelInfo ?? "No model";

        var table = new Table()
            .NoBorder()
            .AddColumn(new TableColumn("").Width(null))
            .AddColumn(new TableColumn("").RightAligned().Width(null));

        table.AddRow(
            new Markup(left),
            new Markup($"[grey]{right}[/]")
        );

        AnsiConsole.Write(table);
        AnsiConsole.Write(new Rule().RuleStyle("grey"));
    }
}
