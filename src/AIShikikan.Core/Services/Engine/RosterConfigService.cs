using System.Text.Json;
using AIShikikan.Core.Models;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Engine;

/// <summary>Session 级别的 Agent 编目配置, 存储在 sessions/{sessionId}/roster.json。</summary>
public class RosterConfig
{
    public List<AgentRosterEntry> Entries { get; set; } = [];
}

public static class RosterConfigService
{
    public static string GetPath(string sessionId) =>
        Path.Combine(AppPaths.SessionsDir, sessionId, "roster.json");

    public static RosterConfig Load(string sessionId)
    {
        var path = GetPath(sessionId);

        // 损坏时回退 .bak: Load 失败会返回空配置, 而 Save 会把空配置写回,
        // 若不做回退, 用户的一次开关切换就把整份 roster 永久清空。
        if (AtomicFile.TryReadText(path, out var content, ValidateRosterJson))
        {
            try
            {
                var config = JsonSerializer.Deserialize(content, AppJsonContext.Default.RosterConfig);
                if (config is not null) return config;
            }
            catch
            {
            }
        }

        return new RosterConfig();
    }

    /// <summary>
    /// 读取会话 Roster 条目, 并显式区分「从未配置」与「配置为空」:
    /// <list type="bullet">
    /// <item>roster.json <b>不存在</b> → 返回 <c>null</c>, 语义为"无限制"(下层
    /// <c>RosterBuilder.Build</c> 据此列出全部子代理);</item>
    /// <item>roster.json 存在但 <c>Entries</c> 为空 → 返回<b>空表</b>, 语义为"用户已清空全部子代理"。</item>
    /// </list>
    ///
    /// <para>为什么必须区分: AgentPanel 的开关只在用户实际改动时才写 roster.json,
    /// 全新会话没有该文件。若把"文件不存在"也当成"空表", 全新会话将一个子代理都拿不到。</para>
    /// </summary>
    public static IReadOnlyList<AgentRosterEntry>? LoadEntriesOrNull(string sessionId)
    {
        var path = GetPath(sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        return Load(sessionId).Entries;
    }

    /// <summary>
    /// 校验 roster.json 是否能反序列化出非 null 对象。
    ///
    /// <para>不变式(勿改): 判据刻意不含 "Entries 非空" —— 用户主动清空全部子代理是合法状态,
    /// 空表必须原样通过校验。若这里加上"Entries 非空", 一次空表保存就会被判为"损坏"
    /// 而回退 .bak, 下一次 Save 再把上一份内容写回去, 空表永远存不下来。</para>
    /// </summary>
    private static bool ValidateRosterJson(string content)
    {
        try
        {
            return JsonSerializer.Deserialize(content, AppJsonContext.Default.RosterConfig) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static void Save(string sessionId, RosterConfig config)
    {
        var path = GetPath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // 原子写: tmp -> 强制刷盘 -> rename 覆盖, 并留 .bak 供 Load 回退
        AtomicFile.TryWriteAllText(
            path,
            JsonSerializer.Serialize(config, AppJsonContext.Default.RosterConfig),
            $"roster.json({sessionId})");
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

    public static void SetEnabled(string sessionId, string agentId, bool enabled)
    {
        var config = Load(sessionId);
        var entry = config.Entries.Find(e => e.AgentId == agentId);
        if (entry is null)
        {
            config.Entries.Add(new AgentRosterEntry
            {
                AgentId = agentId,
                Display = string.Empty,
                Description = string.Empty,
                Enabled = enabled
            });
        }
        else
        {
            entry.Enabled = enabled;
        }

        Save(sessionId, config);
    }

    /// <summary>设置该子代理在当前会话的输出压缩开关。</summary>
    public static void SetCompact(string sessionId, string agentId, bool compact)
    {
        var config = Load(sessionId);
        var entry = config.Entries.Find(e => e.AgentId == agentId);
        if (entry is null)
        {
            config.Entries.Add(new AgentRosterEntry
            {
                AgentId = agentId,
                Display = string.Empty,
                Description = string.Empty,
                Enabled = true,
                CompactEnabled = compact
            });
        }
        else
        {
            entry.CompactEnabled = compact;
        }

        Save(sessionId, config);
    }

    /// <summary>设置该子代理是否在 Plan 模式中使用(会话级)。</summary>
    public static void SetUseInPlanMode(string sessionId, string agentId, bool useInPlanMode)
    {
        var config = Load(sessionId);
        var entry = config.Entries.Find(e => e.AgentId == agentId);
        if (entry is null)
        {
            config.Entries.Add(new AgentRosterEntry
            {
                AgentId = agentId,
                Display = string.Empty,
                Description = string.Empty,
                Enabled = true,
                UseInPlanMode = useInPlanMode
            });
        }
        else
        {
            entry.UseInPlanMode = useInPlanMode;
        }

        Save(sessionId, config);
    }

    /// <summary>删除仅用于开关状态的会话条目(未自定义描述/专家/压缩/Plan 模式时)。返回是否删除了条目。</summary>
    public static bool RemoveIfNoOverride(string sessionId, string agentId)
    {
        var config = Load(sessionId);
        var entry = config.Entries.Find(e => e.AgentId == agentId);
        if (entry is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(entry.Description) || !string.IsNullOrWhiteSpace(entry.PersonaId)
            || entry.CompactEnabled || entry.UseInPlanMode)
        {
            return false;
        }

        config.Entries.RemoveAll(e => e.AgentId == agentId);
        Save(sessionId, config);
        return true;
    }
}
