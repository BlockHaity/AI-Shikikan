using AgentCommander.Cli.Models;
using AgentCommander.Cli.Tui.Renderers;
using AgentCommander.Cli.Tui.Services;
using Spectre.Console;

namespace AgentCommander.Cli.Tui;

public class TuiApp
{
    private readonly List<Message> _messages = [];
    private readonly CommandService _commandService;
    private readonly InputService _inputService;
    private readonly StreamingService _streamingService;

    public TuiApp()
    {
        _commandService = new CommandService(_messages);
        _inputService = new InputService();
        _streamingService = new StreamingService();
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        ShowWelcome();

        _messages.Add(new Message
        {
            Role = MessageRole.System,
            Content = "Agent Commander TUI 已启动。输入消息或 /help 查看可用命令。"
        });

        MessageRenderer.Render(_messages[0]);

        while (!ct.IsCancellationRequested)
        {
            StatusBarRenderer.Render();

            var input = _inputService.ReadInput(_commandService);
            if (input is null)
                break;

            if (string.IsNullOrEmpty(input))
                continue;

            var userMessage = new Message
            {
                Role = MessageRole.User,
                Content = input
            };
            _messages.Add(userMessage);
            MessageRenderer.Render(userMessage);

            var response = await ProcessUserInputAsync(input, ct);

            var assistantMessage = new Message
            {
                Role = MessageRole.Assistant,
                Content = response
            };
            _messages.Add(assistantMessage);
            MessageRenderer.Render(assistantMessage);
        }

        ShowGoodbye();
    }

    private void ShowWelcome()
    {
        AnsiConsole.Clear();

        var figlet = new FigletText("Agent Commander")
            .Color(Color.Green);
        AnsiConsole.Write(figlet);

        AnsiConsole.MarkupLine("[grey]一个强大的 Agent 管理与指挥工具[/]");
        AnsiConsole.MarkupLine("[grey]输入 /help 查看可用命令，/quit 退出[/]");
        AnsiConsole.WriteLine();
    }

    private void ShowGoodbye()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold green]再见！[/]");
    }

    private async Task<string> ProcessUserInputAsync(string input, CancellationToken ct)
    {
        await Task.Delay(100, ct);

        return $@"收到你的消息：""{input}""

目前 Agent Commander 处于初始开发阶段，AI 对话功能尚未接入。
后续将支持：
- 多模型 AI 对话
- Agent 管理与调度
- 工具调用与权限确认
- Markdown 渲染与流式输出";
    }
}
