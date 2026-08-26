using AIShikikan.Cli.Tui.Ui;
using AIShikikan.Core.Services;
using CoreMessageRole = AIShikikan.Core.Models.MessageRole;

namespace AIShikikan.Cli.Tui.Services.Commands;

/// <summary>会话管理: 列表/新建/切换/重命名/删除会话。</summary>
public sealed class SessionCommandHandler : ICommandHandler
{
    private readonly ChatService _chatService;
    private readonly IUiOutput _ui;

    public SessionCommandHandler(ChatService chatService, IUiOutput ui)
    {
        _chatService = chatService;
        _ui = ui;
    }

    public bool CanHandle(string command) => command is "session" or "sessions";

    public IEnumerable<(string Command, string Help)> HelpRows =>
    [
        ("/session [grey]<id>[/]", "列出会话 / 切换会话"),
        ("/session new", "新建会话"),
        ("/session rename [grey]<id> <名称>[/]", "重命名会话"),
        ("/session delete [grey]<id>[/]", "删除会话")
    ];

    public bool TryHandle(string command, string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            ListSessions();
            return true;
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "new":
            case "create":
                NewSession();
                return true;
            case "rename":
                RenameSession(parts.Skip(1).ToArray());
                return true;
            case "delete":
            case "rm":
                DeleteSession(parts.Length > 1 ? parts[1] : string.Empty);
                return true;
            default:
                SwitchSession(parts[0]);
                return true;
        }
    }

    private void ListSessions()
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var s in _chatService.Sessions)
        {
            var current = string.Equals(s.Id, _chatService.CurrentSession?.Id, StringComparison.OrdinalIgnoreCase)
                ? "[green]◀[/]"
                : "";
            rows.Add(new[] { s.Id, current, CommandUi.Shorten(s.DisplayTitle, 40) });
        }

        _ui.Table(["ID", "当前", "标题"], rows);
        _ui.Hint("用法: /session <id> 切换 | /session new | /session rename <id> <名称> | /session delete <id>");
        _ui.ScrollToBottom();
    }

    private void NewSession()
    {
        var session = _chatService.CreateSession();
        _ui.Clear();
        _ui.Ok($"已新建会话: {session.Id}");
        _ui.Hint("直接输入消息开始新对话");
    }

    private void RenameSession(string[] parts)
    {
        if (parts.Length < 2)
        {
            _ui.Error("用法: /session rename <id> <新名称>");
            return;
        }

        if (_chatService.RenameSession(parts[0], string.Join(' ', parts.Skip(1))))
        {
            _ui.Ok($"已重命名会话: {parts[0]}");
        }
        else
        {
            _ui.Error($"未找到会话: {parts[0]}");
        }
    }

    private void DeleteSession(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _ui.Error("用法: /session delete <id>");
            return;
        }

        if (_chatService.DeleteSession(id))
        {
            _ui.Ok($"已删除会话: {id}");
        }
        else
        {
            _ui.Error($"未找到会话: {id}");
        }
    }

    private void SwitchSession(string id)
    {
        var target = _chatService.Sessions.FirstOrDefault(s => s.Id == id);
        if (target is null)
        {
            _ui.Error($"未找到会话: {id}");
            return;
        }

        _chatService.SwitchSession(id);

        _ui.Clear();
        foreach (var message in target.Messages)
        {
            _ui.Message(ToCliRole(message.Role), message.Content);
        }

        _ui.Ok($"已切换会话: {target.Id}");
        _ui.ScrollToBottom();
    }

    private static AIShikikan.Cli.Models.MessageRole ToCliRole(CoreMessageRole role) => role switch
    {
        CoreMessageRole.User => AIShikikan.Cli.Models.MessageRole.User,
        CoreMessageRole.Assistant => AIShikikan.Cli.Models.MessageRole.Assistant,
        CoreMessageRole.System => AIShikikan.Cli.Models.MessageRole.System,
        CoreMessageRole.Tool => AIShikikan.Cli.Models.MessageRole.Tool,
        _ => AIShikikan.Cli.Models.MessageRole.System
    };
}
