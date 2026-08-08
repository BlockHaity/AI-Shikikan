using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentCommander.Core.Serialization;

namespace AgentCommander.Core.Services.Git;

public enum GitStepStatus
{
    Created,
    Running,
    Completed,
    Merged,
    Dropped,
    Reverted
}

public class GitStepRecord
{
    public string StepId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Label { get; set; } = string.Empty;
    public string BaseBranch { get; set; } = string.Empty;
    public string StepBranch { get; set; } = string.Empty;
    public GitStepStatus Status { get; set; } = GitStepStatus.Created;
    public string RollbackMode { get; set; } = "drop";
    public string? MergeCommit { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
}

public class GitCommandResult
{
    public int ExitCode { get; init; }
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
    public bool Succeeded => ExitCode == 0;
}

public sealed class GitStepService
{
    private readonly Dictionary<string, GitStepRecord> _steps = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public GitStepService(string? workspaceRoot = null)
    {
        RepositoryRoot = string.IsNullOrWhiteSpace(workspaceRoot)
            ? Path.GetFullPath(".")
            : Path.GetFullPath(workspaceRoot.Trim());

        Directory.CreateDirectory(AppPaths.StepsDir);
        foreach (var file in Directory.GetFiles(AppPaths.StepsDir, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize(File.ReadAllText(file), AppJsonContext.Default.GitStepRecord);
                if (record is not null)
                {
                    _steps[record.StepId] = record;
                }
            }
            catch
            {
            }
        }
    }

    public string RepositoryRoot { get; }

    public bool IsRepoAvailable => Run("rev-parse", "--is-inside-work-tree").Succeeded;

    public string? CurrentBranch()
    {
        var r = Run("symbolic-ref", "--short", "HEAD");
        return r.Succeeded ? r.Stdout.Trim() : null;
    }

    public bool HasUncommittedChanges()
    {
        var r = Run("status", "--porcelain");
        return r.Succeeded && r.Stdout.Trim().Length > 0;
    }

    public string? LastCommitShort()
    {
        var r = Run("log", "-1", "--format=%h %s");
        return r.Succeeded ? r.Stdout.Trim() : null;
    }

    public IReadOnlyList<GitStepRecord> AllSteps
    {
        get
        {
            lock (_lock)
            {
                return _steps.Values.ToList();
            }
        }
    }

    public GitStepRecord? GetStep(string stepId)
    {
        lock (_lock)
        {
            return _steps.TryGetValue(stepId, out var r) ? r : null;
        }
    }

    public IReadOnlyList<GitStepRecord> PendingReview() =>
        AllSteps.Where(s => s.Status is GitStepStatus.Created or GitStepStatus.Running or GitStepStatus.Completed).ToList();

    /// <summary>为一步创建独立分支作为检查点。</summary>
    public GitStepRecord BeginStep(string label)
    {
        if (!IsRepoAvailable)
        {
            throw new InvalidOperationException(
                "当前目录不是 git 仓库。请先执行 `git init` 或手动初始化, 以启用逐步回滚保护。");
        }

        if (string.IsNullOrWhiteSpace(CurrentBranch()))
        {
            throw new InvalidOperationException("仓库 HEAD 处于 detached 状态, 请先切换到分支。");
        }

        var record = new GitStepRecord
        {
            Label = label,
            BaseBranch = CurrentBranch()!,
            StepBranch = $"ac/{Guid.NewGuid().ToString("N")[..6]}",
            Status = GitStepStatus.Created
        };
        record.StepBranch = $"ac/{record.StepId}";

        var switched = Run("switch", "-c", record.StepBranch);
        if (!switched.Succeeded)
        {
            throw new InvalidOperationException($"创建步骤分支失败: {switched.Stderr.Trim()}");
        }

        SaveRecord(record);
        return record;
    }

    public void MarkRunning(string stepId) => UpdateStatus(stepId, GitStepStatus.Running);

    public void MarkCompleted(string stepId)
    {
        var record = GetStep(stepId);
        if (record is null)
        {
            throw new ArgumentException($"未知步骤: {stepId}");
        }

        record.Status = GitStepStatus.Completed;
        record.CompletedAt = DateTime.Now;
        SaveRecord(record);
    }

    /// <summary>get diff 统计信息 (step 相对 base)。</summary>
    public GitCommandResult GetDiffStat(string stepId)
    {
        var record = GetRecord(stepId)
            ?? throw new ArgumentException($"步骤不存在: {stepId}");

        if (record.Status == GitStepStatus.Merged && !string.IsNullOrEmpty(record.MergeCommit))
        {
            return Run("show", "--stat", "--oneline", record.MergeCommit);
        }

        return Run("diff", "--stat", $"{record.BaseBranch}..{record.StepBranch}");
    }

    public GitCommandResult GetDiff(string stepId)
    {
        var record = GetRecord(stepId)
            ?? throw new ArgumentException($"步骤不存在: {stepId}");

        if (record.Status == GitStepStatus.Merged && !string.IsNullOrEmpty(record.MergeCommit))
        {
            return Run("show", record.MergeCommit);
        }

        return Run("diff", $"{record.BaseBranch}..{record.StepBranch}");
    }

    /// <summary>合并步骤分支到 base 分支 (--no-ff), 并删除步骤分支。</summary>
    public GitCommandResult MergeStep(string stepId)
    {
        var record = GetRecord(stepId)
            ?? throw new ArgumentException($"步骤不存在: {stepId}");

        if (record.Status == GitStepStatus.Merged)
        {
            return new GitCommandResult { ExitCode = 0, Stdout = $"步骤 {stepId} 已合并过。" };
        }

        var checkout = Run("switch", record.BaseBranch);
        if (!checkout.Succeeded)
        {
            throw new InvalidOperationException($"切换回 {record.BaseBranch} 失败: {checkout.Stderr.Trim()}");
        }

        var merge = Run("merge", "--no-ff", "-m", $"ac: 合并步骤 {record.StepId} ({record.Label})", record.StepBranch);
        if (!merge.Succeeded)
        {
            throw new InvalidOperationException($"合并失败: {merge.Stderr.Trim()}");
        }

        record.MergeCommit = Run("rev-parse", "HEAD").Stdout.Trim();
        record.Status = GitStepStatus.Merged;
        SaveRecord(record);

        TryDeleteBranch(record.StepBranch);
        return merge;
    }

    /// <summary>丢弃步骤: 切换回 base 分支并删除步骤分支(需工作区干净)。</summary>
    public GitCommandResult DropStep(string stepId)
    {
        var record = GetRecord(stepId)
            ?? throw new ArgumentException($"步骤不存在: {stepId}");

        if (record.Status is GitStepStatus.Merged or GitStepStatus.Dropped or GitStepStatus.Reverted)
        {
            throw new InvalidOperationException($"步骤 {stepId} 已处于 {record.Status}, 不能再次回滚。");
        }

        if (HasUncommittedChanges())
        {
            throw new InvalidOperationException("工作区有未提交变更, 请先提交或清理后再丢弃步骤(避免误伤)。");
        }

        var checkout = Run("switch", record.BaseBranch);
        if (!checkout.Succeeded)
        {
            throw new InvalidOperationException($"切换回 {record.BaseBranch} 失败: {checkout.Stderr.Trim()}");
        }

        var del = Run("branch", "-D", record.StepBranch);
        if (!del.Succeeded)
        {
            throw new InvalidOperationException($"删除步骤分支失败: {del.Stderr.Trim()}");
        }

        record.Status = GitStepStatus.Dropped;
        record.CompletedAt = DateTime.Now;
        SaveRecord(record);
        return del;
    }

    /// <summary>对已合并的步骤生成反向提交 (保留历史, 非破坏)。</summary>
    public GitCommandResult RevertStep(string stepId)
    {
        var record = GetRecord(stepId)
            ?? throw new ArgumentException($"步骤不存在: {stepId}");

        if (record.Status != GitStepStatus.Merged)
        {
            throw new InvalidOperationException("只有已合并的步骤才能 revert。未合并步骤请用 drop 回滚。");
        }

        var revert = Run("revert", "--no-edit", "-m", "1", record.MergeCommit!);
        if (!revert.Succeeded)
        {
            throw new InvalidOperationException($"revert 失败: {revert.Stderr.Trim()}");
        }

        record.Status = GitStepStatus.Reverted;
        record.CompletedAt = DateTime.Now;
        SaveRecord(record);
        return revert;
    }

    public GitCommandResult Run(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = Path.GetFullPath(RepositoryRoot),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return new GitCommandResult { ExitCode = -1, Stderr = "git 启动失败" };
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return new GitCommandResult
            {
                ExitCode = process.ExitCode,
                Stdout = stdout.Result,
                Stderr = stderr.Result
            };
        }
        catch (Exception ex)
        {
            return new GitCommandResult { ExitCode = -1, Stderr = ex.Message };
        }
    }

    private GitStepRecord GetRecord(string stepId) =>
        GetStep(stepId) ?? throw new ArgumentException($"步骤不存在: {stepId}");

    private void UpdateStatus(string stepId, GitStepStatus status)
    {
        var record = GetRecord(stepId);
        if (record is null)
        {
            return;
        }

        record.Status = status;
        SaveRecord(record);
    }

    private void TryDeleteBranch(string branch)
    {
        var r = Run("branch", "-d", branch);
        if (!r.Succeeded)
        {
            _ = Run("branch", "-D", branch);
        }
    }

    private void SaveRecord(GitStepRecord record)
    {
        lock (_lock)
        {
            _steps[record.StepId] = record;
            Directory.CreateDirectory(AppPaths.StepsDir);
            var file = Path.Combine(AppPaths.StepsDir, $"{record.StepId}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(record, AppJsonContext.Default.GitStepRecord));
        }
    }
}