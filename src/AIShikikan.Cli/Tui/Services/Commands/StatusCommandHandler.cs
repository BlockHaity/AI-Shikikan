using AIShikikan.Cli.Tui.Ui;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Usage;

namespace AIShikikan.Cli.Tui.Services.Commands;

/// <summary>状态总览: 当前模型/Provider、人格、会话、Git 分支、上下文占用与运行中任务。</summary>
public sealed class StatusCommandHandler : ICommandHandler
{
    private readonly CommanderRuntime _runtime;
    private readonly ChatService _chatService;
    private readonly IUiOutput _ui;

    public StatusCommandHandler(CommanderRuntime runtime, ChatService chatService, IUiOutput ui)
    {
        _runtime = runtime;
        _chatService = chatService;
        _ui = ui;
    }

    public bool CanHandle(string command) => command is "status" or "state";

    public IEnumerable<(string Command, string Help)> HelpRows =>
    [
        ("/status", "查看当前状态(模型/人格/Git/上下文)")
    ];

    public bool TryHandle(string command, string args)
    {
        var provider = _runtime.Llm.GetProvider();
        var model = _runtime.Llm.ResolveModel();
        var session = _chatService.CurrentSession;
        var running = _runtime.Assignments.All.Count(a =>
            a.Status is SubagentStatus.Queued or SubagentStatus.Running);
        var branch = _runtime.Git.IsRepoAvailable ? _runtime.Git.CurrentBranch() : null;
        var contextTokens = session is null ? 0 : UsageStatsService.GetLastContextTokens(session.Id);

        _ui.Panel("当前状态",
            $"模型: {model ?? "(未配置)"}    Provider: {provider?.Id ?? "(未配置)"}\n" +
            $"人格: {_runtime.CurrentPersonaText?.Split('\n').FirstOrDefault()?.Trim() ?? "(默认)"}\n" +
            $"会话: {session?.DisplayTitle ?? "(无)"}    上下文: {contextTokens:N0} tokens\n" +
            $"Git 分支: {branch ?? "(非仓库)"}    工作中任务: {running}");
        _ui.Hint("用法: /provider 管理 Provider | /model <模型> 切换模型 | /persona 切换人格");
        _ui.ScrollToBottom();
        return true;
    }
}
