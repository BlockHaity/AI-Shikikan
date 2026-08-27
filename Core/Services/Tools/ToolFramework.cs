using System.Text.Json;

namespace AIShikikan.Core.Services.Tools;

public class ToolResult
{
    public bool IsError { get; set; }
    public string Content { get; set; } = string.Empty;

    public static ToolResult Ok(string content) => new() { Content = content };

    public static ToolResult Error(string message) => new() { IsError = true, Content = message };
}

public class ToolContext
{
    public required string WorkspaceRoot { get; init; }

    /// <summary>子代理实时输出回调(UI 订阅)。</summary>
    public Action<string>? OnToolOutput { get; init; }
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

    public void Register(ITool tool) => _tools[tool.Name] = tool;

    /// <summary>按条件注销一组工具(MCP 刷新时重建桥接条目)。</summary>
    public int UnregisterWhere(Func<ITool, bool> predicate)
    {
        var names = _tools.Where(kv => predicate(kv.Value)).Select(kv => kv.Key).ToList();
        foreach (var n in names)
        {
            _tools.Remove(n);
        }

        return names.Count;
    }

    public bool TryGet(string name, out ITool tool) => _tools.TryGetValue(name, out tool!);

    public ITool Get(string name) =>
        _tools.TryGetValue(name, out var tool) ? tool : throw new KeyNotFoundException($"未知工具: {name}");

    public IReadOnlyList<ITool> All => _tools.Values.ToList();

    public List<Llm.ToolSpec> ToSpecs() =>
        _tools.Values.Select(t => new Llm.ToolSpec
        {
            Name = t.Name,
            Description = t.Description,
            Parameters = t.Parameters
        }).ToList();
}

public static class ToolSchema
{
    public static JsonElement Json(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}