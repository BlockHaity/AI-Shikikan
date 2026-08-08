using System.IO;
using System.Text.Json;
using AgentCommander.Core.Models;

namespace AgentCommander.Core.Services;

public class ChatService
{
    private static readonly string SessionsDir = AppPaths.SessionsDir;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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

    public ChatService()
    {
        LoadAllSessions();
        if (_sessions.Count > 0)
        {
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

        if (CurrentSession?.Id == sessionId)
        {
            CurrentSession = _sessions.Count > 0 ? _sessions[0] : null;
        }

        return true;
    }

    public void SwitchSession(string sessionId)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is not null)
        {
            CurrentSession = session;
        }
    }

    public ChatMessage AddMessage(string sessionId, MessageRole role, string content)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null) throw new ArgumentException($"Session {sessionId} not found");

        var message = new ChatMessage
        {
            Role = role,
            Content = content,
            Timestamp = DateTime.Now
        };

        session.Messages.Add(message);
        session.UpdatedAt = DateTime.Now;

        if (session.Messages.Count == 1 && role == MessageRole.User)
        {
            session.Title = content.Length > 30 ? content[..30] + "..." : content;
        }

        SaveSession(session);
        MessageAdded?.Invoke(this, message);
        return message;
    }

    public void ClearMessages(string sessionId)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null) return;
        session.Messages.Clear();
        session.UpdatedAt = DateTime.Now;
        SaveSession(session);
    }

    private void LoadAllSessions()
    {
        try
        {
            Directory.CreateDirectory(SessionsDir);
            foreach (var file in Directory.GetFiles(SessionsDir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var session = JsonSerializer.Deserialize<ChatSession>(json, JsonOptions);
                    if (session is not null)
                    {
                        _sessions.Add(session);
                    }
                }
                catch
                {
                }
            }

            _sessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        }
        catch
        {
            _sessions.Clear();
        }
    }

    private void SaveSession(ChatSession session)
    {
        try
        {
            Directory.CreateDirectory(SessionsDir);
            var json = JsonSerializer.Serialize(session, JsonOptions);
            var path = Path.Combine(SessionsDir, $"{session.Id}.json");
            File.WriteAllText(path, json);
        }
        catch
        {
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
        catch
        {
        }
    }
}
