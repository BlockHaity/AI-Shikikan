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

/// <summary>基于 git CLI 的工作区解析(与 GitStepService 同为进程外调用, AOT 安全)。</summary>
public sealed class GitWorkspaceResolver : IWorkspaceResolver
{
    private readonly string _defaultRoot;

    public GitWorkspaceResolver(string defaultRoot)
    {
        _defaultRoot = Normalize(string.IsNullOrWhiteSpace(defaultRoot) ? "." : defaultRoot);
    }

    public string ResolveWorkTreeRoot(string dir)
    {
        var full = Normalize(string.IsNullOrWhiteSpace(dir) ? _defaultRoot : dir);
        var toplevel = GitOutput(full, "rev-parse", "--show-toplevel");
        return string.IsNullOrWhiteSpace(toplevel) ? full : Normalize(toplevel);
    }

    public string? ResolveBranch(string dir)
    {
        var full = Normalize(string.IsNullOrWhiteSpace(dir) ? _defaultRoot : dir);
        var branch = GitOutput(full, "rev-parse", "--abbrev-ref", "HEAD");
        if (string.IsNullOrWhiteSpace(branch)) return null;
        branch = branch.Trim();
        // detach HEAD 时 rev-parse 返回 "HEAD": 用字面量作为"分支", 与其它分支区分执行权
        return branch;
    }

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

    private static string? GitOutput(string dir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(dir);
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return null;

            var stdout = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(4000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 尽力终止 */ }
                return null;
            }

            return p.ExitCode == 0 ? stdout : null;
        }
        catch
        {
            return null; // git 不存在 / 目录不可用: 回退为普通目录语义
        }
    }
}
