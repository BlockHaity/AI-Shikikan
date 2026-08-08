using AgentCommander.Core.Services;
using Spectre.Console;

namespace AgentCommander.Cli.Tui.Services;

public class AgentCommandService
{
    private readonly AgentService _agentService = new();

    public void HandleAgentCommand(string args)
    {
        var parts = args.Split(' ', 2);
        var subCommand = parts[0].ToLowerInvariant();
        var subArgs = parts.Length > 1 ? parts[1] : string.Empty;

        switch (subCommand)
        {
            case "list":
                ListAgents();
                break;
            case "add":
                if (string.IsNullOrWhiteSpace(subArgs))
                {
                    AnsiConsole.MarkupLine("[red]用法: /agent add <名称>[/]");
                }
                else
                {
                    var agent = _agentService.Create(subArgs);
                    AnsiConsole.MarkupLine($"[green]已创建 Agent:[/] {agent.Name} (ID: {agent.Id})");
                }
                break;
            case "count":
                AnsiConsole.MarkupLine($"[bold]Agent 数量:[/] {_agentService.Count}");
                break;
            default:
                AnsiConsole.MarkupLine("[grey]用法: /agent list | add <名称> | count[/]");
                break;
        }
        AnsiConsole.WriteLine();
    }

    private void ListAgents()
    {
        var agents = _agentService.GetAll();
        if (agents.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]暂无 Agent[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("ID")
            .AddColumn("名称")
            .AddColumn("状态");

        foreach (var a in agents)
        {
            table.AddRow(a.Id, a.Name, a.Status);
        }

        AnsiConsole.Write(table);
    }
}
