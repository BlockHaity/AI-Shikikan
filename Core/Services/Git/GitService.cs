using System.Diagnostics;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Git;

/// <summary>
/// Git 服务: 显式上下文 API, 禁止全局可变 RepositoryRoot。
/// 所有写操作按 WorkTreeRoot 使用 SemaphoreSlim 串行。
/// </summary>
public sealed class GitService : IDisposable
{
    private readonly GitCheckpointStore _checkpointStore;
    private readonly Dictionary<string, SemaphoreSlim> _repoLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lockDict = new();

    public GitService(GitCheckpointStore checkpointStore)
    {
        _checkpointStore = checkpointStore;
    }

    /// <summary>解析工作目录的 Git 上下文(不改变任何状态)。</summary>
    public GitWorkspaceContext ResolveContext(string workDir)
    {
        var fullWorkDir = Path.GetFullPath(workDir);
        if (!Directory.Exists(fullWorkDir))
        {
            return new GitWorkspaceContext
            {
                WorkDir = fullWorkDir,
                RepositoryRoot = string.Empty,
                BranchName = string.Empty,
                IsValidRepo = false,
                IsDetachedHead = false,
                IsEmptyRepo = false
            };
        }

        var repoRoot = FindRepositoryRoot(fullWorkDir);
        if (string.IsNullOrEmpty(repoRoot))
        {
            return new GitWorkspaceContext
            {
                WorkDir = fullWorkDir,
                RepositoryRoot = string.Empty,
                BranchName = string.Empty,
                IsValidRepo = false,
                IsDetachedHead = false,
                IsEmptyRepo = false
            };
        }

        var branch = GetCurrentBranch(repoRoot);
        var isDetached = string.IsNullOrEmpty(branch);
        var isEmpty = IsEmptyRepository(repoRoot);

        return new GitWorkspaceContext
        {
            WorkDir = fullWorkDir,
            RepositoryRoot = repoRoot,
            BranchName = branch ?? string.Empty,
            IsValidRepo = true,
            IsDetachedHead = isDetached,
            IsEmptyRepo = isEmpty
        };
    }

    /// <summary>获取仓库锁(按仓库根目录串行化写操作)。</summary>
    public SemaphoreSlim GetRepoLock(string repositoryRoot)
    {
        lock (_lockDict)
        {
            if (!_repoLocks.TryGetValue(repositoryRoot, out var sem))
            {
                sem = new SemaphoreSlim(1, 1);
                _repoLocks[repositoryRoot] = sem;
            }
            return sem;
        }
    }

    /// <summary>检查工作区是否干净(无未暂存/未跟踪变更)。</summary>
    public GitCommandResult IsClean(GitWorkspaceContext ctx)
    {
        if (!ctx.IsValidRepo) return GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository);
        var r = Run(ctx.RepositoryRoot, "status", "--porcelain");
        return r.Succeeded && string.IsNullOrWhiteSpace(r.Stdout)
            ? GitCommandResult.Success("clean")
            : GitCommandResult.Failure("工作区不干净", GitServiceError.DirtyWorkTree);
    }

    /// <summary>获取文件状态列表。)</summary>
    public IReadOnlyList<GitFileStatus> GetStatusFiles(GitWorkspaceContext ctx)
    {
        if (!ctx.IsValidRepo) return [];
        var r = Run(ctx.RepositoryRoot, "status", "--porcelain=v1");
        if (!r.Succeeded) return [];

        var list = new List<GitFileStatus>();
        foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 2) continue;
            var path = line.Length > 3 ? line[3..] : string.Empty;
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) path = path[(arrow + 4)..];
            list.Add(new GitFileStatus { Path = path, IndexStatus = line[0], WorkTreeStatus = line[1] });
        }
        return list;
    }

    /// <summary>暂存指定文件。)</summary>
    public GitCommandResult StageFile(GitWorkspaceContext ctx, string path)
    {
        if (!ctx.IsValidRepo) return GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository);
        return Run(ctx.RepositoryRoot, "add", "--", path);
    }

    /// <summary>取消暂存指定文件。)</summary>
    public GitCommandResult UnstageFile(GitWorkspaceContext ctx, string path)
    {
        if (!ctx.IsValidRepo) return GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository);
        return Run(ctx.RepositoryRoot, "restore", "--staged", "--", path);
    }

    /// <summary>暂存所有变更。)</summary>
    public GitCommandResult StageAll(GitWorkspaceContext ctx)
    {
        if (!ctx.IsValidRepo) return GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository);
        return Run(ctx.RepositoryRoot, "add", "-A");
    }

    /// <summary>提交所有已暂存变更。)</summary>
    public GitCommandResult Commit(GitWorkspaceContext ctx, string message)
    {
        if (!ctx.IsValidRepo) return GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository);
        if (ctx.IsDetachedHead) return GitCommandResult.Failure("HEAD 处于 detached 状态，无法提交", GitServiceError.DetachedHead);
        var msg = string.IsNullOrWhiteSpace(message) ? "wip: GUI 提交" : message.Trim();
        return Run(ctx.RepositoryRoot, "commit", "-m", msg);
    }

    /// <summary>确保仓库有首个 commit(unborn HEAD 时自动创建空提交)。</summary>
    public GitCommandResult EnsureInitialCommit(GitWorkspaceContext ctx)
    {
        if (!ctx.IsValidRepo) return GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository);
        if (!ctx.IsEmptyRepo) return GitCommandResult.Success("已有 commit");

        // 空仓库: 尝试 stage 所有文件(若有), 允许 empty commit
        var stageResult = Run(ctx.RepositoryRoot, "add", "-A");
        if (!stageResult.Succeeded)
        {
            Log.Warn("Git", $"首次提交 stage 失败: {stageResult.Stderr}");
        }

        var commitResult = Run(ctx.RepositoryRoot, "commit", "--allow-empty", "-m", "chore: initial commit");
        if (commitResult.Succeeded)
        {
            Log.Info("Git", "已创建初始提交");
        }
        return commitResult;
    }

    /// <summary>提交指定文件(相对仓库根的相对路径): stage + commit, 返回新 commit SHA。)</summary>
    public GitCommandResult CommitFiles(GitWorkspaceContext ctx, IReadOnlyList<string> relativePaths, string message)
    {
        if (!ctx.IsValidRepo) return GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository);
        if (ctx.IsDetachedHead) return GitCommandResult.Failure("HEAD 处于 detached 状态，无法提交", GitServiceError.DetachedHead);
        if (relativePaths.Count == 0) return GitCommandResult.Failure("文件列表为空", GitServiceError.InvalidArgument);

        var sem = GetRepoLock(ctx.RepositoryRoot);
        sem.Wait();
        try
        {
            // stage 指定文件
            foreach (var p in relativePaths)
            {
                var r = Run(ctx.RepositoryRoot, "add", "--", p);
                if (!r.Succeeded)
                    return GitCommandResult.Failure($"stage 失败: {p} - {r.Stderr}", GitServiceError.CommandFailed);
            }

            var msg = string.IsNullOrWhiteSpace(message) ? "wip: AI 提交" : message.Trim();
            var commitResult = Run(ctx.RepositoryRoot, "commit", "-m", msg);
            if (!commitResult.Succeeded)
                return commitResult;

            // 获取新 commit SHA
            var shaResult = Run(ctx.RepositoryRoot, "rev-parse", "HEAD");
            return shaResult.Succeeded ? GitCommandResult.Success(shaResult.Stdout.Trim()) : commitResult;
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>在指定 commit 上创建轻量标签作为检查点, 并持久化记录。)</summary>
    public GitCommandResult MarkCheckpoint(GitWorkspaceContext ctx, GitCheckpointRecord record)
    {
        if (!ctx.IsValidRepo) return GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository);
        if (string.IsNullOrWhiteSpace(record.CommitSha)) return GitCommandResult.Failure("CommitSha 为空", GitServiceError.InvalidArgument);
        if (string.IsNullOrWhiteSpace(record.TagName)) return GitCommandResult.Failure("TagName 为空", GitServiceError.InvalidArgument);

        // 验证 commit 存在
        var verify = Run(ctx.RepositoryRoot, "cat-file", "-t", record.CommitSha);
        if (!verify.Succeeded || !verify.Stdout.Trim().Equals("commit", StringComparison.OrdinalIgnoreCase))
        {
            return GitCommandResult.Failure($"Commit 不存在或非 commit 对象: {record.CommitSha}", GitServiceError.InvalidArgument);
        }

        // 创建轻量标签(如果已存在同名则先删)
        var existing = Run(ctx.RepositoryRoot, "tag", "-l", record.TagName);
        if (existing.Succeeded && !string.IsNullOrWhiteSpace(existing.Stdout))
        {
            var del = Run(ctx.RepositoryRoot, "tag", "-d", record.TagName);
            if (!del.Succeeded)
            {
                return GitCommandResult.Failure($"删除已存在标签失败: {del.Stderr}", GitServiceError.CommandFailed);
            }
        }

        var tagResult = Run(ctx.RepositoryRoot, "tag", record.TagName, record.CommitSha);
        if (!tagResult.Succeeded)
        {
            return GitCommandResult.Failure($"创建标签失败: {tagResult.Stderr}", GitServiceError.CommandFailed);
        }

        // 持久化记录
        try
        {
            _checkpointStore.Save(record);
        }
        catch (Exception ex)
        {
            Log.Warn("Git", ex, $"检查点记录持久化失败: {record.Id}");
            // 标签已创建, 记录失败不回滚标签, 仅记录日志
        }

        return GitCommandResult.Success(record.TagName);
    }

    /// <summary>获取两个 commit 间的 diff(完整 patch)。)</summary>
    public GitCommandResult GetDiff(GitWorkspaceContext ctx, string fromSha, string toSha)
    {
        if (!ctx.IsValidRepo) return GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository);
        return Run(ctx.RepositoryRoot, "diff", $"{fromSha}..{toSha}");
    }

    /// <summary>获取 diff 统计信息。)</summary>
    public GitCommandResult GetDiffStat(GitWorkspaceContext ctx, string fromSha, string toSha)
    {
        if (!ctx.IsValidRepo) return GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository);
        return Run(ctx.RepositoryRoot, "diff", "--stat", $"{fromSha}..{toSha}");
    }

    /// <summary>Fork: 从检查点 commit 创建新分支并切换(要求工作区干净、同仓库、目标分支不存在)。)</summary>
    public GitCommandResult Fork(GitWorkspaceContext ctx, GitCheckpointRecord checkpoint, string newBranchName)
    {
        if (!ctx.IsValidRepo) return GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository);
        if (ctx.IsDetachedHead) return GitCommandResult.Failure("HEAD 处于 detached 状态，无法 Fork", GitServiceError.DetachedHead);
        if (string.IsNullOrWhiteSpace(newBranchName)) return GitCommandResult.Failure("分支名为空", GitServiceError.InvalidArgument);
        if (!string.Equals(ctx.RepositoryRoot, checkpoint.RepositoryRoot, StringComparison.OrdinalIgnoreCase))
            return GitCommandResult.Failure("检查点不属于当前仓库", GitServiceError.InvalidArgument);
        if (!string.Equals(ctx.BranchName, checkpoint.BranchName, StringComparison.OrdinalIgnoreCase))
            return GitCommandResult.Failure($"当前分支({ctx.BranchName})与检查点分支({checkpoint.BranchName})不一致", GitServiceError.InvalidArgument);

        var cleanCheck = IsClean(ctx);
        if (!cleanCheck.Succeeded) return cleanCheck;

        var sem = GetRepoLock(ctx.RepositoryRoot);
        sem.Wait();
        try
        {
            // 检查目标分支是否已存在
            var branchCheck = Run(ctx.RepositoryRoot, "rev-parse", "--verify", $"refs/heads/{newBranchName}");
            if (branchCheck.Succeeded)
            {
                return GitCommandResult.Failure($"分支已存在: {newBranchName}", GitServiceError.InvalidArgument);
            }

            // 创建并切换到新分支
            var switchResult = Run(ctx.RepositoryRoot, "switch", "-c", newBranchName, checkpoint.CommitSha);
            if (!switchResult.Succeeded)
            {
                return GitCommandResult.Failure($"创建分支失败: {switchResult.Stderr}", GitServiceError.CommandFailed);
            }

            return GitCommandResult.Success($"已创建并切换到分支 {newBranchName} (基于 {checkpoint.ShortSha})");
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>Hard Reset 到检查点 commit(仅允许祖先、工作区干净)。)</summary>
    public GitCommandResult ResetHardToCheckpoint(GitWorkspaceContext ctx, GitCheckpointRecord checkpoint)
    {
        if (!ctx.IsValidRepo) return GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository);
        if (ctx.IsDetachedHead) return GitCommandResult.Failure("HEAD 处于 detached 状态，无法重置", GitServiceError.DetachedHead);
        if (!string.Equals(ctx.RepositoryRoot, checkpoint.RepositoryRoot, StringComparison.OrdinalIgnoreCase))
            return GitCommandResult.Failure("检查点不属于当前仓库", GitServiceError.InvalidArgument);
        if (!string.Equals(ctx.BranchName, checkpoint.BranchName, StringComparison.OrdinalIgnoreCase))
            return GitCommandResult.Failure($"当前分支({ctx.BranchName})与检查点分支({checkpoint.BranchName})不一致", GitServiceError.InvalidArgument);

        var cleanCheck = IsClean(ctx);
        if (!cleanCheck.Succeeded) return cleanCheck;

        // 验证 checkpoint 是当前 HEAD 的祖先
        var ancestorCheck = Run(ctx.RepositoryRoot, "merge-base", "--is-ancestor", checkpoint.CommitSha, "HEAD");
        if (!ancestorCheck.Succeeded)
        {
            return GitCommandResult.Failure("检查点不是当前 HEAD 的祖先，无法硬重置", GitServiceError.NotAncestor);
        }

        var sem = GetRepoLock(ctx.RepositoryRoot);
        sem.Wait();
        try
        {
            var resetResult = Run(ctx.RepositoryRoot, "reset", "--hard", checkpoint.CommitSha);
            if (!resetResult.Succeeded)
            {
                return GitCommandResult.Failure($"硬重置失败: {resetResult.Stderr}", GitServiceError.CommandFailed);
            }

            // 更新检查点记录的回滚信息
            checkpoint.LastRollbackMode = CheckpointRollbackMode.ResetHard;
            checkpoint.LastRollbackAt = DateTime.Now;
            checkpoint.LastRollbackCommitSha = checkpoint.CommitSha;
            _checkpointStore.Save(checkpoint);

            return GitCommandResult.Success($"已硬重置到 {checkpoint.ShortSha}");
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>Revert 到检查点: git revert --no-commit <checkpoint>..HEAD 后创建还原 commit(仅允许祖先、工作区干净)。</summary>
    public GitCommandResult RevertToCheckpoint(GitWorkspaceContext ctx, GitCheckpointRecord checkpoint, string? revertMessage = null)
    {
        if (!ctx.IsValidRepo) return GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository);
        if (ctx.IsDetachedHead) return GitCommandResult.Failure("HEAD 处于 detached 状态，无法 revert", GitServiceError.DetachedHead);
        if (!string.Equals(ctx.RepositoryRoot, checkpoint.RepositoryRoot, StringComparison.OrdinalIgnoreCase))
            return GitCommandResult.Failure("检查点不属于当前仓库", GitServiceError.InvalidArgument);
        if (!string.Equals(ctx.BranchName, checkpoint.BranchName, StringComparison.OrdinalIgnoreCase))
            return GitCommandResult.Failure($"当前分支({ctx.BranchName})与检查点分支({checkpoint.BranchName})不一致", GitServiceError.InvalidArgument);

        var cleanCheck = IsClean(ctx);
        if (!cleanCheck.Succeeded) return cleanCheck;

        // 验证 checkpoint 是当前 HEAD 的祖先
        var ancestorCheck = Run(ctx.RepositoryRoot, "merge-base", "--is-ancestor", checkpoint.CommitSha, "HEAD");
        if (!ancestorCheck.Succeeded)
        {
            return GitCommandResult.Failure("检查点不是当前 HEAD 的祖先，无法 revert", GitServiceError.NotAncestor);
        }

        var sem = GetRepoLock(ctx.RepositoryRoot);
        sem.Wait();
        try
        {
            // 使用 --no-commit 批量 revert 从 checkpoint 到 HEAD 的所有提交
            var range = $"{checkpoint.CommitSha}..HEAD";
            var revertResult = Run(ctx.RepositoryRoot, "revert", "--no-commit", range);
            if (!revertResult.Succeeded)
            {
                // 尝试恢复: revert --abort
                var abort = Run(ctx.RepositoryRoot, "revert", "--abort");
                Log.Warn("Git", $"revert 失败并尝试 abort: {revertResult.Stderr}, abort={abort.Succeeded}");
                return GitCommandResult.Failure($"revert 失败: {revertResult.Stderr}", GitServiceError.CommandFailed);
            }

            // 提交还原
            var msg = string.IsNullOrWhiteSpace(revertMessage)
                ? $"revert: 还原到检查点 {checkpoint.ShortSha} ({checkpoint.Label})"
                : revertMessage.Trim();

            var commitResult = Run(ctx.RepositoryRoot, "commit", "-m", msg);
            if (!commitResult.Succeeded)
            {
                // 尝试恢复
                var abort = Run(ctx.RepositoryRoot, "reset", "--hard", "HEAD@{1}");
                Log.Warn("Git", $"revert 提交失败并尝试恢复: {commitResult.Stderr}, restore={abort.Succeeded}");
                return GitCommandResult.Failure($"revert 提交失败: {commitResult.Stderr}", GitServiceError.CommandFailed);
            }

            // 获取新 commit SHA
            var shaResult = Run(ctx.RepositoryRoot, "rev-parse", "HEAD");
            var newSha = shaResult.Succeeded ? shaResult.Stdout.Trim() : string.Empty;

            // 更新检查点记录
            checkpoint.LastRollbackMode = CheckpointRollbackMode.Revert;
            checkpoint.LastRollbackAt = DateTime.Now;
            checkpoint.LastRollbackCommitSha = newSha;
            _checkpointStore.Save(checkpoint);

            return GitCommandResult.Success($"已创建还原提交 {newSha[..Math.Min(8, newSha.Length)]}");
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>获取本地分支列表。)</summary>
    public IReadOnlyList<string> GetLocalBranches(GitWorkspaceContext ctx)
    {
        if (!ctx.IsValidRepo) return [];
        var r = Run(ctx.RepositoryRoot, "branch", "--format=%(refname:short)");
        if (!r.Succeeded) return [];
        return r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>获取提交图谱。)</summary>
    public IReadOnlyList<GitGraphLine> GetCommitGraph(GitWorkspaceContext ctx, int limit = 60)
    {
        if (!ctx.IsValidRepo) return [];
        var r = Run(ctx.RepositoryRoot, "log", "--graph", "--all", "--no-color",
            $"--pretty=format:%x01%h %s", $"-n {limit}");
        if (!r.Succeeded) return [];

        var list = new List<GitGraphLine>();
        foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var sepIdx = line.IndexOf('\x01');
            if (sepIdx < 0)
            {
                list.Add(new GitGraphLine { GraphPart = string.Empty, CommitPart = line });
                continue;
            }
            list.Add(new GitGraphLine { GraphPart = line[..sepIdx], CommitPart = line[(sepIdx + 1)..] });
        }
        return list;
    }

    /// <summary>获取当前分支名(若 detached 返回 null)。)</summary>
    public string? GetCurrentBranch(string repositoryRoot)
    {
        var r = Run(repositoryRoot, "symbolic-ref", "--short", "HEAD");
        return r.Succeeded ? r.Stdout.Trim() : null;
    }

    /// <summary>检查是否为空仓库(无任何 commit)。</summary>
    public bool IsEmptyRepository(string repositoryRoot)
    {
        var r = Run(repositoryRoot, "rev-parse", "--verify", "HEAD");
        return !r.Succeeded; // unborn HEAD 返回失败
    }

    /// <summary>获取仓库当前 HEAD 的完整 SHA；空仓库或失败时返回 null。</summary>
    public string? GetHeadSha(string repositoryRoot)
    {
        var result = Run(repositoryRoot, "rev-parse", "HEAD");
        return result.Succeeded ? result.Stdout.Trim() : null;
    }

    /// <summary>切换当前 worktree 到指定本地分支。</summary>
    public GitCommandResult SwitchBranch(GitWorkspaceContext context, string branch)
    {
        if (!context.IsValidRepo) return GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository);
        if (string.IsNullOrWhiteSpace(branch)) return GitCommandResult.Failure("分支名为空", GitServiceError.InvalidArgument);
        var sem = GetRepoLock(context.RepositoryRoot);
        sem.Wait();
        try
        {
            return Run(context.RepositoryRoot, "switch", branch.Trim());
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>从当前上游拉取。</summary>
    public GitCommandResult Pull(GitWorkspaceContext context) =>
        context.IsValidRepo
            ? Run(context.RepositoryRoot, "pull")
            : GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository);

    /// <summary>推送当前分支到已配置上游。</summary>
    public GitCommandResult Push(GitWorkspaceContext context) =>
        context.IsValidRepo
            ? Run(context.RepositoryRoot, "push")
            : GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository);

    /// <summary>查找仓库根目录(从 workDir 向上查找 .git)。</summary>
    public string? FindRepositoryRoot(string workDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(workDir));
        while (dir != null)
        {
            var gitDir = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitDir) || File.Exists(gitDir))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>执行 git 命令(内部方法)。)</summary>
    private GitCommandResult Run(string repositoryRoot, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = Path.GetFullPath(repositoryRoot),
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
                return GitCommandResult.Failure("git 启动失败");
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
            return GitCommandResult.Failure(ex.Message);
        }
    }

    public void Dispose()
    {
        lock (_lockDict)
        {
            foreach (var sem in _repoLocks.Values)
            {
                sem.Dispose();
            }
            _repoLocks.Clear();
        }
    }
}