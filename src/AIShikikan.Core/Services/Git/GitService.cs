using System.Diagnostics;
using System.Globalization;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Git;

/// <summary>
/// Git 服务: 显式上下文 API, 禁止全局可变 RepositoryRoot。
/// 所有写操作按 WorkTreeRoot 使用 SemaphoreSlim 串行。
///
/// <para><b>同步/异步双 API</b>: 每个会起 git 进程的 public 方法都有一个 <c>*Async</c> 版本,
/// 底层 <c>RunAsync</c> 是真正非阻塞的。同步方法一律是 <c>RunAsync(...).GetAwaiter().GetResult()</c>
/// 的兼容包装, 保留是为了不级联改动全部调用方 —— <b>新代码(尤其 GUI 的 UI 线程)必须用 <c>*Async</c></b>,
/// 否则一次 git 调用会把界面冻结到 git 退出(普通命令 15s, 网络命令 60s)。</para>
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
    public GitWorkspaceContext ResolveContext(string workDir) =>
        ResolveContextAsync(workDir).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="ResolveContext"/>。内部最多起 3 个 git 进程, UI 线程请用本方法。</summary>
    public async Task<GitWorkspaceContext> ResolveContextAsync(string workDir, CancellationToken ct = default)
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

        var branch = await GetCurrentBranchAsync(repoRoot, ct).ConfigureAwait(false);
        var isDetached = string.IsNullOrEmpty(branch);
        var isEmpty = await IsEmptyRepositoryAsync(repoRoot, ct).ConfigureAwait(false);

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
    /// <remarks>
    /// <para><b>不可重入</b>: <c>SemaphoreSlim(1,1)</c> 默认非重入, 同一线程二次 <c>Wait()</c> 会永久挂起。
    /// 因此本类内部任何"持锁期间调用的方法"都必须直接走 <c>Run</c>/<c>RunAsync</c>,
    /// 不能再调会取锁的 public 方法。</para>
    /// <para><b>不回收</b>: 条目只在 <see cref="Dispose"/> 时统一释放, 正常使用期(进程级单例)不会无限增长。</para>
    /// </remarks>
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
    public GitCommandResult IsClean(GitWorkspaceContext ctx) =>
        IsCleanAsync(ctx).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="IsClean"/>。</summary>
    public async Task<GitCommandResult> IsCleanAsync(GitWorkspaceContext ctx, CancellationToken ct = default)
    {
        if (!ctx.IsValidRepo) return GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository);
        var r = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, ct, "status", "--porcelain").ConfigureAwait(false);
        return r.Succeeded && string.IsNullOrWhiteSpace(r.Stdout)
            ? GitCommandResult.Success("clean")
            : GitCommandResult.Failure("工作区不干净", GitServiceError.DirtyWorkTree);
    }

    /// <summary>获取文件状态列表。)</summary>
    public IReadOnlyList<GitFileStatus> GetStatusFiles(GitWorkspaceContext ctx) =>
        GetStatusFilesAsync(ctx).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="GetStatusFiles"/>。</summary>
    public async Task<IReadOnlyList<GitFileStatus>> GetStatusFilesAsync(GitWorkspaceContext ctx, CancellationToken ct = default)
    {
        if (!ctx.IsValidRepo) return [];
        var r = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, ct, "status", "--porcelain=v1").ConfigureAwait(false);
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
    public GitCommandResult StageFile(GitWorkspaceContext ctx, string path) =>
        StageFileAsync(ctx, path).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="StageFile"/>。</summary>
    public Task<GitCommandResult> StageFileAsync(GitWorkspaceContext ctx, string path, CancellationToken ct = default)
    {
        if (!ctx.IsValidRepo) return Task.FromResult(GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository));
        // D7: git add 是写操作, 必须与 commit / reset / revert / switch 共用仓库锁, 否则同一仓库的写并未真正串行
        return WithRepoLockAsync(ctx.RepositoryRoot, ct,
            t => RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "add", "--", path));
    }

    /// <summary>取消暂存指定文件。)</summary>
    public GitCommandResult UnstageFile(GitWorkspaceContext ctx, string path) =>
        UnstageFileAsync(ctx, path).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="UnstageFile"/>。</summary>
    public Task<GitCommandResult> UnstageFileAsync(GitWorkspaceContext ctx, string path, CancellationToken ct = default)
    {
        if (!ctx.IsValidRepo) return Task.FromResult(GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository));
        return WithRepoLockAsync(ctx.RepositoryRoot, ct,
            t => RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "restore", "--staged", "--", path));
    }

    /// <summary>暂存所有变更。)</summary>
    public GitCommandResult StageAll(GitWorkspaceContext ctx) =>
        StageAllAsync(ctx).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="StageAll"/>。</summary>
    public Task<GitCommandResult> StageAllAsync(GitWorkspaceContext ctx, CancellationToken ct = default)
    {
        if (!ctx.IsValidRepo) return Task.FromResult(GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository));
        return WithRepoLockAsync(ctx.RepositoryRoot, ct,
            t => RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "add", "-A"));
    }

    /// <summary>提交所有已暂存变更。)</summary>
    public GitCommandResult Commit(GitWorkspaceContext ctx, string message) =>
        CommitAsync(ctx, message).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="Commit"/>。</summary>
    public Task<GitCommandResult> CommitAsync(GitWorkspaceContext ctx, string message, CancellationToken ct = default)
    {
        if (!ctx.IsValidRepo) return Task.FromResult(GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository));
        if (ctx.IsDetachedHead) return Task.FromResult(GitCommandResult.Failure("HEAD 处于 detached 状态，无法提交", GitServiceError.DetachedHead));
        var msg = string.IsNullOrWhiteSpace(message) ? "wip: GUI 提交" : message.Trim();
        // D7: git commit 是写操作, 必须与 commit --files / reset / revert / switch 共用仓库锁
        return WithRepoLockAsync(ctx.RepositoryRoot, ct,
            t => RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "commit", "-m", msg));
    }

    /// <summary>确保仓库有首个 commit(unborn HEAD 时自动创建空提交)。</summary>
    /// <remarks>
    /// <para>当前无生产调用方(全仓没有任何 <c>git init</c> 调用)。保留是因为用户可能已手动 <c>git init</c>
    /// 出一个空仓库, 而提交 / 检查点 / 回滚都要求 HEAD 落在具体 commit 上。</para>
    /// <para><b>doctor 的相关文案已失实</b>(声称会自动初始化仓库); 该文案在 <c>Program.cs</c>,
    /// 不在本文件, 需另行修正。</para>
    /// </remarks>
    public GitCommandResult EnsureInitialCommit(GitWorkspaceContext ctx) =>
        EnsureInitialCommitAsync(ctx).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="EnsureInitialCommit"/>。</summary>
    public Task<GitCommandResult> EnsureInitialCommitAsync(GitWorkspaceContext ctx, CancellationToken ct = default)
    {
        if (!ctx.IsValidRepo) return Task.FromResult(GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository));
        if (!ctx.IsEmptyRepo) return Task.FromResult(GitCommandResult.Success("已有 commit"));

        // 空仓库: 尝试 stage 所有文件(若有), 允许 empty commit
        return WithRepoLockAsync(ctx.RepositoryRoot, ct, async t =>
        {
            var stageResult = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "add", "-A").ConfigureAwait(false);
            if (!stageResult.Succeeded)
            {
                Log.Warn("Git", $"首次提交 stage 失败: {stageResult.Stderr}");
            }

            var commitResult = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "commit", "--allow-empty", "-m", "chore: initial commit").ConfigureAwait(false);
            if (commitResult.Succeeded)
            {
                Log.Info("Git", "已创建初始提交");
            }
            return commitResult;
        });
    }

    /// <summary>提交指定文件(相对仓库根的相对路径): stage + commit, 返回新 commit SHA。)</summary>
    public GitCommandResult CommitFiles(GitWorkspaceContext ctx, IReadOnlyList<string> relativePaths, string message) =>
        CommitFilesAsync(ctx, relativePaths, message).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="CommitFiles"/>。</summary>
    public Task<GitCommandResult> CommitFilesAsync(GitWorkspaceContext ctx, IReadOnlyList<string> relativePaths, string message, CancellationToken ct = default)
    {
        if (!ctx.IsValidRepo) return Task.FromResult(GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository));
        if (ctx.IsDetachedHead) return Task.FromResult(GitCommandResult.Failure("HEAD 处于 detached 状态，无法提交", GitServiceError.DetachedHead));
        if (relativePaths.Count == 0) return Task.FromResult(GitCommandResult.Failure("文件列表为空", GitServiceError.InvalidArgument));

        return WithRepoLockAsync(ctx.RepositoryRoot, ct, async t =>
        {
            // stage 指定文件
            foreach (var p in relativePaths)
            {
                var r = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "add", "--", p).ConfigureAwait(false);
                if (!r.Succeeded)
                    return GitCommandResult.Failure($"stage 失败: {p} - {r.Stderr}", GitServiceError.CommandFailed);
            }

            var msg = string.IsNullOrWhiteSpace(message) ? "wip: AI 提交" : message.Trim();
            var commitResult = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "commit", "-m", msg).ConfigureAwait(false);
            if (!commitResult.Succeeded)
                return commitResult;

            // 获取新 commit SHA
            var shaResult = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "rev-parse", "HEAD").ConfigureAwait(false);
            return shaResult.Succeeded ? GitCommandResult.Success(shaResult.Stdout.Trim()) : commitResult;
        });
    }

    /// <summary>在指定 commit 上创建轻量标签作为检查点, 并持久化记录。)</summary>
    public GitCommandResult MarkCheckpoint(GitWorkspaceContext ctx, GitCheckpointRecord record) =>
        MarkCheckpointAsync(ctx, record).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="MarkCheckpoint"/>。</summary>
    /// <remarks>
    /// 标签名已存在时<b>直接拒绝</b>而不是删了重建: 检查点 tag 是 Reset/Revert/Fork 的<b>唯一回滚依据</b>,
    /// 静默覆盖等于抹掉既有检查点的可回滚性。当前调用点都用 GUID 8 位 id, 实际不会撞名;
    /// 但若将来 id 生成策略变化(截断/序号/用户输入), 无条件覆盖就会静默毁掉既有检查点。
    /// </remarks>
    public Task<GitCommandResult> MarkCheckpointAsync(GitWorkspaceContext ctx, GitCheckpointRecord record, CancellationToken ct = default)
    {
        if (!ctx.IsValidRepo) return Task.FromResult(GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository));
        if (string.IsNullOrWhiteSpace(record.CommitSha)) return Task.FromResult(GitCommandResult.Failure("CommitSha 为空", GitServiceError.InvalidArgument));
        if (string.IsNullOrWhiteSpace(record.TagName)) return Task.FromResult(GitCommandResult.Failure("TagName 为空", GitServiceError.InvalidArgument));

        // D7: git tag 会改动 refs, 属写操作, 必须与 commit / reset / revert / switch 共用仓库锁,
        // 否则"检查是否已存在"与"创建标签"之间可被另一个写操作穿插
        return WithRepoLockAsync(ctx.RepositoryRoot, ct, async t =>
        {
            // 验证 commit 存在
            var verify = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "cat-file", "-t", record.CommitSha).ConfigureAwait(false);
            if (!verify.Succeeded || !verify.Stdout.Trim().Equals("commit", StringComparison.OrdinalIgnoreCase))
            {
                return GitCommandResult.Failure($"Commit 不存在或非 commit 对象: {record.CommitSha}", GitServiceError.InvalidArgument);
            }

            // E14: 同名标签已存在 → 拒绝, 绝不删除重建(检查点 tag 是唯一回滚依据)
            var existing = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "tag", "-l", record.TagName).ConfigureAwait(false);
            if (existing.Succeeded && !string.IsNullOrWhiteSpace(existing.Stdout))
            {
                return GitCommandResult.Failure(
                    $"检查点标签已存在, 拒绝覆盖: {record.TagName}(检查点 tag 是唯一回滚依据, 不可重建覆盖)",
                    GitServiceError.InvalidArgument);
            }

            var tagResult = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "tag", record.TagName, record.CommitSha).ConfigureAwait(false);
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
        });
    }

    /// <summary>获取两个 commit 间的 diff(完整 patch)。)</summary>
    /// <summary>删除一个检查点标签。仅供 <see cref="GitCheckpointStore"/> 淘汰记录时回收对应 tag —
    /// 记录被淘汰后 tag 若留着, <c>doctor</c> 会一直把它报成"可清理的遗留产物"。
    /// 同步版(给 <see cref="GitCheckpointStore.TagDeleter"/> 委托用)。</summary>
    public bool DeleteCheckpointTag(string repositoryRoot, string tagName) =>
        DeleteCheckpointTagAsync(repositoryRoot, tagName).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="DeleteCheckpointTag"/>。tag 不存在视为成功(幂等)。</summary>
    public async Task<bool> DeleteCheckpointTagAsync(string repositoryRoot, string tagName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || string.IsNullOrWhiteSpace(tagName))
        {
            return false;
        }

        // git tag -d 同样改 refs, 必须走仓库锁
        return await WithRepoLockAsync(repositoryRoot, ct, async t =>
        {
            var result = await RunAsync(repositoryRoot, DefaultTimeout, t, "tag", "-d", tagName).ConfigureAwait(false);
            if (result.Succeeded)
            {
                Log.Info("Git", $"已回收检查点标签: {tagName}");
                return true;
            }

            // tag 已经不在了(可能用户手工删过)也算达成目的
            if (result.Stderr.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            Log.Warn("Git", $"删除检查点标签失败: {tagName}: {result.Stderr}");
            return false;
        }).ConfigureAwait(false);
    }

    public GitCommandResult GetDiff(GitWorkspaceContext ctx, string fromSha, string toSha) =>
        GetDiffAsync(ctx, fromSha, toSha).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="GetDiff"/>。</summary>
    public Task<GitCommandResult> GetDiffAsync(GitWorkspaceContext ctx, string fromSha, string toSha, CancellationToken ct = default)
    {
        if (!ctx.IsValidRepo) return Task.FromResult(GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository));
        if (!TryNormalizeRevisionRange(fromSha, toSha, out var range, out var invalid))
        {
            return Task.FromResult(invalid);
        }
        return RunAsync(ctx.RepositoryRoot, DefaultTimeout, ct, "diff", range);
    }

    /// <summary>获取 diff 统计信息。)</summary>
    public GitCommandResult GetDiffStat(GitWorkspaceContext ctx, string fromSha, string toSha) =>
        GetDiffStatAsync(ctx, fromSha, toSha).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="GetDiffStat"/>。</summary>
    public Task<GitCommandResult> GetDiffStatAsync(GitWorkspaceContext ctx, string fromSha, string toSha, CancellationToken ct = default)
    {
        if (!ctx.IsValidRepo) return Task.FromResult(GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository));
        if (!TryNormalizeRevisionRange(fromSha, toSha, out var range, out var invalid))
        {
            return Task.FromResult(invalid);
        }
        return RunAsync(ctx.RepositoryRoot, DefaultTimeout, ct, "diff", "--stat", range);
    }

    /// <summary>
    /// 校验并归一化 diff 的 rev 区间, 拒绝任何会被 git 当作<b>选项</b>解析的输入。
    /// </summary>
    /// <remarks>
    /// B4: from/to 会被拼成单个 argv(<c>{from}..{to}</c>)。git diff 支持
    /// <c>--output=&lt;path&gt;</c> / <c>--ext-diff</c> / <c>--textconv</c> 等选项,
    /// 因此 <c>--output=/tmp/x</c> 这类值会被解析成选项, 把 diff 内容写进任意文件。
    /// 这里在服务层做白名单校验, 与 <c>AgentToolFactory.GitDiffTool</c> 里的校验构成纵深防御。
    /// </remarks>
    private static bool TryNormalizeRevisionRange(string fromSha, string toSha, out string range, out GitCommandResult invalid)
    {
        invalid = default!;
        range = string.Empty;

        var from = fromSha?.Trim() ?? string.Empty;
        var to = toSha?.Trim() ?? string.Empty;

        // 长度上限: 单个 argv 不应无界, 也能挡住超长输入
        if (from.Length == 0 || to.Length == 0 || from.Length > 256 || to.Length > 256)
        {
            invalid = GitCommandResult.Failure(
                $"非法的 rev 参数: from={fromSha}, to={toSha}(只允许 HEAD / 十六进制 sha / 不以 - 开头的 ref)",
                GitServiceError.InvalidArgument);
            return false;
        }

        if (!IsSafeRevisionToken(from) || !IsSafeRevisionToken(to))
        {
            invalid = GitCommandResult.Failure(
                $"非法的 rev 参数: from={fromSha}, to={toSha}(只允许 HEAD / 十六进制 sha / 不以 - 开头的 ref)",
                GitServiceError.InvalidArgument);
            return false;
        }

        range = $"{from}..{to}";
        return true;
    }

    /// <summary>
    /// rev token 白名单: 首字符必须是字母或数字, 其余只允许 <c>[A-Za-z0-9._/-~^]</c>, 长度 ≤ 256。
    /// </summary>
    /// <remarks>
    /// 关键点是<b>禁止首字符为 <c>-</c></b>——这是 git 把值当成选项(而非 rev)的唯一入口,
    /// 挡掉它就挡掉了 <c>--output=&lt;path&gt;</c> / <c>--ext-diff</c> 这类注入。
    /// 其余白名单字符同时挡掉空白与 shell 元字符(虽然本层已用 <c>ArgumentList</c> 不经过 shell, 仍作纵深防御)。
    /// 保留 <c>~</c> / <c>^</c> 是为了不破坏 <c>HEAD~3</c> / <c>main^</c> 这类合法 rev。
    /// </remarks>
    private static bool IsSafeRevisionToken(string token)
    {
        if (token.Length == 0 || token.Length > 256) return false;
        if (!char.IsAsciiLetterOrDigit(token[0])) return false;
        foreach (var ch in token)
        {
            var ok = char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '/' or '-' or '~' or '^';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Fork: 从检查点 commit 创建新分支并切换(要求工作区干净、同仓库、目标分支不存在)。)</summary>
    public GitCommandResult Fork(GitWorkspaceContext ctx, GitCheckpointRecord checkpoint, string newBranchName) =>
        ForkAsync(ctx, checkpoint, newBranchName).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="Fork"/>。</summary>
    public Task<GitCommandResult> ForkAsync(GitWorkspaceContext ctx, GitCheckpointRecord checkpoint, string newBranchName, CancellationToken ct = default)
    {
        if (!ctx.IsValidRepo) return Task.FromResult(GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository));
        if (ctx.IsDetachedHead) return Task.FromResult(GitCommandResult.Failure("HEAD 处于 detached 状态，无法 Fork", GitServiceError.DetachedHead));
        if (string.IsNullOrWhiteSpace(newBranchName)) return Task.FromResult(GitCommandResult.Failure("分支名为空", GitServiceError.InvalidArgument));
        if (!string.Equals(ctx.RepositoryRoot, checkpoint.RepositoryRoot, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(GitCommandResult.Failure("检查点不属于当前仓库", GitServiceError.InvalidArgument));
        if (!string.Equals(ctx.BranchName, checkpoint.BranchName, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(GitCommandResult.Failure($"当前分支({ctx.BranchName})与检查点分支({checkpoint.BranchName})不一致", GitServiceError.InvalidArgument));

        return WithRepoLockAsync(ctx.RepositoryRoot, ct, async t =>
        {
            // D6: 工作区干净性必须在锁内校验。若放在临界区之外, 校验通过到进入临界区之间
            // 工作区可被并发子代理改动, 随后 git switch -c 会把这些未预期变更一起带到新分支。
            var cleanCheck = await IsCleanAsync(ctx, t).ConfigureAwait(false);
            if (!cleanCheck.Succeeded) return cleanCheck;

            // 检查目标分支是否已存在
            var branchCheck = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "rev-parse", "--verify", $"refs/heads/{newBranchName}").ConfigureAwait(false);
            if (branchCheck.Succeeded)
            {
                return GitCommandResult.Failure($"分支已存在: {newBranchName}", GitServiceError.InvalidArgument);
            }

            // 创建并切换到新分支
            var switchResult = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "switch", "-c", newBranchName, checkpoint.CommitSha).ConfigureAwait(false);
            if (!switchResult.Succeeded)
            {
                return GitCommandResult.Failure($"创建分支失败: {switchResult.Stderr}", GitServiceError.CommandFailed);
            }

            return GitCommandResult.Success($"已创建并切换到分支 {newBranchName} (基于 {checkpoint.ShortSha})");
        });
    }

    /// <summary>Hard Reset 到检查点 commit(仅允许祖先、工作区干净)。)</summary>
    public GitCommandResult ResetHardToCheckpoint(GitWorkspaceContext ctx, GitCheckpointRecord checkpoint) =>
        ResetHardToCheckpointAsync(ctx, checkpoint).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="ResetHardToCheckpoint"/>。</summary>
    public Task<GitCommandResult> ResetHardToCheckpointAsync(GitWorkspaceContext ctx, GitCheckpointRecord checkpoint, CancellationToken ct = default)
    {
        if (!ctx.IsValidRepo) return Task.FromResult(GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository));
        if (ctx.IsDetachedHead) return Task.FromResult(GitCommandResult.Failure("HEAD 处于 detached 状态，无法重置", GitServiceError.DetachedHead));
        if (!string.Equals(ctx.RepositoryRoot, checkpoint.RepositoryRoot, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(GitCommandResult.Failure("检查点不属于当前仓库", GitServiceError.InvalidArgument));
        if (!string.Equals(ctx.BranchName, checkpoint.BranchName, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(GitCommandResult.Failure($"当前分支({ctx.BranchName})与检查点分支({checkpoint.BranchName})不一致", GitServiceError.InvalidArgument));

        return WithRepoLockAsync(ctx.RepositoryRoot, ct, async t =>
        {
            // D6: 干净性与祖先校验必须在锁内完成。若放在临界区之外, 校验通过到 reset --hard 之间
            // 仍可能被并发子代理写入新文件, 而 reset --hard 会把这些"校验之后才出现"的改动直接抹掉。
            var cleanCheck = await IsCleanAsync(ctx, t).ConfigureAwait(false);
            if (!cleanCheck.Succeeded) return cleanCheck;

            // 验证 checkpoint 是当前 HEAD 的祖先
            var ancestorCheck = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "merge-base", "--is-ancestor", checkpoint.CommitSha, "HEAD").ConfigureAwait(false);
            if (!ancestorCheck.Succeeded)
            {
                return GitCommandResult.Failure("检查点不是当前 HEAD 的祖先，无法硬重置", GitServiceError.NotAncestor);
            }

            var resetResult = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "reset", "--hard", checkpoint.CommitSha).ConfigureAwait(false);
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
        });
    }

    /// <summary>Revert 到检查点: git revert --no-commit <checkpoint>..HEAD 后创建还原 commit(仅允许祖先、工作区干净)。</summary>
    public GitCommandResult RevertToCheckpoint(GitWorkspaceContext ctx, GitCheckpointRecord checkpoint, string? revertMessage = null) =>
        RevertToCheckpointAsync(ctx, checkpoint, revertMessage).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="RevertToCheckpoint"/>。</summary>
    public Task<GitCommandResult> RevertToCheckpointAsync(GitWorkspaceContext ctx, GitCheckpointRecord checkpoint, string? revertMessage = null, CancellationToken ct = default)
    {
        if (!ctx.IsValidRepo) return Task.FromResult(GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository));
        if (ctx.IsDetachedHead) return Task.FromResult(GitCommandResult.Failure("HEAD 处于 detached 状态，无法 revert", GitServiceError.DetachedHead));
        if (!string.Equals(ctx.RepositoryRoot, checkpoint.RepositoryRoot, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(GitCommandResult.Failure("检查点不属于当前仓库", GitServiceError.InvalidArgument));
        if (!string.Equals(ctx.BranchName, checkpoint.BranchName, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(GitCommandResult.Failure($"当前分支({ctx.BranchName})与检查点分支({checkpoint.BranchName})不一致", GitServiceError.InvalidArgument));

        return WithRepoLockAsync(ctx.RepositoryRoot, ct, async t =>
        {
            // D6: 干净性与祖先校验必须在锁内完成(revert 会提交一整个还原 commit,
            // 若校验在临界区之外, 校验之后新出现的改动会被一并卷进这次 revert)
            var cleanCheck = await IsCleanAsync(ctx, t).ConfigureAwait(false);
            if (!cleanCheck.Succeeded) return cleanCheck;

            // 验证 checkpoint 是当前 HEAD 的祖先
            var ancestorCheck = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "merge-base", "--is-ancestor", checkpoint.CommitSha, "HEAD").ConfigureAwait(false);
            if (!ancestorCheck.Succeeded)
            {
                return GitCommandResult.Failure("检查点不是当前 HEAD 的祖先，无法 revert", GitServiceError.NotAncestor);
            }

            // 使用 --no-commit 批量 revert 从 checkpoint 到 HEAD 的所有提交
            var range = $"{checkpoint.CommitSha}..HEAD";
            var revertResult = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "revert", "--no-commit", range).ConfigureAwait(false);
            if (!revertResult.Succeeded)
            {
                // 尝试恢复: revert --abort
                var abort = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "revert", "--abort").ConfigureAwait(false);
                Log.Warn("Git", $"revert 失败并尝试 abort: {revertResult.Stderr}, abort={abort.Succeeded}");
                return GitCommandResult.Failure($"revert 失败: {revertResult.Stderr}", GitServiceError.CommandFailed);
            }

            // 提交还原
            var msg = string.IsNullOrWhiteSpace(revertMessage)
                ? $"revert: 还原到检查点 {checkpoint.ShortSha} ({checkpoint.Label})"
                : revertMessage.Trim();

            var commitResult = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "commit", "-m", msg).ConfigureAwait(false);
            if (!commitResult.Succeeded)
            {
                // 恢复: revert --no-commit 不移动 HEAD, 因此必须回到 HEAD(而非 HEAD@{1}, 那会多回退一个提交),
                // 同时清理 revert 遗留的 sequencer 状态。
                var quit = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "revert", "--quit").ConfigureAwait(false);
                var reset = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "reset", "--hard", "HEAD").ConfigureAwait(false);
                Log.Warn("Git", $"revert 提交失败并尝试恢复: {commitResult.Stderr}, quit={quit.Succeeded}, reset={reset.Succeeded}");
                return GitCommandResult.Failure($"revert 提交失败: {commitResult.Stderr}", GitServiceError.CommandFailed);
            }

            // 获取新 commit SHA
            var shaResult = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, t, "rev-parse", "HEAD").ConfigureAwait(false);
            var newSha = shaResult.Succeeded ? shaResult.Stdout.Trim() : string.Empty;

            // 更新检查点记录
            checkpoint.LastRollbackMode = CheckpointRollbackMode.Revert;
            checkpoint.LastRollbackAt = DateTime.Now;
            checkpoint.LastRollbackCommitSha = newSha;
            _checkpointStore.Save(checkpoint);

            return GitCommandResult.Success($"已创建还原提交 {newSha[..Math.Min(8, newSha.Length)]}");
        });
    }

    /// <summary>获取本地分支列表。)</summary>
    public IReadOnlyList<string> GetLocalBranches(GitWorkspaceContext ctx) =>
        GetLocalBranchesAsync(ctx).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="GetLocalBranches"/>。</summary>
    public async Task<IReadOnlyList<string>> GetLocalBranchesAsync(GitWorkspaceContext ctx, CancellationToken ct = default)
    {
        if (!ctx.IsValidRepo) return [];
        var r = await RunAsync(ctx.RepositoryRoot, DefaultTimeout, ct, "branch", "--format=%(refname:short)").ConfigureAwait(false);
        if (!r.Succeeded) return [];
        return r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>获取提交图谱。)</summary>
    public IReadOnlyList<GitGraphLine> GetCommitGraph(GitWorkspaceContext ctx, int limit = 60) =>
        GetCommitGraphAsync(ctx, limit).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="GetCommitGraph"/>。</summary>
    public async Task<IReadOnlyList<GitGraphLine>> GetCommitGraphAsync(GitWorkspaceContext ctx, int limit = 60, CancellationToken ct = default)
    {
        if (!ctx.IsValidRepo) return [];
        // -n 与数值必须分两个 argv: 之前合成单个 "-n 60" 依赖 git 短选项解析的未文档化行为,
        // 且失败时静默返回空图谱。--all 改为仅当前分支, 避免检查点 tag 膨胀后遍历全部 ref。
        // C7: 用专用 30s 超时(DefaultTimeout 15s 对大仓库的 git log --graph 偏紧, 会静默退化成空图谱)
        var r = await RunAsync(ctx.RepositoryRoot, GraphTimeout, ct, "log", "--graph", "--no-color",
            "--pretty=format:%x01%h %s", "-n", limit.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        if (!r.Succeeded)
        {
            Log.Warn("Git", $"获取提交图谱失败: {r.Stderr.Trim()}");
            return [];
        }

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
    public string? GetCurrentBranch(string repositoryRoot) =>
        GetCurrentBranchAsync(repositoryRoot).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="GetCurrentBranch"/>。</summary>
    public async Task<string?> GetCurrentBranchAsync(string repositoryRoot, CancellationToken ct = default)
    {
        var r = await RunAsync(repositoryRoot, DefaultTimeout, ct, "symbolic-ref", "--short", "HEAD").ConfigureAwait(false);
        return r.Succeeded ? r.Stdout.Trim() : null;
    }

    /// <summary>检查是否为空仓库(无任何 commit)。</summary>
    public bool IsEmptyRepository(string repositoryRoot) =>
        IsEmptyRepositoryAsync(repositoryRoot).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="IsEmptyRepository"/>。</summary>
    public async Task<bool> IsEmptyRepositoryAsync(string repositoryRoot, CancellationToken ct = default)
    {
        var r = await RunAsync(repositoryRoot, DefaultTimeout, ct, "rev-parse", "--verify", "HEAD").ConfigureAwait(false);
        return !r.Succeeded; // unborn HEAD 返回失败
    }

    /// <summary>获取仓库当前 HEAD 的完整 SHA；空仓库或失败时返回 null。</summary>
    public string? GetHeadSha(string repositoryRoot) =>
        GetHeadShaAsync(repositoryRoot).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="GetHeadSha"/>。</summary>
    public async Task<string?> GetHeadShaAsync(string repositoryRoot, CancellationToken ct = default)
    {
        var result = await RunAsync(repositoryRoot, DefaultTimeout, ct, "rev-parse", "HEAD").ConfigureAwait(false);
        return result.Succeeded ? result.Stdout.Trim() : null;
    }

    /// <summary>切换当前 worktree 到指定本地分支。</summary>
    public GitCommandResult SwitchBranch(GitWorkspaceContext context, string branch) =>
        SwitchBranchAsync(context, branch).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="SwitchBranch"/>。</summary>
    public Task<GitCommandResult> SwitchBranchAsync(GitWorkspaceContext context, string branch, CancellationToken ct = default)
    {
        if (!context.IsValidRepo) return Task.FromResult(GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository));
        if (string.IsNullOrWhiteSpace(branch)) return Task.FromResult(GitCommandResult.Failure("分支名为空", GitServiceError.InvalidArgument));
        var target = branch.Trim();
        return WithRepoLockAsync(context.RepositoryRoot, ct,
            t => RunAsync(context.RepositoryRoot, DefaultTimeout, t, "switch", target));
    }

    /// <summary>从当前上游拉取。</summary>
    public GitCommandResult Pull(GitWorkspaceContext context) =>
        PullAsync(context).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="Pull"/>。</summary>
    public Task<GitCommandResult> PullAsync(GitWorkspaceContext context, CancellationToken ct = default) =>
        context.IsValidRepo
            ? RunAsync(context.RepositoryRoot, NetworkTimeout, ct, "pull")
            : Task.FromResult(GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository));

    /// <summary>推送当前分支到已配置上游。</summary>
    public GitCommandResult Push(GitWorkspaceContext context) =>
        PushAsync(context).GetAwaiter().GetResult();

    /// <summary>异步版 <see cref="Push"/>。</summary>
    public Task<GitCommandResult> PushAsync(GitWorkspaceContext context, CancellationToken ct = default) =>
        context.IsValidRepo
            ? RunAsync(context.RepositoryRoot, NetworkTimeout, ct, "push")
            : Task.FromResult(GitCommandResult.Failure("非 Git 仓库", GitServiceError.NotARepository));

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

    /// <summary>
    /// 同步执行 git 命令(内部方法) —— <b>UI 线程的兼容层</b>。
    /// </summary>
    /// <remarks>
    /// <para>底层 <see cref="RunAsync"/> 已是真正非阻塞的; 这里用
    /// <c>GetAwaiter().GetResult()</c> 把调用线程阻塞到 git 退出。
    /// 保留同步版本是为了不级联改动全部调用方(ChatPageViewModel / GitPanelViewModel /
    /// StatusPanelViewModel / AgentToolFactory / RosterBuilder)。
    /// <b>新代码与 UI 线程必须改用各 <c>*Async</c> 方法</b>: 一次发消息前会连起约 6 个 git 进程,
    /// 同步版最坏会把界面冻结数十秒。</para>
    /// <para><c>GetAwaiter().GetResult()</c> 不会死锁, 因为 <see cref="RunAsync"/> 内部所有 await
    /// 都带 <c>ConfigureAwait(false)</c>, 续体不会回到 UI 的 SynchronizationContext。</para>
    /// </remarks>
    private GitCommandResult Run(string repositoryRoot, params string[] args)
    {
        return Run(repositoryRoot, DefaultTimeout, args);
    }

    /// <summary>默认只读/轻量命令的超时(秒)。</summary>
    private const int DefaultTimeout = 15;

    /// <summary>涉及网络或大仓库的命令使用更长超时(秒)。</summary>
    private const int NetworkTimeout = 60;

    /// <summary>
    /// <c>git log --graph</c> 的专用超时(秒)。
    /// </summary>
    /// <remarks>
    /// C7: 大仓库上 <c>git log --graph</c> 走 15s 的 <see cref="DefaultTimeout"/> 偏紧,
    /// 超时会静默返回空图谱(只留一条 Warn), 用户看不出是超时而不是"没有提交"。
    /// 只放宽这一条命令, <see cref="NetworkTimeout"/>(pull/push 的 60s 网络上限)是有意设定, 不动。
    /// </remarks>
    private const int GraphTimeout = 30;

    /// <summary>取仓库锁并在其内执行 <paramref name="body"/>; 释放锁在 finally 中完成。</summary>
    /// <remarks>
    /// 异步版取锁用 <c>WaitAsync</c>(不阻塞线程), 因此 UI 线程上的写操作也不会因为
    /// 别的会话正持有该仓库锁而冻结界面。
    /// <para><b>不可重入</b>: 锁不可重入, 因此 <paramref name="body"/> 内禁止再调用任何会取锁的方法
    /// (会永久挂起)。</para>
    /// </remarks>
    private async Task<GitCommandResult> WithRepoLockAsync(string repositoryRoot, CancellationToken ct,
        Func<CancellationToken, Task<GitCommandResult>> body) =>
        await WithRepoLockAsync<GitCommandResult>(repositoryRoot, ct, body).ConfigureAwait(false);

    /// <summary>泛型版仓库锁。用信号量而非 reentrant lock, 避免"持锁者再次取同仓库锁"直接死锁
    /// (那种情况会挂死在 <c>Wait</c> 上, 连超时都救不回来)。</summary>
    private async Task<T> WithRepoLockAsync<T>(string repositoryRoot, CancellationToken ct,
        Func<CancellationToken, Task<T>> body)
    {
        var sem = GetRepoLock(repositoryRoot);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await body(ct).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    private GitCommandResult Run(string repositoryRoot, int timeoutSeconds, params string[] args)
    {
        return RunAsync(repositoryRoot, timeoutSeconds, CancellationToken.None, args).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 真正异步地执行 git 命令(内部方法): 整个等待过程不占用调用线程。
    ///
    /// <para>安全约束(此前缺失, 会导致 UI 永久冻结):
    /// <list type="bullet">
    /// <item>设置 <c>GIT_TERMINAL_PROMPT=0</c> / <c>GCM_INTERACTIVE=never</c>,
    /// 使需要凭据的 pull/push 直接失败而不是挂起等待终端输入。</item>
    /// <item><b>超时必需</b>: 调用点不止 UI 线程——<c>RosterBuilder.BuildGitSection</c> 每回合在
    /// 引擎线程起约 4 个 git 进程, <c>GitWorkspaceResolver</c> 也在引擎线程;
    /// 无超时会同时冻住界面与首 token 延迟。超时后强杀整棵进程树。</item>
    /// <item>先异步读干 stdout/stderr 两条管道再等退出, 避免管道缓冲区写满造成死锁。</item>
    /// </list></para>
    /// </summary>
    /// <remarks>
    /// 取消语义: <paramref name="ct"/> 被调用方取消时抛 <see cref="OperationCanceledException"/>,
    /// 与"超时"(<c>OperationCanceledException</c> 被吞掉并返回失败结果)严格区分 ——
    /// 这样 UI 的"停止"按钮才能真正中断等待, 而超时仍表现为一条可读的失败原因。
    /// </remarks>
    private async Task<GitCommandResult> RunAsync(string repositoryRoot, int timeoutSeconds, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = Path.GetFullPath(repositoryRoot),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 无 TTY: 让 git 认为不能在终端交互, 配合下面的环境变量避免凭据提示挂起
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        // 禁止一切交互式凭据提示: 无 TTY 时 git 可能退化为无限等待
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["GIT_ASKPASS"] = "echo";

        try
        {
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return GitCommandResult.Failure("git 启动失败");
            }

            // 先异步读干两条管道, 再等待退出: 避免管道缓冲区写满造成死锁
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            // 超时分支会直接返回(进程已被 kill, using 随即 Dispose 流), 两个读任务可能以异常结束。
            // 挂吞异常的续体, 避免留下"未观察任务异常"(正常路径仍会 await 它们取内容)。
            // 用块体 lambda: 只匹配 Action<Task<string>> 重载, 不会与 Func 重载产生歧义。
            _ = stdoutTask.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
            _ = stderrTask.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);

            // 超时与调用方取消合并到同一个 token, 但二者的处理方式必须不同(见 remarks)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 调用方主动取消: 原样上抛(不吞), 否则 UI 无法区分"用户停止"与"git 卡住"
                ct.ThrowIfCancellationRequested();
                TryKill(process);
                return GitCommandResult.Failure(
                    $"git 命令超时({timeoutSeconds}s): git {string.Join(' ', args)}");
            }

            return new GitCommandResult
            {
                ExitCode = process.ExitCode,
                Stdout = await stdoutTask.ConfigureAwait(false),
                Stderr = await stderrTask.ConfigureAwait(false)
            };
        }
        catch (OperationCanceledException)
        {
            // 必须排在 catch(Exception) 之前: 否则"用户停止"会被降级成一条普通失败结果
            throw;
        }
        catch (Exception ex)
        {
            return GitCommandResult.Failure(ex.Message);
        }
    }

    /// <summary>强杀 git 进程树(超时/取消时调用)。</summary>
    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能已退出或无权限; 忽略, 由超时错误信息兜底
        }
    }

    /// <summary>
    /// 扫描已废弃的 Git 步骤机制残留(旧版本子代理自动建分支功能产生的数据)。
    /// 子代理现已统一在当前分支工作, 这些数据不再被任何代码读取, 仅供用户判断是否手动清理。
    /// </summary>
    /// <remarks>
    /// 只读诊断: 任何 git 调用失败都退化为空结果, 绝不抛出。
    /// 注意 <c>ai-shikikan/checkpoint/*</c> tag 是<b>当前</b>检查点系统(Reset/Revert/Fork)的正常工作产物,
    /// 只是历史累积, 报告方需提示用户自行判断, 不可自动删除。
    /// </remarks>
    /// <param name="workDir">工作目录(内部会向上解析仓库根)。</param>
    public LegacyArtifactScanResult ScanLegacyArtifacts(string workDir)
    {
        try
        {
            var staleBranches = new List<string>();
            var stepsFileCount = 0;
            var checkpointTagCount = 0;

            // steps/*.json: 旧步骤状态文件目录(AppPaths.StepsDir 已随机制移除, 此处自行拼接)
            try
            {
                var stepsDir = Path.Combine(AppPaths.DataDir, "steps");
                if (Directory.Exists(stepsDir))
                {
                    stepsFileCount = Directory.EnumerateFiles(stepsDir, "*.json", SearchOption.AllDirectories).Count();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Git", ex, "统计遗留 steps/*.json 失败, 按 0 处理");
            }

            var repoRoot = FindRepositoryRoot(workDir);
            if (string.IsNullOrEmpty(repoRoot))
            {
                // 非 git 目录: 只能统计数据目录, git 相关项留空
                return new LegacyArtifactScanResult { StepsFileCount = stepsFileCount };
            }

            // ac/* 分支: for-each-ref 比 branch --list 更稳(不依赖通配匹配/当前分支状态)
            try
            {
                var branchResult = Run(repoRoot, "for-each-ref", "--format=%(refname:short)", "refs/heads/ac/");
                if (branchResult.Succeeded)
                {
                    staleBranches.AddRange(branchResult.Stdout
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0));
                }
                else
                {
                    Log.Warn("Git", $"扫描遗留 ac/* 分支失败, 按空处理: {branchResult.Stderr.Trim()}");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Git", ex, "扫描遗留 ac/* 分支失败, 按空处理");
            }

            // 检查点 tag: 当前检查点系统的正常产物, 仅统计数量供用户判断
            try
            {
                var tagResult = Run(repoRoot, "tag", "--list", "ai-shikikan/checkpoint/*");
                if (tagResult.Succeeded)
                {
                    checkpointTagCount = tagResult.Stdout
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Count(s => s.Trim().Length > 0);
                }
                else
                {
                    Log.Warn("Git", $"统计检查点 tag 失败, 按 0 处理: {tagResult.Stderr.Trim()}");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Git", ex, "统计检查点 tag 失败, 按 0 处理");
            }

            return new LegacyArtifactScanResult
            {
                StaleBranches = staleBranches,
                StepsFileCount = stepsFileCount,
                CheckpointTagCount = checkpointTagCount
            };
        }
        catch (Exception ex)
        {
            // doctor 诊断不应因残留扫描异常而中断
            Log.Warn("Git", ex, "扫描废弃 Git 步骤机制残留失败, 返回空结果");
            return new LegacyArtifactScanResult();
        }
    }

    /// <summary>释放仓库锁。</summary>
    /// <remarks>
    /// <b>当前无生产调用方</b>: <c>CommanderRuntime</c> 不实现 <c>IDisposable</c>, 也没有任何地方
    /// 调用 <c>GitService.Dispose()</c>。由于 <c>GitService</c> 是进程级单例(生命周期 = 应用),
    /// 不释放不会造成实际泄漏; 但若将来给 <c>CommanderRuntime</c> 加统一释放, 这里即可生效。
    /// 另注: 在有并发持有者时 Dispose 会让 <c>SemaphoreSlim.Release()</c> 抛
    /// <c>ObjectDisposedException</c>, 因此补调用方时必须先确保无在途 git 操作。
    /// </remarks>
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

/// <summary>废弃 Git 步骤机制(旧版本子代理自动建分支)的残留统计。</summary>
/// <remarks>
/// 历史上定义在 <c>GitService.cs</c> 文件末尾(服务类之外), 位置确实怪异: 它是一个纯数据类型,
/// 语义上应与 <c>GitCommandResult</c> / <c>GitFileStatus</c> / <c>GitGraphLine</c> 一起放在 <c>GitTypes.cs</c>。
/// 目前它未注册进 <c>AppJsonContext</c>, 迁移本身是安全的; 但迁移会牵动多个 agent 的文件清单,
/// 收益不抵风险, 因此暂不迁移, 仅在此标注以便后续单独处理。
/// </remarks>
public sealed class LegacyArtifactScanResult
{
    /// <summary>是否检测到任何残留。</summary>
    public bool HasAny => StaleBranches.Count > 0 || StepsFileCount > 0 || CheckpointTagCount > 0;

    /// <summary>遗留的 ac/* 分支名。</summary>
    public List<string> StaleBranches { get; init; } = [];

    /// <summary>遗留的 steps/*.json 文件数。</summary>
    public int StepsFileCount { get; init; }

    /// <summary>遗留的 ai-shikikan/checkpoint/* tag 数量(当前检查点系统的正常产物, 非废弃数据)。</summary>
    public int CheckpointTagCount { get; init; }
}