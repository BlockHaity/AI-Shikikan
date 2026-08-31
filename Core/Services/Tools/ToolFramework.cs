using System.Text.Json;
using AIShikikan.Core.Models;

namespace AIShikikan.Core.Services.Tools;

public class ToolResult
{
    public bool IsError { get; set; }
    public string Content { get; set; } = string.Empty;

    /// <summary>关联的 git 检查点步骤 ID(子 Agent 调用产生检查点时填写), UI 据此提供回滚按钮。</summary>
    public string? StepId { get; set; }

    /// <summary>结构化卡片展示数据(按工具类型渲染专属卡体); 为空时卡片回退到通用文本展示。</summary>
    public ToolCardDetail? Detail { get; set; }

    public static ToolResult Ok(string content) => new() { Content = content };

    public static ToolResult Error(string message) => new() { IsError = true, Content = message };
}

public class ToolContext
{
    public required string WorkspaceRoot { get; init; }

    /// <summary>主对话是否处于 Plan 模式(决定子代理是否以 plan_args 启动)。</summary>
    public bool IsPlanMode { get; init; }

    /// <summary>子代理实时输出回调(UI 订阅)。</summary>
    public Action<string>? OnToolOutput { get; init; }

    /// <summary>向用户反问并等待回答的回调(由引擎注入, 返回 null 表示用户未作答/取消)。</summary>
    public Func<string, CancellationToken, Task<string?>>? AskUser { get; init; }
}

public interface ITool
{
    string Name { get; }

    string Description { get; }

    JsonElement Parameters { get; }

    bool RequiresApproval { get; }

    Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default);
}

public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public void Register(ITool tool)
    {
        lock (_gate)
        {
            _tools[tool.Name] = tool;
        }
    }

    /// <summary>按条件注销一组工具(MCP 刷新/右侧栏关闭时重建)。</summary>
    public int UnregisterWhere(Func<ITool, bool> predicate)
    {
        lock (_gate)
        {
            var names = _tools.Where(kv => predicate(kv.Value)).Select(kv => kv.Key).ToList();
            foreach (var n in names)
            {
                _tools.Remove(n);
            }

            return names.Count;
        }
    }

    public bool TryGet(string name, out ITool tool)
    {
        lock (_gate)
        {
            return _tools.TryGetValue(name, out tool!);
        }
    }

    public ITool Get(string name)
    {
        lock (_gate)
        {
            return _tools.TryGetValue(name, out var tool)
                ? tool
                : throw new KeyNotFoundException($"未知工具: {name}");
        }
    }

    public IReadOnlyList<ITool> All
    {
        get
        {
            lock (_gate)
            {
                return _tools.Values.ToList();
            }
        }
    }

    public List<Llm.ToolSpec> ToSpecs()
    {
        lock (_gate)
        {
            return _tools.Values.Select(t => new Llm.ToolSpec
            {
                Name = t.Name,
                Description = t.Description,
                Parameters = t.Parameters
            }).ToList();
        }
    }
}

public static class ToolSchema
{
    public static JsonElement Json(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}