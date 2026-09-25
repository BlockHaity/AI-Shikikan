namespace AIShikikan.Core.Services.Git;

/// <summary>Git 服务错误码。</summary>
public enum GitServiceError
{
    None,
    NotARepository,
    DetachedHead,
    EmptyRepo,
    DirtyWorkTree,
    NotAncestor,
    InvalidArgument,
    CommandFailed
}

/// <summary>Git 命令执行结果。</summary>
public record GitCommandResult
{
    public int ExitCode { get; init; }
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
    public bool Succeeded => ExitCode == 0;
    public GitServiceError Error { get; init; } = GitServiceError.None;

    public static GitCommandResult Success(string stdout = "") => new() { ExitCode = 0, Stdout = stdout };
    public static GitCommandResult Failure(string stderr, GitServiceError error = GitServiceError.CommandFailed, int exitCode = -1)
        => new() { ExitCode = exitCode, Stderr = stderr, Error = error };
}

/// <summary>porcelain 状态中的单个文件条目。</summary>
public record GitFileStatus
{
    public required string Path { get; init; }
    public char IndexStatus { get; init; }
    public char WorkTreeStatus { get; init; }
    public bool IsUntracked => IndexStatus == '?' && WorkTreeStatus == '?';
    public bool IsStaged => IndexStatus is not (' ' or '?');
    public bool HasWorkTreeChange => WorkTreeStatus is not (' ' or '?');

    public string StatusLabel => (IndexStatus, WorkTreeStatus) switch
    {
        ('?', _) => "未跟踪",
        ('A', _) => "新增(已暂存)",
        ('M', ' ') => "已暂存修改",
        ('M', _) => "修改",
        ('D', ' ') => "已暂存删除",
        ('D', _) => "删除",
        ('R', _) => "重命名",
        ('C', _) => "复制",
        (_, 'M') => "修改",
        (_, 'D') => "删除",
        (' ', '?') => "未跟踪",
        _ => "变更"
    };
}

public record GitGraphLine
{
    public string GraphPart { get; init; } = string.Empty;
    public string CommitPart { get; init; } = string.Empty;
}

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