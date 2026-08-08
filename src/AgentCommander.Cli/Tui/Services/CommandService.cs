using AgentCommander.Cli.Models;
using Spectre.Console;

namespace AgentCommander.Cli.Tui.Services;

public class CommandService
{
    private readonly List<Message> _messages;

    public CommandService(List<Message> messages)
    {
        _messages = messages;
    }

    public bool TryHandle(string input)
    {
        if (!input.StartsWith('/'))
            return false;

        var parts = input.Split(' ', 2);
        var command = parts[0].ToLowerInvariant();
        var args = parts.Length > 1 ? parts[1] : string.Empty;

        switch (command)
        {
            case "/help":
                ShowHelp();
                return true;
            case "/clear":
                ClearMessages();
                return true;
            case "/quit":
            case "/exit":
                return true;
            case "/status":
                ShowStatus();
                return true;
            case "/model":
                ShowModel(args);
                return true;
            default:
                AnsiConsole.MarkupLine($"[red]未知命令: {Markup.Escape(command)}[/]");
                AnsiConsole.MarkupLine("[grey]输入 /help 查看可用命令[/]");
                return true;
        }
    }

    private void ShowHelp()
    {
        AnsiConsole.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]命令[/]"))
            .AddColumn(new TableColumn("[bold]说明[/]"));

        table.AddRow("/help", "显示帮助信息");
        table.AddRow("/clear", "清除对话历史");
        table.AddRow("/model [grey]<name>[/]", "查看/切换模型");
        table.AddRow("/status", "显示系统状态");
        table.AddRow("/quit, /exit", "退出 TUI");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private void ClearMessages()
    {
        _messages.Clear();
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[green]对话历史已清除[/]");
        AnsiConsole.WriteLine();
    }

    private void ShowStatus()
    {
        AnsiConsole.WriteLine();
        var panel = new Panel(
            new Rows(
                new Markup($"[bold]消息数量:[/] {_messages.Count}"),
                new Markup($"[bold]终端宽度:[/] {Console.WindowWidth}"),
                new Markup($"[bold]运行时间:[/] {DateTime.Now:HH:mm:ss}")
            ))
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Blue)
            .Header("[bold]系统状态[/]");
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    private void ShowModel(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            AnsiConsole.MarkupLine("[bold]当前模型:[/] [green]default[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[bold]切换模型至:[/] [green]{Markup.Escape(modelName)}[/]");
        }
        AnsiConsole.WriteLine();
    }
}
