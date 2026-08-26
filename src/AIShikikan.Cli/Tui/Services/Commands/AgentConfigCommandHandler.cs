using AIShikikan.Cli.Tui.Ui;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Engine;

namespace AIShikikan.Cli.Tui.Services.Commands;

/// <summary>Agent 配置管理: 查看配置、重载 Agent 定义、查看/调整会话 Roster 编目。</summary>
public sealed class AgentConfigCommandHandler : ICommandHandler
{
    private readonly CommanderRuntime _runtime;
    private readonly ChatService _chatService;
    private readonly IUiOutput _ui;

    public AgentConfigCommandHandler(CommanderRuntime runtime, ChatService chatService, IUiOutput ui)
    {
        _runtime = runtime;
        _chatService = chatService;
        _ui = ui;
    }

    public bool CanHandle(string command) => command is "agent-config" or "roster";

    public IEnumerable<(string Command, string Help)> HelpRows =>
    [
        ("/agent-config", "查看 Agent 配置文件与分派规则"),
        ("/agent-config reload", "重载 agents.toml 配置"),
        ("/roster", "查看当前会话 Roster 编目"),
        ("/roster enable|disable [grey]<agentId>[/]", "启用/停用 Roster 中的 Agent")
    ];

    public bool TryHandle(string command, string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;

        if (command == "roster")
        {
            switch (sub)
            {
                case "enable":
                    ToggleRosterEntry(parts.Length > 1 ? parts[1] : string.Empty, true);
                    return true;
                case "disable":
                    ToggleRosterEntry(parts.Length > 1 ? parts[1] : string.Empty, false);
                    return true;
                default:
                    ShowRoster();
                    return true;
            }
        }

        if (sub == "reload")
        {
            ReloadConfig();
            return true;
        }

        ShowConfig();
        return true;
    }

    private void ShowConfig()
    {
        var file = AgentConfigService.LoadUserFile();

        _ui.Panel("Agent 配置",
            $"配置文件: {AppPaths.AgentsPath}\n" +
            $"Agent 数量: {file.Agents.Count}\n" +
            $"分派规则: {(string.IsNullOrWhiteSpace(file.Rules) ? "(未设置)" : CommandUi.Shorten(file.Rules, 120))}");
        _ui.Hint("用法: /agent-config reload 重载配置 | 直接编辑 agents.toml 后重载");
    }

    private void ReloadConfig()
    {
        AgentConfigService.Refresh();
        _ui.Ok("已重载 Agent 配置");
        _ui.Hint("新定义将在下一次分派/工具调用时生效; 侧栏列表需重启后刷新");
    }

    private void ShowRoster()
    {
        var sessionId = _chatService.CurrentSession?.Id;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            _ui.Error("当前没有活动会话");
            return;
        }

        var config = RosterConfigService.Load(sessionId);
        var rows = new List<IReadOnlyList<string>>();
        foreach (var e in config.Entries)
        {
            rows.Add(new[]
            {
                e.AgentId,
                MarkupEscape.Escape(e.DisplayText),
                e.Enabled ? "[green]启用[/]" : "[red]停用[/]",
                CommandUi.Shorten(e.Description ?? string.Empty, 50)
            });
        }

        _ui.Table(["Agent", "显示名", "状态", "说明"], rows);
        _ui.Hint($"Roster 文件: {RosterConfigService.GetPath(sessionId)}");
        _ui.Hint("用法: /roster enable|disable <agentId> | 列表来自 agents.toml 与 session 编目");
        _ui.ScrollToBottom();
    }

    private void ToggleRosterEntry(string agentId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            _ui.Error(enabled ? "用法: /roster enable <agentId>" : "用法: /roster disable <agentId>");
            return;
        }

        var sessionId = _chatService.CurrentSession?.Id;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            _ui.Error("当前没有活动会话");
            return;
        }

        var agent = AgentConfigService.Find(agentId, _runtime.Agents);
        if (agent is null)
        {
            _ui.Error($"未找到 Agent: {agentId}");
            return;
        }

        RosterConfigService.SetEnabled(sessionId, agent.Id, enabled);
        if (!enabled)
        {
            RosterConfigService.RemoveIfNoOverride(sessionId, agent.Id);
        }

        _runtime.SetRosterEntries(RosterConfigService.Load(sessionId).Entries);
        _ui.Ok(enabled
            ? $"已启用 Roster 条目: {agent.Id}"
            : $"已停用 Roster 条目: {agent.Id}");
    }
}
