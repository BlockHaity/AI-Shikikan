using AIShikikan.Cli.Tui.Ui;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Runtime;

namespace AIShikikan.Cli.Tui.Services.Commands;

/// <summary>Agent 管理: 查看 Agent 状态、派发任务、停止与查看执行记录。</summary>
public sealed class AgentCommandHandler : ICommandHandler
{
    private readonly CommanderRuntime _runtime;
    private readonly IUiOutput _ui;

    public AgentCommandHandler(CommanderRuntime runtime, IUiOutput ui)
    {
        _runtime = runtime;
        _ui = ui;
    }

    public bool CanHandle(string command) => command is "agents" or "agent";

    public IEnumerable<(string Command, string Help)> HelpRows =>
    [
        ("/agents", "列出 Agent 与当前状态"),
        ("/agent run [grey]<id> <任务>[/]", "派发任务给指定 Agent(后台执行)"),
        ("/agent stop [grey]<id>[/]", "停止 Agent 的运行中任务"),
        ("/agent logs [grey]<id>[/]", "查看 Agent 执行记录")
    ];

    public bool TryHandle(string command, string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            ListAgents();
            return true;
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "run":
            case "dispatch":
                RunAgent(parts.Skip(1).ToArray());
                return true;
            case "stop":
            case "cancel":
                StopAgent(parts.Length > 1 ? parts[1] : string.Empty);
                return true;
            case "logs":
            case "log":
                ShowAgentLogs(parts.Length > 1 ? parts[1] : string.Empty);
                return true;
            case "assignments":
                ShowAssignments();
                return true;
            default:
                ListAgents();
                return true;
        }
    }

    private void ListAgents()
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var agent in _runtime.Agents)
        {
            var (status, running) = GetAgentStatus(agent.Id);
            rows.Add(new[]
            {
                agent.Id,
                MarkupEscape.Escape(agent.Name),
                agent.DefaultMode,
                running > 0 ? $"[yellow]工作中[/] ({running})" : status
            });
        }

        _ui.Table(["ID", "名称", "模式", "状态"], rows);
        _ui.Hint("用法: /agent run <id> <任务> | /agent stop <id> | /agent logs <id>");
        _ui.ScrollToBottom();
    }

    private (string Status, int Running) GetAgentStatus(string agentId)
    {
        var assignments = _runtime.Assignments.All.Where(a => a.AgentId == agentId).ToList();
        var running = assignments.Count(a => a.Status is SubagentStatus.Queued or SubagentStatus.Running);
        if (running > 0)
        {
            return ("工作中", running);
        }

        var latest = assignments.FirstOrDefault();
        if (latest is null)
        {
            return ("[grey]空闲[/]", 0);
        }

        var status = latest.Status switch
        {
            SubagentStatus.Completed => $"[green]{latest.Status}[/]",
            SubagentStatus.Failed => $"[red]{latest.Status}[/]",
            SubagentStatus.Cancelled => "[yellow]已取消[/]",
            SubagentStatus.TimedOut => "[yellow]超时[/]",
            _ => "[grey]空闲[/]"
        };
        return (status, 0);
    }

    private void RunAgent(string[] parts)
    {
        if (parts.Length < 2)
        {
            _ui.Error("用法: /agent run <id> <任务描述>");
            return;
        }

        var agent = FindAgent(parts[0]);
        if (agent is null)
        {
            _ui.Error($"未找到 Agent: {parts[0]}");
            return;
        }

        var task = string.Join(' ', parts.Skip(1));

        var assignment = _runtime.Assignments.Create(agent, task, mode: "async");
        var personaText = AgentExecutor.ResolvePersonaText(
            agent, _runtime.Personas, _runtime.Templates,
            personaId: null, templateId: null,
            _runtime.CurrentPersonaText, useCommanderPersona: true);
        var finalPrompt = AgentExecutor.BuildFinalPrompt(assignment.Task, personaText);

        _runtime.Assignments.StartAsync(assignment, finalPrompt, null);
        _ui.Ok($"已派发任务给 {agent.Name} (任务 {assignment.AssignmentId}): {task}");
    }

    private void StopAgent(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _ui.Error("用法: /agent stop <id>");
            return;
        }

        var agent = FindAgent(id);
        if (agent is null)
        {
            _ui.Error($"未找到 Agent: {id}");
            return;
        }

        var cancelled = false;
        foreach (var assignment in _runtime.Assignments.All.Where(a =>
                     a.AgentId == agent.Id &&
                     a.Status is SubagentStatus.Queued or SubagentStatus.Running))
        {
            _runtime.Assignments.Cancel(assignment.AssignmentId);
            cancelled = true;
        }

        _ui.Ok(cancelled
            ? $"已请求停止 {agent.Name} 的运行中任务"
            : $"{agent.Name} 当前没有运行中任务");
    }

    private void ShowAgentLogs(string id)
    {
        var agent = string.IsNullOrWhiteSpace(id) ? null : FindAgent(id);
        if (agent is null)
        {
            _ui.Error("未找到 Agent");
            return;
        }

        var logs = _runtime.Assignments.All.Where(a => a.AgentId == agent.Id).TakeLast(20).ToList();
        if (logs.Count == 0)
        {
            _ui.Hint($"{agent.Name} 暂无执行记录");
            return;
        }

        var rows = new List<IReadOnlyList<string>>();
        foreach (var a in logs)
        {
            rows.Add(new[]
            {
                a.CreatedAt.ToString("MM-dd HH:mm"),
                a.AssignmentId,
                a.Status.ToString(),
                CommandUi.Shorten(a.ShortTask, 40)
            });
        }

        _ui.Table(["时间", "任务 ID", "状态", "任务"], rows);
        _ui.Hint("任务输出尾部可用 /agent output <任务ID> 查看");
        _ui.ScrollToBottom();
    }

    private void ShowAssignments()
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var a in _runtime.Assignments.All.Take(30))
        {
            rows.Add(new[]
            {
                a.AgentId,
                a.AssignmentId,
                a.Status.ToString(),
                CommandUi.Shorten(a.ShortTask, 50)
            });
        }

        _ui.Table(["Agent", "任务 ID", "状态", "任务"], rows);
        _ui.ScrollToBottom();
    }

    private CliAgentDefinition? FindAgent(string id) =>
        _runtime.Agents.FirstOrDefault(a =>
            string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Name, id, StringComparison.OrdinalIgnoreCase));
}
