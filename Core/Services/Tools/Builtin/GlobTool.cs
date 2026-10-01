using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;

namespace AIShikikan.Core.Services.Tools.Builtin;

public class GlobTool : ITool
{
    /// <summary>单次 glob 模式匹配的正则回溯预算; 与 <see cref="MatchGlob"/> 的说明一起看。</summary>
    private const int MatchTimeoutMs = 500;

    /// <summary>
    /// 单次 glob 调用的整场扫描预算。仅靠 <see cref="MatchTimeoutMs"/> 不够: 病态模式会在**每个**文件上
    /// 各耗满 500ms, 10 万文件就是十几个小时。引擎线程上没有任何墙钟兜底, 必须在这里收口。
    /// 15s 远超"扫完一棵大树"所需(命中 max_results 会提前 break), 命中时在结果里注明不完整。
    /// </summary>
    private const int ScanBudgetMs = 15_000;

    /// <summary>
    /// 目录遍历深度上限。旧实现用 <c>Directory.GetFiles(root, "*", AllDirectories)</c> 无限递归;
    /// Linux 的 PATH_MAX(4096) 允许两千多层的目录链, 撞上会压爆递归栈(StackOverflow 不可捕获, 整个进程死)。
    /// 64 层远超任何真实仓库的目录深度, 命中即在结果里注明, 不会静默截断。
    /// </summary>
    private const int MaxDepth = 64;

    /// <summary>"**" 的正则占位符。用控制字符而不是可读标记: <c>Regex.Escape</c> 不会产出控制字符,
    /// 所以不可能与用户模式里的字面文本撞车(旧实现用 "__DOUBLE__", 模式里恰好含这串字面量时会被误替换成 <c>.*</c>)。</summary>
    private const string DoubleStarToken = "\u0001";

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
        // 判 ValueKind: LLM 可能传 "pattern": 123 / null, 未判时 GetString() 抛 InvalidOperationException
        var pattern = args.TryGetProperty("pattern", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Task.FromResult(ToolResult.Error("缺少参数: pattern(必须是非空字符串, 如 \"**/*.cs\")"));
        }

        var maxResults = args.TryGetProperty("max_results", out var m) && m.ValueKind == JsonValueKind.Number
            ? Math.Clamp(m.GetInt32(), 1, 500)
            : 100;

        var root = Path.GetFullPath(ctx.WorkspaceRoot);
        var results = new List<string>();
        var state = new WalkState();
        var scanDeadline = Environment.TickCount64 + ScanBudgetMs;

        // 取消检查留在遍历循环里、且不放在任何 catch 的覆盖范围内: 旧实现用 catch { } 包住整个循环,
        // 把 OperationCanceledException 一起吞了 —— 用户点"停止"时 glob 反而返回部分结果当成功。
        foreach (var file in EnumerateFiles(root, 0, state, ct))
        {
            ct.ThrowIfCancellationRequested();

            // 预算检查必须在 continue 之前: 最坏情况(病态模式在每个文件上都耗满 500ms 且一个都不匹配)
            // 走的全是 continue 分支, 放在匹配分支里就永远触发不了
            if (Environment.TickCount64 > scanDeadline)
            {
                state.BudgetExhausted = true;
                break;
            }

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

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"匹配 \"{pattern}\": {results.Count} 个");
        foreach (var r in results)
        {
            sb.AppendLine(r);
        }

        var truncated = results.Count >= maxResults;
        if (truncated)
        {
            sb.AppendLine("(已达结果上限)");
        }

        if (state.DepthTruncated)
        {
            sb.AppendLine($"(目录层级超过 {MaxDepth} 层, 更深处的文件未参与匹配)");
        }

        if (state.BudgetExhausted)
        {
            sb.AppendLine($"(扫描超过 {ScanBudgetMs / 1000} 秒预算已提前收工, 结果可能不完整; 请收窄 pattern 或使用 path 限定目录)");
        }

        // 结构化卡片数据: 查询条件 + 匹配路径
        var detail = new GlobDetail
        {
            Pattern = pattern,
            Matches = results.Select(r => new FileMatchEntry { Path = r }).ToList(),
            Truncated = truncated
        };
        return Task.FromResult(new ToolResult { Content = sb.ToString(), Detail = detail });
    }

    private static readonly string[] SkipDirs = ["obj", "bin", ".git", "node_modules"];

    private static bool ShouldSkipMatch(string rel)
    {
        foreach (var part in rel.Split('/'))
        {
            if (SkipDirs.Contains(part))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>遍历状态(用类而不是 ref 参数, 因为递归是迭代器, 迭代器不允许 ref/out 形参)。</summary>
    private sealed class WalkState
    {
        /// <summary>是否因超过 <see cref="MaxDepth"/> 提前收工, 用于在结果里向 LLM 注明结果不完整。</summary>
        public bool DepthTruncated { get; set; }

        /// <summary>是否因超过 <see cref="ScanBudgetMs"/> 整场扫描预算提前收工。</summary>
        public bool BudgetExhausted { get; set; }
    }

    /// <summary>
    /// 流式枚举工作区文件, 手动控制递归。
    /// 不用 <c>Directory.GetFiles(root, "*", AllDirectories)</c>: 它会**一次性物化整个文件树**
    /// (大仓库可达几十万条路径), 而且 obj/bin/node_modules 虽在匹配阶段被跳过, 枚举成本早已付完。
    /// 这里在**进入**这些目录之前就跳过, 并惰性产出单个目录的文件, 内存占用与结果集大小无关。
    /// </summary>
    private static IEnumerable<string> EnumerateFiles(string root, int depth, WalkState state, CancellationToken ct)
    {
        if (depth > MaxDepth)
        {
            state.DepthTruncated = true;
            Log.Debug("Tool", $"glob 遍历深度超过 {MaxDepth} 已停止: {root}");
            yield break;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 单个无权限/已删除的目录不该让整个 glob 报错(对齐 .NET 递归枚举的 IgnoreInaccessible 语义)
            Log.Debug("Tool", $"glob 跳过不可读目录: {root}");
            yield break;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            yield return file;
        }

        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (SkipDirs.Contains(name))
            {
                continue;
            }

            // 目录软链/联接点: 防 `a -> ..` 式的递归死循环, 也避免顺着软链读到工作区外
            if (ToolPathSanitizer.IsReparsePoint(dir))
            {
                continue;
            }

            foreach (var f in EnumerateFiles(dir, depth + 1, state, ct))
            {
                yield return f;
            }
        }
    }

    private static bool MatchGlob(string pattern, string path)
    {
        var regex = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*", DoubleStarToken)
            .Replace(@"\*", "[^/]*")
            .Replace(DoubleStarToken, ".*") + "$";

        // pattern 完全由 LLM 控制, 展开后是回溯型正则: 形如 "a*a*a*a*a*b" 的模式遇到长路径会指数级回溯。
        // 无 matchTimeout 会把引擎线程钉死(UI 无响应), 而这里要对文件树里每个文件跑一次, 代价被成倍放大。
        // 取 500ms 而非 GrepTool 的 2s: grep 是单文件逐行匹配, 预算可以大; 这里是"每文件一次", 必须小得多。
        try
        {
            return Regex.IsMatch(path, regex, RegexOptions.None, TimeSpan.FromMilliseconds(MatchTimeoutMs));
        }
        catch (RegexMatchTimeoutException)
        {
            // 当"不匹配"处理而不是整体失败: 一个病态模式不该让整个 glob 工具报错退出
            Log.Debug("Tool", $"glob 模式匹配超时, 按不匹配跳过: {pattern}");
            return false;
        }
    }
}
