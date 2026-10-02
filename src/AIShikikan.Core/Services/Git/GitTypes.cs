namespace AIShikikan.Core.Services.Git;

/// <summary>Git 服务错误码。</summary>
/// <remarks>
/// 该枚举注册在 <c>AppJsonContext</c>(AOT 源生成上下文, A12 维护)里, 因此它的<b>数值编号属于
/// JSON 契约的一部分</b>。即使某个值当前无使用点(见 <see cref="EmptyRepo"/>), 也<b>不要删除或重排</b> ——
/// 重排会静默改变已序列化内容的含义。若将来真要移除, 应先把该值标记为弃用并保留一个显式编号。
/// </remarks>
public enum GitServiceError
{
    None,
    NotARepository,
    DetachedHead,
    /// <summary>预留, 当前无使用点: 空仓库(unborn HEAD)目前只由 <c>GitService.IsEmptyRepository</c>
    /// 以布尔值表达, 没有对应的失败路径返回这个码。保留是为了不打乱后续枚举值的编号。</summary>
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