using System.Text.Json;
using System.Text.Json.Nodes;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;

namespace AIShikikan.Core.Services.Tools.Builtin;

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
            // followLinks: path 是显式参数(LLM 完全控制), 解析符号链接后再判边界
            full = ToolPathSanitizer.Resolve(ctx.WorkspaceRoot, string.IsNullOrWhiteSpace(path) ? "." : path,
                followLinks: !string.IsNullOrWhiteSpace(path));
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
        var entries = new List<DirectoryEntry>();

        Walk(full, 0, recursive, sb, ref count, entries, ct);

        if (count >= MaxEntries)
        {
            sb.AppendLine($"...(条目已截断, 超过 {MaxEntries})");
        }

        // 结构化卡片数据: 名称/类型/大小/修改时间
        var detail = new DirectoryListDetail
        {
            Path = path ?? ".",
            Recursive = recursive,
            Entries = entries,
            Truncated = count >= MaxEntries
        };
        return Task.FromResult(new ToolResult { Content = sb.ToString(), Detail = detail });
    }

    private static void Walk(
        string dir, int depth, bool recursive, System.Text.StringBuilder sb,
        ref int count, List<DirectoryEntry> entries, CancellationToken ct)
    {
        if (count >= MaxEntries) return;

        // GetDirectories/GetFiles 遇到无权限目录会抛, 旧实现没兜 → 整次 list_directory 变成
        // "工具执行异常" 且已列出的条目全丢。只吞 IO 类异常, 取消异常必须照常上抛。
        try
        {
            foreach (var sub in Directory.GetDirectories(dir))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(sub);
                if (SkipDirs.Contains(name)) continue;

                // 目录软链/联接点: 防 `a -> ..` 递归死循环, 也避免顺着软链读到工作区外
                if (ToolPathSanitizer.IsReparsePoint(sub)) continue;

                if (count >= MaxEntries) return;

                var subInfo = new DirectoryInfo(sub);
                sb.AppendLine($"{new string(' ', depth * 2)}[{name}/]");
                entries.Add(new DirectoryEntry
                {
                    Name = name,
                    IsDirectory = true,
                    ModifiedAt = subInfo.LastWriteTime,
                    Depth = depth
                });
                count++;
                if (recursive && depth < MaxDepth)
                {
                    Walk(sub, depth + 1, true, sb, ref count, entries, ct);
                }
            }

            foreach (var file in Directory.GetFiles(dir))
            {
                ct.ThrowIfCancellationRequested();

                // 单个目录内的文件同样可能超限: 入口那一处检查只管住了递归层
                if (count >= MaxEntries) return;

                var info = new FileInfo(file);
                sb.AppendLine($"{new string(' ', depth * 2)}{info.Name} ({info.Length / 1024}KB)");
                entries.Add(new DirectoryEntry
                {
                    Name = info.Name,
                    IsDirectory = false,
                    SizeBytes = info.Length,
                    ModifiedAt = info.LastWriteTime,
                    Depth = depth
                });
                count++;
            }
        }
        catch (OperationCanceledException)
        {
            throw; // 铁律: 回合被中断必须上抛
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("Tool", $"list_directory 跳过不可读目录: {dir}");
        }
    }
}