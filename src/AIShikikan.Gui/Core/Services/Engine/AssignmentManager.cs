using System.Text.Json;
using System.Text.Json.Serialization;
using AIShikikan.Core.Serialization;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Git;

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
    public string StepId { get; set; } = string.Empty;
    public string? WorkingDirectory { get; set; }
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

/// <summary>分派管理: 记录、异步后台执行、与 Git 步骤生命周期绑定、持久化。</summary>
public sealed class AssignmentManager
{
    private readonly Dictionary<string, Assignment> _assignments = new(StringComparer.OrdinalIgnoreCase);
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
        string? workingDirectory = null)
    {
        var assignment = new Assignment
        {
            AgentId = agent.Id,
            AgentName = agent.Display,
            Task = task,
            TemplateId = templateId,
            PersonaId = personaId,
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