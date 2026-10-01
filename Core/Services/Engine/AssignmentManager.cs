using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Serialization;
using AIShikikan.Core.Services.Agents;

namespace AIShikikan.Core.Services.Engine;

public enum SubagentStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
    TimedOut
}

public class Assignment
{
    public string AssignmentId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string AgentId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string Task { get; set; } = string.Empty;
    public string? TemplateId { get; set; }
    public string? PersonaId { get; set; }

    /// <summary>已废弃: 早期用 <c>ac/&lt;stepId&gt;</c> 分支给每个子代理做检查点, 该机制已下线
    /// (子代理统一在当前分支就地工作, 回滚入口是"每条用户消息"的检查点), 因此本字段再无赋值点。
    /// 保留仅为兼容旧 <c>assignments/*.json</c> 的反序列化(旧文件里有这个键), 不参与任何逻辑。</summary>
    [Obsolete("子代理检查点机制已废弃, StepId 恒为空串; 回滚请用每条用户消息的检查点。")]
    public string StepId { get; set; } = string.Empty;

    public string? WorkingDirectory { get; set; }

    /// <summary>是否以 Plan 模式启动(追加 Agent 的 plan_args)。</summary>
    public bool PlanMode { get; set; }
    public SubagentStatus Status { get; set; } = SubagentStatus.Queued;
    public string? OutputTail { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }

    [JsonIgnore]
    public string Display => $"[{AgentName}] {ShortTask} {Status}";

    [JsonIgnore]
    public string ShortTask => Task.Length <= 40 ? Task : Task[..40] + "...";
}

/// <summary>分派管理: 记录、后台执行、状态跟踪与持久化。Git 检查点由外层会话/工作区服务负责。</summary>
public sealed class AssignmentManager
{
    private readonly Dictionary<string, Assignment> _assignments = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public AssignmentManager()
    {
        Directory.CreateDirectory(AppPaths.AssignmentsDir);
        foreach (var file in Directory.GetFiles(AppPaths.AssignmentsDir, "*.json"))
        {
            try
            {
                // 用 TryReadText + validate 而非 TryReadRaw: 后者不做 .bak 回退, 主文件半截时
                // 这条分派记录就彻底读不出来。分派记录只是历史信息, 但它同时是"哪些子代理在跑/跑完"
                // 的唯一凭据, 读不出来会让 Agent 面板状态与实际不符。
                if (!AtomicFile.TryReadText(file, out var raw, t => t.Contains("\"assignmentId\"", StringComparison.Ordinal)))
                {
                    continue;
                }

                var a = JsonSerializer.Deserialize(raw, AppJsonContext.Default.Assignment);
                if (a is not null && !string.IsNullOrWhiteSpace(a.AssignmentId))
                {
                    _assignments[a.AssignmentId] = a;
                }
            }
            catch (Exception ex)
            {
                // 原先这里是空 catch: 某条分派记录读不出来时零痕迹, 排障时完全看不出
                // Agent 面板为什么少了一条记录
                Log.Warn("Assignments", ex, $"读取分派记录失败: {Path.GetFileName(file)}");
            }
        }
    }

    public event Action<Assignment>? AssignmentChanged;

    public IReadOnlyList<Assignment> All
    {
        get
        {
            lock (_lock)
            {
                return _assignments.Values
                    .OrderByDescending(a => a.CreatedAt)
                    .ToList();
            }
        }
    }

    public Assignment? Get(string assignmentId)
    {
        // 与 All / Create 走同一把锁: 子代理并发跑时 Dictionary 正在被写入,
        // 无锁读取在扩容瞬间可能抛或读到半构造对象
        lock (_lock)
        {
            return _assignments.TryGetValue(assignmentId, out var a) ? a : null;
        }
    }

    public Assignment Create(CliAgentDefinition agent, string task,
        string? templateId = null, string? personaId = null,
        string? workingDirectory = null, bool planMode = false)
    {
        var assignment = new Assignment
        {
            AgentId = agent.Id,
            AgentName = agent.Display,
            Task = task,
            TemplateId = templateId,
            PersonaId = personaId,
            WorkingDirectory = workingDirectory ?? string.Empty,
            PlanMode = planMode
        };

        lock (_lock)
        {
            _assignments[assignment.AssignmentId] = assignment;
        }

        Save(assignment);
        AssignmentChanged?.Invoke(assignment);
        return assignment;
    }

    /// <summary>常规(同步)模式: 运行子 Agent → 落终态并返回完整结果。</summary>
    public async Task<(Assignment Assignment, CliAgentRunResult Run)> RunSyncAsync(
        Assignment assignment, string finalPrompt,
        IProgress<string>? progressOutput, CancellationToken ct = default)
    {
        UpdateStatus(assignment, SubagentStatus.Running);
        Log.Info("Agent", $"子Agent 启动: {assignment.AgentName} (assignment={assignment.AssignmentId}) 任务: {assignment.ShortTask}");

        try
        {
            var result = await RunCliAsync(assignment, finalPrompt, progressOutput, ct);

            assignment.ExitCode = result.ExitCode;
            assignment.OutputTail = TailOf(result.Output);
            assignment.Status = result.Cancelled ? SubagentStatus.Cancelled
                : result.TimedOut ? SubagentStatus.TimedOut
                : result.Succeeded ? SubagentStatus.Completed
                : SubagentStatus.Failed;
            assignment.Error = result.Cancelled ? "用户取消"
                : result.TimedOut ? "超时被强制终止"
                : result.Succeeded ? null : "非零退出码";
            assignment.FinishedAt = DateTime.Now;
            Save(assignment);
            AssignmentChanged?.Invoke(assignment);

            Log.Info("Agent",
                $"子Agent 结束: {assignment.AgentName} 状态={assignment.Status}, exit={result.ExitCode}, 耗时 {(int)result.Elapsed.TotalSeconds}s, 输出 {result.Output.Length} 字符");

            return (assignment, result);
        }
        catch (OperationCanceledException)
        {
            // 取消必须上抛(项目铁律): 吞掉会让"停止"按钮只停住主循环, 子进程仍在跑。
            // 但上抛前仍要落终态 —— 否则 Agent 面板转圈到天荒地老, 且 Running 被持久化。
            Settle(assignment, SubagentStatus.Cancelled, "用户取消");
            Log.Info("Agent", $"子Agent 被取消: {assignment.AgentName} (assignment={assignment.AssignmentId})");
            throw;
        }
        catch (Exception ex)
        {
            // RunCliAsync 会因"未找到 Agent"等抛异常; 不收敛的话状态永远停在 Running:
            // 既不发终态事件(CommanderRuntime.RecordAgentCall 不触发、Agent 面板一直转圈),
            // 还会被当作 Running 写进 assignments/*.json。收敛后仍上抛, 由调用方转成工具错误。
            Settle(assignment, SubagentStatus.Failed, ex.Message);
            Log.Warn("Agent", ex, $"子Agent 异常终止: {assignment.AgentName} (assignment={assignment.AssignmentId})");
            throw;
        }
    }

    /// <summary>异常/取消路径统一落终态(状态 + 错误 + 完成时间 + 持久化 + 广播)。</summary>
    private void Settle(Assignment assignment, SubagentStatus status, string error)
    {
        assignment.Status = status;
        assignment.Error = error;
        assignment.FinishedAt = DateTime.Now;
        Save(assignment);
        AssignmentChanged?.Invoke(assignment);
    }

    private async Task<CliAgentRunResult> RunCliAsync(
        Assignment assignment, string finalPrompt,
        IProgress<string>? progressOutput, CancellationToken ct)
    {
        var agent = AgentConfigService.Find(assignment.AgentId)
            ?? throw new InvalidOperationException($"未找到 Agent: {assignment.AgentId}");

        // 兜底: AgentExecutor 侧已保证工作目录非空(ctx.WorkspaceRoot 兜底), 这里只覆盖
        // AppShell.Dispatch 等直接调 Create 的外部路径 —— 它们可能不传工作目录。
        // 优先用运行时工作区根, 否则子代理会跑到进程 CWD(工作区外)去;
        // 运行时未装配(单元/自检)时才退回进程 CWD, 保持旧行为。
        var workDir = string.IsNullOrEmpty(assignment.WorkingDirectory)
            ? (CommanderRuntime.Instance?.WorkspaceRoot is { Length: > 0 } ws
                ? Path.GetFullPath(ws)
                : Path.GetFullPath("."))
            : Path.GetFullPath(assignment.WorkingDirectory!);

        return await CliAgentRunner.RunAsync(agent, finalPrompt, workDir, progressOutput, assignment.PlanMode, ct);
    }

    /// <summary>OutputTail 落盘/回传的长度上限。
    /// 常量化以便 AgentToolFactory 侧对齐截断, 避免两处各写一个上限导致其中一处永远不生效。</summary>
    public const int MaxOutputTailChars = 12 * 1024;

    private static string TailOf(string output)
    {
        const int max = MaxOutputTailChars;
        return output.Length <= max ? output : output[^max..] + "\n...(较长已截断)";
    }

    private void UpdateStatus(Assignment assignment, SubagentStatus status)
    {
        assignment.Status = status;
        Save(assignment);
        AssignmentChanged?.Invoke(assignment);
    }

    private void Save(Assignment assignment)
    {
        // 原子写: 写失败只记日志, 不打断子 Agent 主流程(写坏的分派记录不应让整轮执行失败)
        AtomicFile.TryWriteAllText(
            Path.Combine(AppPaths.AssignmentsDir, $"{assignment.AssignmentId}.json"),
            JsonSerializer.Serialize(assignment, AppJsonContext.Default.Assignment),
            $"assignment {assignment.AssignmentId}");
    }
}