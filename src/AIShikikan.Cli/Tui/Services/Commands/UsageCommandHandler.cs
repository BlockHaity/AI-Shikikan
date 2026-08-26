using AIShikikan.Cli.Tui.Ui;
using AIShikikan.Core.Services.Usage;

namespace AIShikikan.Cli.Tui.Services.Commands;

/// <summary>用量统计: 查看 LLM 调用与子 Agent 调用统计(今日/累计, 按模型/会话/Agent 分布)。</summary>
public sealed class UsageCommandHandler : ICommandHandler
{
    private readonly IUiOutput _ui;

    public UsageCommandHandler(IUiOutput ui)
    {
        _ui = ui;
    }

    public bool CanHandle(string command) => command is "usage" or "stats";

    public IEnumerable<(string Command, string Help)> HelpRows =>
    [
        ("/usage", "查看用量总览(今日/累计)"),
        ("/usage models", "按模型查看用量"),
        ("/usage sessions", "按会话查看用量"),
        ("/usage agents", "按子 Agent 查看调用统计")
    ];

    public bool TryHandle(string command, string args)
    {
        var sub = args.Trim().ToLowerInvariant();
        switch (sub)
        {
            case "models":
                ShowModels();
                return true;
            case "sessions":
                ShowSessions();
                return true;
            case "agents":
                ShowAgents();
                return true;
            case "":
                ShowSummary();
                return true;
            default:
                _ui.Error("用法: /usage [models|sessions|agents]");
                return true;
        }
    }

    private void ShowSummary()
    {
        var s = UsageStatsService.GetSnapshot();
        _ui.Panel("用量统计",
            $"今日: 输入 {s.TodayInputTokens:N0} / 输出 {s.TodayOutputTokens:N0} token   调用 {s.TotalLlmCalls} 次\n" +
            $"累计: 输入 {s.TotalInputTokens:N0} / 输出 {s.TotalOutputTokens:N0} token   调用 {s.TotalLlmCalls} 次\n" +
            $"子 Agent 调用: {s.TotalAgentCalls} 次");
        _ui.Hint("用法: /usage models | /usage sessions | /usage agents");
        _ui.ScrollToBottom();
    }

    private void ShowModels()
    {
        var s = UsageStatsService.GetSnapshot();
        var rows = new List<IReadOnlyList<string>>();
        foreach (var m in s.ModelStats)
        {
            rows.Add(new[]
            {
                MarkupEscape.Escape(m.Model),
                m.Calls.ToString(),
                m.InputTokens.ToString("N0"),
                m.OutputTokens.ToString("N0"),
                m.TotalTokens.ToString("N0")
            });
        }

        _ui.Table(["模型", "调用", "输入", "输出", "总计"], rows);
        _ui.ScrollToBottom();
    }

    private void ShowSessions()
    {
        var s = UsageStatsService.GetSnapshot();
        var rows = new List<IReadOnlyList<string>>();
        foreach (var ss in s.SessionStats)
        {
            rows.Add(new[]
            {
                ss.SessionId,
                MarkupEscape.Escape(CommandUi.Shorten(ss.SessionTitle, 30)),
                ss.Calls.ToString(),
                ss.TotalTokens.ToString("N0")
            });
        }

        _ui.Table(["会话", "标题", "调用", "Tokens"], rows);
        _ui.ScrollToBottom();
    }

    private void ShowAgents()
    {
        var s = UsageStatsService.GetSnapshot();
        var rows = new List<IReadOnlyList<string>>();
        foreach (var a in s.AgentStats)
        {
            var successRate = a.Calls > 0 ? (double)a.Succeeded / a.Calls * 100 : 0;
            rows.Add(new[]
            {
                MarkupEscape.Escape(a.AgentName),
                a.Calls.ToString(),
                a.Succeeded.ToString(),
                $"{successRate:F0}%"
            });
        }

        _ui.Table(["Agent", "调用", "成功", "成功率"], rows);
        _ui.ScrollToBottom();
    }
}
