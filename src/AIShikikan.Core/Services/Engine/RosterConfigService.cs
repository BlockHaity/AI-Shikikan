using System.Text.Json;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Engine;

/// <summary>Session 级别的 Agent 编目配置, 存储在 sessions/{sessionId}/roster.json。</summary>
public class RosterConfig
{
    public bool RosterEnabled { get; set; } = true;
    public List<AgentRosterEntry> Entries { get; set; } = [];
}

public static class RosterConfigService
{
    public static string GetPath(string sessionId) =>
        Path.Combine(AppPaths.SessionsDir, sessionId, "roster.json");

    public static RosterConfig Load(string sessionId)
    {
        var path = GetPath(sessionId);
        try
        {
            if (File.Exists(path))
            {
                var config = JsonSerializer.Deserialize(
                    File.ReadAllText(path), AppJsonContext.Default.RosterConfig);
                if (config is not null) return config;
            }
        }
        catch
        {
        }

        return new RosterConfig();
    }

    public static void Save(string sessionId, RosterConfig config)
    {
        var path = GetPath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(config, AppJsonContext.Default.RosterConfig));
    }

    public static void AddEntry(string sessionId, string agentId, string display, string description, string? personaId)
    {
        var config = Load(sessionId);
        var existing = config.Entries.Find(e => e.AgentId == agentId);
        if (existing is not null)
        {
            existing.Display = display;
            existing.Description = description;
            existing.PersonaId = personaId;
        }
        else
        {
            config.Entries.Add(new AgentRosterEntry
            {
                AgentId = agentId,
                Display = display,
                Description = description,
                PersonaId = personaId,
                Enabled = true
            });
        }
        Save(sessionId, config);
    }

    public static void RemoveEntry(string sessionId, string agentId)
    {
        var config = Load(sessionId);
        config.Entries.RemoveAll(e => e.AgentId == agentId);
        Save(sessionId, config);
    }

    public static void UpdatePersona(string sessionId, string agentId, string? personaId)
    {
        var config = Load(sessionId);
        var entry = config.Entries.Find(e => e.AgentId == agentId);
        if (entry is not null)
        {
            entry.PersonaId = personaId;
            Save(sessionId, config);
        }
    }

    public static void ToggleEnabled(string sessionId, string agentId)
    {
        var config = Load(sessionId);
        var entry = config.Entries.Find(e => e.AgentId == agentId);
        if (entry is not null)
        {
            entry.Enabled = !entry.Enabled;
            Save(sessionId, config);
        }
    }
}
