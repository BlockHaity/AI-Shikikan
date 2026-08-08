namespace AIShikikan.Core.Services.Tools;

/// <summary>路径安全解析：确保解析结果始终位于工作区根目录内，禁止越界访问。</summary>
public static class ToolPathSanitizer
{
    public static string Resolve(string workspaceRoot, string inputPath)
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

        var rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.Equals(root, StringComparison.Ordinal) && !full.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"拒绝访问工作区之外: {full}");
        }

        return full;
    }

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
}