using AgentCommander.Cli.Models;
using Spectre.Console;

namespace AgentCommander.Cli.Tui.Renderers;

public static class MessageRenderer
{
    public static void Render(Message message)
    {
        string icon;
        Color color;
        Color headerColor;

        switch (message.Role)
        {
            case MessageRole.User:
                icon = "\U0001F464";
                color = Color.Blue;
                headerColor = Color.CornflowerBlue;
                break;
            case MessageRole.Assistant:
                icon = "\U0001F916";
                color = Color.Green;
                headerColor = Color.LightGreen;
                break;
            case MessageRole.System:
                icon = "\u2139\uFE0F";
                color = Color.Grey;
                headerColor = Color.Grey;
                break;
            case MessageRole.Tool:
                icon = "\U0001F527";
                color = Color.Yellow;
                headerColor = Color.Olive;
                break;
            default:
                icon = "?";
                color = Color.White;
                headerColor = Color.White;
                break;
        }

        AnsiConsole.MarkupLine($"[{headerColor}]{icon} {message.RoleLabel}[/]");
        AnsiConsole.WriteLine();

        var panel = new Panel(Markup.Escape(message.Content))
            .Border(BoxBorder.Rounded)
            .BorderColor(color)
            .Padding(1, 0, 1, 0);

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }
}
