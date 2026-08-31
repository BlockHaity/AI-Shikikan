using System.Text.Json;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Usage;

/// <summary>单次 LLM 调用用量记录。</summary>
public sealed class LlmUsageEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string SessionId { get; set; } = string.Empty;
    public string SessionTitle { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }

    /// <summary>命中的缓存输入 token 数(旧数据无此字段时为 0)。</summary>
    public int CachedInputTokens { get; set; }
}

/// <summary>单次子 Agent 调用记录。</summary>
public sealed class AgentCallEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string AgentId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
}

/// <summary>用量统计持久化数据。</summary>
public sealed class UsageData
{
    public List<LlmUsageEntry> LlmEntries { get; set; } = [];
    public List<AgentCallEntry> AgentEntries { get; set; } = [];

    /// <summary>已删除会话 Id 墓碑: 原始用量记录全部保留, 仅会话分布不再展示这些会话。</summary>
    public List<string> DeletedSessionIds { get; set; } = [];

    private const int MaxEntries = 5000;

    public void Trim()
    {
        if (LlmEntries.Count > MaxEntries)
        {
            LlmEntries.RemoveRange(0, LlmEntries.Count - MaxEntries);
        }

        if (AgentEntries.Count > MaxEntries)
        {
            AgentEntries.RemoveRange(0, AgentEntries.Count - MaxEntries);
        }
    }
}

/// <summary>按模型维度的汇总。</summary>
public sealed class ModelUsageStat
{
    public string Model { get; set; } = string.Empty;
    public int Calls { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }

    public int TotalTokens => InputTokens + OutputTokens;
}

/// <summary>按会话维度的汇总。</summary>
public sealed class SessionUsageStat
{
    public string SessionId { get; set; } = string.Empty;
    public string SessionTitle { get; set; } = string.Empty;
    public int Calls { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedTokens { get; set; }

    public int TotalTokens => InputTokens + OutputTokens;
}

/// <summary>按子 Agent 维度的调用统计。</summary>
public sealed class AgentCallStat
{
    public string AgentName { get; set; } = string.Empty;
    public int Calls { get; set; }
    public int Succeeded { get; set; }
}

/// <summary>按天聚合的用量点(用于首页趋势折线图)。</summary>
public sealed class DailyUsageStat
{
    public DateTime Date { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int Calls { get; set; }
}

/// <summary>首页用量统计快照。</summary>
public sealed class UsageSnapshot
{
    public int TodayInputTokens { get; set; }
    public int TodayOutputTokens { get; set; }
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public int TotalLlmCalls { get; set; }
    public int TotalAgentCalls { get; set; }
    public List<ModelUsageStat> ModelStats { get; set; } = [];
    public List<SessionUsageStat> SessionStats { get; set; } = [];
    public List<AgentCallStat> AgentStats { get; set; } = [];

    /// <summary>按天聚合的用量序列(从首个使用日起到今日, 无记录的天为 0)。</summary>
    public List<DailyUsageStat> DailyStats { get; set; } = [];
}

/// <summary>用量统计服务: 记录 LLM 调用与子 Agent 调用, 持久化到 usage.json, 生成汇总快照。</summary>
public static class UsageStatsService
{
    private static readonly object Lock = new();
    private static UsageData? _data;

    private static UsageData Data
    {
        get
        {
            lock (Lock)
            {
                return _data ??= Load();
            }
        }
    }

    private static UsageData Load()
    {
        try
        {
            if (File.Exists(AppPaths.UsageStatsPath))
            {
                var content = File.ReadAllText(AppPaths.UsageStatsPath);
                var data = JsonSerializer.Deserialize(content, AppJsonContext.Default.UsageData);
                if (data is not null)
                {
                    data.Trim();
                    return data;
                }
            }
        }
        catch
        {
        }

        return new UsageData();
    }

    private static void Save()
    {
        try
        {
            var data = _data;
            if (data is null) return;

            data.Trim();
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(AppPaths.UsageStatsPath,
                JsonSerializer.Serialize(data, AppJsonContext.Default.UsageData));
        }
        catch
        {
        }
    }

    /// <summary>记录一次 LLM 调用用量; 输入输出均为 0 时忽略。</summary>
    public static void RecordLlmUsage(
        string sessionId, string sessionTitle, string provider, string model,
        int inputTokens, int outputTokens, int cachedInputTokens = 0)
    {
        if (inputTokens <= 0 && outputTokens <= 0) return;

        lock (Lock)
        {
            Data.LlmEntries.Add(new LlmUsageEntry
            {
                Timestamp = DateTime.Now,
                SessionId = sessionId,
                SessionTitle = sessionTitle,
                Provider = provider,
                Model = model,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CachedInputTokens = cachedInputTokens
            });
            Save();
        }
    }

    /// <summary>记录一次子 Agent 调用(仅终态调用一次)。</summary>
    public static void RecordAgentCall(string agentId, string agentName, bool succeeded)
    {
        lock (Lock)
        {
            Data.AgentEntries.Add(new AgentCallEntry
            {
                Timestamp = DateTime.Now,
                AgentId = agentId,
                AgentName = agentName,
                Succeeded = succeeded
            });
            Save();
        }
    }

    /// <summary>记录会话已删除: 写入墓碑使会话分布不再展示该会话, 原始用量记录全部保留。</summary>
    public static void MarkSessionDeleted(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        lock (Lock)
        {
            var data = Data;
            var exists = false;
            foreach (var id in data.DeletedSessionIds)
            {
                if (string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            if (exists) return;

            data.DeletedSessionIds.Add(sessionId);
            Save();
        }
    }

    /// <summary>聚合指定会话的用量(调用次数 + 输入/输出/缓存 token)。</summary>
    public static SessionUsageStat GetSessionStat(string sessionId)
    {
        lock (Lock)
        {
            var stat = new SessionUsageStat { SessionId = sessionId };
            foreach (var e in Data.LlmEntries)
            {
                if (!string.Equals(e.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) continue;

                stat.Calls++;
                stat.InputTokens += e.InputTokens;
                stat.OutputTokens += e.OutputTokens;
                stat.CachedTokens += e.CachedInputTokens;
            }

            return stat;
        }
    }

    /// <summary>最近一次 LLM 调用的输入 token 数, 即当前上下文占用(含系统提示/历史/工具)。</summary>
    public static int GetLastContextTokens(string sessionId)
    {
        lock (Lock)
        {
            LlmUsageEntry? last = null;
            foreach (var e in Data.LlmEntries)
            {
                if (!string.Equals(e.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) continue;
                if (last is null || e.Timestamp >= last.Timestamp)
                {
                    last = e;
                }
            }

            return last?.InputTokens ?? 0;
        }
    }

    /// <summary>
    /// 生成统计快照(今日/累计 + 按模型/会话/子Agent 分布)。
    /// 已删除会话(墓碑)不再计入会话分布, 但原始用量记录保留并仍计入总量、模型分布与趋势。
    /// </summary>
    public static UsageSnapshot GetSnapshot()
    {
        lock (Lock)
        {
            var data = Data;
            var today = DateTime.Today;
            var snapshot = new UsageSnapshot();

            var deleted = new HashSet<string>(data.DeletedSessionIds, StringComparer.OrdinalIgnoreCase);
            var modelMap = new Dictionary<string, ModelUsageStat>(StringComparer.OrdinalIgnoreCase);
            var sessionMap = new Dictionary<string, SessionUsageStat>(StringComparer.OrdinalIgnoreCase);
            var dayMap = new Dictionary<DateTime, DailyUsageStat>();

            foreach (var e in data.LlmEntries)
            {
                if (e.Timestamp >= today)
                {
                    snapshot.TodayInputTokens += e.InputTokens;
                    snapshot.TodayOutputTokens += e.OutputTokens;
                }

                snapshot.TotalInputTokens += e.InputTokens;
                snapshot.TotalOutputTokens += e.OutputTokens;
                snapshot.TotalLlmCalls++;

                var day = e.Timestamp.Date;
                if (!dayMap.TryGetValue(day, out var ds))
                {
                    dayMap[day] = ds = new DailyUsageStat { Date = day };
                }

                ds.InputTokens += e.InputTokens;
                ds.OutputTokens += e.OutputTokens;
                ds.CachedTokens += e.CachedInputTokens;
                ds.Calls++;

                var model = string.IsNullOrWhiteSpace(e.Model) ? "未知模型" : e.Model;
                if (!modelMap.TryGetValue(model, out var ms))
                {
                    modelMap[model] = ms = new ModelUsageStat { Model = model };
                }

                ms.Calls++;
                ms.InputTokens += e.InputTokens;
                ms.OutputTokens += e.OutputTokens;

                var sessionKey = string.IsNullOrWhiteSpace(e.SessionId) ? "unknown" : e.SessionId;
                // 已删除的会话不再计入会话分布(原始记录保留, 仍计入总量/模型/趋势)
                if (deleted.Contains(sessionKey))
                {
                    continue;
                }

                if (!sessionMap.TryGetValue(sessionKey, out var ss))
                {
                    sessionMap[sessionKey] = ss = new SessionUsageStat
                    {
                        SessionId = e.SessionId,
                        SessionTitle = e.SessionTitle
                    };
                }

                ss.Calls++;
                ss.InputTokens += e.InputTokens;
                ss.OutputTokens += e.OutputTokens;
            }

            snapshot.ModelStats = modelMap.Values
                .OrderByDescending(m => m.InputTokens + m.OutputTokens)
                .Take(5)
                .ToList();

            snapshot.SessionStats = sessionMap.Values
                .OrderByDescending(s => s.InputTokens + s.OutputTokens)
                .Take(5)
                .ToList();

            if (dayMap.Count > 0)
            {
                // 从首个使用日到今日按天补零, 保证折线图时间轴连续
                var first = dayMap.Keys.Min();
                var daily = new List<DailyUsageStat>();
                for (var d = first; d <= today; d = d.AddDays(1))
                {
                    daily.Add(dayMap.TryGetValue(d, out var ds)
                        ? ds
                        : new DailyUsageStat { Date = d });
                }

                snapshot.DailyStats = daily;
            }

            var agentMap = new Dictionary<string, AgentCallStat>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in data.AgentEntries)
            {
                snapshot.TotalAgentCalls++;
                var name = string.IsNullOrWhiteSpace(e.AgentName) ? e.AgentId : e.AgentName;
                if (!agentMap.TryGetValue(name, out var as_))
                {
                    agentMap[name] = as_ = new AgentCallStat { AgentName = name };
                }

                as_.Calls++;
                if (e.Succeeded)
                {
                    as_.Succeeded++;
                }
            }

            snapshot.AgentStats = agentMap.Values
                .OrderByDescending(a => a.Calls)
                .Take(5)
                .ToList();

            return snapshot;
        }
    }
}
