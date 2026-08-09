using AIShikikan.Cli.Models;
using AIShikikan.Cli.Tui.Renderers;
using AIShikikan.Cli.Tui.Services;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Git;
using AIShikikan.Core.Services.Usage;
using Spectre.Console;

namespace AIShikikan.Cli.Tui;

public class TuiApp
{
    private readonly List<Message> _messages = [];
    private readonly CommandService _commandService;
    private readonly InputService _inputService;
    private readonly CommanderRuntime _runtime;

    public TuiApp(string? personaId = null)
    {
        _runtime = CommanderRuntime.Boot(Environment.CurrentDirectory, personaId);
        _commandService = new CommandService(_messages, _runtime);
        _inputService = new InputService();
        _runtime.Engine.OnEvent += OnEngineEvent;
        _runtime.Engine.OnEvent += OnUsageRecorded;
        _runtime.Assignments.AssignmentChanged += a =>
            AnsiConsole.MarkupLine($"[grey]分派更新: {a.Display}[/]");
    }

    /// <summary>记录 CLI 会话的 LLM 用量统计。</summary>
    private void OnUsageRecorded(AgentEngineEvent e)
    {
        if (e is not EngineUsageRecorded usage) return;
        UsageStatsService.RecordLlmUsage(
            "tui", "TUI 会话",
            usage.Provider, usage.Model,
            usage.Usage.InputTokens, usage.Usage.OutputTokens);
    }

    public CommanderRuntime Runtime => _runtime;

    public async Task RunAsync(CancellationToken ct = default)
    {
        ShowWelcome();

        _messages.Add(new Message
        {
            Role = MessageRole.System,
            Content = "AI-Shikikan TUI 已启动。输入消息或 /help 查看可用命令。"
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

        AnsiConsole.Write(new FigletText("AI-Shikikan").Color(Color.Green));
        AnsiConsole.MarkupLine("[grey]一个强大的 Agent 管理与指挥工具[/]");
        AnsiConsole.MarkupLine("[grey]输入 /help 查看可用命令, /quit 退出[/]");
        AnsiConsole.WriteLine();
    }

    private void ShowGoodbye()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold green]再见！[/]");
    }

    private async Task<string> ProcessUserInputAsync(string input, CancellationToken ct)
    {
        var beforeSteps = _runtime.Git.PendingReview().Select(s => s.StepId).ToHashSet(StringComparer.Ordinal);

        var response = await _runtime.Engine.RunTurnAsync(input, ct);

        await ReviewPendingStepsAsync(beforeSteps);
        return response;
    }

    private async Task ReviewPendingStepsAsync(HashSet<string> before)
    {
        var pending = _runtime.Git.PendingReview()
            .Where(s => !before.Contains(s.StepId))
            .ToList();

        foreach (var step in pending)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold cyan]步骤检查点:[/] {step.StepId} [{Markup.Escape(step.Label)}]  分支: {step.StepBranch} → 基: {step.BaseBranch}");

            while (true)
            {
                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[grey]如何处理该步骤的变更?[/]")
                        .AddChoices("查看 diff", "合并到主分支", "丢弃(回滚该步)", "暂不处理"));

                switch (choice)
                {
                    case "查看 diff":
                        var diff = _runtime.Git.GetDiff(step.StepId);
                        AnsiConsole.WriteLine(diff.Succeeded ? diff.Stdout : diff.Stderr);
                        continue;

                    case "合并到主分支":
                        ExecuteGitSafe(() => _runtime.Git.MergeStep(step.StepId),
                            $"已合并步骤 {step.StepId} 到 {step.BaseBranch}");
                        break;

                    case "丢弃(回滚该步骤)":
                        if (AnsiConsole.Confirm("丢弃将删除该步骤分支并放弃全部变更, 确认?", false))
                        {
                            ExecuteGitSafe(() => _runtime.Git.DropStep(step.StepId),
                                $"已丢弃步骤 {step.StepId}");
                        }

                        break;
                }

                break;
            }
        }
    }

    private void ExecuteGitSafe(Func<GitCommandResult> action, string successMessage)
    {
        try
        {
            var result = action();
            if (!result.Succeeded)
            {
                AnsiConsole.MarkupLine($"[red]git 操作失败:[/] {Markup.Escape(result.Stderr)}");
                return;
            }

            AnsiConsole.MarkupLine($"[green]{Markup.Escape(successMessage)}[/]");
            if (!string.IsNullOrWhiteSpace(result.Stdout))
            {
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(Truncate(result.Stdout, 2000))}[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]git 操作失败:[/] {Markup.Escape(ex.Message)}");
        }
    }

    private void OnEngineEvent(AgentEngineEvent e)
    {
        switch (e)
        {
            case EngineToolStarted started:
                var args = started.Arguments.Length > 100
                    ? started.Arguments[..100] + "..."
                    : started.Arguments;
                AnsiConsole.MarkupLine($"[cyan]🔥 工具调用:[/] [bold]{started.ToolName}[/]");
                if (args.Length > 0)
                {
                    AnsiConsole.MarkupLine($"[grey]   {Markup.Escape(args)}[/]");
                }

                break;

            case EngineToolOutput output:
                AnsiConsole.MarkupLine($"[grey]   ├ {Markup.Escape(TruncateLine(output.Line))}[/]");
                break;

            case EngineToolFinished finished:
                AnsiConsole.MarkupLine(finished.Result.IsError
                    ? $"[red]   └ 工具失败: {Markup.Escape(Truncate(finished.Result.Content, 200))}[/]"
                    : $"[green]   └ 工具完成 ✓[/]");
                break;

            case EngineApprovalRequested approval:
                var approved = AnsiConsole.Confirm(
                    $"[bold yellow]批准调用工具 {approval.ToolName}[/] ({Markup.Escape(Truncate(approval.Arguments, 120))})?",
                    false);
                approval.UserDecision.TrySetResult(approved);
                break;
        }
    }

    private static string Truncate(string s, int len) => s.Length <= len ? s : s[..len] + "...";

    private static string TruncateLine(string s) => s.Length <= 160 ? s : s[..160] + "...";
}