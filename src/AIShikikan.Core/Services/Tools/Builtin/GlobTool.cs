using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIShikikan.Core.Services.Tools.Builtin;

public class GlobTool : ITool
{
    public string Name => "glob";

    public string Description =>
        "在工作区内按 glob 模式查找文件/目录。参数: pattern(必填, 如 \"**/*.cs\"), " +
        "max_results(可选, 默认100)。支持 * ? **。只读安全。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "glob 模式" },
            "max_results": { "type": "integer", "description": "最大结果数, 默认100" }
          },
          "required": ["pattern"]
        }
""");

    public bool RequiresApproval => false;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var pattern = args.TryGetProperty("pattern", out var p) ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Task.FromResult(ToolResult.Error("缺少参数: pattern"));
        }

        var maxResults = args.TryGetProperty("max_results", out var m) && m.ValueKind == JsonValueKind.Number
            ? Math.Clamp(m.GetInt32(), 1, 500)
            : 100;

        var root = Path.GetFullPath(ctx.WorkspaceRoot);
        var results = new List<string>();

        try
        {
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (ShouldSkipMatch(rel) || !MatchGlob(pattern, rel))
                {
                    continue;
                }

                results.Add(rel);
                if (results.Count >= maxResults)
                {
                    break;
                }
            }
        }
        catch
        {
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"匹配 \"{pattern}\": {results.Count} 个");
        foreach (var r in results)
        {
            sb.AppendLine(r);
        }

        if (results.Count >= maxResults)
        {
            sb.AppendLine("(已达结果上限)");
        }

        return Task.FromResult(ToolResult.Ok(sb.ToString()));
    }

    private static bool ShouldSkipMatch(string rel)
    {
        var parts = rel.Split('/');
        foreach (var part in parts)
        {
            if (part is "obj" or "bin" or ".git" or "node_modules")
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchGlob(string pattern, string path)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*\\*", "__DOUBLE__")
            .Replace("\\*", "[^/]*")
            .Replace("__DOUBLE__", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(path, regex);
    }
}