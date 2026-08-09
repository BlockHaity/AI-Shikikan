using Spectre.Console;

namespace AIShikikan.Cli.Tui.Services;

public class InputService
{
    private readonly List<string> _history = [];
    private int _historyIndex = -1;

    public string? ReadInput(CommandService commandService)
    {
        AnsiConsole.Markup("[bold green]> [/]");
        var input = Console.ReadLine();

        if (input is null)
            return null;

        input = input.Trim();

        if (string.IsNullOrEmpty(input))
            return string.Empty;

        if (commandService.TryHandle(input))
            return input is "/quit" or "/exit" ? null : string.Empty;

        _history.Add(input);
        _historyIndex = _history.Count;

        return input;
    }
}
