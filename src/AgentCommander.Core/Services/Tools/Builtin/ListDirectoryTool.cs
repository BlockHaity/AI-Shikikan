using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCommander.Core.Services.Tools.Builtin;

public class ListDirectoryTool : ITool
{
    private const int MaxEntries = 500;
    private const int MaxDepth = 5;

    public string Name => "list_directory";

    public string Description =>
        "列出工作区内目录内容。参数: path(可选, 默认根), " +
        "recursive(可选, 是否递归, 默认false, 深度上限5), 最多返回500个条目。只读安全。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "目录路径(工作区内)" },
            "recursive": { "type": "boolean", "description": "是否递归列出" }
          }
        }
""");

    public bool RequiresApproval => false;

    private static readonly string[] SkipDirs = ["obj", "bin", ".git", "node_modules", ".idea", ".vs", ".trae"];

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var path = args.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var recursive = args.TryGetProperty("recursive", out var r) && r.ValueKind == JsonValueKind.True;

        string full;
        try
        {
            full = ToolPathSanitizer.Resolve(ctx.WorkspaceRoot, string.IsNullOrWhiteSpace(path) ? "." : path);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ToolResult.Error(ex.Message));
        }

        if (!Directory.Exists(full))
        {
            return Task.FromResult(ToolResult.Error($"目录不存在: {path ?? "."}"));
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"目录: {path ?? "."}");
        var count = 0;

        Walk(full, 0, recursive, sb, ref count, ct);

        if (count >= MaxEntries)
        {
            sb.AppendLine($"...(条目已截断, 超过 {MaxEntries})");
        }

        return Task.FromResult(ToolResult.Ok(sb.ToString()));
    }

    private static void Walk(string dir, int depth, bool recursive, System.Text.StringBuilder sb, ref int count, CancellationToken ct)
    {
        if (count >= MaxEntries) return;

        foreach (var sub in Directory.GetDirectories(dir))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(sub);
            if (SkipDirs.Contains(name)) continue;

            sb.AppendLine($"{new string(' ', depth * 2)}[{name}/]");
            count++;
            if (recursive && depth < MaxDepth)
            {
                Walk(sub, depth + 1, true, sb, ref count, ct);
            }
        }

        foreach (var file in Directory.GetFiles(dir))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            sb.AppendLine($"{new string(' ', depth * 2)}{info.Name} ({info.Length / 1024}KB)");
            count++;
        }
    }
}