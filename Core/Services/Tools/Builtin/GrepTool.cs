using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIShikikan.Core.Models;

namespace AIShikikan.Core.Services.Tools.Builtin;

public class GrepTool : ITool
{
    private const int MaxMatches = 200;
    private const int MaxDepth = 6;

    public string Name => "grep";

    public string Description =>
        "在工作区内按正则表达式搜索文件内容, 返回 文件:行号:匹配行。参数: " +
        "pattern(必填, 正则), path(可选, 限定子目录/文件), case_sensitive(可选, 默认false), " +
        "max_results(可选, 默认200)。跳过 obj/bin/.git/node_modules。只读安全。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "正则表达式" },
            "path": { "type": "string", "description": "限定搜索目录/文件" },
            "case_sensitive": { "type": "boolean" },
            "max_results": { "type": "integer" }
          },
          "required": ["pattern"]
        }
""");

    public bool RequiresApproval => false;

    private static readonly string[] SkipDirs = { "obj", "bin", ".git", "node_modules", ".idea", ".vs", ".trae" };

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var pattern = args.TryGetProperty("pattern", out var p) ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Task.FromResult(ToolResult.Error("缺少参数: pattern"));
        }

        var caseSensitive = args.TryGetProperty("case_sensitive", out var cs) && cs.ValueKind == JsonValueKind.True;
        var maxResults = args.TryGetProperty("max_results", out var m) && m.ValueKind == JsonValueKind.Number
            ? Math.Clamp(m.GetInt32(), 1, 1000)
            : MaxMatches;

        Regex regex;
        try
        {
            regex = new Regex(pattern, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolResult.Error($"无效正则: {ex.Message}"));
        }

        string? searchDir = null;
        string? searchFile = null;
        if (args.TryGetProperty("path", out var pa) && pa.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pa.GetString()))
        {
            try
            {
                var full = ToolPathSanitizer.Resolve(ctx.WorkspaceRoot, pa.GetString()!);
                if (File.Exists(full))
                {
                    searchFile = full;
                }
                else if (Directory.Exists(full))
                {
                    searchDir = full;
                }
                else
                {
                    return Task.FromResult(ToolResult.Error($"路径不存在: {pa.GetString()}"));
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                return Task.FromResult(ToolResult.Error(ex.Message));
            }
        }

        var files = searchDir is not null
            ? EnumerateFiles(searchDir, 0)
            : searchFile is not null
                ? [searchFile]
                : EnumerateFiles(Path.GetFullPath(ctx.WorkspaceRoot), 0);

        var sb = new System.Text.StringBuilder();
        var count = 0;
        var matches = new List<GrepMatchEntry>();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (count >= maxResults)
            {
                break;
            }

            try
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (count >= maxResults)
                    {
                        break;
                    }

                    if (!regex.IsMatch(lines[i]))
                    {
                        continue;
                    }

                    var rel = Path.GetRelativePath(Path.GetFullPath(ctx.WorkspaceRoot), file).Replace('\\', '/');
                    var line = lines[i].Trim();
                    sb.AppendLine($"{rel}:{i + 1}: {(line.Length > 160 ? line[..160] + "..." : line)}");
                    // 结构化卡片数据: 保留原始缩进的匹配行上下文(超长截断)
                    matches.Add(new GrepMatchEntry
                    {
                        Path = rel,
                        Line = i + 1,
                        Text = lines[i].Length > 500 ? lines[i][..500] + "..." : lines[i]
                    });
                    count++;
                }
            }
            catch
            {
            }
        }

        if (count == 0)
        {
            return Task.FromResult(ToolResult.Ok($"未找到匹配 \"{pattern}\""));
        }

        var truncated = count >= maxResults;
        if (truncated)
        {
            sb.AppendLine($"(已达 {maxResults} 条上限)");
        }

        // 结构化卡片数据: 查询条件 + 路径/行号/上下文
        var detail = new GrepDetail
        {
            Pattern = pattern,
            CaseSensitive = caseSensitive,
            Matches = matches,
            Truncated = truncated
        };
        return Task.FromResult(new ToolResult { Content = sb.ToString(), Detail = detail });
    }

    private static IEnumerable<string> EnumerateFiles(string dir, int depth)
    {
        if (depth > MaxDepth)
        {
            yield break;
        }

        foreach (var file in Directory.GetFiles(dir))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".exe" or ".dll" or ".pdb" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".ico" or ".woff" or ".woff2" or ".ttf")
            {
                continue;
            }

            yield return file;
        }

        foreach (var sub in Directory.GetDirectories(dir))
        {
            if (SkipDirs.Contains(Path.GetFileName(sub)))
            {
                continue;
            }

            foreach (var f in EnumerateFiles(sub, depth + 1))
            {
                yield return f;
            }
        }
    }
}