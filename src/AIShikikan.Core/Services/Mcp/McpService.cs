using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AIShikikan.Core.Logging;

namespace AIShikikan.Core.Services.Mcp;

/// <summary>MCP 连接管理器: 按配置建立/断开服务器连接(stdio / Streamable HTTP / SSE),
/// 聚合工具列表并路由调用。工具对外统一命名为 mcp_&lt;serverId&gt;_&lt;toolName&gt;。</summary>
public sealed class McpService : IAsyncDisposable
{
    /// <summary>后台探活间隔: 到点对已连接服务器重发一次 tools/list, 失败即视为掉线。
    /// 取 60s 兼顾两点: 进程崩溃后能较快被 UI 发现, 又不至于频繁打扰正常工作的服务器。</summary>
    private static readonly TimeSpan HealthProbeInterval = TimeSpan.FromSeconds(60);

    // 三张表统一用无锁容器。早期版本读侧锁 _clients、写侧锁 _connectLock —— 两把不同的锁护同一批
    // 可变 Dictionary, 并发读写可能损坏内部结构并抛出 KeyNotFoundException, 是本文件最危险的一处缺陷。
    private readonly ConcurrentDictionary<string, McpClientBase> _clients = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, List<McpToolDescriptor>> _toolsByServer = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>桥接名 → (服务器, 工具) 直接索引。原先按桥接名要遍历全部服务器的工具反查,
    /// 每次调用 O(M·K); 有了这张表就是 O(1), 且因为是整名精确匹配, 也不存在"按 _ 分割反推"的歧义。
    /// 比较器用 Ordinal: 原反查用的是 string == , 保持同样的精确语义。</summary>
    private readonly ConcurrentDictionary<string, (string ServerId, McpToolDescriptor Descriptor)> _bridgeIndex =
        new(StringComparer.Ordinal);

    /// <summary>每服务器的连接闸: 同 id 的 Connect/Disconnect 互斥, 不同 id 之间可并行
    /// (ConnectAllAsync 并发建连就靠它避免同 id 重复连接)。条目不回收: 数量等于配置文件里的服务器数;
    /// 回收会让"正在等闸"的调用拿到一把新闸而失去互斥。</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _serverGates = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _probeSync = new();
    private CancellationTokenSource? _probeCts;
    private Task? _probeLoop;
    private int _probing;

    /// <summary>已连接服务器数量。</summary>
    public int ConnectedCount => _clients.Count;

    /// <summary>把某服务器的全部 MCP 工具桥接为 ITool 注册表条目(仅已连接部分)。
    /// 失败的服务器跳过并返回错误说明, 不阻塞其余服务器。</summary>
    public async Task<List<string>> ConnectAllAsync(CancellationToken ct = default)
    {
        var servers = McpConfigService.LoadAll().Where(s => s.Enabled).ToList();
        var messages = new List<string>();

        // 并发建连: 每个 ConnectAsync 内部只按 id 各自加闸, 服务器的握手 / 枚举完全在闸外等待 I/O。
        // 串行版本下 N 个坏服务器要等 N×30s(握手超时), 并发后总耗时约等于最慢的那一个。
        // Task.WhenAll 的结果顺序与输入顺序一致, 因此消息顺序仍与配置一致。
        var tasks = servers.Select(def => TryConnectAsync(def, ct)).ToList();
        foreach (var message in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        return messages;
    }

    private async Task<string?> TryConnectAsync(McpServerDefinition def, CancellationToken ct)
    {
        try
        {
            await ConnectAsync(def, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return $"[MCP] {def.Id} 连接失败: {ex.Message}";
        }
    }

    /// <summary>连接单个服务器并缓存其工具列表(幂等)。按传输方式选择 stdio 或 HTTP 客户端。</summary>
    public async Task ConnectAsync(McpServerDefinition def, CancellationToken ct = default)
    {
        if (!IsSafeNameSegment(def.Id))
        {
            // id 会原样进入工具名 mcp_<id>_<tool>, 空白等字符会产出非法函数名(LLM 侧只接受
            // 字母数字与 -/_), 与其把整份工具声明搞坏, 不如在连接前就拒绝并留痕。
            Log.Warn("MCP", $"MCP 服务器 id \"{def.Id}\" 含非法字符(仅允许字母/数字/-/_/.), 已跳过连接");
            throw new InvalidOperationException(
                $"MCP 服务器 id 非法: \"{def.Id}\"(仅允许字母、数字、-、_、.)");
        }

        var gate = _serverGates.GetOrAdd(def.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_clients.ContainsKey(def.Id))
            {
                return; // 已连接
            }

            McpClientBase client;
            if (McpHttpClient.IsHttpTransport(def.Transport))
            {
                client = await McpHttpClient.StartAsync(def, ct).ConfigureAwait(false);
            }
            else
            {
                client = await McpStdioClient.StartAsync(def, ct).ConfigureAwait(false);
            }

            try
            {
                var tools = await client.ListToolsAsync(ct).ConfigureAwait(false);
                RegisterServer(def.Id, client, tools);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>断开并移除某服务器连接。</summary>
    public async Task DisconnectAsync(string serverId)
    {
        var gate = _serverGates.GetOrAdd(serverId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_clients.TryRemove(serverId, out var client))
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            // 无条件清理: 客户端可能已在探活/异常路径里被摘掉, 但工具表还留着
            RemoveServerEntries(serverId);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>连接状态(纯缓存读取, 不含任何 I/O)。
    /// 该属性在 UI 线程被高频轮询, 若在此同步探活(ping / 重开 socket / 等子进程退出)会直接卡住界面,
    /// 因此健康状态一律由后台探活循环维护, 状态最多滞后 <see cref="HealthProbeInterval"/>。</summary>
    public bool IsConnected(string serverId) => _clients.ContainsKey(serverId);

    /// <summary>枚举全部桥接后的工具描述(名称已加 mcp_&lt;serverId&gt;_ 前缀)。</summary>
    /// <remarks>返回一次性快照而不是 yield 迭代器: 迭代器体内的 lock 会被编译器移进 MoveNext 的
    /// try/finally, 于是锁会跨两次 MoveNext 之间一直持有到消费结束, 消费方(设置页统计、Boot 注册工具)
    /// 一边枚举一边持有整表写锁。返回类型由 IEnumerable 收紧为 IReadOnlyList ——
    /// foreach 与 LINQ .Count(...) 均零改动可编译。方法名保留 Enumerate 前缀以免动调用方。</remarks>
    public IReadOnlyList<(string BridgeName, string ServerId, McpToolDescriptor Tool)> EnumerateTools()
    {
        var list = new List<(string BridgeName, string ServerId, McpToolDescriptor Tool)>();
        foreach (var (serverId, tools) in _toolsByServer)
        {
            foreach (var t in tools)
            {
                list.Add((BridgeName(serverId, t.Name), serverId, t));
            }
        }

        return list;
    }

    /// <summary>按桥接名路由调用到对应服务器(直接查桥接索引, O(1))。</summary>
    public async Task<(string Text, bool IsError)> CallBridgeToolAsync(
        string bridgeName, JsonObject arguments, CancellationToken ct)
    {
        // 索引键就是完整桥接名, 天然没有"serverId 含 _ 时按 _ 分割反推得到错误 toolName"的歧义 ——
        // 这正是原先必须遍历反查的原因, 换成字典后该约束自动消失。
        if (!_bridgeIndex.TryGetValue(bridgeName, out var entry))
        {
            throw new InvalidOperationException($"未找到 MCP 工具 {bridgeName}, 请确认服务器已连接并启用");
        }

        if (!_clients.TryGetValue(entry.ServerId, out var client))
        {
            throw new InvalidOperationException($"MCP 服务器 {entry.ServerId} 未连接, 请在设置中检查其状态");
        }

        return await client.CallToolAsync(entry.Descriptor.Name, arguments, ct).ConfigureAwait(false);
    }

    public static string BridgeName(string serverId, string toolName) => $"mcp_{serverId}_{toolName}";

    public async ValueTask DisposeAsync()
    {
        // 只停探活循环, 不终结实例本身: CommanderRuntime.RefreshMcpToolsAsync 是
        // 「Dispose → ConnectAllAsync」的组合, 把实例拆掉会让后续连接失去探活能力。
        lock (_probeSync)
        {
            _probeCts?.Cancel();
        }

        var clients = new List<McpClientBase>();
        foreach (var serverId in _clients.Keys)
        {
            if (_clients.TryRemove(serverId, out var client))
            {
                clients.Add(client);
            }
        }

        _toolsByServer.Clear();
        _bridgeIndex.Clear();

        foreach (var c in clients)
        {
            await c.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>写入某服务器的客户端 / 工具表 / 桥接索引。先清旧条目以覆盖重连场景(旧工具已下线)。</summary>
    private void RegisterServer(string serverId, McpClientBase client, List<McpToolDescriptor> tools)
    {
        RemoveServerEntries(serverId);

        var usable = new List<McpToolDescriptor>(tools.Count);
        foreach (var t in tools)
        {
            if (!IsSafeNameSegment(t.Name))
            {
                // 工具名会直接作为函数名交给 LLM, 一个非法名就可能拖垮整份 tools 声明, 直接跳过并留痕
                Log.Warn("MCP", $"MCP 服务器 {serverId} 的工具名 \"{t.Name}\" 含非法字符, 已跳过注册");
                continue;
            }

            usable.Add(t);
        }

        _clients[serverId] = client;
        _toolsByServer[serverId] = usable;
        foreach (var t in usable)
        {
            _bridgeIndex[BridgeName(serverId, t.Name)] = (serverId, t);
        }

        EnsureHealthProbe();
    }

    /// <summary>清掉某服务器的工具表与桥接索引(桥接键由工具名精确拼出, O(工具数))。</summary>
    private void RemoveServerEntries(string serverId)
    {
        if (_toolsByServer.TryRemove(serverId, out var old))
        {
            foreach (var t in old)
            {
                _bridgeIndex.TryRemove(BridgeName(serverId, t.Name), out _);
            }
        }
    }

    /// <summary>服务端 id / 工具名两段都要过的字符校验: 只允许字母、数字、-、_、. 。
    /// 刻意放行下划线 —— 精确字典查表已消除分割反查的歧义, 贸然禁掉会让现存含 _ 的 serverId 配置直接失效;
    /// 卡的是空白与控制字符, 那才是会产出非法函数名的部分。</summary>
    private static bool IsSafeNameSegment(string? s)
        => !string.IsNullOrWhiteSpace(s)
           && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>有连接时拉起后台探活循环(幂等); 无连接时循环自行退出, 不做空转唤醒。</summary>
    private void EnsureHealthProbe()
    {
        lock (_probeSync)
        {
            // 上一轮可能正在被 DisposeAsync 取消(此时 IsCompleted 仍为 false), 必须同时看 CTS 标志,
            // 否则会在"循环刚要退出"的窗口里误判为已在运行, 从此不再探活。
            if (_probeLoop is { IsCompleted: false } && _probeCts is { IsCancellationRequested: false })
            {
                return;
            }

            // 刻意不 Dispose 旧 CTS: 上一轮循环的 Task.Delay 注册可能还没摘干净,
            // 与之并发 Dispose 是文档明确不支持的用法。CTS 无终结器, 不释放只是让 GC 回收。
            _probeCts = new CancellationTokenSource();
            _probeLoop = RunHealthProbeAsync(_probeCts.Token);
        }
    }

    private async Task RunHealthProbeAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HealthProbeInterval, ct).ConfigureAwait(false);
                if (_clients.IsEmpty)
                {
                    return; // 无连接就不必继续轮询, 下次连接时由 EnsureHealthProbe 重新拉起
                }

                await ProbeOnceAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // DisposeAsync 取消所致, 正常退出
        }
        catch (Exception ex)
        {
            Log.Warn("MCP", ex, "MCP 后台探活循环异常退出");
        }
    }

    /// <summary>对全部已连接服务器各发一次 tools/list: 进程崩溃 / 连接断开都会在这里暴露,
    /// 直接把该服务器从三张表里摘掉, 让 IsConnected 与工具注册表随之收敛。</summary>
    private async Task ProbeOnceAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _probing, 1) == 1)
        {
            return; // 上一轮还没跑完(服务器响应慢), 跳过本轮避免堆积
        }

        try
        {
            foreach (var (serverId, client) in _clients)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await client.ListToolsAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn("MCP", ex, $"MCP 服务器 {serverId} 探活失败, 标记为已断开");
                    await DisconnectAsync(serverId).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _probing, 0);
        }
    }
}