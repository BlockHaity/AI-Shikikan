using System.IO;
using System.Text.Json;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;
using AIShikikan.Core.Serialization;
using AIShikikan.Core.Services.Usage;

namespace AIShikikan.Core.Services;

public class ChatService
{
    private static readonly string SessionsDir = AppPaths.SessionsDir;

    private readonly List<ChatSession> _sessions = [];
    private ChatSession? _currentSession;

    public IReadOnlyList<ChatSession> Sessions => _sessions.AsReadOnly();

    public ChatSession? CurrentSession
    {
        get => _currentSession;
        private set
        {
            _currentSession = value;
            CurrentSessionChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<ChatSession?>? CurrentSessionChanged;
    public event EventHandler<ChatMessage>? MessageAdded;
    public event EventHandler<ChatSession>? SessionRenamed;
    public event EventHandler<ChatSession>? SessionWorkDirChanged;

    public ChatService()
    {
        LoadAllSessions();
        if (_sessions.Count > 0)
        {
            EnsureLoaded(_sessions[0]);
            CurrentSession = _sessions[0];
        }
    }

    public ChatSession CreateSession(string? title = null)
    {
        var session = new ChatSession
        {
            Title = title ?? $"Session {_sessions.Count + 1}",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        _sessions.Insert(0, session);
        CurrentSession = session;
        SaveSession(session);
        return session;
    }

    public bool DeleteSession(string sessionId)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null) return false;

        _sessions.Remove(session);
        DeleteSessionFile(sessionId);
        // 主页会话分布不再展示该会话(原始用量记录保留)
        UsageStatsService.MarkSessionDeleted(sessionId);

        if (CurrentSession?.Id == sessionId)
        {
            if (_sessions.Count > 0)
            {
                EnsureLoaded(_sessions[0]);
            }

            CurrentSession = _sessions.Count > 0 ? _sessions[0] : null;
        }

        return true;
    }

    public void SwitchSession(string sessionId)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is not null)
        {
            EnsureLoaded(session);
            CurrentSession = session;
        }
    }

    public bool RenameSession(string sessionId, string newTitle)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null) return false;

        var title = newTitle?.Trim();
        if (string.IsNullOrEmpty(title)) return false;

        EnsureLoaded(session); // 整文件写回, 未加载会覆盖丢消息
        session.Title = title;
        session.UpdatedAt = DateTime.Now;
        SaveSession(session);
        SessionRenamed?.Invoke(this, session);
        return true;
    }

    /// <summary>记录会话绑定的工作目录(发生变化才落盘), 供按目录整理会话与切换会话时恢复。</summary>
    public void SetSessionWorkDir(string sessionId, string? workDir)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null) return;

        var dir = workDir?.Trim() ?? string.Empty;
        if (string.Equals(session.WorkDir, dir, StringComparison.Ordinal)) return;

        session.WorkDir = dir;
        SaveSession(session);
        SessionWorkDirChanged?.Invoke(this, session);
    }

    public ChatMessage AddMessage(string sessionId, MessageRole role, string content)
    {
        return AddMessage(sessionId, role, [new MessageSegment { Kind = MessageSegmentKind.Text, Content = content }], content);
    }

    public ChatMessage AddMessage(string sessionId, MessageRole role, IReadOnlyList<MessageSegment> segments)
    {
        var plain = string.Join("\n", segments.Where(s => s.Kind == MessageSegmentKind.Text).Select(s => s.Content));
        return AddMessage(sessionId, role, segments, plain);
    }

    private ChatMessage AddMessage(string sessionId, MessageRole role, IReadOnlyList<MessageSegment> segments, string plainText)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null) throw new ArgumentException($"Session {sessionId} not found");

        EnsureLoaded(session); // 写回前先加载, 否则会用空消息覆盖已有会话文件

        var message = new ChatMessage
        {
            Role = role,
            Segments = segments.ToList(),
            Timestamp = DateTime.Now
        };

        session.Messages.Add(message);
        session.UpdatedAt = DateTime.Now;

        if (session.Messages.Count == 1 && role == MessageRole.User && !string.IsNullOrEmpty(plainText))
        {
            session.Title = plainText.Length > 30 ? plainText[..30] + "..." : plainText;
        }

        SaveSession(session);
        MessageAdded?.Invoke(this, message);
        return message;
    }

    public void ClearMessages(string sessionId)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null) return;
        EnsureLoaded(session);
        session.Messages.Clear();
        session.UpdatedAt = DateTime.Now;
        SaveSession(session);
    }

    /// <summary>删除一条用户消息及其后紧跟的助手回复(到下一条用户消息前)。返回是否删除。</summary>
    public bool DeleteMessage(string sessionId, string messageId)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null) return false;
        EnsureLoaded(session);

        var idx = session.Messages.FindIndex(m => m.Id == messageId);
        if (idx < 0) return false;

        var removeCount = 1;
        while (idx + removeCount < session.Messages.Count &&
               session.Messages[idx + removeCount].Role != MessageRole.User)
        {
            removeCount++;
        }

        session.Messages.RemoveRange(idx, removeCount);
        session.UpdatedAt = DateTime.Now;
        SaveSession(session);
        return true;
    }

    /// <summary>fork 编辑用户消息: 更新其文本并截断其后的全部助手回复(保留之前的对话作为分支基础)。
    /// 返回是否成功。</summary>
    public bool EditUserMessage(string sessionId, string messageId, string newText)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null) return false;
        EnsureLoaded(session);

        var idx = session.Messages.FindIndex(m => m.Id == messageId);
        if (idx < 0 || session.Messages[idx].Role != MessageRole.User) return false;

        // 截断其后所有回复
        session.Messages.RemoveRange(idx + 1, session.Messages.Count - idx - 1);

        // 更新用户消息文本: 保留原有图片分段, 替换/追加文本分段
        var msg = session.Messages[idx];
        var images = msg.Segments.Where(s => s.Kind == MessageSegmentKind.Image).ToList();
        msg.Segments =
        [
            .. images,
            new MessageSegment { Kind = MessageSegmentKind.Text, Content = newText }
        ];
        session.UpdatedAt = DateTime.Now;
        SaveSession(session);
        return true;
    }

    /// <summary>懒加载: 启动仅解析每个会话的轻量元数据(id/title/时间/消息条数),
    /// 消息正文延迟到打开会话或写回时再反序列化, 避免启动卡顿。</summary>
    private void LoadAllSessions()
    {
        try
        {
            Directory.CreateDirectory(SessionsDir);
            foreach (var file in Directory.GetFiles(SessionsDir, "*.json"))
            {
                try
                {
                    var session = ReadMetadata(file);
                    if (session is not null)
                    {
                        _sessions.Add(session);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Session", ex, $"会话文件元数据解析失败(已跳过): {file}");
                }
            }

            Log.Info("Session", $"加载 {_sessions.Count} 个会话(元数据)");
            _sessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        }
        catch (Exception ex)
        {
            Log.Error("Session", ex, "会话目录读取失败");
            _sessions.Clear();
        }
    }

    /// <summary>只读根级字段与消息条数, 不反序列化消息内容。</summary>
    private static ChatSession? ReadMetadata(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        var root = doc.RootElement;

        if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var id = idEl.GetString();
        if (string.IsNullOrEmpty(id)) return null;

        var session = new ChatSession
        {
            Id = id,
            Title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? "New Session"
                : "New Session",
            WorkDir = root.TryGetProperty("workDir", out var w) && w.ValueKind == JsonValueKind.String
                ? w.GetString() ?? string.Empty
                : string.Empty,
            UpdatedAt = root.TryGetProperty("updatedAt", out var u) && u.TryGetDateTime(out var ut)
                ? ut
                : DateTime.Now
        };

        if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
        {
            session.MetadataMessageCount = msgs.GetArrayLength();
        }

        session.IsLoaded = false;
        return session;
    }

    /// <summary>确保会话消息已从文件加载(幂等)。加载后置 IsLoaded, 后续写回安全。</summary>
    private void EnsureLoaded(ChatSession session)
    {
        if (session.IsLoaded) return;
        session.IsLoaded = true;

        try
        {
            var path = Path.Combine(SessionsDir, $"{session.Id}.json");
            if (!File.Exists(path))
            {
                session.Messages = [];
                return;
            }

            var full = JsonSerializer.Deserialize(
                File.ReadAllText(path), AppJsonContext.Default.ChatSession);
            if (full is not null)
            {
                session.Messages = full.Messages;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Session", ex, $"会话消息加载失败: {session.Id}");
            session.Messages = [];
        }
    }

    private void SaveSession(ChatSession session)
    {
        try
        {
            Directory.CreateDirectory(SessionsDir);
            var json = JsonSerializer.Serialize(session, AppJsonContext.Default.ChatSession);
            var path = Path.Combine(SessionsDir, $"{session.Id}.json");
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log.Warn("Session", ex, $"会话保存失败: {session.Id}");
        }
    }

    private void DeleteSessionFile(string sessionId)
    {
        try
        {
            var path = Path.Combine(SessionsDir, $"{sessionId}.json");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Session", ex, $"会话文件删除失败: {sessionId}");
        }
    }
}
