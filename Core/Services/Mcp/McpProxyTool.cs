using System.Text.Json;
using System.Text.Json.Nodes;
using AIShikikan.Core.Services.Tools;

namespace AIShikikan.Core.Services.Mcp;

/// <summary>MCP 工具 → ITool 桥接: 把远端 MCP 服务器的单个工具包装为本引擎可调用的 ITool。
/// 名称规则 mcp_&lt;serverId&gt;_&lt;toolName&gt;; inputSchema 原样透传给 LLM。</summary>
public sealed class McpProxyTool : ITool
{
    private readonly McpService _service;
    private readonly string _serverId;

    public McpProxyTool(McpService service, string serverId, McpToolDescriptor descriptor)
    {
        _service = service;
        _serverId = serverId;
        Descriptor = descriptor;
    }

    public McpToolDescriptor Descriptor { get; }

    public string Name => McpService.BridgeName(_serverId, Descriptor.Name);

    public string Description =>
        $"[MCP:{_serverId}] {Descriptor.Description}".Trim();

    public JsonElement Parameters
    {
        get
        {
            // inputSchema 缺失时兜底为空对象 schema
            if (Descriptor.InputSchema.ValueKind is JsonValueKind.Object)
            {
                return Descriptor.InputSchema.Clone();
            }

            return ToolSchema.Json("""{ "type": "object", "properties": {} }""");
        }
    }

    /// <summary>MCP 工具可能修改外部状态, 默认需要批准。</summary>
    public bool RequiresApproval => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        try
        {
            var arguments = args.ValueKind is JsonValueKind.Object && args.GetRawText().Length > 2
                ? JsonNode.Parse(args.GetRawText()) as JsonObject ?? []
                : [];
            var (text, isError) = await _service.CallBridgeToolAsync(Name, arguments, ct).ConfigureAwait(false);
            return isError ? ToolResult.Error(text) : ToolResult.Ok(text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"MCP 工具调用失败({Name}): {ex.Message}");
        }
    }
}
