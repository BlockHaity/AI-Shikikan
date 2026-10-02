using System.Diagnostics;
using System.Text.Json;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;
using AIShikikan.Core.Services.Tools;

namespace AIShikikan.Core.Services.Worker;

/// <summary>
/// Worker 工具 → <see cref="ITool"/> 桥接: 把独立进程 <c>AIShikikan.Worker</c> 暴露的单个工具
/// 包装成主进程引擎可直接调用的 <see cref="ITool"/>, 输入 schema 原样透传给 LLM。
/// </summary>
/// <remarks>
/// <para><b>形态照抄 <see cref="AIShikikan.Core.Services.Mcp.McpProxyTool"/></b>:
/// 那个类是"进程外工具的 ITool 包装"这一模式的既有范式, 本类是它的同构物 ——
/// 因此 <c>AgentEngine</c> 与 <c>ToolRegistry</c> 一行都不用改, 工具循环、审批闸、
/// 结果落库、卡片渲染全部复用现成链路。差异只在传输层(MCP JSON-RPC ↔ Worker 协议)
/// 与实时输出(Worker 会主动推 <c>notify/toolOutput</c>, MCP 不会)。</para>
///
/// <para>⚠️ <b>本类型必须与 <see cref="AIShikikan.Core.Services.Mcp.McpProxyTool"/> 保持两个独立类型,
/// 不可抽公共基类复用(哪怕基类只放共用成员)。</b>
/// <c>CommanderRuntime.RefreshMcpToolsAsync</c> 用 <c>Registry.UnregisterWhere(t =&gt; t is McpProxyTool)</c>
/// 注销 MCP 工具。若 Worker 工具挂在同一类型上, 或挂在它的**基类**上, 这条谓词会在每次
/// "重连 MCP"时把 Worker 工具连坐删掉 —— 表现为"动一下 MCP 设置, Worker 工具集体消失",
/// 没有任何报错, 排查成本极高。两个代理工具各自的 <c>Name</c> 都有
/// <c>mcp_/worker_</c> 式前缀, 天然不会撞名, 但<b>类型隔离是唯一真正的护栏</b>。</para>
///
/// <para><b>实例是可重入的</b>: 同一代理工具对象可能被多个会话引擎并发调用(前台会话 + 后台会话),
/// 所以本类<b>不持有任何"本次调用"的 mutable 状态</b>(没有 _currentCallId 之类)。
/// 一切与单次调用绑定的数据都走局部变量 + callId 路由。</para>
/// </remarks>
public sealed class WorkerProxyTool : ITool
{
    private const string LogCategory = "Worker";

    /// <summary>
    /// 单行封顶。Worker 是外部进程, 一条"行"可以是任意长(哪怕根本没有换行);
    /// 不封顶的话单个事件就能把几 MB 文本推进 GUI 的 <c>pendingEvents</c> 队列,
    /// 表现为一帧 UI 卡死。与子代理路径 <c>CliAgentDefinition.ReadStreamAsync</c> 的逐行裁剪同一意图。
    /// </summary>
    private const int MaxForwardedLineChars = 4000;

    private readonly WorkerClient _client;
    private readonly string? _sourceKey;

    // Parameters 的缓存(按原始 JSON 文本失效, 保证 tools/list 重新同步后能刷新 schema)。
    private readonly object _schemaGate = new();
    private string? _schemaSource;
    private JsonElement _schema;

    /// <param name="client">目标 Worker 的连接句柄。</param>
    /// <param name="descriptor">Worker 侧上报的工具描述符。</param>
    /// <param name="sourceKey">工具来源标识(Worker 标签/来源); 为 null 时回退到 <c>client.WorkerKey</c>, 只用于描述前缀与日志。</param>
    public WorkerProxyTool(WorkerClient client, WorkerToolDescriptor descriptor, string? sourceKey = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _sourceKey = sourceKey;

        // Name 在构造时**快照**成只读属性: ToolRegistry 以 name 为键, 注册之后若描述符的
        // Name 被改成别的值, 索引会与实际对象对不上(取不到 / 取到旧工具), 那是更难查的问题。
        // 参数 schema 则相反 —— 允许随重新同步刷新(见 Parameters), 因为它不参与索引。
        Name = Text(descriptor.Name);
        Description = BuildDescription();

        if (Name.Length == 0)
        {
            // 不抛异常: 一次 tools/list 里某个条目缺名, 不该让整批注册失败。
            // 但绝不能静默 —— 空名字的工具既不可选也不可调, 症状是"AI 好像少了个能力"。
            Log.Warn(LogCategory, $"Worker {Origin} 上报了空工具名, 该工具不可用");
        }
    }

    /// <summary>Worker 侧上报的原始描述符(诊断与"重新同步后原地更新描述"用)。</summary>
    public WorkerToolDescriptor Descriptor { get; }

    /// <summary>工具来源标识。</summary>
    public string? SourceKey => _sourceKey;

    /// <summary>工具名 = Worker 侧原样上报的名字。</summary>
    public string Name { get; }

    /// <summary>工具描述, 带来源前缀 —— 让 LLM(以及用户看工具卡时)知道这个能力来自哪个外部进程。</summary>
    public string Description { get; }

    /// <summary>
    /// 工具入参 schema, 原样透传 Worker 上报的 JSON。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>必须 <c>.Clone()</c></b>: <c>JsonElement</c> 只是 <c>JsonDocument</c> 的一个视图,
    /// 文档被回收(超出 using 作用域)后再读会抛 <c>ObjectDisposedException</c>,
    /// 而 <c>Parameters</c> 的读取时机(每回合 <c>ToSpecs</c>)远晚于解析点。
    /// 同一坑见 <c>ToolSchema.Json</c> 与 <c>McpProxyTool.Parameters</c>。
    ///
    /// <b>降级策略</b>: 缺失/非对象/解析失败一律给空对象 schema 并记 Warn,
    /// 与 <c>McpProxyTool</c> 一致 —— 工具仍可见可用(LLM 会尽量不传参),
    /// 比让整个工具集注册失败划算。
    /// </remarks>
    public JsonElement Parameters
    {
        get
        {
            var raw = Text(Descriptor.ParametersJson);
            lock (_schemaGate)
            {
                // 按**文本**判失效而不是每次重解析: ToSpecs 每回合对每个工具都读一次,
                // 而 tools/list 重新同步是低频事件 —— 两者用"文本是否变化"就能正确分开。
                if (_schemaSource is not null && string.Equals(_schemaSource, raw, StringComparison.Ordinal))
                {
                    return _schema;
                }

                _schema = ParseSchema(raw, Name);
                _schemaSource = raw;
                return _schema;
            }
        }
    }

    /// <summary>
    /// ⚠️ <b>审批留在主进程求值</b> —— 这是"Worker 工具能被 AgentEngine 正常审批"能成立的<b>全部</b>关键。
    /// <para>值直接取 Worker 侧上报的标志, 但<b>求值发生在主进程</b>: <c>AgentEngine.ExecuteToolAsync</c>
    /// 的审批闸读的是 <c>ITool.RequiresApproval</c>, 审批对话框在 GUI 进程里弹。
    /// Worker 进程既拿不到 UI, 也无法代表用户做决定; 若把审批放到 Worker 侧,
    /// 就必须新增一条跨进程的审批往返协议, 且 <c>AgentEngine</c> 那 25 行审批闸全部要重写。</para>
    /// <para>因此这里<b>不</b>覆写为恒 true/恒 false: 每个 Worker 工具的风险面不同
    /// (只读 vs 改文件), 由 Worker 作者自己声明, 主进程负责执行。</para>
    /// </remarks>
    public bool RequiresApproval => Descriptor.RequiresApproval;

    /// <summary>把工具调用转发到 Worker, 并把实时输出 / 取消 / 故障一路打通。</summary>
    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var callId = NewCallId();

        // ⚠️ 前置连通性检查 —— "Worker 崩溃时绝不能挂起"的第一道闸。
        // 断开的 Worker 进程再往里写只会立刻 EPIPE/写失败, 而 GUI 无人订阅时
        // 更容易把"发不出去"演变成"永远等不到响应"。直接快速失败并留痕。
        if (!_client.IsConnected)
        {
            _client.ReportFault($"工具 {Name} 调用前 Worker 已断开");
            Log.Warn(LogCategory, $"Worker 工具 {Name} 调用前已断开连接, 直接失败(callId={callId})");
            return ToolResult.Error(
                $"Worker 未连接, 工具 {Name} 无法执行(来源 {Origin})。请检查该 Worker 进程状态后重试。");
        }

        var request = new WorkerToolCallRequest
        {
            CallId = callId,
            Name = Name,
            Arguments = NormalizeArguments(args),
            // 回合上下文一并带过去: Worker 侧据此决定 Plan 模式下的行为, 以及要用哪个 Provider/模型。
            // ⚠️ 刻意**不带**工作目录: Worker 在 hello 握手时就已绑定 (workDir, sessionId),
            // 它的 ToolContext.WorkspaceRoot 就是该 workDir —— 即路径沙箱的根。
            // 每调用再传一份目录, 就等于给"临时换个目录跑子代理"开了后门(见 ToolPathSanitizer),
            // 且两侧各算一次归一化路径很容易分叉。
            IsPlanMode = ctx.IsPlanMode,
            SessionId = ctx.SessionId,
            ProviderId = ctx.ProviderId,
            Model = ctx.Model,
        };

        // ⚠️ 订阅要在**发请求之前**完成, 否则 Worker 极快回推的首行会漏给 UI。
        // using 保证响应/取消/异常三条路径都退订 —— 漏退订会让迟到的输出写进已经收尾的卡片。
        using var outputSub = SubscribeOutput(callId, ctx.OnToolOutput);
        using var cancelReg = RegisterCancel(callId, ct);

        try
        {
            var response = await _client.CallToolAsync(request, ct).ConfigureAwait(false);
            if (response is null)
            {
                // 传输契约上不该返回 null; 真发生了按"Worker 响应缺失"处理, 不让 NRE 逃出去。
                _client.ReportFault($"工具 {Name} 收到空响应");
                return ToolResult.Error($"Worker 未返回工具结果({Name}), 连接可能已异常中断。");
            }

            Log.Info(LogCategory,
                $"工具 {Name} 完成 ({sw.ElapsedMilliseconds}ms){(response.IsError ? " [isError]" : "")}");

            return new ToolResult
            {
                Content = Text(response.Content),
                IsError = response.IsError,
                // Worker 回传的 stepId 只在 Worker 侧自己建过检查点时才有意义;
                // 主进程不认这些记录(检查点库按仓库分目录), 故仅透传给卡片显示, 不参与回滚入口。
                StepId = string.IsNullOrWhiteSpace(response.StepId) ? null : response.StepId,
                Detail = SafeDecodeDetail(response.DetailType, response.DetailJson),
            };
        }
        catch (OperationCanceledException)
        {
            // ⚠️ **必须**原样上抛, 且必须排在下面的 catch(Exception) 之前。
            // 被后者吞掉的后果: 用户点了"停止", 主循环停了、引擎也往下走了,
            // 但 Worker 进程里那个 callId 还在跑(我们的取消通知也可能因此发不出去) ——
            // 用户以为停了实际没停, 改文件/跑构建的副作用继续落盘。
            // 同一条纪律见 AgentToolFactory / AssignmentManager。
            // 上抛后由 AgentEngine 兜住: 它会补一条"用户中断了此工具调用"的 tool 结果保持配对完整。
            throw;
        }
        catch (Exception ex)
        {
            // ⚠️ Worker 崩溃/管道断裂/协议错误, 一律转成 Error 让引擎当**软失败**继续本回合。
            // 绝不能让异常逃到引擎外层 —— 那里虽然也有 catch, 但那意味着本回合已经走到收尾,
            // LLM 拿不到"这个工具暂时不可用"的信息, 会把整轮回复退化成一句无意义的错误文本。
            _client.ReportFault($"工具 {Name} 调用失败: {ex.Message}");
            Log.Warn(LogCategory, ex, $"Worker 工具 {Name} 调用失败({sw.ElapsedMilliseconds}ms, callId={callId})");
            return ToolResult.Error(
                $"Worker 工具调用失败({Name}, 来源 {Origin}): {ex.Message}。" +
                "该 Worker 可能已崩溃或断开, 请检查其状态; 若只是偶发, 可换用其他手段继续。");
        }
    }

    /// <summary>
    /// 实时输出转发。⚠️ <b>这里刻意不套 <c>Progress&lt;string&gt;</c></b>, 与
    /// <c>AgentToolFactory</c> 里 <c>new Progress&lt;string&gt;(ctx.OnToolOutput)</c> 的写法<b>刻意不同</b>, 理由:
    /// <list type="number">
    /// <item><c>Progress&lt;T&gt;</c> 在**构造点**捕获 <c>SynchronizationContext</c>, 只有构造发生在
    /// UI 线程时它的 marshal 语义才成立。<c>AgentToolFactory</c> 那处确实在引擎的工具执行线程上构造
    /// (前台会话时就是 UI 线程), 而本处跑在 <b>transport 读循环/线程池</b>上 ——
    /// 捕获到的必然是 null, 于是回退 <c>TaskScheduler.Default</c>, 每行一次独立 <c>Post</c>,
    /// <b>行与行之间不再保序</b>, 反而把 Worker 有序输出打乱。</item>
    /// <item>下游已经线程安全且自带节流: <c>ctx.OnToolOutput</c> → <c>EngineEventHub.Publish</c>
    /// (逐订阅者 try/catch) → <c>ChatPageViewModel</c> 的 <c>ConcurrentQueue</c> + <c>Interlocked</c>
    /// → <c>Dispatcher.UIThread.Post</c>。再包一层 <c>Progress</c> 是重复搬运, 只增加丢行风险。</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <b>保序责任划分</b>: 同一 callId 的行在 transport 的**单一读循环**里按 Worker 写入顺序到达,
    /// 本层原样转发、不重排。跨 callId 的交错由 <c>callId</c> 路由天然隔离 ——
    /// 每个在飞调用各自持有自己的 sink, 谁的输出进谁的卡片。
    /// 前置条件在协议层: Worker 必须把该调用的全部输出行写在最终响应<b>之前</b>
    /// (否则最后几行会与"响应已返回"竞争, 落在订阅已退订之后而被丢弃)。
    /// </remarks>
    private IDisposable SubscribeOutput(string callId, Action<string>? sink)
    {
        // 可选回调的降级: ToolContext.OnToolOutput 是可空的(无界面订阅者/自检环境),
        // 此时不注册 sink, 工具照常执行 —— 与 AskUserTool 对 ctx.AskUser 为 null 的处理同一思路。
        if (sink is null) return NoopDisposable.Instance;

        return _client.SubscribeToolOutput(callId, line =>
        {
            if (line is null) return;
            var capped = line.Length > MaxForwardedLineChars
                ? line[..MaxForwardedLineChars] + "…(已截断)"
                : line;
            try
            {
                sink(capped);
            }
            catch (Exception ex)
            {
                // 单条输出行把 GUI 订阅者打挂, 不该连带判定这次工具调用失败:
                // 输出只是展示物, 结果内容才是 LLM 真正需要的。
                Log.Warn(LogCategory, ex, $"Worker 工具 {Name} 的实时输出转发异常(callId={callId})");
            }
        });
    }

    /// <summary>
    /// 取消传播: 回合 <paramref name="ct"/> 一取消, 立刻通知 Worker 放弃这个 callId。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>取消回调里绝不能 await / 阻塞等待异步操作</b>: 回调可能正跑在 transport 持锁的
    /// 读循环线程上, 同步等一个"要写同一条管道"的请求就是自死锁(经典: 回调线程等自己)。
    /// 故 fire-and-forget, 异常在内部吃干净 —— 取消通知发不出去本身不该变成新的未观察异常。
    /// </remarks>
    private CancellationTokenRegistration RegisterCancel(string callId, CancellationToken ct)
    {
        // 不可取消的 token 注册了也永远不会触发, 直接跳过(省一次闭包分配)。
        if (!ct.CanBeCanceled) return default;

        var client = _client;
        var toolName = Name;
        return ct.Register(() =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // ⚠️ 传 CancellationToken.None 而不是 ct: 此刻 ct 已取消,
                    // 用它会让这条"通知 Worker 放弃"的请求自己先被取消 —— Worker 永远收不到,
                    // 取消传播形同虚设。Worker 侧有自己的 WorkerProtocol.CancelTimeout 兜底, 不必再限时。
                    await client.SendCancelAsync(callId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 到这里调用方已经取消, 退出路径会走 shutdown/dispose 收拾残局;
                    // 这条只作为诊断线索留痕, 不影响取消语义。
                    Log.Warn(LogCategory, ex, $"向 Worker 发送取消失败(工具 {toolName}, callId={callId})");
                }
            });
        });
    }

    /// <summary>
    /// 参数归一: 只有"真正带成员的对象"才算有参数, 其余(缺省 / 空对象 / 数组 / null / 空串)
    /// 一律传 <c>{}</c>。
    /// </summary>
    /// <remarks>
    /// 不用 <c>GetRawText().Length &gt; 2</c> 那类近似判定: 它分不清 <c>{}</c> / <c>[]</c> / <c>null</c> /
    /// 空字符串, 也会被带空白的 <c>"{ }"</c> 骗过。与 <c>McpProxyTool</c> 保持同一套判定。
    /// </remarks>
    private static string NormalizeArguments(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object && args.EnumerateObject().MoveNext())
        {
            return args.GetRawText();
        }

        return "{}";
    }

    /// <summary>
    /// callId: 跨进程关联 <c>notify/toolOutput</c> 与取消通知的**唯一钥匙**, 必须唯一。
    /// 取 Guid 前 16 个 hex(64 位)而非 12 个(48 位): 多会话并发 + 长跑进程下,
    /// 碰撞一次的代价是"A 的输出流进 B 的卡片"这种几乎无法定位的现象, 多 4 个字符换 2^16 倍余量很划算。
    /// </summary>
    private static string NewCallId() => Guid.NewGuid().ToString("N")[..16];

    /// <summary>解析 Worker 上报的 schema; 任何异常都降级成空对象 schema 并留痕, 不让工具集注册失败。</summary>
    private static JsonElement ParseSchema(string raw, string toolName)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            Log.Warn(LogCategory, $"Worker 工具 {toolName} 未上报参数 schema, 降级为空对象 schema");
            return EmptySchema();
        }

        try
        {
            // ⚠️ Clone 不可省: 见 Parameters 的 remarks。
            var parsed = JsonDocument.Parse(raw).RootElement.Clone();
            if (parsed.ValueKind != JsonValueKind.Object)
            {
                // 非对象 schema 无法作为函数声明发给 LLM(各家 API 都要求 object), 同样降级。
                Log.Warn(LogCategory,
                    $"Worker 工具 {toolName} 的参数 schema 顶层是 {parsed.ValueKind} 而非 object, 降级为空对象 schema");
                return EmptySchema();
            }

            return parsed;
        }
        catch (JsonException ex)
        {
            Log.Warn(LogCategory, ex, $"Worker 工具 {toolName} 的参数 schema 不是合法 JSON, 降级为空对象 schema");
            return EmptySchema();
        }
    }

    private static JsonElement EmptySchema() =>
        ToolSchema.Json("""{ "type": "object", "properties": {} }""");

    /// <summary>
    /// 解析结构化卡片数据。解析失败只降级为通用文本卡, <b>不让它把一次成功的工具调用报成失败</b>:
    /// 卡片是展示增强, 不是结果本身。
    /// </summary>
    private static ToolCardDetail? SafeDecodeDetail(string? typeName, string? json)
    {
        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return ToolCardDetailCodec.Deserialize(typeName, json);
        }
        catch (Exception ex)
        {
            Log.Warn(LogCategory, ex, $"Worker 工具卡片解析失败(type={typeName}), 降级为通用文本卡");
            return null;
        }
    }

    private string Origin => string.IsNullOrWhiteSpace(_sourceKey) ? _client.WorkerKey : _sourceKey!;

    private string BuildDescription()
    {
        var body = Text(Descriptor.Description).Trim();
        return $"[Worker:{Origin}] {body}".Trim();
    }

    /// <summary>DTO 字段按可空处理: 描述符来自外部进程, 不假设任何一个字段非 null。</summary>
    private static string Text(string? s) => s ?? string.Empty;

    /// <summary>可空回调的 no-op 句柄(比返回 null 更省心: 调用方不必判空就能写 <c>using</c>)。</summary>
    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
