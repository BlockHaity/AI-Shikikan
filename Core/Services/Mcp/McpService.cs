using System.Text.Json.Nodes;

namespace AIShikikan.Core.Services.Mcp;

/// <summary>MCP 连接管理器: 按配置启动/停止 stdio 服务器连接, 聚合工具列表并路由调用。
/// 工具对外统一命名为 mcp_&lt;serverId&gt;_&lt;toolName&gt;。</summary>
public sealed class McpService : IAsyncDisposable
{
    private readonly Dictionary<string, McpStdioClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<McpToolDescriptor>> _toolsByServer = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    /// <summary>已连接服务器数量。</summary>
    public int ConnectedCount
    {
        get
        {
            lock (_clients)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>把某服务器的全部 MCP 工具桥接为 ITool 注册表条目(仅已连接部分)。
    /// 失败的服务器跳过并返回错误说明, 不阻塞其余服务器。</summary>
    public async Task<List<string>> ConnectAllAsync(CancellationToken ct = default)
    {
        var servers = McpConfigService.LoadAll().Where(s => s.Enabled).ToList();
        var messages = new List<string>();

        foreach (var def in servers)
        {
            try
            {
                await ConnectAsync(def, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                messages.Add($"[MCP] {def.Id} 连接失败: {ex.Message}");
            }
        }

        return messages;
    }

    /// <summary>连接单个服务器并缓存其工具列表(幂等)。</summary>
    public async Task ConnectAsync(McpServerDefinition def, CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_clients.ContainsKey(def.Id))
            {
                return; // 已连接
            }

            var client = await McpStdioClient.StartAsync(def, ct).ConfigureAwait(false);
            var tools = await client.ListToolsAsync(ct).ConfigureAwait(false);

            _clients[def.Id] = client;
            _toolsByServer[def.Id] = tools;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>断开并移除某服务器连接。</summary>
    public async Task DisconnectAsync(string serverId)
    {
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_clients.Remove(serverId, out var client))
            {
                await client.DisposeAsync().ConfigureAwait(false);
                _toolsByServer.Remove(serverId);
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public bool IsConnected(string serverId)
    {
        lock (_clients)
        {
            return _clients.ContainsKey(serverId);
        }
    }

    /// <summary>枚举全部桥接后的工具描述(名称已加 mcp_&lt;serverId&gt;_ 前缀)。</summary>
    public IEnumerable<(string BridgeName, string ServerId, McpToolDescriptor Tool)> EnumerateTools()
    {
        lock (_clients)
        {
            foreach (var (serverId, tools) in _toolsByServer)
            {
                foreach (var t in tools)
                {
                    yield return (BridgeName(serverId, t.Name), serverId, t);
                }
            }
        }
    }

    /// <summary>按桥接名路由调用到对应服务器(在已注册工具表中反查, 避免 id 分割歧义)。</summary>
    public async Task<(string Text, bool IsError)> CallBridgeToolAsync(
        string bridgeName, JsonObject arguments, CancellationToken ct)
    {
        (string ServerId, string ToolName)? found = null;
        lock (_clients)
        {
            foreach (var (serverId, tools) in _toolsByServer)
            {
                if (tools.Any(t => BridgeName(serverId, t.Name) == bridgeName))
                {
                    found = (serverId, tools.First(t => BridgeName(serverId, t.Name) == bridgeName).Name);
                    break;
                }
            }
        }

        if (found is null)
        {
            throw new InvalidOperationException($"未找到 MCP 工具 {bridgeName}, 请确认服务器已连接并启用");
        }

        McpStdioClient? client;
        lock (_clients)
        {
            _clients.TryGetValue(found.Value.ServerId, out client);
        }

        if (client is null)
        {
            throw new InvalidOperationException($"MCP 服务器 {found.Value.ServerId} 未连接, 请在设置中检查其状态");
        }

        return await client.CallToolAsync(found.Value.ToolName, arguments, ct).ConfigureAwait(false);
    }

    public static string BridgeName(string serverId, string toolName) => $"mcp_{serverId}_{toolName}";

    public async ValueTask DisposeAsync()
    {
        List<McpStdioClient> clients;
        lock (_clients)
        {
            clients = [.. _clients.Values];
            _clients.Clear();
            _toolsByServer.Clear();
        }

        foreach (var c in clients)
        {
            await c.DisposeAsync().ConfigureAwait(false);
        }
    }
}
