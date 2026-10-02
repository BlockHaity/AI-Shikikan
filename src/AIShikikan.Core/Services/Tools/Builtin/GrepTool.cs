using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;

namespace AIShikikan.Core.Services.Tools.Builtin;

public class GrepTool : ITool
{
    private const int MaxMatches = 200;
    private const int MaxDepth = 6;

    /// <summary>单个被搜索文件的大小上限(4MB)。grep 只回传匹配行的前 160/500 字符,
    /// 再大的文件对结果没有增量价值, 却会按文件体积一次性分配内存。
    /// 比 <c>ReadFileTool.MaxBytes</c>(512KB) 宽一档: 大 JSON/CSV 常是 grep 的正当目标, 4MB 足够。</summary>
    private const long MaxFileBytes = 4L * 1024 * 1024;

    public string Name => "grep";

    public string Description =>
        "在工作区内按正则表达式搜索文件内容, 返回 文件:行号:匹配行。参数: " +
        "pattern(必填, 正则), path(可选, 限定子目录/文件), case_sensitive(可选, 默认false), " +
        "max_results(可选, 默认200)。跳过 obj/bin/.git/node_modules、二进制与超过4MB的文件。只读安全。";

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
        // 判 ValueKind: LLM 可能传 "pattern": 123 / null, 未判时 GetString() 抛 InvalidOperationException
        var pattern = args.TryGetProperty("pattern", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Task.FromResult(ToolResult.Error("缺少参数: pattern(必须是非空字符串正则)"));
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
                // followLinks: path 是显式参数(LLM 完全控制), 解析符号链接后再判边界;
                // 未指定 path 的全量遍历不在此列, 遍历范围本身已限定在工作区内
                var full = ToolPathSanitizer.Resolve(ctx.WorkspaceRoot, pa.GetString()!, followLinks: true);
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

        var workspaceRoot = Path.GetFullPath(ctx.WorkspaceRoot);
        var files = searchDir is not null
            ? EnumerateFiles(searchDir, 0, ct)
            : searchFile is not null
                ? [searchFile]
                : EnumerateFiles(workspaceRoot, 0, ct);

        var sb = new System.Text.StringBuilder();
        var count = 0;
        var timedOutFiles = 0;
        var oversizedFiles = 0;
        var matches = new List<GrepMatchEntry>();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (count >= maxResults)
            {
                break;
            }

            // 懒计算: 绝大多数文件一行都不匹配, 不值得为每个文件都求一次相对路径
            string? rel = null;

            try
            {
                var size = new FileInfo(file).Length;
                if (size > MaxFileBytes)
                {
                    // 只按扩展名挡二进制挡不住几百 MB 的 .json/.csv: 那样会一次性读进内存。
                    Log.Debug("Tool", $"grep 跳过超大文件({size / 1024 / 1024}MB): {file}");
                    oversizedFiles++;
                    continue;
                }

                // 流式逐行, 不用 ReadAllLines: 避免把整个文件再复制成一份 string[] 占用同等内存
                var lineNo = 0;
                foreach (var raw in File.ReadLines(file))
                {
                    lineNo++;
                    if (count >= maxResults)
                    {
                        break;
                    }

                    if (!regex.IsMatch(raw))
                    {
                        continue;
                    }

                    rel ??= Path.GetRelativePath(workspaceRoot, file).Replace('\\', '/');
                    var line = raw.Trim();
                    sb.AppendLine($"{rel}:{lineNo}: {(line.Length > 160 ? line[..160] + "..." : line)}");
                    // 结构化卡片数据: 保留原始缩进的匹配行上下文(超长截断)
                    matches.Add(new GrepMatchEntry
                    {
                        Path = rel,
                        Line = lineNo,
                        Text = raw.Length > 500 ? raw[..500] + "..." : raw
                    });
                    count++;
                }
            }
            catch (OperationCanceledException)
            {
                throw; // 铁律: 回合被中断必须上抛
            }
            catch (RegexMatchTimeoutException ex)
            {
                // 2s 超时是按每次 IsMatch 独立计时的, 病态正则只会拖住当前文件的剩余行。
                // 旧实现用 catch { } 吞掉它, 用户只看到"未找到匹配" —— 把超时误报成无匹配。
                Log.Warn("Tool", ex, $"grep 正则匹配超时, 跳过 {rel ?? file} 的剩余行");
                timedOutFiles++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug("Tool", $"grep 跳过不可读文件: {rel ?? file}");
            }
        }

        // 跳过的文件必须让 LLM 看见, 否则"未找到匹配"会被当成事实(尤其是超时导致的假阴性)
        var notes = new System.Text.StringBuilder();
        if (timedOutFiles > 0)
        {
            notes.Append($"[{timedOutFiles} 个文件因正则超时被跳过, 结果可能不完整; 建议简化正则或收窄 path]");
        }

        if (oversizedFiles > 0)
        {
            notes.Append($"[{oversizedFiles} 个文件超过 {MaxFileBytes / 1024 / 1024}MB 上限被跳过]");
        }

        if (count == 0)
        {
            var emptyNote = notes.Length > 0 ? " " + notes.ToString() : string.Empty;
            return Task.FromResult(ToolResult.Ok($"未找到匹配 \"{pattern}\"{emptyNote}"));
        }

        var truncated = count >= maxResults;
        if (truncated)
        {
            sb.AppendLine($"(已达 {maxResults} 条上限)");
        }

        if (notes.Length > 0)
        {
            sb.AppendLine(notes.ToString());
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

    private static IEnumerable<string> EnumerateFiles(string dir, int depth, CancellationToken ct)
    {
        if (depth > MaxDepth)
        {
            yield break;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 迭代器内的异常会在 MoveNext 时抛给调用方, 旧实现没兜 → 整个 grep 变成"工具执行异常",
            // 已扫出的结果全丢。单个不可读目录跳过即可(与 glob 的处理保持一致)。
            Log.Debug("Tool", $"grep 跳过不可读目录: {dir}");
            yield break;
        }

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".exe" or ".dll" or ".pdb" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".ico" or ".woff" or ".woff2" or ".ttf")
            {
                continue;
            }

            yield return file;
        }

        string[] subDirs;
        try
        {
            subDirs = Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var sub in subDirs)
        {
            // 目录树里可能有大量空目录, 只靠外层"每文件一次"的检查不够及时
            ct.ThrowIfCancellationRequested();

            if (SkipDirs.Contains(Path.GetFileName(sub)))
            {
                continue;
            }

            // 目录软链/联接点: 防 `a -> ..` 递归死循环, 也避免顺着软链读到工作区外
            if (ToolPathSanitizer.IsReparsePoint(sub))
            {
                continue;
            }

            foreach (var f in EnumerateFiles(sub, depth + 1, ct))
            {
                yield return f;
            }
        }
    }
}