/*
 * Worker 进程里的协议服务端: 接管 stdin/stdout, 跑 NDJSON + JSON-RPC 2.0 子集的请求循环。
 *
 * ── 与父进程侧的关系(对称的两半) ──
 *   父进程: PipeTransport + WorkerRpcCore + WorkerClient/WorkerProxyTool  ──请求──>  WorkerServer(本文件)
 *   本文件: 读循环 / 分派 / 握手 / 取消表 / 心跳 / 反向通道(输出·分派·心跳)          <──响应── 父进程
 * 形状与派发逻辑照抄 McpClientBase(手写 stdio JSON-RPC 的既有范式): id 归一化、"响应/错误"分流、
 * 断线时完结在飞请求。这里是**镜像**(服务端 vs 客户端), 但恰恰因为同构, 两侧口径不会漂。
 *
 * ── 本文件承担的四件"必须做对"的事(否则症状都是静默的) ──
 *   1. 握手硬前置: 没跑过 worker/hello 就不接任何别的请求, 否则工具宿主是在没有会话上下文的情况下
 *      执行的(AgentExecutionScope 的委托全 null ⇒ Plan 授权恒 false、roster 恒空表, 无异常无日志)。
 *   2. tools/call 丢到后台执行: 子代理工具合法跑 30 分钟, 在读循环里 await 等于这段时间内
 *      读循环停摆, notify/cancel 进不来 ⇒ "用户点停止"退化成"主循环停了、子进程还在跑"。
 *   3. 该调用的**全部**输出行必须排在它的响应帧之前(父进程收到响应即退订输出订阅)。
 *   4. 取消跨进程要"双写": 父侧既要取消自己的等待, 也要写一帧 notify/cancel 通知子进程放弃那个 CTS;
 *      本文件就是这第二半的落点。
 *
 * ── 序列化纪律 ──
 *   JSON-RPC 信封(jsonrpc/id/method/params/result/error)没有 DTO, 一律 JsonObject 手拼(同 WorkerFrameCodec);
 *   params/result 的载荷一律走 AppJsonContext 的源生成 JsonTypeInfo, 禁止 JsonSerializer.Serialize(obj)。
 */

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Serialization;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Tools;

namespace AIShikikan.Core.Services.Worker;

/// <summary>
/// Worker 进程内的 JSON-RPC 服务端: 读循环、方法分派、握手、取消表、心跳与反向通道(输出/分派通知)。
/// </summary>
/// <remarks>
/// <para><b>⚠️ 帧的写锁归 <see cref="WorkerFrameWriter"/>, 本类不重复加锁</b>。
/// 它内部有实例级 <c>SemaphoreSlim</c>, 保证"一帧一次 WriteAsync"不会与另一帧字节交错
/// (管道流非线程安全)。本类并发写是常态: 心跳定时器、每个 <c>tools/call</c> 的排空任务、
/// 分派状态回调都在各写各的。若哪天 <see cref="WorkerFrameWriter"/> 去掉了这把锁,
/// 症状是偶发的"一帧被劈成两半" —— 表现为对端解析异常, 且只在并发写时出现, 极难复现。</para>
///
/// <para><b>⚠️ 日志落盘的最后一步不在本类</b>: <c>Log.Flush()</c> 会把日志系统置为"已收工"状态,
/// 之后写的日志(含后台任务收尾日志)会被静默丢弃。Worker 宿主程序(<c>Program.cs</c>)必须在
/// <see cref="RunAsync"/> 返回<b>之后</b>再 Flush, 且不要在同一个进程里复活。</para>
/// </remarks>
public sealed class WorkerServer
{
    private const string LogCategory = "Worker";

    /// <summary>
    /// JSON-RPC 错误码。用标准码而不是一律 <c>-32603</c>:
    /// 父进程侧当前只取 <c>error.message</c> 造异常(<c>WorkerRpcCore.BuildRpcException</c>),
    /// 但 code 与 data 是给"将来"留的位置 —— 想让结构化错误码真正生效, 需要父侧扩展派发逻辑
    /// (见 <see cref="WriteErrorAsync"/> 的 remarks)。本文件<b>照协议发完整结构</b>,
    /// 不因为"反正读不到"而省字段。
    /// </summary>
    private const int ErrorInvalidParams = -32602;
    private const int ErrorInternal = -32603;

    /// <summary>请求被取消(LSP 约定的 RequestCancelled)。</summary>
    private const int ErrorRequestCancelled = -32800;

    /// <summary>尚未握手就来了别的请求(服务端自定义区间 -32000..-32099)。</summary>
    private const int ErrorHandshakeRequired = -32002;

    /// <summary>退出前等待在飞工具调用收尾的预算。</summary>
    /// <remarks>
    /// ⚠️ 刻意<b>小于</b> <see cref="WorkerProtocol.ShutdownTimeout"/>(5s): 父进程在我们回 ok 之后
    /// 最多等 ShutdownTimeout, 我们必须在这之前把控制权交还进程退出流程。
    /// 卡在预算里的调用意味着它的子代理进程树可能成为孤儿(我们已尽力 Cancel, 但工具可能不配合);
    /// 这条 Warn 就是父进程回收时该连进程树一起结束的线索。
    /// </remarks>
    private static readonly TimeSpan ExitDrainBudget = TimeSpan.FromSeconds(2);

    /// <summary>「把某次调用的输出排空」的时间预算。</summary>
    /// <remarks>
    /// 正常情况下是微秒级(队列里通常只有几十行小帧); 超预算意味着管道写侧被父进程拖住。
    /// 此时仍按协议写出响应帧(否则父进程在 <c>tools/call</c> 上会等到天荒地老 ——
    /// <see cref="WorkerProtocol.ToolCallTimeout"/> 是<b>刻意无限</b>的), 只是尾部若干行落在响应之后被丢弃。
    /// </remarks>
    private static readonly TimeSpan OutputFlushBudget = TimeSpan.FromSeconds(2);

    /// <summary>单次写帧的兜底超时。</summary>
    /// <remarks>
    /// 写侧可能被一个不肯读管道的父进程堵死。协议帧可以很大(整份文件内容 + WriteIndented 的卡片 JSON),
    /// 没有这道闸的话一个卡住的父进程能把 Worker 永久钉在 WriteAsync 里 —— 而 Worker 是个独立进程,
    /// 它卡住 = 那一整个(目录 × 会话)的工具能力永久不可用。
    /// </remarks>
    private static readonly TimeSpan WriteBudget = TimeSpan.FromSeconds(30);

    /// <summary>退出前等心跳循环收尾的预算(它只是一次写, 通常瞬间完成)。</summary>
    private static readonly TimeSpan HeartbeatStopBudget = TimeSpan.FromMilliseconds(500);

    // ⚠️ 工具声明的生成(Kind / RequiresGitWrite / schema 降级 / 空名剔除)已全部搬去
    // WorkerToolDescriptorFactory —— 那里是唯一落点, 主进程的内联降级列举也调它。
    // 曾经本类里有一份 BuildDescriptors/DeriveKind/GitWritingTools, 漂移的症状只是
    // 「工具分类显示错误」这类没有报错的小事, 所以两份都不能留。

    private readonly IWorkerToolHost _host;

    /// <summary>callId → 本次调用的取消源。收到 <c>notify/cancel</c> 时<b>只</b>读表并 Cancel(不移除)。</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _callCts = new(StringComparer.Ordinal);

    /// <summary>callId → 该调用的实时输出通道(见 <see cref="CallOutputChannel"/> 的顺序保证)。</summary>
    private readonly ConcurrentDictionary<string, CallOutputChannel> _outputChannels = new(StringComparer.Ordinal);

    /// <summary>在飞的 <c>tools/call</c> 处理任务。退出时要等它们(有界), 否则会把它们的异常变成
    /// <see cref="TaskScheduler.UnobservedTaskException"/> 噪声, 或让管道在它们还准备写响应时就被 Dispose。</summary>
    private readonly ConcurrentDictionary<Task, byte> _inflightCalls = new();

    private WorkerFrameWriter? _writer;
    private WorkerRpcCore? _rpc;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _writeBudget;
    private Task? _heartbeatTask;
    private volatile bool _handshakeDone;
    private int _runStarted;

    /// <param name="host">工具宿主。Worker 进程里由 SlimHost/Program 提供实现。</param>
    public WorkerServer(IWorkerToolHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>接管 stdin/stdout 跑协议循环; 管道 EOF 或 <paramref name="ct"/> 取消时返回。</summary>
    /// <remarks>
    /// <para><b>流的所有权不转移</b>: 只读 <paramref name="input"/>、只写 <paramref name="output"/>,
    /// 两者都不在返回时关闭(关闭管道是父进程 <c>PipeTransport.DisposeAsync</c> 的职责, 它要按
    /// 「关写端 → 等自退 → 必要时 Kill → Dispose 进程」的顺序走)。</para>
    /// <para><b>只能调一次</b>: stdin/stdout 的接管是一次性的, 第二次调用抛
    /// <see cref="InvalidOperationException"/>。</para>
    /// <para><b>返回即"可以退出了"</b>: 返回时在飞的工具调用已被取消(有界等待, 见
    /// <see cref="ExitDrainBudget"/>), 心跳已停, 在途的反向请求已完结。宿主程序随后直接退出即可,
    /// <b>不要</b>在这里(或之后)去杀父进程 —— 父进程此刻正在等我们退出, 反过来做只会互等。</para>
    /// </remarks>
    public async Task RunAsync(Stream input, Stream output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException("WorkerServer.RunAsync 只能调用一次: stdin/stdout 的接管是一次性的");
        }

        // 进程内生命周期令牌: 三条退出路径(EOF / worker/shutdown / 外部 ct)汇合到它。
        // 每个在飞 tools/call 的 CTS 都 linked 到它 —— 于是"父进程消失 / 收到 shutdown"能连带取消子代理进程树。
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _lifetime = lifetime;
        _writeBudget = new CancellationTokenSource(WriteBudget);

        // 反向通道(Worker → 父)的请求配对内核。当前唯一可能的用途是预留的 request/askUser(未启用),
        // 但内核必须先在位: 启用时只需加一个方法调用, 而不是重新发明一遍 id 分配/挂起表/故障完结。
        using var rpc = new WorkerRpcCore(TransmitAsync);
        _rpc = rpc;

        // using 而非手动 Dispose: RunAsync 中途抛异常时写闸也要关(WorkerFrameWriter.Dispose 只关闸不关流)。
        using var writer = new WorkerFrameWriter(output);
        _writer = writer;

        Log.Info(LogCategory,
            $"Worker 协议循环启动: pid={Environment.ProcessId}, 协议版本={WorkerProtocol.ProtocolVersion}");

        try
        {
            await ReadLoopAsync(input, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常退出(shutdown / 外部 ct / 管道读被打断), 不算故障
        }
        catch (Exception ex)
        {
            // 管道 IO 异常(父进程被强杀时管道随之关闭)属预期路径, 但不能完全静默:
            // "读循环早就死了、之后一切只能等超时"这类问题需要留痕。
            Log.Debug(LogCategory, $"Worker 读循环结束: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await FinishAsync().ConfigureAwait(false);

            // ⚠️ 顺序: 必须在 FinishAsync **之后** 才断写引用 —— 收尾期间在飞调用还要写它们的响应帧。
            Volatile.Write(ref _writer, null);
            _writeBudget?.Dispose();
            _writeBudget = null;
            _rpc = null;
            _lifetime = null;

            Log.Info(LogCategory, "Worker 协议循环已退出");
        }
    }

    // ───────────────────────── 反向通道(宿主侧出口) ─────────────────────────

    /// <summary>
    /// 投递一行工具实时输出(对应 <c>notify/toolOutput</c>)。<b>刻意做成同步且只入队</b>,
    /// 供宿主直接挂到 <c>ToolContext.OnToolOutput</c>(类型是 <c>Action&lt;string&gt;</c>, 没有 await 的位置)。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么入队而不是当场写管道</b>: 现有工具实现用 <c>new Progress&lt;string&gt;(ctx.OnToolOutput)</c>
    /// 转发子代理输出, 而 <c>Progress&lt;T&gt;</c> 的 <c>Report()</c> 只是入队 —— 真正的投递发生在
    /// <b>工具返回之后</b>的线程池上。若宿主在这里当场同步写管道, 那一行仍会与最终响应竞争,
    /// 而父进程侧 WorkerProxyTool 收到响应<b>即退订</b>输出订阅, 末几行直接丢失(且无任何报错)。
    /// 入队 + 单一排空任务 + 响应前先排空, 才拿得到「输出全在响应之前」这个确定性保证
    /// (实现见 <see cref="CallOutputChannel"/> 与 <see cref="FlushOutputChannelAsync"/>)。</para>
    /// <para><b>不会阻塞, 因此可以安全地挂在 <c>Action&lt;string&gt;</c> 上</b>: 入队是纯内存操作。</para>
    /// <para><b>⚠️ 已知且接受的残余风险</b>: 若某行是在「响应阶段已经开始」之后才被 <c>Report()</c> 的
    /// (即 <c>Progress&lt;T&gt;</c> 那次延迟投递赶在了收尾之后), 它会被<b>丢弃</b>而不是乱序地排在响应之后。
    /// 丢几行尾部日志远好过让输出与响应竞争 —— 后者会让卡片结尾缺几行且毫无提示。</para>
    /// <para>无对应在途调用(callId 不在表里)时也只记 Debug 并丢弃: 见方法体的说明。</para>
    /// </remarks>
    /// <param name="callId">关联的在途调用; 空值直接丢弃(无处投递)。</param>
    /// <param name="line">一行输出文本, 按 <see cref="WorkerProtocol.MaxLineChars"/> 封顶。</param>
    public void EmitToolOutput(string callId, string line)
    {
        if (string.IsNullOrEmpty(callId))
        {
            // 无归属输出: 父进程侧也只会跳过工具路由(见 WorkerClient.OnTransportToolOutput),
            // 硬塞给某个在飞调用反而会把无关文本写进用户的工具卡片。
            Log.Debug(LogCategory, "丢弃一条无 callId 的工具输出");
            return;
        }

        if (!_outputChannels.TryGetValue(callId, out var channel))
        {
            // ⚠️ 只往**已存在**的通道投递, 不在这里新建: 没有通道 = 这次调用已经收尾(响应已写或正在写)。
            // 此时新建通道会没人 Close, 排空任务也就永远挂着(每行泄漏一个 Task + 一个 Channel)。
            Log.Debug(LogCategory, $"工具输出迟到(callId={callId}): 该调用已收尾, 已丢弃");
            return;
        }

        if (!channel.TryEnqueue(CapLine(line)))
        {
            Log.Debug(LogCategory, $"工具输出被丢弃(callId={callId}): 该调用的输出通道已封闭");
        }
    }

    /// <summary>
    /// <see cref="EmitToolOutput(string, string)"/> 的通知形态重载, 形状对齐
    /// <c>Action&lt;WorkerToolOutputNotification&gt;</c> —— 宿主可以直接
    /// <c>host.OutputSink = server.EmitToolOutput</c>(方法组隐式转换会挑中这一条), 接线是一个等号。
    /// </summary>
    /// <remarks>
    /// 这里<b>不做</b>任何"补字段"的工作: 通知 DTO 的字段名与帧的 params 结构由
    /// <c>BuildNotification</c> 用的源生成上下文保证唯一真源, 重载之间不允许出现行为差异
    /// (那正是"两份实现慢慢漂移"的起点)。</remarks>
    /// <param name="notification">一行输出通知; null 直接忽略。</param>
    public void EmitToolOutput(WorkerToolOutputNotification notification)
    {
        if (notification is null)
        {
            Log.Debug(LogCategory, "收到空的工具输出通知, 已忽略");
            return;
        }

        EmitToolOutput(notification.CallId, notification.Line);
    }

    /// <summary>
    /// 推送一条分派状态变更(对应 <c>notify/assignment</c>, 载荷是整份 <see cref="Assignment"/> 快照)。
    /// </summary>
    /// <remarks>
    /// <para><b>同步阻塞写出</b>(与 <see cref="EmitToolOutput(string, string)"/> 的"只入队"不同):
    /// 分派通知是低频事件(每个分派几次状态变化), 但父进程侧靠它驱动 Agent 面板刷新与
    /// <c>assignments/*.json</c> 落盘 —— 顺序错了会让 UI 短暂显示已完成却还挂着"运行中"。
    /// 直接写掉(而不是排队)可以让"谁先发生谁先到"由这里的调用顺序决定, 不引入第二个乱序来源。</para>
    /// <para>Worker 进程没有 <c>SynchronizationContext</c>(没有 UI 线程), 因此这里的
    /// <c>GetAwaiter().GetResult()</c> 不存在经典 async 死锁; 它最多等一个正在写别的帧的任务,
    /// 而那些帧写完后父进程就会继续读。</para>
    /// </remarks>
    /// <param name="assignment">分派记录快照; null 直接忽略。</param>
    public void EmitAssignment(Assignment assignment)
    {
        if (assignment is null)
        {
            return;
        }

        var frame = BuildNotification(
            WorkerProtocol.NotifyAssignment,
            new WorkerAssignmentNotification { Assignment = assignment },
            AppJsonContext.Default.WorkerAssignmentNotification);

        try
        {
            WriteFrameAsync(frame, WriteToken).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // 分派状态只是展示/统计增强, 写不出去不该把产生它的那段业务代码(往往是引擎线程)带崩
            Log.Warn(LogCategory, ex, $"推送分派状态失败(assignment={assignment.AssignmentId})");
        }
    }

    // ───────────────────────── 读循环与分派 ─────────────────────────

    /// <summary>
    /// 读循环: 单消费者, 逐帧读 → 分派。返回条件只有三个 —— 管道 EOF、生命周期令牌取消、读侧异常。
    /// </summary>
    /// <remarks>
    /// <b>⚠️ 这里绝对不能出现 <c>Task.Delay</c></b>: 读循环一旦 Delay, 父进程发来的 <c>notify/cancel</c>
    /// 就要等 Delay 结束才被读到, 于是「用户点停止」会变成「工具继续跑 N 秒」, 而子代理的进程树还在写文件。
    /// 心跳因此必须跑在独立后台任务里(见 <see cref="StartHeartbeat"/>)。
    ///
    /// <para><b>⚠️ 解析不了的行由 <see cref="WorkerFrameReader"/> 内部消化</b>(记 Debug 后继续读下一行):
    /// 管道里混着对端的启动 banner 与任何调试输出, 把它们当成"坏帧"升级成连接故障是纯粹的误伤。
    /// 本类因此只需要处理两类"读不下去": 真正 EOF(返回 null)与流上的 IO 异常。</para>
    /// </remarks>
    private async Task ReadLoopAsync(Stream input, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            JsonObject? frame;
            try
            {
                frame = await WorkerFrameReader.ReadAsync(input, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Debug(LogCategory, $"Worker 读管道结束: {ex.GetType().Name}: {ex.Message}");
                break;
            }

            if (frame is null)
            {
                Log.Info(LogCategory, "父进程关闭了管道写端(EOF), Worker 准备退出");
                break;
            }

            try
            {
                await DispatchAsync(frame, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 单帧处理失败不得带走整条连接: 一条坏帧 / 一个坏工具不该让父子两侧一起重连。
                // 父进程那边会因这次请求没等到响应而超时, 那已经足够让它把这件事记下来。
                var method = ReadString(frame, "method") ?? "(无)";
                Log.Warn(LogCategory, ex, $"Worker 处理帧异常(method={method}), 读循环继续");
            }
        }
    }

    /// <summary>
    /// 分派一帧。<b>带 <c>method</c> 的是请求/通知(要我们应答), 不带的是父进程对我方反向请求的响应</b> ——
    /// 这个分流必须与 <see cref="WorkerRpcCore.DispatchResponse"/> 的口径一致(它见到 method 会记 Warn 并原样退回)。
    /// </summary>
    private async Task DispatchAsync(JsonObject frame, CancellationToken ct)
    {
        var method = ReadString(frame, "method");
        if (method is null)
        {
            _rpc?.DispatchResponse(frame);
            return;
        }

        var id = ReadRpcId(frame);

        // 握手硬前置: hello 之前不接受任何其它方法。
        // 此时宿主还没 BindHello, 工具执行会静默降级(见 IWorkerToolHost 的类注释), 宁可明确拒绝。
        if (!_handshakeDone && !string.Equals(method, WorkerProtocol.MethodHello, StringComparison.Ordinal))
        {
            var why = "尚未完成 worker/hello 握手, 请求被拒绝";
            if (id is not null)
            {
                await WriteErrorAsync(id, ErrorHandshakeRequired, $"{method}: {why}").ConfigureAwait(false);
            }
            else
            {
                Log.Debug(LogCategory, $"{method}: {why}(通知形态, 无响应可回)");
            }

            return;
        }

        switch (method)
        {
            case WorkerProtocol.MethodHello:
                await HandleHelloAsync(frame, id, ct).ConfigureAwait(false);
                break;

            case WorkerProtocol.MethodToolsList:
                await HandleToolsListAsync(id).ConfigureAwait(false);
                break;

            case WorkerProtocol.MethodToolsSync:
                await HandleToolsSyncAsync(frame, id).ConfigureAwait(false);
                break;

            case WorkerProtocol.MethodPing:
                await HandlePingAsync(id).ConfigureAwait(false);
                break;

            case WorkerProtocol.MethodShutdown:
                await HandleShutdownAsync(id).ConfigureAwait(false);
                break;

            case WorkerProtocol.MethodToolsCall:
                // ⚠️ 丢后台, **不 await**: 见类级说明第 2 条(读循环必须始终能读到 notify/cancel)。
                DispatchToolCall(frame, id, ct);
                break;

            case WorkerProtocol.NotifyCancel:
                HandleCancel(frame);
                break;

            case WorkerProtocol.RequestAskUser:
                await HandleAskUserAsync(id).ConfigureAwait(false);
                break;

            default:
                // 收到不认识的方法: 只记 Warn, **不回任何响应**。
                // 通知(无 id)本来就不该有响应; 而带 id 的未知方法在正常时序下不会出现
                // (握手已把两侧版本钉死), 真出现了也宁可让父进程按自己的超时收场
                // —— 回一个"我不认识"反而可能撞上父侧的派发假设。这条 Warn 就是唯一的排障线索。
                Log.Warn(LogCategory,
                    $"收到未知的 Worker 方法 {method}, 已忽略(id={(id is null ? "(通知)" : "有")})");
                break;
        }
    }

    /// <summary>握手: 校验协议版本 → 绑定宿主 → 回 <see cref="WorkerHelloResponse"/>。</summary>
    private async Task HandleHelloAsync(JsonObject frame, JsonNode? id, CancellationToken ct)
    {
        if (id is null)
        {
            Log.Warn(LogCategory, "worker/hello 以通知形态到达(无 id), 已忽略: 父进程收不到回执, 会一直等到握手超时");
            return;
        }

        if (_handshakeDone)
        {
            // BindHello 是一次性的(宿主会话上下文已按第一次握手构造完毕), 重新绑定只会让
            // "工具跑在 A 会话的上下文里、却声称自己是 B 会话"。明确拒绝而不是静默接受。
            Log.Warn(LogCategory, "收到第二次 worker/hello, 已拒绝(会话上下文不可重绑)");
            await WriteErrorAsync(id, ErrorInvalidParams,
                "重复握手: 本 Worker 的会话上下文已绑定, 不接受第二次 worker/hello")
                .ConfigureAwait(false);
            return;
        }

        var request = TryReadPayload(frame, AppJsonContext.Default.WorkerHelloRequest, WorkerProtocol.MethodHello);
        if (request is null)
        {
            await WriteErrorAsync(id, ErrorInvalidParams,
                $"worker/hello 的 params 缺失或不是合法的 {nameof(WorkerHelloRequest)}").ConfigureAwait(false);
            return;
        }

        // 版本不符 → 回 Ok=false 并主动退出, 让父进程重拉一个匹配的 Worker。
        // 绝不能"先跑起来再说": 协议里跨进程出现的枚举以数字落盘(AppJsonContext 未开 UseStringEnumConverter),
        // 版本错位是**静默**的 —— 会表现为 Assignment.Status 莫名跳变、分派卡住不消失, 极难定位。
        if (request.ProtocolVersion != WorkerProtocol.ProtocolVersion)
        {
            var why = $"协议版本不一致(父进程={request.ProtocolVersion}, 本 Worker={WorkerProtocol.ProtocolVersion})";
            Log.Warn(LogCategory, $"{why}, 拒绝服务并退出, 由父进程重拉匹配的 Worker");

            await WriteResultAsync(id, new WorkerHelloResponse
            {
                Ok = false,
                Error = why,
                WorkerPid = Environment.ProcessId,
                ProtocolVersion = WorkerProtocol.ProtocolVersion,
            }, AppJsonContext.Default.WorkerHelloResponse).ConfigureAwait(false);

            RequestSelfStop();
            return;
        }

        try
        {
            _host.BindHello(request);
        }
        catch (Exception ex)
        {
            // 宿主启动期装载失败 = 这个进程提供不了服务, 与其留一个"工具全失败"的僵尸, 不如立刻退。
            Log.Error(LogCategory, ex, "工具宿主绑定握手上下文失败, Worker 退出");
            await WriteResultAsync(id, new WorkerHelloResponse
            {
                Ok = false,
                Error = $"工具宿主绑定会话上下文失败: {ex.Message}",
                WorkerPid = Environment.ProcessId,
                ProtocolVersion = WorkerProtocol.ProtocolVersion,
            }, AppJsonContext.Default.WorkerHelloResponse).ConfigureAwait(false);

            RequestSelfStop();
            return;
        }

        _handshakeDone = true;

        // 心跳在握手成功后才起: 它的语义是"我已完成装载、正在服务",
        // 一个即将因版本不符而终止的进程不该发出这种心跳。
        StartHeartbeat(ct);

        Log.Info(LogCategory,
            $"Worker 握手完成: pid={Environment.ProcessId}, session={request.SessionId}, " +
            $"workDir={request.WorkDir}, planMode={request.IsPlanMode}");

        await WriteResultAsync(id, new WorkerHelloResponse
        {
            Ok = true,
            WorkerPid = Environment.ProcessId,
            ProtocolVersion = WorkerProtocol.ProtocolVersion,
        }, AppJsonContext.Default.WorkerHelloResponse).ConfigureAwait(false);
    }

    /// <summary><c>worker/tools/list</c>: 回宿主当前工具集的<b>全量快照</b>(不是增量)。</summary>
    private async Task HandleToolsListAsync(JsonNode? id)
    {
        if (id is null)
        {
            Log.Debug(LogCategory, "worker/tools/list 以通知形态到达, 已忽略");
            return;
        }

        IReadOnlyList<ITool> tools;
        try
        {
            tools = _host.SnapshotTools() ?? Array.Empty<ITool>();
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(id, ErrorInternal, $"列举工具失败: {ex.Message}", ex).ConfigureAwait(false);
            return;
        }

        // ⚠ 过滤不在这里做: 宿主注册表**已经是**握手与 tools/sync 之后的过滤结果,
        // 再按请求载荷裁一遍等于把策略应用两遍(见 WorkerToolDescriptorFactory.Build 的说明)。
        var descriptors = WorkerToolDescriptorFactory.Build(tools);
        Log.Debug(LogCategory, $"回 worker/tools/list: {descriptors.Count} 个工具");
        await WriteResultAsync(id, new WorkerToolsResponse { Tools = descriptors },
            AppJsonContext.Default.WorkerToolsResponse).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>worker/tools/sync</c>: 把运行态开关(右侧栏可见性 / Plan 模式授权)交给宿主。
    /// 回 <see cref="WorkerToolsResponse"/> —— 即<b>同步之后</b>的权威工具清单, 一次往返搞定。
    /// </summary>
    /// <remarks>
    /// 早期版本这里回 <see cref="WorkerOkResponse"/> 并让父进程再拉一次 <c>tools/list</c>,
    /// 理由是"让工具清单只保留一个真源"。但 <c>IToolTransport.SyncToolsAsync</c> 的返回类型
    /// 是 <c>WorkerToolsResponse</c>, 两边口径不一致的代价很具体: 父侧按
    /// <c>WorkerToolsResponse</c> 去读一个 <c>WorkerOkResponse</c> 的载荷, 得到的
    /// <c>Tools</c> 就是<b>空清单</b>, 而症状是"Worker 侧一个工具都没有", 极难归因。
    /// <para>现在改成单跳, 顺带消掉了一个竞态: 两跳之间若有另一个 sync 插进来,
    /// 父进程会拿到与它刚发的配置不对应的清单。</para>
    /// <para>"只保留一个真源"这个诉求仍然成立 —— 真源是 <c>BuildDescriptors</c>(见
    /// <c>HandleToolsListAsync</c>), sync 与 list 调的是<b>同一个</b>函数, 不是两份实现。</para>
    /// </remarks>
    private async Task HandleToolsSyncAsync(JsonObject frame, JsonNode? id)
    {
        if (id is null)
        {
            Log.Debug(LogCategory, "worker/tools/sync 以通知形态到达, 已忽略");
            return;
        }

        var request = TryReadPayload(frame, AppJsonContext.Default.WorkerToolsSyncRequest,
            WorkerProtocol.MethodToolsSync);
        if (request is null)
        {
            await WriteErrorAsync(id, ErrorInvalidParams,
                $"worker/tools/sync 的 params 缺失或不是合法的 {nameof(WorkerToolsSyncRequest)}")
                .ConfigureAwait(false);
            return;
        }

        if (_host is IWorkerToolHostSync sync)
        {
            try
            {
                sync.ApplyToolsSync(request);
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(id, ErrorInternal, $"应用工具集同步失败: {ex.Message}", ex).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            // 刻意仍回 Ok=true: 见 IWorkerToolHostSync 的 remarks(回 false 会被父进程当成"Worker 不可用")。
            Log.Warn(LogCategory,
                "宿主未实现 IWorkerToolHostSync, 本次 worker/tools/sync 为空操作" +
                $"(coreTools={request.CoreTools}, subagentVisible={request.SubagentVisible}, " +
                $"planMode={request.PlanMode}, allowed={request.AllowedAgentIds?.Count ?? 0}): " +
                "已启动 Worker 的工具集不会跟随右侧栏开关与 Plan 模式变化");
        }

        // 单跳返回同步后的权威清单(见本方法的 remarks): 与 tools/list 走同一个
        // WorkerToolDescriptorFactory.Build, 所以"清单只有一个真源"依然成立。
        IReadOnlyList<WorkerToolDescriptor> descriptors;
        try
        {
            descriptors = WorkerToolDescriptorFactory.Build(
                _host.SnapshotTools() ?? Array.Empty<ITool>());
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(id, ErrorInternal,
                $"同步后枚举工具集失败: {ex.Message}", ex).ConfigureAwait(false);
            return;
        }

        await WriteResultAsync(id, new WorkerToolsResponse { Tools = descriptors },
            AppJsonContext.Default.WorkerToolsResponse).ConfigureAwait(false);
    }

    /// <summary><c>worker/ping</c>: 活性探测, 只回 <c>Ok</c>。同步处理, 不碰任何有状态的东西。</summary>
    private async Task HandlePingAsync(JsonNode? id)
    {
        if (id is null)
        {
            Log.Debug(LogCategory, "worker/ping 以通知形态到达, 已忽略");
            return;
        }

        await WriteResultAsync(id, new WorkerOkResponse { Ok = true },
            AppJsonContext.Default.WorkerOkResponse).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>worker/shutdown</c>: 回 ok, 然后<b>主动结束 <see cref="RunAsync"/></b>。
    /// </summary>
    /// <remarks>
    /// 父进程随后会关管道并等不超过 <see cref="WorkerProtocol.ShutdownTimeout"/>, 所以这里必须
    /// <b>先写完回执再停</b>(顺序反了就是"父进程等一个永不到来的 ok", 然后只能走超时强杀路径,
    /// 而强杀不会带走子代理进程树)。这里的 <c>RequestSelfStop</c> 只是取消本进程的生命周期令牌:
    /// 读循环随即退出, 由 <see cref="RunAsync"/> 的 finally 做统一收尾。<b>不要</b>去关管道或杀父进程
    /// —— 方向是反的, 父在等我们退出。</remarks>
    private async Task HandleShutdownAsync(JsonNode? id)
    {
        if (id is not null)
        {
            await WriteResultAsync(id, new WorkerOkResponse { Ok = true },
                AppJsonContext.Default.WorkerOkResponse).ConfigureAwait(false);
        }
        else
        {
            Log.Warn(LogCategory, "worker/shutdown 以通知形态到达(无 id, 无回执可写), 仍按请求退出");
        }

        Log.Info(LogCategory, "收到 worker/shutdown, 开始收尾");
        RequestSelfStop();
    }

    /// <summary>
    /// <c>request/askUser</c>: <b>协议预留位, 当前方案不实现</b>, 一律回 error。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么现在不做</b>: <c>ask_user</c> 刻意留在主进程 —— 它要弹 GUI 卡片并等用户作答,
    /// 那条链路本来就在父进程侧(见 <see cref="WorkerAskUserRequest"/> 的说明与架构文档 2.4)。
    /// 留主则审批/提问的事件机制(<c>EngineEventHub</c> 那条"提问强制全投"的规则)零改动;
    /// 绕 Worker 出去只是多一跳, 却要在子进程里再造一套等待用户的能力。</para>
    /// <para><b>⚠️ 将来启用时必须同时解决超时</b>(这是它不能"顺手就做"的原因):
    /// 这个请求是在某个 <see cref="WorkerToolCallRequest"/> <b>在途</b>期间发出的,
    /// 而 <see cref="WorkerProtocol.ToolCallTimeout"/> 是<b>刻意无限</b>的 —— 用户不答就永远挂着。
    /// 启用方必须自行套 <c>AgentEngine.ApprovalTimeout</c>(5 分钟)级别的超时,
    /// 并在超时时回一个 <see cref="WorkerAskUserResponse"/>(<c>Answered = false</c>, <c>Answer = null</c>),
    /// 语义与 <c>ToolContext.AskUser</c> 返回 null 一致。</para>
    /// <para><b>启用步骤(四处都要改, 缺一处就是静默挂起)</b>:
    /// ① 本方法改成: 用本类持有的 <c>WorkerRpcCore</c> 的
    /// <c>RequestAsync(WorkerProtocol.RequestAskUser, params, timeout, ct)</c> 发出请求,
    /// 其中 params 是 <c>AppJsonContext.Default.WorkerAskUserRequest</c> 序列化出来的节点;
    /// ② 父进程侧 <c>PipeTransport</c> 的读循环要把 method 为 <c>request/askUser</c> 的帧当**请求**处理,
    /// 弹与 <c>AskUserTool</c> 同一个对话框, 再把 <c>WorkerAskUserResponse</c> 作为 result 回写;
    /// ③ 父进程侧 <c>WorkerProxyTool</c> 把它接到 <c>ToolContext.AskUser</c> 上
    /// (即让 <c>AskUserTool</c> 在 Worker 侧也能工作 —— 它已有 <c>ctx.AskUser is null</c> 的安全降级);
    /// ④ Worker 侧宿主把 <c>ToolContext.AskUser</c> 接到这个反向请求上。</para>
    /// </remarks>
    private async Task HandleAskUserAsync(JsonNode? id)
    {
        if (id is null)
        {
            Log.Debug(LogCategory, "收到无 id 的 request/askUser, 已忽略");
            return;
        }

        Log.Warn(LogCategory,
            "收到 request/askUser: 该协议位当前未启用(ask_user 工具刻意留在主进程), 已回错误");

        await WriteErrorAsync(id, ErrorInvalidParams,
            "request/askUser 当前未启用: ask_user 工具在主进程执行, 无需经由 Worker 反问" +
            "(启用需要同时打通父侧对话框与超时兜底, 见 WorkerMessages.WorkerAskUserRequest 的说明)")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>notify/cancel</c>: 按 callId 取消在飞调用的 CTS。
    /// </summary>
    /// <remarks>
    /// <para><b>取消跨进程必须"双写"</b>: 父进程取消的是「父进程自己的等待」, 与「Worker 进程里的那个调用」
    /// 是<b>两个进程里的两件事</b>, 中间隔着一条随时可能已断的管道。只取消自己的等待, 结果就是一个
    /// 跑飞了却没人管的孤儿(还在改文件、还在跑构建)。所以父侧必须额外写一帧 <c>notify/cancel</c>,
    /// 本方法就是它的落点 —— 取消最终要变成 <c>CliAgentRunner</c> 里的
    /// <c>process.Kill(entireProcessTree: true)</c>, 而那只能由子进程自己做。</para>
    /// <para><b>⚠️ 这里刻意只 Cancel、不从 <c>_callCts</c> 里 TryRemove</b>(与"收到就摘掉"的直觉相反):
    /// 条目的移除与 <c>Dispose</c> 必须由 <b>唯一主人</b> —— 调用处理方法的 finally —— 来做。
    /// 若取消侧把条目摘走, 调用侧就再也拿不到那个 CTS, 于是它永远不会被 Dispose(泄漏),
    /// 而且它 linked 出去的令牌也没人收。竞态下"摘不到"恰好等价于"已经收尾", 不需要额外处理。</para>
    /// <para>取消回调里<b>不能同步等异步操作</b>: <c>Cancel()</c> 是在读的这条线程上跑的, 而工具侧
    /// 已有的取消回调会去 <c>Kill</c> 进程树; 若那里再写管道就是自死锁。这是"取消很急"的固有形状,
    /// 本方法只保证不引入新的同步等待。</para>
    /// </remarks>
    private void HandleCancel(JsonObject frame)
    {
        var request = TryReadPayload(frame, AppJsonContext.Default.WorkerCancelNotification,
            WorkerProtocol.NotifyCancel);
        if (request is null || string.IsNullOrEmpty(request.CallId))
        {
            Log.Debug(LogCategory, "notify/cancel 的 params 缺失或 callId 为空, 已忽略");
            return;
        }

        if (!_callCts.TryGetValue(request.CallId, out var cts))
        {
            // 正常竞态: 这次调用已经结束(完成/失败/已被取消过)。静默属正常, 但留一条 Debug,
            // 因为"反复出现"意味着两侧的 callId 口径不一致(那才是真 bug)。
            Log.Debug(LogCategory, $"notify/cancel: callId={request.CallId} 已不在在途表(已结束或已取消过)");
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 竞态: 刚取到引用, 调用侧就完成了执行并在 finally 里 Dispose 了它。
            // Dispose 与 Cancel 并发在 CTS 上是允许的失败方向(语义上等价于"已经结束了")。
            Log.Debug(LogCategory, $"notify/cancel: callId={request.CallId} 的取消源已被释放(调用刚好收尾)");
        }
        catch (AggregateException)
        {
            // 某个工具注册的取消回调自己抛了: 不能让这一条取消通知把读循环带走(读循环一停, 后面所有取消都进不来)。
            Log.Warn(LogCategory, $"取消 callId={request.CallId} 时有回调抛异常, 已忽略");
        }

        Log.Info(LogCategory, $"已取消调用 {request.CallId}");
    }

    // ───────────────────────── tools/call 的执行 ─────────────────────────

    /// <summary>把一次工具调用丢到后台任务, 并登记以便退出时收尾。</summary>
    private void DispatchToolCall(JsonObject frame, JsonNode? id, CancellationToken lifetimeToken)
    {
        var task = Task.Run(() => HandleToolCallAsync(frame, id, lifetimeToken), CancellationToken.None);
        _inflightCalls.TryAdd(task, 0);

        // 登记完再挂清理续体(ExecuteSynchronously): 任务已完成时续体会就地跑完,
        // 不会留下"已完成却还在表里"的条目让退出流程多等一次预算。
        _ = task.ContinueWith(
            static (t, state) =>
            {
                var set = (ConcurrentDictionary<Task, byte>)state!;
                set.TryRemove(t, out _);

                // 没有下家的异常不能变成 UnobservedTaskException 噪声(它会在别的线程上炸出一条
                // 与本处毫无关系的报错)。正常路径本不该 fault, 这里是兜底。
                if (t.IsFaulted)
                {
                    Log.Warn(LogCategory, t.Exception, "Worker 工具调用处理任务异常(已收尾)");
                }
            },
            _inflightCalls,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>执行一次工具调用并回响应。跑在后台任务上(读循环不能等它)。</summary>
    private async Task HandleToolCallAsync(JsonObject frame, JsonNode? id, CancellationToken lifetimeToken)
    {
        if (id is null)
        {
            // 通知形态的 tools/call: 没有响应通道, 执行结果无处可回。宁可拒绝也不执行 ——
            // 跑一个没人知道结果的工具(可能是 git_commit)比不跑更糟。
            Log.Warn(LogCategory, "收到无 id 的 worker/tools/call(通知形态), 已拒绝执行: 结果无处可回");
            return;
        }

        var request = TryReadPayload(frame, AppJsonContext.Default.WorkerToolCallRequest,
            WorkerProtocol.MethodToolsCall);
        if (request is null)
        {
            await WriteErrorAsync(id, ErrorInvalidParams,
                $"worker/tools/call 的 params 缺失或不是合法的 {nameof(WorkerToolCallRequest)}")
                .ConfigureAwait(false);
            return;
        }

        var callId = request.CallId ?? string.Empty;
        if (callId.Length == 0)
        {
            await WriteErrorAsync(id, ErrorInvalidParams,
                "callId 不能为空: 它是流式输出与取消通知的唯一关联键, 空值会让输出无处投递、取消无从生效")
                .ConfigureAwait(false);
            return;
        }

        // 每调用一个 CTS, linked 到进程生命周期: 父进程消失 / 收到 shutdown 时连带取消子代理进程树。
        var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        if (!_callCts.TryAdd(callId, cts))
        {
            // 同 callId 复用会让 notify/cancel 打到错误的调用上, 输出也会串进同一个通道 ——
            // 症状是"A 的输出流进 B 的卡片", 几乎无法事后分辨。callId 由父进程保证唯一(见 WorkerMessages)。
            cts.Dispose();
            Log.Warn(LogCategory, $"callId={callId} 已在执行中, 拒绝重复的 tools/call({request.Name})");
            await WriteErrorAsync(id, ErrorInvalidParams,
                $"callId {callId} 已在执行中: 同一 Worker 生命周期内 callId 必须唯一").ConfigureAwait(false);
            return;
        }

        // 输出通道必须在执行**之前**开好: 工具可能在第一个 token 就 Report 一行。
        var channel = OpenOutputChannel(callId, lifetimeToken);

        WorkerToolCallResponse? response = null;
        int errorCode = 0;
        string? errorMessage = null;
        var reply = true;

        try
        {
            response = await _host.ExecuteAsync(request, cts.Token).ConfigureAwait(false);

            if (response is null)
            {
                // 契约上不该返回 null(见 IWorkerToolHost.ExecuteAsync)。真发生了按"宿主实现有 bug"处理:
                // 合成一条错误响应而不是让 NRE 逃出去, 否则父进程只看到一句莫名其妙的失败。
                Log.Warn(LogCategory, $"工具宿主对 {request.Name} 返回了 null(callId={callId}), 已合成错误响应");
                response = new WorkerToolCallResponse
                {
                    IsError = true,
                    Content = $"工具宿主返回了空响应({request.Name}): 这是宿主实现的 bug",
                };
            }
        }
        catch (OperationCanceledException)
        {
            // ⚠️ 必须排在 catch(Exception) 之前(仓库铁律): 取消不是失败, 把它说成失败会让日志里
            // 出现"用户点停止 = 工具报错"的错误结论, 冲掉真正的故障线索。
            if (lifetimeToken.IsCancellationRequested)
            {
                // 进程自己在收尾(shutdown / 管道断了 / 父进程消失): 管道即将关闭, 写了也没人收。
                Log.Debug(LogCategory, $"工具 {request.Name} 因 Worker 收尾而中止(callId={callId})");
                reply = false;
            }
            else
            {
                Log.Info(LogCategory, $"工具 {request.Name} 已被取消(callId={callId})");
                errorCode = ErrorRequestCancelled;
                errorMessage = $"调用已取消(工具 {request.Name}, callId={callId})";
            }
        }
        catch (Exception ex)
        {
            // 宿主本该把工具异常收成 IsError 响应(见 IWorkerToolHost.ExecuteAsync 的异常边界)。
            // 这里是最后一道兜底: 它挡住的是"宿主实现有 bug"这类不该存在的情况, 而不是常规失败。
            Log.Warn(LogCategory, ex, $"工具宿主执行 {request.Name} 时抛出未收拢的异常(callId={callId})");
            errorCode = ErrorInternal;
            errorMessage = $"工具 {request.Name} 执行失败: {ex.Message}";
        }
        finally
        {
            // CTS 的移除 + 释放**只有一个主人**: 本 finally(理由见 HandleCancel 的说明)。
            // 放在写响应之前: 从此刻起再来的 notify/cancel 就是"迟到的", 不该再去动一个已结束的调用。
            if (_callCts.TryRemove(callId, out var owned))
            {
                owned.Dispose();
            }
        }

        // ⚠️ 协议层的隐含契约: 该调用的**全部**输出行必须排在响应帧之前。
        // 父进程侧 WorkerProxyTool 用 `using var outputSub = SubscribeOutput(callId, ...)` 订阅,
        // 收到响应即退订 —— 输出行若与响应竞争, 末几行落在退订之后被丢弃。
        // 排空预算用尽时仍继续写响应: tools/call 无超时, 不回应 = 父进程永远等下去。
        await FlushOutputChannelAsync(channel).ConfigureAwait(false);

        if (!reply)
        {
            return;
        }

        if (errorMessage is not null)
        {
            await WriteErrorAsync(id, errorCode, TruncateForFrame(errorMessage)).ConfigureAwait(false);
            return;
        }

        await WriteResultAsync(id, response!, AppJsonContext.Default.WorkerToolCallResponse)
            .ConfigureAwait(false);
    }

    // ───────────────────────── 心跳 ─────────────────────────

    /// <summary>
    /// 起心跳: 每 <see cref="WorkerProtocol.HeartbeatInterval"/> 发一帧 <c>notify/heartbeat</c>。
    /// </summary>
    /// <remarks>
    /// <para><b>⚠️ 必须跑在后台, 绝不能写成读循环里的 <c>Task.Delay</c></b>:
    /// 读循环一旦 Delay, 父进程发来的 <c>notify/cancel</c> 就要等 Delay 结束才被读到,
    /// 于是「用户点停止」变成「工具继续跑 5 秒」, 而子代理的进程树还在写文件。</para>
    /// <para><b>⚠️ 心跳不能用来判断"Worker 卡在某个工具上"</b>: 它由独立定时器发, 与工具执行无关。
    /// 父进程侧用 60 秒无帧判僵死(12 倍余量, 足以吸收 GC 与磁盘 IO 抖动),
    /// 判断单个调用是否卡住只能靠取消通道。</para>
    /// <para>心跳体走 <c>WorkerHeartbeatNotification</c> DTO 而不是手写字段名 ——
    /// DTO 是字段名的唯一真源, 两处各写一份必然漂移。</para>
    /// </remarks>
    private void StartHeartbeat(CancellationToken ct)
    {
        _heartbeatTask = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(WorkerProtocol.HeartbeatInterval);
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    var frame = BuildNotification(
                        WorkerProtocol.NotifyHeartbeat,
                        new WorkerHeartbeatNotification { Ticks = Environment.TickCount64 },
                        AppJsonContext.Default.WorkerHeartbeatNotification);

                    if (!await WriteFrameAsync(frame, WriteToken).ConfigureAwait(false))
                    {
                        break; // 写侧没了(管道已断), 写方法已请求自退, 心跳不必再跑
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 生命周期取消 = 正常收尾
            }
            catch (Exception ex)
            {
                // 心跳失败不致命: 它只是"我还活着"的信号, 真正的故障由写侧/读侧各自报。
                Log.Debug(LogCategory, $"Worker 心跳循环结束: {ex.GetType().Name}: {ex.Message}");
            }
        }, CancellationToken.None);
    }

    // ───────────────────────── 输出通道(顺序保证的落点) ─────────────────────────

    /// <summary>取出(或创建)某个 callId 的输出通道, 并起它的单一排空任务。</summary>
    /// <remarks>
    /// 用 <c>TryGetValue</c>/<c>TryAdd</c> 循环而<b>不是</b> <c>GetOrAdd</c>:
    /// <c>GetOrAdd</c> 的工厂在竞争失败时<b>也会执行</b>, 而我们的工厂会启动一个排空任务 ——
    /// 那个被丢弃的通道将永远没人 Close, 它的排空任务也就永远挂着(每个竞争失败泄漏一个 Task)。
    /// 先建后 TryAdd 保证了"只有赢家才起任务", 输家手上的空对象直接被 GC 回收。
    /// </remarks>
    private CallOutputChannel OpenOutputChannel(string callId, CancellationToken lifetimeToken)
    {
        while (true)
        {
            if (_outputChannels.TryGetValue(callId, out var existing))
            {
                return existing;
            }

            var created = new CallOutputChannel(callId);
            if (_outputChannels.TryAdd(callId, created))
            {
                // 直接调用而不 Task.Run: async 方法在第一个 await(读一个空队列会立刻挂起)之前不会占用线程。
                created.Drain = DrainOutputAsync(created, lifetimeToken);
                return created;
            }
        }
    }

    /// <summary>
    /// 排空一个调用的输出通道: 封闭队列 → 有界等待写完 → 从表里摘除。
    /// </summary>
    /// <remarks>
    /// 这一步就是「响应排在全部输出之后」的<b>具体实现</b>。没有它, 工具线程刚 Report 完、
    /// 还没被排空任务取走的行就会与响应竞争(见 <see cref="EmitToolOutput(string, string)"/> 与
    /// <see cref="CallOutputChannel"/> 的说明)。</remarks>
    private async Task FlushOutputChannelAsync(CallOutputChannel channel)
    {
        channel.Close();
        _outputChannels.TryRemove(channel.CallId, out _);

        var drain = channel.Drain;
        if (drain is null)
        {
            return;
        }

        try
        {
            var finished = await Task.WhenAny(drain, Task.Delay(OutputFlushBudget, CancellationToken.None))
                .ConfigureAwait(false);
            if (finished != drain)
            {
                Log.Warn(LogCategory,
                    $"调用 {channel.CallId} 的实时输出未在 {OutputFlushBudget.TotalSeconds:0.#}s 内排空; " +
                    "仍按协议写出响应帧, 尾部若干行可能落在响应之后被父进程丢弃");
                return;
            }

            await drain.ConfigureAwait(false); // 观察异常: 排空任务已自行收拢, 这里只是不让它悬着
        }
        catch (Exception ex)
        {
            Log.Warn(LogCategory, ex,
                $"调用 {channel.CallId} 的输出排空异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>单个调用的一条有序输出通道: 入队 → 单一排空任务写出。</summary>
    /// <remarks>
    /// <para><b>为什么需要它(而不是"工具线程直接写管道")</b>: 见 <see cref="EmitToolOutput(string, string)"/> 的说明 ——
    /// 工具侧的输出转发是 fire-and-forget 的, 行与响应之间天然存在竞争。</para>
    /// <para><b>为什么用 Channel 而不是 <c>ConcurrentQueue</c> + 信号量</b>: 需要的是
    /// 「等它彻底空掉」这个语义, 而 <c>ChannelReader.ReadAllAsync</c> 正好在「封闭 + 空」时正常完成,
    /// 不用自己维护计数与唤醒。</para>
    /// <para><b>⚠️ <c>AllowSynchronousContinuations = false</c> 是必需的</b>: 否则排空任务的续体会
    /// 在<b>调用方线程</b>(也就是 <c>EmitToolOutput</c> 的那一行)上就地跑, 把管道写搬到了工具线程 ——
    /// 那正是我们想避开的"Report 变阻塞"。</para>
    /// </remarks>
    private sealed class CallOutputChannel
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        private int _closed;

        public CallOutputChannel(string callId) => CallId = callId;

        public string CallId { get; }

        /// <summary>排空任务(由 <c>OpenOutputChannel</c> 赋值)。</summary>
        public Task? Drain { get; set; }

        /// <summary>封闭: 此后入队的行一律丢弃(调用已收尾)。</summary>
        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                _lines.Writer.TryComplete();
            }
        }

        /// <summary>入队一行; 已封闭则返回 false。</summary>
        public bool TryEnqueue(string line) =>
            Volatile.Read(ref _closed) == 0 && _lines.Writer.TryWrite(line);

        /// <summary>逐行读取直到封闭且排空。</summary>
        public IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct) => _lines.Reader.ReadAllAsync(ct);
    }

    /// <summary>排空任务主体: 把队列里的每一行变成一帧 <c>notify/toolOutput</c>。</summary>
    private async Task DrainOutputAsync(CallOutputChannel channel, CancellationToken ct)
    {
        try
        {
            await foreach (var line in channel.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var frame = BuildNotification(
                    WorkerProtocol.NotifyToolOutput,
                    new WorkerToolOutputNotification { CallId = channel.CallId, Line = line },
                    AppJsonContext.Default.WorkerToolOutputNotification);

                if (!await WriteFrameAsync(frame, WriteToken).ConfigureAwait(false))
                {
                    break; // 写侧没了, 没人会再收这些行了
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 进程收尾, 剩余行随通道一起作废
        }
        catch (ChannelClosedException)
        {
            // 通道被外部封闭: 正常收尾
        }
        catch (Exception ex)
        {
            Log.Warn(LogCategory, ex, $"调用 {channel.CallId} 的输出转发中断, 已停止排空");
        }
    }

    // ───────────────────────── 退出收尾 ─────────────────────────

    /// <summary>
    /// 退出收尾: 停心跳 → 取消全部在飞调用 → 有界等它们收尾 → 完结在途的反向请求 → 清表释放。
    /// </summary>
    /// <remarks>
    /// <b>⚠️ 不要在这里(或之后)去杀父进程</b>: 父进程此刻正在等我们退出
    /// (收到 <c>worker/shutdown</c> 的 ok 之后它会关管道并等不超过 <see cref="WorkerProtocol.ShutdownTimeout"/>,
    /// 管道 EOF 后也会等我们自行了断)。方向反了就是互等 —— 表现为两边都卡到超时才各自动了断。</remarks>
    private async Task FinishAsync()
    {
        // 1) 停心跳: 取消生命周期令牌即停(它是唯一心跳循环), 并顺手让所有 linked 的在飞 CTS 一起取消。
        RequestSelfStop();
        var heartbeat = _heartbeatTask;
        if (heartbeat is not null)
        {
            if (!await WaitBoundedAsync(heartbeat, HeartbeatStopBudget).ConfigureAwait(false))
            {
                Log.Debug(LogCategory, "心跳任务未在预算内结束, 继续收尾");
            }
        }

        // 2) 显式取消每个在飞调用的 CTS(生命周期取消其实已经连带取消了, 这里是把语义写明 + 兜住时序)。
        //    不直接清表: 清表会让调用侧的 finally 拿不到 CTS 而无法 Dispose(见 HandleCancel 的说明)。
        foreach (var entry in _callCts)
        {
            try
            {
                entry.Value.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 该调用刚好在快照之后收尾并释放了 CTS
            }
            catch (AggregateException)
            {
                Log.Warn(LogCategory, $"退出时取消 callId={entry.Key} 时有回调抛异常, 已忽略");
            }
        }

        // 3) 有界等待在飞 tools/call 收尾: 它们各自还会写一帧"调用已取消"的响应, 管道要活到那时。
        if (!_inflightCalls.IsEmpty)
        {
            Task all;
            try
            {
                all = Task.WhenAll(_inflightCalls.Keys.ToArray());
            }
            catch (ArgumentException)
            {
                // 快照瞬间恰好全空(刚被清理完), 无需等待
                all = Task.CompletedTask;
            }

            if (!await WaitBoundedAsync(all, ExitDrainBudget).ConfigureAwait(false))
            {
                Log.Warn(LogCategory,
                    $"退出前仍有 {_inflightCalls.Count} 个工具调用未在 {ExitDrainBudget.TotalSeconds:0.#}s 内收尾; " +
                    "它们已收到取消, 但其子代理进程树可能成为孤儿 —— 父进程回收本 Worker 时应连进程树一起结束");
            }
            else
            {
                await ObserveAsync(all).ConfigureAwait(false);
            }
        }

        // 4) 在途的反向请求(Worker → 父 的 request/*)全部完结。
        //    ⚠️ 当前恒为空(request/askUser 未启用), 但收尾路径必须先有这条: 等它真的启用时,
        //    「父进程已经走了、我还在等一个永远不会来的答复」就会变成一个只在启用后才暴露的挂起。
        FailPendingOutbound("Worker 正在退出");

        // 5) 释放残留的 CTS 与输出通道(到这一步还没被调用侧 finally 摘走的, 说明那次调用没能正常收尾)。
        foreach (var key in _callCts.Keys.ToArray())
        {
            if (_callCts.TryRemove(key, out var cts))
            {
                cts.Dispose();
            }
        }

        foreach (var key in _outputChannels.Keys.ToArray())
        {
            if (_outputChannels.TryRemove(key, out var channel))
            {
                channel.Close();
            }
        }
    }

    /// <summary>完结在途的反向请求, 并记下它们的存在(诊断用)。</summary>
    /// <remarks>
    /// 走 <see cref="WorkerRpcCore.FailAllPending"/> 而不是自己遍历一张表:
    /// 内核的挂起表是它的私有状态, 而它<b>已经</b>实现了"锁内快照再遍历"(那是一边遍历一边被派发路径摘条目
    /// 会抛 <see cref="InvalidOperationException"/> 的经典坑, 见该方法的 remarks)。
    /// 在反问通道启用之前这里的计数必然是 0, 但<b>必须走同一条路径</b>:
    /// "当前为空"是结果, 不是可以跳过清理的理由。</remarks>
    private void FailPendingOutbound(string reason)
    {
        var pending = _rpc?.PendingCount ?? 0;
        if (pending > 0)
        {
            Log.Warn(LogCategory, $"退出前仍有 {pending} 个在途的反向请求(request/*): {reason}");
        }

        _rpc?.FailAllPending(reason);
    }

    /// <summary>请求本进程退出: 取消生命周期令牌, 让读循环与所有 linked 的在飞调用一起收尾。</summary>
    private void RequestSelfStop()
    {
        try
        {
            _lifetime?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // RunAsync 已经走到 using 的释放阶段, 没有别的可收的了
        }
    }

    // ───────────────────────── 帧的读写与编解码 ─────────────────────────

    /// <summary>写一帧。返回 false 表示"写侧已不可用"(已释放/管道已断), 调用方据此收工。</summary>
    /// <remarks>
    /// ⚠️ 写失败一律<b>请求自退</b>: 管道写不通只意味着一件事 —— 父进程那边没了。
    /// 继续跑就等于留下一个孤儿进程占着工作目录的 git 锁(而子代理还在往那儿写文件)。
    /// 父侧 pid 探测(WorkerHelloRequest.ParentPid)是更慢的第二道闸, 写侧失败是更早的信号。</remarks>
    private async Task<bool> WriteFrameAsync(JsonObject frame, CancellationToken ct)
    {
        var writer = Volatile.Read(ref _writer);
        if (writer is null)
        {
            Log.Debug(LogCategory, "Worker 写侧已释放, 丢弃一帧(收尾中)");
            return false;
        }

        try
        {
            await writer.WriteAsync(frame, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(LogCategory, ex, "Worker 写帧失败, 判定管道已断并请求自退");
            RequestSelfStop();
            return false;
        }
    }

    /// <summary><see cref="WorkerRpcCore"/> 的 transmit 委托: 它拿到的是内核已按超时规则合并过的令牌。</summary>
    private Task TransmitAsync(JsonObject frame, CancellationToken ct) => WriteFrameAsync(frame, ct);

    /// <summary>
    /// 写帧用的令牌: 单次写有 <see cref="WriteBudget"/> 兜底; 预算已用完或 CTS 已释放则退化成"不限时"。
    /// </summary>
    /// <remarks>
    /// ⚠️ 取 <c>CancellationTokenSource.Token</c> 在 CTS 已 <c>Dispose</c> 之后会抛
    /// <see cref="ObjectDisposedException"/>, 而它与 <c>RunAsync</c> 收尾时的 Dispose 天然并发
    /// (在途的心跳 / 排空任务随时会来取)。退化成 <see cref="CancellationToken.None"/> 就够了:
    /// 那一刻写闸正要关, 真正的兜底是 <see cref="WriteFrameAsync"/> 的 catch。
    /// </remarks>
    private CancellationToken WriteToken
    {
        get
        {
            var budget = _writeBudget;
            if (budget is null)
            {
                return CancellationToken.None;
            }

            try
            {
                return budget.IsCancellationRequested ? CancellationToken.None : budget.Token;
            }
            catch (ObjectDisposedException)
            {
                return CancellationToken.None;
            }
        }
    }

    /// <summary>回一个 <c>result</c> 帧。</summary>
    /// <remarks>
    /// ⚠️ <b>id 必须原样回显</b>(<c>DeepClone</c> 后回填): JSON-RPC 允许 id 是数字或字符串,
    /// 父侧用归一化后的字符串配对(见 <c>WorkerRpcCore.NormalizeRpcId</c>), 我们擅自换成自己的序号
    /// 就再也配不上等待者 —— 症状是"Worker 从来不响应"而日志干净。
    /// <c>DeepClone</c> 不可省: 把一个已挂父节点的 <see cref="JsonNode"/> 直接赋进新对象会抛
    /// <c>InvalidOperationException</c>。
    /// <para>序列化失败(宿主 payload 里有源生成覆盖不到的成员)也必须变成一个 <c>error</c> 帧:
    /// 不回任何东西的话父进程会一直等到天荒地老(<c>tools/call</c> 无超时)。</para>
    /// </remarks>
    private Task WriteResultAsync<T>(JsonNode id, T payload, JsonTypeInfo<T> typeInfo)
    {
        JsonObject frame;
        try
        {
            frame = new JsonObject
            {
                ["jsonrpc"] = WorkerProtocol.JsonRpcVersion,
                ["id"] = id.DeepClone(),
                ["result"] = JsonSerializer.SerializeToNode(payload, typeInfo) ?? new JsonObject(),
            };
        }
        catch (Exception ex)
        {
            return WriteErrorAsync(id, ErrorInternal, $"序列化响应失败: {ex.Message}", ex);
        }

        return WriteFrameAsync(frame, WriteToken);
    }

    /// <summary>回一个 <c>error</c> 帧: <c>{"jsonrpc","id","error":{"code","message"}}</c>。</summary>
    /// <remarks>
    /// <para><b>⚠️ 结构要发完整, 但要知道父侧目前只读 <c>message</c></b>:
    /// 父侧 <c>WorkerRpcCore.BuildRpcException</c> 把 code 与 data 拼进异常文案, 而调用方
    /// （PipeTransport / WorkerClient / WorkerProxyTool）最终只把 <c>ex.Message</c> 塞进
    /// <c>ToolResult.Error</c> —— 于是 code 在 UI 上是不可见的。
    /// 将来若要按错误码分流(例如 -32800 不弹错误卡), 需要<b>父侧扩展派发</b>
    /// （把 code 提到异常的一个属性上）, 这不是 Worker 侧单方面能解决的。</para>
    /// <para>message 会截断: 异常文案可以很长(path 堆栈、内嵌的大段内容), 而一帧就是一行,
    /// 无上限的 message 会把管道写出成一条几 MB 的"错误提示"。</para>
    /// </remarks>
    private Task WriteErrorAsync(JsonNode id, int code, string message, Exception? cause = null)
    {
        if (cause is null)
        {
            Log.Debug(LogCategory, $"Worker 回错误帧(code={code}): {message}");
        }
        else
        {
            Log.Warn(LogCategory, cause, $"Worker 回错误帧(code={code}): {message}");
        }

        var frame = new JsonObject
        {
            ["jsonrpc"] = WorkerProtocol.JsonRpcVersion,
            ["id"] = id.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = TruncateForFrame(message),
            },
        };

        return WriteFrameAsync(frame, WriteToken);
    }

    /// <summary>构造一个通知帧(无 id, 对端不回执)。</summary>
    private static JsonObject BuildNotification<T>(string method, T payload, JsonTypeInfo<T> typeInfo)
    {
        var frame = new JsonObject
        {
            ["jsonrpc"] = WorkerProtocol.JsonRpcVersion,
            ["method"] = method,
        };

        var node = JsonSerializer.SerializeToNode(payload, typeInfo);
        if (node is not null)
        {
            frame["params"] = node;
        }

        return frame;
    }

    /// <summary>读出帧的 <c>id</c>; 无 id 或 id 为 JSON null 都算"通知形态"(与 McpClientBase 的归一化口径一致)。</summary>
    private static JsonNode? ReadRpcId(JsonObject frame) =>
        frame.TryGetPropertyValue("id", out var id) ? id : null;

    private static string? ReadString(JsonObject frame, string property) =>
        frame.TryGetPropertyValue(property, out var node)
        && node is JsonValue value
        && value.TryGetValue<string>(out var text)
            ? text
            : null;

    /// <summary>
    /// 解析 <c>params</c> 为强类型 DTO。返回 null = 缺 params 或不是合法 JSON,
    /// 调用方负责回协议错误(<b>不要在这里抛</b>: 一个坏帧不该带走读循环)。
    /// </summary>
    private static T? TryReadPayload<T>(JsonObject frame, JsonTypeInfo<T> typeInfo, string what)
        where T : class
    {
        if (!frame.TryGetPropertyValue("params", out var node) || node is null)
        {
            Log.Debug(LogCategory, $"{what} 帧缺少 params");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(node, typeInfo);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            Log.Warn(LogCategory, $"{what} 的 params 不是合法的 {typeof(T).Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>error.message 的长度上限。异常文案可以很长(path 堆栈、内嵌大段内容), 而一帧就是一行。</summary>
    private const int MaxErrorMessageChars = 2000;

    /// <summary>流式输出行的长度封顶(见 <see cref="WorkerProtocol.MaxLineChars"/>: 只约束"子进程输出的一行")。</summary>
    private static string CapLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        return line.Length <= WorkerProtocol.MaxLineChars
            ? line
            : line[..WorkerProtocol.MaxLineChars] + "…(已截断)";
    }

    private static string TruncateForFrame(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "(无详情)";
        }

        return message.Length <= MaxErrorMessageChars
            ? message
            : message[..MaxErrorMessageChars] + "…(已截断)";
    }

    /// <summary>有界等待。返回 false 表示预算内没等到。</summary>
    private static async Task<bool> WaitBoundedAsync(Task task, TimeSpan budget)
    {
        if (task.IsCompleted)
        {
            await ObserveAsync(task).ConfigureAwait(false);
            return true;
        }

        var finished = await Task.WhenAny(task, Task.Delay(budget, CancellationToken.None)).ConfigureAwait(false);
        if (finished != task)
        {
            return false;
        }

        await ObserveAsync(task).ConfigureAwait(false);
        return true;
    }

    /// <summary>等待并把异常读掉(收尾路径上"任务没成不成就算了", 但不许它变成未观察异常)。</summary>
    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(LogCategory, $"收尾等待的任务带异常(已忽略): {ex.GetType().Name}: {ex.Message}");
        }
    }
}