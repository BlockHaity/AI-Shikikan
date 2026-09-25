namespace AIShikikan.Core.Models;

/// <summary>Git 工作区上下文: 显式传递给 GitService 的不可变上下文, 禁止全局可变 RepositoryRoot。</summary>
public record GitWorkspaceContext
{
    /// <summary>工作目录(用户 cwd, 用于相对路径解析)。</summary>
    public required string WorkDir { get; init; }

    /// <summary>仓库根目录(包含 .git 的目录, 由 ResolveContext 解析得到)。</summary>
    public required string RepositoryRoot { get; init; }

    /// <summary>当前分支名(若 detached 则为空)。</summary>
    public required string BranchName { get; init; }

    /// <summary>是否为有效 Git 仓库(有 .git 且非裸仓库)。</summary>
    public required bool IsValidRepo { get; init; }

    /// <summary>是否处于 detached HEAD 状态。)</summary>
    public required bool IsDetachedHead { get; init; }

    /// <summary>是否为空仓库(无任何 commit, 即 unborn HEAD)。</summary>
    public required bool IsEmptyRepo { get; init; }
}