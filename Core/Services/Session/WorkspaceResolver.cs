using System.Diagnostics;

namespace AIShikikan.Core.Services.Session;

/// <summary>工作区解析: 把任意目录解析为规范化 WorkTreeRoot 与当前分支(非 git 目录返回 null 分支)。
/// 抽象成接口便于单元级自检注入假实现。</summary>
public interface IWorkspaceResolver
{
    /// <summary>解析目录所属 git 工作树根(非 git 仓库时返回该目录自身的规范化全路径)。</summary>
    string ResolveWorkTreeRoot(string dir);

    /// <summary>解析目录当前分支; 非 git 仓库/无法解析时返回 null。</summary>
    string? ResolveBranch(string dir);
}

/// <summary>基于 git CLI 的工作区解析(与 GitService 同为进程外调用, AOT 安全)。</summary>
public sealed class GitWorkspaceResolver : IWorkspaceResolver
{
    /// <summary>
    /// 解析结果缓存存活时长。ResolveWorkTreeRoot + ResolveBranch 各起一个 git 进程,
    /// 而 WorkspaceExecutionCoordinator 每回合申请执行权都要解析一次(同 worktree 同分支还允许并发,
    /// 于是同一目录会被反复解析)。2s TTL 把"每回合 2 个 git 进程"降到"每 2s 至多 2 个"。
    ///
    /// <para>代价: 用户在别处切分支后, 至多 2s 内协调器仍按旧分支算执行权键(TOCTOU 窗口)。
    /// 该窗口已是毫秒级(切分支的人为动作远慢于一次解析), 换来的是显著更少的子进程。
    /// <b>若实测发现"切分支后隔离失效", 优先把 TTL 降到 500ms</b>, 而不要去掉缓存。</para>
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

    /// <summary>缓存条目上限; 超出时先清过期项, 仍超出则整体清空(纯进程内缓存, 宁可丢不可涨)。</summary>
    private const int CacheCapacity = 256;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private readonly string _defaultRoot;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(PathComparer);

    public GitWorkspaceResolver(string defaultRoot)
    {
        _defaultRoot = Normalize(string.IsNullOrWhiteSpace(defaultRoot) ? "." : defaultRoot);
    }

    public string ResolveWorkTreeRoot(string dir) => Resolve(dir).WorkTreeRoot;

    public string? ResolveBranch(string dir) => Resolve(dir).Branch;

    /// <summary>一次解析同时给出 worktree 根与分支(两个公开入口共用, 保证两者来自同一次快照)。</summary>
    private WorkspaceKey Resolve(string dir)
    {
        var full = Normalize(string.IsNullOrWhiteSpace(dir) ? _defaultRoot : dir);

        // TickCount64 是单调时钟: 不受系统时间回拨/夏令时影响, 够用且免去 DateTime 语义负担
        var now = Environment.TickCount64;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(full, out var cached) && cached.ExpiresAt > now)
            {
                return cached.Key;
            }
        }

        // 关键: git 子进程调用绝不放进 _cacheLock, 否则一次慢的 git 会把所有解析请求堵在锁上
        var resolved = ResolveCore(full);

        lock (_cacheLock)
        {
            if (_cache.Count >= CacheCapacity) PruneCacheLocked(now);
            _cache[full] = new CacheEntry(resolved, now + (long)CacheTtl.TotalMilliseconds);
        }

        return resolved;
    }

    /// <summary>未命中缓存时的真实解析: 保持两次独立 rev-parse 调用, 不合并成一个进程,
    /// 以免改变"非 git 目录 / detach HEAD"等边界下的既有返回值语义。</summary>
    private static WorkspaceKey ResolveCore(string full)
    {
        var toplevel = GitOutput(full, "rev-parse", "--show-toplevel");
        var root = string.IsNullOrWhiteSpace(toplevel) ? full : Normalize(toplevel);

        var branch = GitOutput(full, "rev-parse", "--abbrev-ref", "HEAD");
        if (string.IsNullOrWhiteSpace(branch))
        {
            // 非 git 目录: 无分支, 退化为"该目录即工作区"
            return new WorkspaceKey(root, null);
        }

        // detach HEAD 时 rev-parse 返回 "HEAD": 用字面量作为"分支", 与其它分支区分执行权
        return new WorkspaceKey(root, branch.Trim());
    }

    /// <summary>容量超限时的淘汰: 先删已过期项, 仍超限则整体清空。调用方须持 _cacheLock。</summary>
    private void PruneCacheLocked(long now)
    {
        var expired = new List<string>();
        foreach (var (path, entry) in _cache)
        {
            if (entry.ExpiresAt <= now) expired.Add(path);
        }

        foreach (var path in expired) _cache.Remove(path);

        if (_cache.Count < CacheCapacity) return;

        _cache.Clear();
    }

    private readonly record struct CacheEntry(WorkspaceKey Key, long ExpiresAt);

    /// <summary>规范化路径: 全路径 + 去掉末尾分隔符(根目录除外)。</summary>
    public static string Normalize(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }

        if (full.Length > 1 && (full.EndsWith(Path.DirectorySeparatorChar) || full.EndsWith(Path.AltDirectorySeparatorChar)))
        {
            // Windows 盘符 "C:\" 保止单分隔符
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length > 0 &&
                !(trimmed.Length == 2 && trimmed[1] == ':'))
            {
                full = trimmed;
            }
        }

        return full;
    }

    /// <summary>git 只读解析命令的超时(毫秒)。</summary>
    private const int GitTimeoutMs = 4000;

    /// <summary>执行一次只读 git 命令并取 stdout; 非零退出 / 超时 / git 不可用一律返回 null。</summary>
    /// <remarks>
    /// <para>P1-9 修复: 原实现先同步 <c>ReadToEnd()</c> 再 <c>WaitForExit(4000)</c>, 顺序反了。
    /// 若子进程往 stderr 写超过管道缓冲区(常见于 <c>GIT_*</c> 配置异常 / 凭据 helper 报错),
    /// 进程会阻塞在 stderr 写上而永不退出; 此时 stdout 已读完但拿不到退出码, 4s 后被 kill,
    /// <b>合法结果被降级为 null</b>(调用方据此判定"非 git 目录", 连带执行权键算错)。</para>
    /// <para>现在照抄 <c>GitService.RunAsync</c> 的正确顺序: 先把两条管道异步读干, 再等退出。
    /// 另外补上 <c>RedirectStandardInput</c> 与那三个禁交互环境变量(与 <c>GitService</c> 对齐):
    /// 无 TTY 时让 git 知道不能在终端交互, 避免退化为无限等待。</para>
    /// <para>仍是同步方法(接口 <see cref="IWorkspaceResolver"/> 为同步, 改 async 会级联到
    /// <c>WorkspaceExecutionCoordinator</c>); 调用点在<b>引擎线程</b>, 由 2s 缓存摊薄。</para>
    /// </remarks>
    private static string? GitOutput(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(dir);
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            // 与 GitService.RunAsync 保持一致: 禁止一切交互式凭据提示导致的无限等待
            // (放在 try 内: 本方法承诺绝不抛出)
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GCM_INTERACTIVE"] = "never";
            psi.Environment["GIT_ASKPASS"] = "echo";

            using var p = Process.Start(psi);
            if (p is null) return null;

            // 先异步读干两条管道, 再等待退出: 避免管道缓冲区写满造成死锁
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            // 超时分支会直接返回, 此时两个读任务会因进程被 kill / 流被 Dispose 而以异常结束。
            // 挂吞异常的续体, 避免留下"未观察任务异常"; 块体 lambda 只匹配 Action 重载, 无歧义。
            _ = stdoutTask.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
            _ = stderrTask.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);

            if (!p.WaitForExit(GitTimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 尽力终止 */ }
                return null;
            }

            // WaitForExit(int) 返回时读任务可能还差几个字节, 这里同步等它收尾(已接近完成, 不会长时间阻塞)
            var stdout = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();

            return p.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch
        {
            return null; // git 不存在 / 目录不可用: 回退为普通目录语义
        }
    }
}
