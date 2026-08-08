using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCommander.Core.Serialization;
using AgentCommander.Core.Services.Agents;
using AgentCommander.Core.Services.Git;

namespace AgentCommander.Core.Services.Engine;

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
    public string Mode { get; set; } = "sync";
    public string StepId { get; set; } = string.Empty;
    public string? WorkingDirectory { get; set; }
    public SubagentStatus Status { get; set; } = SubagentStatus.Queued;
    public string? OutputTail { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }

    [JsonIgnore]
    public string Display => $"[{AgentName}] {ShortTask} [{Mode}] {Status}";

    [JsonIgnore]
    public string ShortTask => Task.Length <= 40 ? Task : Task[..40] + "...";
}

/// <summary>分派管理: 记录、异步后台执行、与 Git 步骤生命周期绑定、持久化。</summary>
public sealed class AssignmentManager
{
    private readonly Dictionary<string, Assignment> _assignments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public AssignmentManager(GitStepService git)
    {
        Git = git;
        Directory.CreateDirectory(AppPaths.AssignmentsDir);
        foreach (var file in Directory.GetFiles(AppPaths.AssignmentsDir, "*.json"))
        {
            try
            {
                var a = JsonSerializer.Deserialize(File.ReadAllText(file), AppJsonContext.Default.Assignment);
                if (a is not null)
                {
                    _assignments[a.AssignmentId] = a;
                }
            }
            catch
            {
            }
        }
    }

    public GitStepService Git { get; }

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

    public Assignment? Get(string assignmentId) =>
        _assignments.TryGetValue(assignmentId, out var a) ? a : null;

    public Assignment Create(CliAgentDefinition agent, string task,
        string? templateId = null, string? personaId = null,
        string mode = "sync", string? workingDirectory = null)
    {
        var assignment = new Assignment
        {
            AgentId = agent.Id,
            AgentName = agent.Display,
            Task = task,
            TemplateId = templateId,
            PersonaId = personaId,
            Mode = mode,
            WorkingDirectory = workingDirectory ?? string.Empty
        };

        lock (_lock)
        {
            _assignments[assignment.AssignmentId] = assignment;
        }

        Save(assignment);
        AssignmentChanged?.Invoke(assignment);
        return assignment;
    }

    /// <summary>常规(同步)模式: 创建 git 步骤→运行→完成并返回完整结果。</summary>
    public async Task<(Assignment Assignment, CliAgentRunResult Run)> RunSyncAsync(
        Assignment assignment, string finalPrompt,
        IProgress<string>? progressOutput, CancellationToken ct = default)
    {
        UpdateStatus(assignment, SubagentStatus.Running);

        GitStepRecord step;
        try
        {
            step = Git.BeginStep($"agent-{assignment.AgentId}");
            assignment.StepId = step.StepId;
            Git.MarkRunning(step.StepId);
        }
        catch (Exception ex)
        {
            assignment.Status = SubagentStatus.Failed;
            assignment.Error = ex.Message;
            assignment.FinishedAt = DateTime.Now;
            Save(assignment);
            AssignmentChanged?.Invoke(assignment);
            throw;
        }

        var result = await RunCliAsync(assignment, finalPrompt, progressOutput, ct);

        if (assignment.Status != SubagentStatus.Cancelled)
        {
            Git.MarkCompleted(step.StepId);
        }

        assignment.ExitCode = result.ExitCode;
        assignment.OutputTail = TailOf(result.Output);
        assignment.Error = result.TimedOut ? "超时被强制终止" : result.Succeeded ? null : "非零退出码";
        assignment.Status = result.TimedOut
            ? SubagentStatus.TimedOut
            : result.Succeeded
                ? SubagentStatus.Completed
                : SubagentStatus.Failed;
        assignment.FinishedAt = DateTime.Now;
        Save(assignment);
        AssignmentChanged?.Invoke(assignment);

        return (assignment, result);
    }

    /// <summary>异步模式: 立即返回, 后台线程执行。</summary>
    public Assignment StartAsync(Assignment assignment, string finalPrompt,
        IProgress<string>? progressOutput)
    {
        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            _cancellations[assignment.AssignmentId] = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var stepRecord = null as GitStepRecord;
                try
                {
                    assignment.Status = SubagentStatus.Running;
                    var step = Git.BeginStep($"async-{assignment.AgentId}");
                    assignment.StepId = step.StepId;
                    Git.MarkRunning(step.StepId);
                    stepRecord = step;
                }
                catch (Exception ex)
                {
                    assignment.Status = SubagentStatus.Failed;
                    assignment.Error = ex.Message;
                    assignment.FinishedAt = DateTime.Now;
                    Save(assignment);
                    AssignmentChanged?.Invoke(assignment);
                    return;
                }

                var result = await RunCliAsync(assignment, finalPrompt, progressOutput, cts.Token);

                if (stepRecord is not null && assignment.Status != SubagentStatus.Cancelled)
                {
                    Git.MarkCompleted(stepRecord.StepId);
                }

                assignment.ExitCode = result.ExitCode;
                assignment.OutputTail = TailOf(result.Output);
                assignment.Status = result.TimedOut
                    ? SubagentStatus.TimedOut
                    : result.Succeeded
                        ? SubagentStatus.Completed
                        : SubagentStatus.Failed;
                assignment.FinishedAt = DateTime.Now;
                Save(assignment);
                AssignmentChanged?.Invoke(assignment);
            }
            catch (OperationCanceledException)
            {
                assignment.Status = SubagentStatus.Cancelled;
                assignment.FinishedAt = DateTime.Now;
                Save(assignment);
                AssignmentChanged?.Invoke(assignment);
            }
            catch (Exception ex)
            {
                assignment.Status = SubagentStatus.Failed;
                assignment.Error = ex.Message;
                assignment.FinishedAt = DateTime.Now;
                Save(assignment);
                AssignmentChanged?.Invoke(assignment);
            }
            finally
            {
                lock (_lock)
                {
                    _cancellations.Remove(assignment.AssignmentId);
                }
            }
        });

        return assignment;
    }

    public void Cancel(string assignmentId)
    {
        Assignment? a = Get(assignmentId);
        if (a is null || a.Status is SubagentStatus.Completed or SubagentStatus.Failed
            or SubagentStatus.Cancelled or SubagentStatus.TimedOut)
        {
            return;
        }

        lock (_lock)
        {
            if (_cancellations.TryGetValue(assignmentId, out var cts))
            {
                cts.Cancel();
            }
        }

        a.Status = SubagentStatus.Cancelled;
        a.FinishedAt = DateTime.Now;
        Save(a);
        AssignmentChanged?.Invoke(a);
    }

    private async Task<CliAgentRunResult> RunCliAsync(
        Assignment assignment, string finalPrompt,
        IProgress<string>? progressOutput, CancellationToken ct)
    {
        var agent = AgentConfigService.Find(assignment.AgentId)
            ?? throw new InvalidOperationException($"未找到 Agent: {assignment.AgentId}");

        var workDir = string.IsNullOrEmpty(assignment.WorkingDirectory)
            ? Path.GetFullPath(".")
            : Path.GetFullPath(assignment.WorkingDirectory!);

        return await CliAgentRunner.RunAsync(agent, finalPrompt, workDir, progressOutput, ct);
    }

    private static string TailOf(string output)
    {
        const int max = 12 * 1024;
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
        Directory.CreateDirectory(AppPaths.AssignmentsDir);
        File.WriteAllText(
            Path.Combine(AppPaths.AssignmentsDir, $"{assignment.AssignmentId}.json"),
            JsonSerializer.Serialize(assignment, AppJsonContext.Default.Assignment));
    }
}