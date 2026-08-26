using AIShikikan.Cli.Models;
using AIShikikan.Cli.Tui.Services.Commands;
using AIShikikan.Cli.Tui.Ui;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Personas;

namespace AIShikikan.Cli.Tui.Services;

/// <summary>命令分发中心: 将 "/" 前缀命令路由到各领域处理器。
/// 每个处理器负责一组相关命令, 便于维护与扩展。输出统一渲染进 TUI 聊天日志。</summary>
public class CommandService
{
    private readonly List<Message> _messages;
    private readonly CommanderRuntime _runtime;
    private readonly ChatService _chatService;
    private readonly IUiOutput _ui;
    private readonly List<ICommandHandler> _handlers;

    /// <summary>核心命令帮助行(含用法说明, 用于 /help 表格)。</summary>
    private static readonly IReadOnlyList<(string Command, string Help)> CoreCommands =
    [
        ("/help", "显示帮助信息"),
        ("/commands", "显示命令列表(同 /help)"),
        ("/clear", "清除对话历史"),
        ("/persona <id/名称|import <路径>>", "列出/切换专家人格, import 导入专家文件"),
        ("/templates", "列出专家模板"),
        ("/tools", "列出当前可用工具"),
        ("/quit, /exit", "退出 TUI")
    ];

    /// <summary>全部可用命令(核心 + 各领域处理器), 供自动补全使用: (命令名, 说明)。</summary>
    public IReadOnlyList<(string Command, string Help)> AllCommands { get; }

    public CommandService(List<Message> messages, CommanderRuntime runtime, ChatService chatService, IUiOutput ui)
    {
        _messages = messages;
        _runtime = runtime;
        _chatService = chatService;
        _ui = ui;
        _handlers =
        [
            new SessionCommandHandler(chatService, ui),
            new AgentCommandHandler(runtime, ui),
            new AgentConfigCommandHandler(runtime, chatService, ui),
            new GitCommandHandler(runtime, ui),
            new StatusCommandHandler(runtime, chatService, ui),
            new UsageCommandHandler(ui),
            new ProviderCommandHandler(runtime, ui)
        ];

        AllCommands = ExtractCommands(CoreCommands)
            .Concat(_handlers.SelectMany(h => ExtractCommands(h.HelpRows)))
            .DistinctBy(c => c.Command, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>从帮助行提取补全用的命令名: 取 "/" 开头的单词片段, 忽略 "<占位符>" 与用法说明。</summary>
    private static IEnumerable<(string Command, string Help)> ExtractCommands(
        IEnumerable<(string Command, string Help)> rows)
    {
        foreach (var (cmd, help) in rows)
        {
            foreach (var token in cmd.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
            {
                var name = token.Trim();
                if (name.StartsWith('/') && !name.Contains('<'))
                {
                    yield return (name, help);
                }
            }
        }
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
            case "/commands":
                ShowHelp();
                return true;
            case "/clear":
                ClearMessages();
                return true;
            case "/quit":
            case "/exit":
                return true;
            case "/persona":
                ShowPersonas(args);
                return true;
            case "/personas":
                ShowPersonas(string.Empty);
                return true;
            case "/templates":
                ShowTemplates();
                return true;
            case "/tools":
                ShowTools();
                return true;
        }

        foreach (var handler in _handlers)
        {
            if (handler.CanHandle(command[1..]) && handler.TryHandle(command[1..], args))
            {
                return true;
            }
        }

        _ui.Error($"未知命令: {command}");
        _ui.Hint("输入 /help 查看可用命令");
        return true;
    }

    private void ShowHelp()
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var (cmd, help) in CoreCommands)
        {
            rows.Add([cmd, help]);
        }

        foreach (var handler in _handlers)
        {
            foreach (var (cmd, help) in handler.HelpRows)
            {
                rows.Add([cmd, help]);
            }
        }

        _ui.Table(["命令", "说明"], rows);
        _ui.ScrollToBottom();
    }

    private void ClearMessages()
    {
        _messages.Clear();
        _ui.Clear();

        // 与 GUI 清空行为一致: 同时清空会话存储与引擎对话, 避免换界面后历史残留
        var session = _chatService.CurrentSession;
        if (session is not null)
        {
            _chatService.ClearMessages(session.Id);
        }

        _runtime.Engine.ClearConversation();
        _ui.Ok("对话历史已清除");
    }

    private void ShowPersonas(string arg)
    {
        if (!string.IsNullOrWhiteSpace(arg))
        {
            var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var sub = parts[0];

            if (string.Equals(sub, "import", StringComparison.OrdinalIgnoreCase))
            {
                ImportPersonaFile(parts.Length > 1 ? parts[1].Trim() : string.Empty);
                return;
            }

            var persona = PersonaService.Find(arg, _runtime.Personas);
            if (persona is null)
            {
                _ui.Error($"未找到人格: {arg}");
                _ui.Hint("用法: /persona <id|名称> 切换主 Agent 人格 | /persona import <文件路径> 导入专家文件");
            }
            else
            {
                _runtime.Engine.SetPersonaText(persona.SystemPrompt);
                _ui.Ok($"已切换主 Agent 人格: {persona.Name} ({persona.Id})");
            }

            return;
        }

        var rows = new List<IReadOnlyList<string>>();
        foreach (var p in _runtime.Personas)
        {
            rows.Add([p.Id, p.Name, CommandUi.Shorten(p.Description ?? string.Empty)]);
        }

        _ui.Table(["ID", "名称", "说明"], rows);
        _ui.Hint("用法: /persona <id|名称> 切换主 Agent 人格 | /persona import <文件路径> 导入专家文件");
    }

    private void ImportPersonaFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _ui.Error("用法: /persona import <专家文件.md>");
            return;
        }

        try
        {
            var (persona, error) = PersonaService.ImportFile(path);
            if (persona is null)
            {
                _ui.Error(error ?? "导入失败");
            }
            else
            {
                _ui.Ok($"✔ 已导入专家: {persona.Name} ({persona.Id})");
                _runtime.Engine.SetPersonaText(persona.SystemPrompt);
                _ui.Hint($"已切换主 Agent 人格为 {persona.Name}");
            }
        }
        catch (Exception ex)
        {
            _ui.Error(ex.Message);
        }
    }

    private void ShowTemplates()
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var t in _runtime.Templates)
        {
            rows.Add([t.Id, t.Name, CommandUi.Shorten(t.SystemPrompt, 80)]);
        }

        _ui.Table(["ID", "名称", "说明"], rows);
    }

    private void ShowTools()
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var tool in _runtime.Registry.All.OrderBy(t => t.Name))
        {
            rows.Add(
            [
                tool.Name,
                tool.RequiresApproval ? "[yellow]需确认[/]" : "[green]自动[/]",
                CommandUi.Shorten(tool.Description, 80)
            ]);
        }

        _ui.Table(["名称", "权限", "说明"], rows);
    }
}
