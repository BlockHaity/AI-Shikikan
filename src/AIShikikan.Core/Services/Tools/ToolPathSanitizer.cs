using AIShikikan.Core.Logging;

namespace AIShikikan.Core.Services.Tools;

/// <summary>路径安全解析：确保解析结果始终位于工作区根目录内，禁止越界访问。</summary>
public static class ToolPathSanitizer
{
    /// <summary>
    /// 平台相关的路径比较方式。Windows/macOS 文件系统默认大小写不敏感, Linux 区分大小写。
    /// 一律用 <see cref="StringComparison.Ordinal"/> 会在 Windows 上把 `C:\Repo` 与 `c:\repo\x` 判成越界 ——
    /// 方向是"拒绝"所以不漏安全, 但属纯可用性 bug(用户换个大小写就访问不到自己的仓库)。
    /// 此处与 <c>WorkspaceExecutionCoordinator.RootComparer</c> 的平台比较器策略保持一致。
    /// </summary>
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>解析工作区内的路径；越界时抛 <see cref="UnauthorizedAccessException"/>（文案面向 LLM 可读）。</summary>
    /// <param name="followLinks">
    /// 是否额外解析符号链接。**只对显式传入的 path 参数打开**: 目录遍历(glob/grep)得到的每个文件都去
    /// stat 一次符号链接会明显拖慢大仓库, 而遍历结果本身已经限定在工作区内, 加固性价比很低;
    /// 显式参数是 LLM 能完全控制的入口, 是符号链接逃逸的主要来源。
    /// </param>
    public static string Resolve(string workspaceRoot, string inputPath, bool followLinks = false)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var combined = Path.IsPathRooted(inputPath)
            ? inputPath
            : Path.Combine(root, inputPath);

        string full;
        try
        {
            full = Path.GetFullPath(combined);
        }
        catch
        {
            throw new UnauthorizedAccessException($"非法路径: {inputPath}");
        }

        EnsureInside(root, full);

        if (followLinks)
        {
            EnsureNoLinkEscape(root, full);
        }

        return full;
    }

    /// <summary>判断 <paramref name="fullPath"/> 是否位于 <paramref name="root"/> 之内（纯字符串前缀比对）。</summary>
    public static bool IsInside(string root, string fullPath)
    {
        try
        {
            Resolve(root, fullPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判断路径是否为符号链接或联接点(Windows reparse point)。遍历目录树时应跳过这类条目:
    /// 既防 `a -&gt; ..` 式的递归死循环, 也避免顺着软链读到工作区外。
    /// 取不到属性时按"是"处理(不再遍历该目录), 方向偏保守。
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    /// <summary>纯字符串前缀边界检查(已规范化、无尾部分隔符)。</summary>
    private static void EnsureInside(string root, string full)
    {
        var rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.Equals(root, PathComparison) && !full.StartsWith(rootWithSep, PathComparison))
        {
            throw new UnauthorizedAccessException($"拒绝访问工作区之外: {full}");
        }
    }

    /// <summary>
    /// 逐段解析符号链接后重新做边界检查。纯字符串前缀比对拦不住"工作区内软链指向工作区外"
    /// (最常见的是 <c>workspace/link → /etc</c>, link 还是中间目录, 只解析末段会漏掉)。
    /// root 与 full 都先归约成物理路径再比, 这样工作区根自身经软链挂载时也不会误判。
    /// </summary>
    private static void EnsureNoLinkEscape(string root, string full)
    {
        var boundary = ResolvePhysical(root);
        var target = ResolvePhysical(full);
        if (IsInside(boundary, target))
        {
            return;
        }

        throw new UnauthorizedAccessException($"拒绝访问工作区之外(符号链接指向): {target}");
    }

    /// <summary>
    /// 把绝对路径的每一段都按符号链接的最终目标归约, 得到物理路径。
    /// 成本是"路径段数次 stat", 只对显式传入的 path 参数走这条路(见 <see cref="Resolve"/> 的说明),
    /// 相对"读一个文件"的开销可以忽略。
    /// </summary>
    private static string ResolvePhysical(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;

        // 去掉盘符/根锚点再切分, 否则 Windows 上会切出一个没有意义的 "C:" 段
        foreach (var seg in full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (seg.Length == 0 || seg == ".")
            {
                continue;
            }

            if (seg == "..")
            {
                // 符号链接目标里带 .. 时(软链指向 a/../b)按物理栈回退
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            var next = Path.Combine(current, seg);
            current = TryResolveLink(next) ?? next;
        }

        return current;
    }

    /// <summary>解析符号链接/联接点的最终目标；不是链接或不存在时返回 null。
    /// File 与 Directory 两个入口都试一遍：走哪条分支取决于链接指向文件还是目录。</summary>
    private static string? TryResolveLink(string path)
    {
        try
        {
            var info = File.ResolveLinkTarget(path, returnFinalTarget: true)
                ?? Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            return info?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 解析失败(如无权限/路径过长)时按"不是链接"处理: 硬失败会误伤大量正常文件
            Log.Debug("Tool", $"符号链接解析失败, 按非链接处理: {path}");
            return null;
        }
    }
}
