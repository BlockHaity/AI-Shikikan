using System.Text.Json;
using AIShikikan.Core.Logging;
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
    /// <remarks>
    /// 刻意保留墓碑而不是直接删记录: 用户删会话往往只是清聊天窗口, 若连带抹掉 token 统计,
    /// 累计用量/模型分布/趋势图会凭空掉一段, 反而让用户怀疑数据被吞。
    /// 因此删除只影响"会话分布"这一维度(<see cref="UsageStatsService.GetSnapshot"/> 里 continue 跳过),
    /// 总量、模型分布与按天趋势仍按原记录累加。
    /// </remarks>
    public List<string> DeletedSessionIds { get; set; } = [];

    private const int MaxEntries = 5000;

    /// <summary>
    /// 裁剪到上限: 超出后从头(最旧)丢弃。
    ///
    /// <para><b>刻意不区分记录类型</b>: LLM 用量与子 Agent 调用共用同一条时间线,
    /// 若按"类型配额"裁剪, 某类记录会在用户高频使用另一类时被整段清空,
    /// 统计曲线的历史形状会随使用习惯突变。这里统一按总量保留最近 5000 条。</para>
    /// </summary>
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

    /// <summary>
    /// 统计数据是否处于只读保护状态: usage.json 与其 .bak 备份均无法反序列化。
    /// 为 true 时 <see cref="Flush"/> 拒绝写入, 避免空数据永久覆盖用户历史用量。
    /// </summary>
    private static bool _readOnly;

    /// <summary>
    /// 落盘合并窗口(毫秒)。
    ///
    /// <para><b>为什么需要延迟落盘</b>: 一次 LLM 调用就产生一条记录, 而
    /// <see cref="AtomicFile.TryWriteAllText"/> 内部含 <c>Flush(flushToDisk: true)</c> 强制刷盘。
    /// 原实现在每次 <c>RecordLlmUsage</c> / <c>RecordAgentCall</c> / <c>MarkSessionDeleted</c> 时
    /// 于静态 <see cref="Lock"/> <b>内部</b>做 fsync: 既把写放大成"每条记录一次物理刷盘",
    /// 又让持锁时间被磁盘 IO 拉长, 期间所有用量查询(首页快照、状态栏)全部阻塞。</para>
    ///
    /// <para><b>为什么延迟不会丢正确性</b>: 用量统计是纯累计量, 崩溃丢失最后几秒的记录
    /// 只影响精度, 不影响任何"是否该记录"的判定; 内存中的 <c>_data</c> 仍立即更新,
    /// 读路径(<c>GetSnapshot</c> / <c>GetSessionStat</c> / <c>GetLastContextTokens</c>)立刻可见最新值。
    /// 且窗口内的多次记录会合并成一次写, 写频率从"每条记录"降到"每 1.5 秒最多一次"。</para>
    ///
    /// <para><b>退出路径</b>: 进程退出前必须调用 <see cref="Flush"/>(由 Program.cs 退出流程负责),
    /// 否则最多丢失一个窗口内的记录。</para>
    /// </summary>
    private const int SaveDebounceMs = 1500;

    /// <summary>是否存在未落盘的变更(0 = 已落盘)。用 Interlocked 而非 bool, 因为定时器在线程池线程上触发。</summary>
    private static int _dirty;

    /// <summary>串行化所有落盘动作(延迟写与 <see cref="Flush"/> 共用), 保证同一时刻只有一个写者。</summary>
    private static readonly object SaveGate = new();

    /// <summary>延迟落盘计时器(懒创建)。线程池线程为后台线程, 不会阻止进程退出。</summary>
    private static Timer? _saveTimer;

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
        // 损坏时回退 .bak; 两者都不可用则降级为只读空数据, 禁止后续落盘覆盖原始文件
        var ok = AtomicFile.TryReadText(AppPaths.UsageStatsPath, out var content, text =>
        {
            try
            {
                return JsonSerializer.Deserialize(text, AppJsonContext.Default.UsageData) is not null;
            }
            catch
            {
                return false;
            }
        });

        if (!ok)
        {
            // 主文件与 .bak 都读不出来: 说明磁盘上的历史用量已损坏。
            // 此时若继续写入, 内存里的空数据会把用户的原始记录永久覆盖, 因此锁定写入。
            if (File.Exists(AppPaths.UsageStatsPath) || File.Exists(AppPaths.UsageStatsPath + ".bak"))
            {
                Log.Error("Usage", "usage.json 损坏且无可用备份, 已锁定写入以保护历史用量数据");
                _readOnly = true;
            }

            return new UsageData();
        }

        try
        {
            var data = JsonSerializer.Deserialize(content, AppJsonContext.Default.UsageData);
            if (data is null)
            {
                _readOnly = true;
                return new UsageData();
            }

            data.Trim();
            return data;
        }
        catch (Exception ex)
        {
            Log.Error("Usage", ex, "usage.json 解析失败, 已锁定写入");
            _readOnly = true;
            return new UsageData();
        }
    }

    /// <summary>
    /// 标记存在未落盘变更, 并在 <see cref="SaveDebounceMs"/> 后合并写一次。
    /// 内存数据此时已经更新完毕, 落盘只是延后, 不影响任何读路径。
    /// </summary>
    private static void MarkDirty()
    {
        // 只在"首次变脏"时启动计时器: 窗口内的后续记录自动合并进同一次写, 不重复排期。
        // 若改为每次都 Change, 持续调用会把写入永远推迟到"安静下来"为止, 高频使用下反而不落盘。
        if (Interlocked.Exchange(ref _dirty, 1) != 0) return;

        SaveTimer.Change(TimeSpan.FromMilliseconds(SaveDebounceMs), Timeout.InfiniteTimeSpan);
    }

    private static Timer SaveTimer
    {
        get
        {
            var timer = Volatile.Read(ref _saveTimer);
            if (timer is not null) return timer;

            // 初始为"永不触发", 只由 MarkDirty 显式 Change 排期
            var created = new Timer(OnSaveTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return Interlocked.CompareExchange(ref _saveTimer, created, null) ?? created;
        }
    }

    private static void OnSaveTimer(object? _)
    {
        try
        {
            Flush();
        }
        catch (Exception ex)
        {
            Log.Error("Usage", ex, "延迟落盘用量统计失败");
        }
    }

    /// <summary>
    /// 立即把内存中的用量统计落盘(无变更时直接返回)。
    /// <para><b>进程退出前必须调用</b>(Program.cs 退出路径), 否则崩溃/强杀会丢掉
    /// 最近一个 <see cref="SaveDebounceMs"/> 窗口内的记录。</para>
    /// </summary>
    public static void Flush()
    {
        lock (SaveGate)
        {
            // 必须"先清脏标记再取快照": 若顺序相反, 与取快照并发到达的新记录会先置脏、
            // 随后被这里的清零抹掉, 这些记录就再没有落盘时机(静默丢数据)。
            if (Interlocked.Exchange(ref _dirty, 0) == 0) return;

            var json = SerializeSnapshot();
            if (json is null) return;

            // 磁盘 IO 刻意放在 Lock 外: fsync 是毫秒级阻塞操作, 持锁做会让
            // 每次落盘窗口都卡住所有用量读路径(这正是 E20 的根因)。
            // 序列化快照已在锁内完成, 因此与并发记录的合并是安全的。
            Directory.CreateDirectory(AppPaths.DataDir);
            AtomicFile.TryWriteAllText(AppPaths.UsageStatsPath, json, "usage.json");
        }
    }

    /// <summary>在锁内取一份可落盘的 JSON 快照; 返回 null 表示当前禁止写入或无数据。</summary>
    private static string? SerializeSnapshot()
    {
        lock (Lock)
        {
            // 历史数据损坏时拒绝写入: 否则空数据会永久覆盖用户的用量记录
            if (_readOnly)
            {
                Log.Warn("Usage", "统计数据处于只读保护状态, 跳过写入");
                return null;
            }

            var data = _data;
            if (data is null) return null;

            data.Trim();
            return JsonSerializer.Serialize(data, AppJsonContext.Default.UsageData);
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

            // 落盘延后: 内存已是最新(读路径立刻可见), 磁盘写入合并到窗口末尾统一做
            MarkDirty();
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

            MarkDirty();
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
            MarkDirty();
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
                // 刻意 continue: 总量/按天趋势/模型分布在上面已累加完毕, 这里 continue 只跳过
                // "会话分布"这一个维度 —— 删会话不应让累计用量与趋势曲线凭空掉一段(见 DeletedSessionIds)
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
                // 从首个使用日到今日按天补零, 保证折线图时间轴连续。
                // 刻意不做上限截断: 补零区间一旦被裁剪, 折线图 x 轴就会"跳段",
                // 读者会误以为那段时间没有用量。上界由 Trim() 的 5000 条记录间接限制
                // (单条记录一天也只贡献一个点), 故点数天然有界。
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
