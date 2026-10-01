using System.Text.Json;
using System.Text.Json.Nodes;
using AIShikikan.Core.Models;

namespace AIShikikan.Core.Services.Tools.Builtin;

public class ReadFileTool : ITool
{
    private const int MaxBytes = 512 * 1024;

    public string Name => "read_file";

    public string Description =>
        "读取工作区内文本文件内容。参数: path(必填, 相对工作区或绝对路径), " +
        "offset(可选, 起始行, 从0开始), limit(可选, 最大行数, 默认200)。只读安全。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "文件路径(工作区内)" },
            "offset": { "type": "integer", "description": "起始行(0起)" },
            "limit": { "type": "integer", "description": "最大行数, 默认200" }
          },
          "required": ["path"]
        }
""");

    public bool RequiresApproval => false;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        // 参数解析与路径解析都放进 try: 两者都会抛(LLM 传错类型 → GetString() 抛 InvalidOperationException;
        // 路径越界 → Resolve 抛 UnauthorizedAccessException)。抛到工具外会被 AgentEngine 的 catch(Exception)
        // 兜成"工具执行异常: ...", LLM 拿不到"该改哪个参数"的提示, 只会反复用同样的错误参数重试。
        try
        {
            ct.ThrowIfCancellationRequested();

            // 必须判 ValueKind: LLM 可能传 "path": 123 / null / [] , 未判时 GetString() 直接抛异常
            if (!args.TryGetProperty("path", out var p)
                || p.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(p.GetString()))
            {
                return Task.FromResult(
                    ToolResult.Error("参数 path 必须是非空字符串(如 \"src/Program.cs\"), 请重新给出工作区内的文件路径。"));
            }

            var path = p.GetString()!;

            // followLinks: path 由 LLM 完全指定, 是符号链接逃逸的入口, 解析链接后再判边界
            var fullPath = ToolPathSanitizer.Resolve(ctx.WorkspaceRoot, path, followLinks: true);
            if (!File.Exists(fullPath))
            {
                return Task.FromResult(ToolResult.Error($"文件不存在: {path}"));
            }

            var info = new FileInfo(fullPath);
            if (info.Length > MaxBytes)
            {
                return Task.FromResult(ToolResult.Error($"文件过大({info.Length / 1024}KB)，拒绝读取，请改用 grep"));
            }

            var lines = File.ReadAllLines(fullPath);
            var offset = args.TryGetProperty("offset", out var o) && o.ValueKind == JsonValueKind.Number ? Math.Max(0, o.GetInt32()) : 0;
            var limit = args.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number
                ? Math.Clamp(l.GetInt32(), 1, 500)
                : 200;

            var take = lines.Skip(offset).Take(limit).ToArray();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"文件: {path} ({lines.Length} 行)  [行 {offset + 1}-{offset + take.Length}]");
            for (var i = 0; i < take.Length; i++)
            {
                sb.AppendLine($"{offset + i + 1,6} | {take[i]}");
            }

            var truncated = offset + take.Length < lines.Length;
            if (truncated)
            {
                sb.AppendLine($"...(还有 {lines.Length - offset - take.Length} 行未显示)");
            }

            // 结构化卡片数据: 完整路径 + 原始内容(保留格式与缩进)
            var detail = new FileReadDetail
            {
                Path = fullPath,
                Content = string.Join("\n", take),
                TotalLines = lines.Length,
                StartLine = offset + 1,
                LinesShown = take.Length,
                Truncated = truncated
            };
            return Task.FromResult(new ToolResult { Content = sb.ToString(), Detail = detail });
        }
        catch (OperationCanceledException)
        {
            throw; // 铁律: 回合被中断必须上抛, 否则停止按钮只停住外层循环
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ToolResult.Error(ex.Message));
        }
        catch (IOException ex)
        {
            return Task.FromResult(ToolResult.Error($"读取失败: {ex.Message}"));
        }
        catch (FormatException ex)
        {
            // GetInt32() 在 offset/limit 超出 int 范围(如 1e20)时抛 FormatException
            return Task.FromResult(ToolResult.Error($"参数 offset/limit 必须是 32 位整数: {ex.Message}"));
        }
    }
}