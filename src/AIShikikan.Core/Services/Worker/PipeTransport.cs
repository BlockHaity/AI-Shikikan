/*
 * PipeTransport: 父进程侧的 Worker 传输实现 —— 真实子进程 + stdio 匿名管道 + NDJSON 行协议。
 *
 * ── 与 McpStdioClient 的关系(照抄它的哪些部分) ──
 *   ① ProcessStartInfo: RedirectStandardInput/Output/Error + CreateNoWindow + 显式 UTF-8;
 *   ② stderr 单独后台任务读到 EOF(只读防阻塞, 绝不让子进程的诊断信息堵死协议管道);
 *   ③ 写侧 SemaphoreSlim 串行(落在 WorkerFrameWriter 里, 与 MCP 同一个形状);
 *   ④ Dispose 的顺序: FailAllPending → 关 stdin 让对端自退 → 有界等待 → Kill(entireProcessTree) → Dispose;
 *   ⑤ 读循环的异常全在循环内捕获并留一条 Debug, 绝不冒泡成"未观察到的任务异常"。
 *   这些之外的地方本类刻意与 MCP 分道扬镳 —— 理由见类注释末尾的"与 MCP 的取舍差异"。
 */

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Worker;

/// <summary>管道传输: 把 <c>AIShikikan.Worker</c> 作为子进程拉起, 用 NDJSON 行协议(JSON-RPC 2.0 子集)
/// 在 stdio 匿名管道上跑 <see cref="IToolTransport"/> 协议。降级路径见 <see cref="InlineTransport"/>。</summary>
/// <remarks>
/// <para><b>进程所有权</b>: 本类拥有它 spawn 出来的 <see cref="Process"/>, 且<b>只有本类能关它</b>。
/// 因此「启动失败」「握手失败」「版本不符」这些路径上本类都不自行 kill, 而是让调用方
/// (未来的 WorkerManager)在判定降级后调 <see cref="DisposeAsync"/> 收尸 —— 把「要不要换一个 Worker」
/// 这种策略决定留在上层。真出现调用方忘了释放, Worker 侧还有 <c>ParentPid</c> 自检兜底
/// (见 <see cref="WorkerHelloRequest.ParentPid"/>)。</para>
///
/// <para><b>三个后台循环, 各司其职, 缺一不可</b>(每一个的失败都是一条独立的故障入口,
/// 所以每一个都必须汇进 <see cref="SignalFaultOnce"/>):
/// <list type="bullet">
/// <item><b>读循环</b>: 收帧并分派派发响应/通知; EOF 与 IO 异常都在它的 finally 里收尾。</item>
/// <item><b>stderr 泵</b>: 把子进程的诊断输出排给 <see cref="Log"/>。它不参与协议,
/// 但**必须**读到 EOF —— 否则子进程写满 stderr 管道缓冲区后会永远阻塞在 write 上,
/// 表现为「Worker 启动后什么都不干」。</item>
/// <item><b>心跳看门狗</b>: <see cref="WorkerProtocol.HeartbeatTimeout"/> 内没收到<b>任何</b>帧即判僵死。
/// 读循环活着不代表对端活着: 管道 EOF 之外的死法(死循环、STW、磁盘 IO 卡死)不会让管道关。</item>
/// </list></para>
///
/// <para><b>⚠️ 为什么 Faulted 必须"至多一次"</b>: 上层(WorkerClient → WorkerManager)按事件
/// 做记账 —— 重复触发就是重复记故障史、重复降级、重复抢锁。而且 <see cref="WorkerProtocol.ToolCallTimeout"/>
/// 是<b>刻意无限</b>的, 超时<b>不会</b>兜底: 漏掉任何一条故障入口(EOF / IOException / 进程退出 /
/// 写失败 / 心跳僵死), 那个在飞调用就<b>永久挂起</b> —— 用户点停止只能靠上层主动取消救回来。
/// 所以"一次性"与"全入口覆盖"是一对: 前者防重复记账, 后者防永久挂起。</para>
///
/// <para><b>与 MCP 的取舍差异(为什么不直接复用 <c>McpStdioClient</c>)</b>:
/// <list type="number">
/// <item><b>心跳看门狗</b>: MCP 没有这个概念, 它不需要 —— MCP 每个请求都有 300s 上限,
/// 而 Worker 的 <c>tools/call</c> 无上限(子代理合法跑 30 分钟), 于是"进程还在、但不响应了"
/// 在 MCP 侧等价于"这个请求超时", 在 Worker 侧却是"引擎永久等待"。</item>
/// <item><b>kill 的触发权</b>: MCP 只在 Dispose 时杀; Worker 还要在「心跳僵死」与「握手校验失败」时
/// 就具备杀的能力, 所以杀进程被提到独立入口, 不埋在 Dispose 里。</item>
/// <item><b>子→父请求</b>: MCP 侧对 server→client 请求(sampling/roots)记 Warn 就丢;
/// Worker 侧必须<b>回一个 error 响应</b> —— <c>request/askUser</c> 是在某个在途调用期间发出的,
/// 而那个调用的超时是无限的, 静默丢弃等于让对端永久挂起。</item>
/// <item><b>工作目录</b>: MCP 服务器的工作目录由用户配置决定; Worker 的是<b>会话工作目录</b>,
/// 它同时参与 <see cref="WorkerProtocol.MakeWorkerKey"/> 与工具的相对路径解析。</item>
/// </list>
/// 但行协议层仍与 <c>McpStdioClient</c> 逐字同构 —— 复用的是形状, 不是代码: 两者的生命周期规则、
/// 故障语义与超时预算都不同, 共用一个类只会得到一个谁都不合适的中间态。</para>
/// </remarks>
public sealed class PipeTransport : IToolTransport
{
    /// <summary>日志 category(与 Worker 其余部分一致)。</summary>
    private const string Category = "Worker";

    /// <summary>强杀进程树之后给收尾的有界等待。与 McpStdioClient 取同一个值。</summary>
    private static readonly TimeSpan KillWaitTimeout = TimeSpan.FromSeconds(1);

    private readonly Process _process;

    /// <summary>协议读侧: 只用 <c>BaseStream</c>, 逐帧由 <see cref="WorkerFrameReader"/> 处理。</summary>
    private readonly Stream _stdout;

    /// <summary>OS 分配的 pid(在进程句柄被释放后仍可读, 所以单独记一份)。</summary>
    private readonly int _osPid;

    /// <summary>日志前缀(默认 <c>[Worker:{key}]</c>)。</summary>
    private readonly string _logPrefix;

    private readonly WorkerFrameWriter _writer;
    private readonly WorkerRpcCore _core;
    private readonly CancellationTokenSource _stopCts = new();

    /// <summary>最后收到<b>任何</b>一帧的时刻(<see cref="Environment.TickCount64"/>, 单调)。心跳看门狗的判据。</summary>
    private long _lastFrameTick;

    /// <summary>握手完成且未断线。语义是"现在能不能立刻发请求", 故障后必须翻 false。</summary>
    private volatile bool _connected;

    private int _faultRaised;
    private int _disposed;
    private int _shutdownSent;

    /// <summary>Worker 侧自报的 pid(握手前为 0)。</summary>
    private int _workerPid;

    private PipeTransport(Process process, string logPrefix, string workerKey)
    {
        _process = process;
        _logPrefix = logPrefix;
        WorkerKey = workerKey;
        _osPid = process.Id;

        // ⚠️ 写侧/读侧都只取 BaseStream: StandardInput/StandardOutput 自带的 StreamReader/StreamWriter
        // 都有缓冲, 协议侧再自带一层缓冲就会互相吃掉数据(症状是随机丢帧, 且只在帧大于缓冲时发生)。
        _stdout = process.StandardOutput!.BaseStream;

        _writer = new WorkerFrameWriter(process.StandardInput!.BaseStream);
        _core = new WorkerRpcCore((frame, ct) => _writer.WriteAsync(frame, ct));

        Interlocked.Exchange(ref _lastFrameTick, Environment.TickCount64);

        // ⚠️ 读循环必须早于握手启动: worker/hello 的响应只能靠它派发(照 McpStdioClient 的同款注释)。
        // 这三个任务的异常都在各自内部吞掉, 这里的 Task 未观察是有意为之。
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(PumpStderrAsync);
        _ = Task.Run(WatchLivenessAsync);
    }

    /// <summary>该 Worker 的稳定标识(由 <see cref="WorkerProtocol.MakeWorkerKey"/> 从启动入参算出)。
    /// 仅用于日志与诊断; 生命周期侧通常由上层另有一份(<b>必须同源</b>, 见该方法的跨进程一致性约束)。</summary>
    public string WorkerKey { get; }

    /// <summary>Worker 进程 pid(握手后由 <c>worker/hello</c> 回填; 握手前取 OS 分配的 pid)。</summary>
    public int WorkerPid
    {
        get
        {
            var reported = Volatile.Read(ref _workerPid);
            return reported > 0 ? reported : _osPid;
        }
    }

    /// <summary>是否可用: <b>握手成功且此后没有断线</b>。</summary>
    /// <remarks>
    /// spawn 失败(对象根本没建起来)、进程退出、管道 EOF、握手版本不符, 任一发生都翻 false;
    /// <see cref="DisposeAsync"/> 也翻 false —— 已释放的传输不该再被当成"可用"。
    /// 反过来, 它<b>不是</b>"曾经连上过"的记录: 那份历史在 <c>WorkerClient.LastFault</c> 里。
    /// </remarks>
    public bool IsConnected => _connected;

    /// <summary>拉起 Worker 子进程, 启动读循环/stderr 泵/心跳看门狗。<b>不含握手</b>(见 <see cref="HandshakeAsync"/>)。</summary>
    /// <remarks>
    /// <b>刻意是同步的</b>: <c>Process.Start</c> 本身就是同步 API, 本方法里也确实没有任何 await 点
    /// (握手之后才有真正的异步)。包一层 <c>Task.FromResult</c> 只会让人误以为"可以传 ct 取消"——
    /// 而那种取消是假的, 比没有更误导。真正的取消点是握手(自带
    /// <see cref="WorkerProtocol.HandshakeTimeout"/>)。
    /// </remarks>
    /// <param name="executablePath">可执行文件路径(通常取自 <c>WorkerLocator.Locate()</c> 的
    /// <c>ExecutablePath</c>; 扩展名与可执行位的判断是定位器的职责, 本类不重复做)。</param>
    /// <param name="hello">会话身份: workDir / workspaceRoot / personaText / rosterEntries / planMode / parentPid 等。
    /// 其中 <see cref="WorkerHelloRequest.WorkDir"/> 还被用作子进程的<b>工作目录</b>(目录存在时才设)。
    /// <para>⚠️ 本方法只把它当<b>启动上下文</b>(工作目录 + 算 key + 日志前缀); 真正下发的握手载荷以
    /// <see cref="HandshakeAsync"/> 的入参为准 —— 因为那一帧里有会变的字段(每回合可切的 Plan 模式)。
    /// 两者同源由上层保证, 不一致不会致命。</para></param>
    /// <param name="logPrefix">日志前缀; 传 null 时用 <c>[Worker:{key}]</c>。</param>
    /// <exception cref="InvalidOperationException">进程拉不起来(路径错、无执行位、被杀…)。</exception>
    public static PipeTransport Start(
        string executablePath,
        WorkerHelloRequest hello,
        string? logPrefix = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(executablePath);
        ArgumentNullException.ThrowIfNull(hello);

        var key = SafeWorkerKey(hello);
        var prefix = string.IsNullOrWhiteSpace(logPrefix) ? $"[Worker:{key}]" : logPrefix;

        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,

            // ⚠️ 三条流都必须显式 UTF-8: 协议帧里含中文(工具描述、文件内容、错误文案),
            // 走系统默认编码的子进程在 Windows 上会按 GBK 写, 这边按 UTF-8 读 → 整帧乱码且无法解析。
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // 工作目录 = 会话工作目录: 子代理 CLI 的相对路径、git 的 -C 都以它为基准,
        // 与主进程内执行时保持一致(否则同一份配置在两种模式下的行为会出现差异)。
        // 目录不存在时**不设**: Process.Start 会直接抛, 而"目录不存在"本身不该阻断传输层的建立
        // (上层会在第一次工具调用里拿到更精确的报错)。
        if (!string.IsNullOrWhiteSpace(hello.WorkDir) && Directory.Exists(hello.WorkDir))
        {
            psi.WorkingDirectory = hello.WorkDir;
        }

        // 身份既走命令行也走 hello 一帧 —— **两者都传是刻意的冗余**, 不是重复契约:
        //   · 命令行是 Worker 进程启动期的唯一依据(Worker 的 Program 把 --workdir 定为必填,
        //     缺失即以退出码 2 退出; 它还要在 hello 到达之前就算出 WorkerKey 并打启动日志);
        //   · hello 帧才是协议层的权威(承载 roster / 人格 / Plan 模式等无法进命令行的部分)。
        // ⚠️ 两处**必须同源**: argv 的值与 hello 的值都由调用方的同一个 WorkerHelloRequest 提供,
        //   不要在这里对 workDir/sessionId 做第二次规范化 —— 各自规范化一次就会分叉。
        if (!string.IsNullOrWhiteSpace(hello.WorkDir))
        {
            psi.ArgumentList.Add("--workdir");
            psi.ArgumentList.Add(hello.WorkDir);
        }

        if (!string.IsNullOrWhiteSpace(hello.SessionId))
        {
            psi.ArgumentList.Add("--session");
            psi.ArgumentList.Add(hello.SessionId);
        }

        if (Environment.ProcessId > 0)
        {
            psi.ArgumentList.Add("--parent-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        }

        // 不传 --key: Worker 用 hello 里同源的 (workDir, sessionId) 走同一个 MakeWorkerKey,
        // 结果必然一致; 多传一份反而多一处可能分叉的来源。
        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException($"进程启动返回空: {executablePath}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{prefix} 启动 Worker 进程失败({executablePath}): {ex.Message} — " +
                "请确认该文件存在且有执行位(WorkerLocator 已做这项检查)", ex);
        }

        try
        {
            var transport = new PipeTransport(process, prefix, key);
            Log.Debug(Category,
                $"{prefix} 已拉起 Worker 进程(pid={process.Id}, exe={executablePath}, workDir={psi.WorkingDirectory})");
            return transport;
        }
        catch
        {
            // 三个后台循环已启动之后再出任何问题, 都不能把进程留成孤儿
            KillQuietly(process, "启动阶段失败");
            process.Dispose();
            throw;
        }
    }

    /// <summary>握手: 下发 <c>worker/hello</c> 并校验协议版本。</summary>
    /// <remarks>
    /// <para><b>两个由传输层补齐的字段</b>: <c>ProtocolVersion</c>(<=0 时填当前协议版本)与
    /// <c>ParentPid</c>(<=0 时填本进程 pid)。前者保证"永远是当前版本", 后者让 Worker 能做孤儿自检 ——
    /// 调用方不该有机会把它填错, 而这两处填错的后果分别是"版本静默错位"与"Worker 无法自杀"。</para>
    ///
    /// <para><b>⚠️ 版本不符/回 Ok=false 时本类只翻 <see cref="IsConnected"/> 并广播一次
    /// <see cref="Faulted"/>, 不自行 kill</b>: "重拉一个新 Worker 还是退回内联"是策略决定, 属于上层
    /// (见类注释「进程所有权」)。</para>
    /// </remarks>
    public async Task<WorkerHelloResponse> HandshakeAsync(WorkerHelloRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);

        if (request.ProtocolVersion <= 0)
        {
            request.ProtocolVersion = WorkerProtocol.ProtocolVersion;
        }

        if (request.ParentPid <= 0)
        {
            request.ParentPid = Environment.ProcessId;
        }

        var earlyExit = TryDescribeEarlyExit();
        if (earlyExit is not null)
        {
            SignalFaultOnce(earlyExit);
            throw new InvalidOperationException($"{_logPrefix} {earlyExit}");
        }

        var result = await ExchangeAsync(
            WorkerProtocol.MethodHello,
            ToParams(request, AppJsonContext.Default.WorkerHelloRequest, "hello 请求"),
            WorkerProtocol.HandshakeTimeout,
            ct,
            WorkerProtocol.MethodHello).ConfigureAwait(false);

        if (result is null)
        {
            // 原因串不带前缀: 它会被原样传给 Faulted, 由上层加抬头
            const string empty = "握手失败: Worker 没有返回 result";
            SignalFaultOnce(empty);
            throw new InvalidOperationException($"{_logPrefix} {empty}");
        }

        var hello = ReadPayload<WorkerHelloResponse>(result, AppJsonContext.Default.WorkerHelloResponse, "hello 响应");

        if (!hello.Ok)
        {
            var rejected = $"握手被 Worker 拒绝: {hello.Error ?? "(未给原因)"}";
            SignalFaultOnce(rejected);
            throw new InvalidOperationException($"{_logPrefix} {rejected}");
        }

        // ⚠️ 版本校验是硬闸: 协议里的 Assignment.Status 以**数字**跨进程传递,
        // 版本错位是**静默**的(不抛异常, 只是 UI 上状态跳变)。宁可当场断开。
        if (hello.ProtocolVersion != WorkerProtocol.ProtocolVersion)
        {
            var mismatch =
                $"协议版本不符: Worker={hello.ProtocolVersion}, 本进程={WorkerProtocol.ProtocolVersion}";
            SignalFaultOnce(mismatch);
            throw new InvalidOperationException($"{_logPrefix} {mismatch}");
        }

        Volatile.Write(ref _workerPid, hello.WorkerPid);
        _connected = true;
        Log.Debug(Category, $"{_logPrefix} 握手完成(pid={hello.WorkerPid}, 协议版本 {hello.ProtocolVersion})");
        return hello;
    }

    /// <summary>全量重配置 Worker 侧工具集(<c>worker/tools/sync</c>), 返回该配置下的权威清单。</summary>
    /// <remarks>
    /// 超时用 <see cref="WorkerProtocol.ToolsListTimeout"/>: 同步只是"注销/重建工具", 是纯内存操作,
    /// 15s 足够; 这道闸的用途是兜住"Worker 侧内部卡死", 不是业务超时。
    /// </remarks>
    public async Task<WorkerToolsResponse> SyncToolsAsync(WorkerToolsSyncRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureUsable("工具集同步");

        var result = await ExchangeAsync(
            WorkerProtocol.MethodToolsSync,
            ToParams(request, AppJsonContext.Default.WorkerToolsSyncRequest, "tools/sync 请求"),
            WorkerProtocol.ToolsListTimeout,
            ct,
            WorkerProtocol.MethodToolsSync).ConfigureAwait(false);

        return ReadPayload<WorkerToolsResponse>(result, AppJsonContext.Default.WorkerToolsResponse, "tools/sync 响应");
    }

    /// <summary>执行一次工具调用(<c>worker/tools/call</c>)。</summary>
    /// <remarks>
    /// <para>超时用 <see cref="WorkerProtocol.ToolCallTimeout"/>(<b>无限</b>)—— 子代理合法跑 30 分钟
    /// (<c>CliAgentDefinition.TimeoutMinutes</c>), 在传输层加一个更短的上限等于把"正常的慢"判成失败。
    /// 兜底靠三件事, 缺一不可:
    /// ① 外部 <c>CancellationToken</c> 直通(用户点停止);
    /// ② <see cref="SendCancelAsync"/> 把取消送进 Worker 进程(否则"父进程放弃等待" ≠ "子进程树消失");
    /// ③ <see cref="SignalFaultOnce"/> 在 Worker 死亡/僵死时完结所有在飞调用。</para>
    ///
    /// <para><b>⚠️ 传输层故障抛异常, 不转成 <c>IsError=true</c> 的响应</b>: <c>IsError</c> 的语义是
    /// "工具执行失败"(Worker 侧真实发生了失败, LLM 可以据此重试或换招); 而"管道断了"属于
    /// <b>进程级故障</b>, 必须经 <see cref="Faulted"/> 让上层换一条路(重拉 Worker 或降级到内联),
    /// 而不是让 LLM 在同一个已死的 Worker 上反复重试。异常最终由 <c>WorkerProxyTool</c> 转成
    /// <c>ToolResult.Error</c>(软失败), 引擎不会因此崩。</para>
    /// </remarks>
    public async Task<WorkerToolCallResponse> CallToolAsync(WorkerToolCallRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureUsable($"工具调用 {request.Name}");

        var result = await ExchangeAsync(
            WorkerProtocol.MethodToolsCall,
            ToParams(request, AppJsonContext.Default.WorkerToolCallRequest, "tools/call 请求"),
            WorkerProtocol.ToolCallTimeout,
            ct,
            WorkerProtocol.MethodToolsCall).ConfigureAwait(false);

        return ReadPayload<WorkerToolCallResponse>(
            result, AppJsonContext.Default.WorkerToolCallResponse, "tools/call 响应");
    }

    /// <summary>把"父进程放弃等待"翻译成"Worker 进程里那个调用中止"(<c>notify/cancel</c>)。</summary>
    /// <remarks>
    /// <para><b>⚠️ 发送失败绝不抛</b>: 管道已断时抛异常会破坏调用方的取消语义 ——
    /// 调用方(工具层)正是在取消注册里调它, 它一抛就变成"取消本身失败", 于是"点停止"的路径上多一层异常,
    /// 而真正该记的东西(Worker 已死)反而被淹没。这里只记一条 Warn。</para>
    ///
    /// <para>唯一会传播的是<b>调用方自己给的 ct 取消</b>(那是调用方的语义, 不是"取消没送达")。
    /// 写帧用 <see cref="WorkerProtocol.CancelTimeout"/> 兜底: 管道写侧可能被一个不肯退出的对端堵死,
    /// "写一帧"这种本该瞬间完成的操作不该有能力把主进程钉住。</para>
    /// </remarks>
    public async Task SendCancelAsync(string callId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(callId))
        {
            return;
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            // 不抛: 见上方「发送失败绝不抛」
            Log.Debug(Category, $"{_logPrefix} 传输已释放, 跳过取消通知(callId={callId})");
            return;
        }

        // 载荷走源生成的 DTO 而不是手搓 JsonObject: 帧形状只有一处定义, 两侧契约就不会漂移
        // (编帧纪律: 多行内容必须靠 JSON 字符串字段转义, 见 WorkerMessages.cs 的文件头)。
        var payload = new WorkerCancelNotification { CallId = callId };
        var frame = new JsonObject
        {
            ["jsonrpc"] = WorkerProtocol.JsonRpcVersion,
            ["method"] = WorkerProtocol.NotifyCancel,
            ["params"] = ToParams(payload, AppJsonContext.Default.WorkerCancelNotification, "取消通知")
        };

        try
        {
            await ToolTransportContract.SendAsync(
                token => _writer.WriteAsync(frame, token),
                WorkerProtocol.CancelTimeout,
                ct,
                "取消通知").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 铁律: 调用方自己的取消原样上抛
        }
        catch (Exception ex)
        {
            // 竞态很常见(调用已经完成 / Worker 刚好崩了): 记 Warn 但不抛, 取消语义保持成立
            Log.Warn(Category, $"{_logPrefix} 取消通知发送失败(callId={callId}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>请求 Worker 退出(<c>worker/shutdown</c>)。<b>幂等</b>, 且到点没退就交给崩溃路径。</summary>
    /// <remarks>
    /// <para>是请求不是命令: 超时后<b>不在这里等</b>、也<b>不抛</b>。真正的进程收尾一律在
    /// <see cref="DisposeAsync"/>(关 stdin → 等 → Kill), 因为释放路径必须能在应用退出时被调到,
    /// 不能被一个不肯退的 Worker 拖住。</para>
    /// </remarks>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _shutdownSent, 1) != 0)
        {
            return; // 幂等: 调用方很可能在"正常关闭"与"异常兜底清理"两条路径上各调一次
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            var result = await ExchangeAsync(
                WorkerProtocol.MethodShutdown,
                null,
                WorkerProtocol.ShutdownTimeout,
                ct,
                WorkerProtocol.MethodShutdown).ConfigureAwait(false);

            var ok = ReadPayload<WorkerOkResponse>(result, AppJsonContext.Default.WorkerOkResponse, "shutdown 响应");
            Log.Debug(Category,
                $"{_logPrefix} shutdown 已应答: Ok={ok.Ok}{(ok.Ok ? string.Empty : $", 原因={ok.Error}")}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 管道已断/超时: 属正常收尾路径, 不抛(见方法注释)
            Log.Debug(Category, $"{_logPrefix} shutdown 未成功({ex.GetType().Name}: {ex.Message}); 交由 DisposeAsync 收尾");
        }
    }

    /// <summary>Worker 推送的工具实时输出(子代理 stdout 逐行)。</summary>
    public event Action<WorkerToolOutputNotification>? ToolOutputReceived;

    /// <summary>Worker 推送的分派状态变更。</summary>
    public event Action<WorkerAssignmentNotification>? AssignmentReceived;

    /// <summary>连接彻底不可用(读循环 EOF / IO 异常 / 进程退出 / 写失败 / 心跳僵死)。<b>至多一次。</b></summary>
    public event Action<string>? Faulted;

    /// <summary>
    /// 后台循环: 逐帧读取 stdout, 按帧的形状分派。EOF 即收尾。
    /// </summary>
    /// <remarks>
    /// <para><b>⚠️ finally 里的收尾是本类最关键的一段</b>: 它必须同时做三件事 ——
    /// 完结全部在飞请求、翻 <see cref="IsConnected"/>、广播一次 <see cref="Faulted"/>。
    /// 少任何一件都会留下"永远等不到结果"的调用(见类注释「Faulted 至多一次」)。</para>
    ///
    /// <para>读循环自己捕获所有异常并在 finally 收尾, 因此本任务永不 fault
    /// (否则会变成无人观察的任务异常, 在别的线程上炸出与本处无关的噪声)。</para>
    /// </remarks>
    private async Task ReadLoopAsync()
    {
        // 原因串刻意**不带**前缀: 它会被原样传给 Faulted, 上层会自己加 "Worker {key} 连接故障: " 之类的抬头
        var reason = "管道读取结束(对端退出或关闭了 stdout)";

        try
        {
            while (true)
            {
                var frame = await WorkerFrameReader.ReadAsync(_stdout, _stopCts.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break; // 正常 EOF
                }

                // ⚠️ 心跳判据是「收到**任何**一帧」, 不是「收到心跳」: 长耗时工具执行期间 Worker 仍会发心跳,
                // 但把心跳当唯一判据会让"只在响应时才说话"的实现错位被误判成僵死。
                // 单调时钟(TickCount64), 不受墙上时钟跳变影响。
                Interlocked.Exchange(ref _lastFrameTick, Environment.TickCount64);

                DispatchFrame(frame);
            }
        }
        catch (OperationCanceledException)
        {
            reason = "管道读取被取消(传输已释放)";
        }
        catch (Exception ex)
        {
            // 进程被杀 / 流被关闭 / 对端失控(帧超长)都在这里; 完全静默会让
            // 「读循环早就死了、之后所有调用只能等超时」这类问题无从排查
            reason = $"管道读取失败: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            SignalFaultOnce(reason);
        }
    }

    /// <summary>
    /// 帧分派表(父侧视角)。
    /// <list type="bullet">
    /// <item>有 id、无 method → 响应 → <see cref="WorkerRpcCore.DispatchResponse"/> 配对。</item>
    /// <item>有 id、有 method → 子→父<b>请求</b> → 记 Warn 并回一个 error 响应。</item>
    /// <item>无 id、<c>notify/toolOutput</c> → 抬 <see cref="ToolOutputReceived"/>。</item>
    /// <item>无 id、<c>notify/assignment</c> → 抬 <see cref="AssignmentReceived"/>。</item>
    /// <item>无 id、<c>notify/heartbeat</c> → 只留 Trace(时间戳已在读循环刷新)。</item>
    /// <item>无 id、其它 method → 记 Debug: 协议向前兼容, 未知通知必须忽略而不是断连。</item>
    /// </list>
    /// </summary>
    private void DispatchFrame(JsonObject frame)
    {
        var hasId = frame.TryGetPropertyValue("id", out var idNode) && idNode is not null;
        var method = ReadMethod(frame);

        if (method is null)
        {
            if (hasId)
            {
                _core.DispatchResponse(frame);
                return;
            }

            Log.Debug(Category, $"{_logPrefix} 收到既无 id 也无 method 的帧, 已忽略");
            return;
        }

        if (hasId)
        {
            HandlePeerRequest(idNode, method);
            return;
        }

        switch (method)
        {
            case WorkerProtocol.NotifyToolOutput:
                var output = TryRead(frame, AppJsonContext.Default.WorkerToolOutputNotification, "toolOutput 通知");
                if (output is not null)
                {
                    RaiseSafe(ToolOutputReceived, output);
                }

                return;

            case WorkerProtocol.NotifyAssignment:
                var assignment = TryRead(frame, AppJsonContext.Default.WorkerAssignmentNotification, "assignment 通知");
                if (assignment is not null)
                {
                    RaiseSafe(AssignmentReceived, assignment);
                }

                return;

            case WorkerProtocol.NotifyHeartbeat:
                // 刻意不在这里刷新时间戳: 读循环已经对**任何**帧刷新过(见 ReadLoopAsync 的注释)。
                // 单列这一支只是把"心跳是协议位、不是未知通知"这件事写进代码。
                Log.Trace(Category, $"{_logPrefix} 收到 Worker 心跳");
                return;

            case WorkerProtocol.NotifyCancel:
                // 方向反了: notify/cancel 是父 → 子(见 WorkerProtocol 的行内标注)。
                // 记 Warn 而非 Debug, 因为它通常是"两侧实现接反了"这类真 bug 的信号。
                Log.Warn(Category, $"{_logPrefix} 收到方向错误的 notify/cancel(应为父→子), 已忽略");
                return;

            default:
                // 协议向前兼容: 两侧版本不同步时, 新版本的 Worker 可能多发几种通知,
                // 忽略即可(真正的不兼容由握手时的版本校验解决, 而不是靠断连)。
                Log.Debug(Category, $"{_logPrefix} 收到未识别的通知({method}), 已忽略");
                return;
        }
    }

    /// <summary>
    /// 子→父请求(当前协议里只有保留位 <c>request/askUser</c>)。
    /// ⚠️ 必须回一个 error 响应: 这个请求是在某个<b>在途</b>调用期间发出的, 而 <c>tools/call</c>
    /// 的超时是无限的 —— 静默丢弃会让对端挂到它自己的超时(实际是永远)。
    /// </summary>
    private void HandlePeerRequest(JsonNode? idNode, string method)
    {
        Log.Warn(Category,
            $"{_logPrefix} 收到 Worker 侧请求({method}, id={idNode?.ToJsonString() ?? "(无)"}), " +
            "但当前方案不启用子→父请求通道(ask_user 留在主进程); 已回 error 让对端立即失败而不是挂到超时");

        // 异步回执, 不占住读循环: 写帧可能要等写锁(此刻另一个请求正在写)
        _ = ReplyErrorAsync(idNode, method);
    }

    private async Task ReplyErrorAsync(JsonNode? idNode, string method)
    {
        var reply = new JsonObject
        {
            ["jsonrpc"] = WorkerProtocol.JsonRpcVersion,
            ["error"] = new JsonObject
            {
                ["code"] = -32601, // Method not found
                ["message"] = $"父进程不处理 Worker 侧的请求: {method}"
            }
        };

        // ⚠️ 必须回**原样**的 id: JSON-RPC 的 id 允许是数字或字符串, 归一化后再发可能让对端配不上
        // (那会把"回了一个错 id"变成"对端永远等不到响应", 比不回更糟)。
        if (idNode is not null)
        {
            reply["id"] = idNode.DeepClone();
        }

        try
        {
            await ToolTransportContract.SendAsync(
                token => _writer.WriteAsync(reply, token),
                WorkerProtocol.CancelTimeout,
                CancellationToken.None,
                "错误回执").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 铁律: OCE 排在 catch(Exception) 前面。这里用 ct=None, 实际上到不了这一支;
            // 也不重抛 —— 它是 fire-and-forget 任务, 重抛会变成没人观察的任务异常
            Log.Debug(Category, $"{_logPrefix} 回 error 响应时被取消({method})");
        }
        catch (Exception ex)
        {
            // 回不回得出去不是我们能补救的(对端要么已放弃, 要么管道已断); 不抛, 不惊动读循环
            Log.Debug(Category, $"{_logPrefix} 回 error 响应失败({method}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 后台任务: 把 Worker 的 stderr 读到 EOF, 每行以 <c>[Worker:{key}]</c> 前缀排给 <see cref="Log"/>。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须读</b>: 子进程写满 stderr 管道缓冲区(~64KB)后会阻塞在 write 上, 于是它既不退出
    /// 也不处理协议, 表现为「Worker 起来了但什么都不干」—— 而 stdout 一切正常, 日志里只有一条"启动成功"。
    /// 只读不排更糟: 崩溃原因全丢。
    /// <para><b>为什么是 Debug 级</b>: stderr 里既有真实诊断, 也有第三方库的无害抱怨(缺字体、缺 ICU…),
    /// 提到 Warn 会让"Worker 起不来"的真信号被淹没。用户的排查入口仍是 doctor。</para>
    /// </remarks>
    private async Task PumpStderrAsync()
    {
        try
        {
            while (await _process.StandardError!.ReadLineAsync(_stopCts.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                Log.Debug(Category, $"{_logPrefix} stderr: {line}");
            }
        }
        catch (OperationCanceledException)
        {
            // DisposeAsync 取消 _stopCts 后的正常退出路径
        }
        catch (Exception ex)
        {
            Log.Debug(Category, $"{_logPrefix} stderr 读取结束: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 心跳看门狗: <see cref="WorkerProtocol.HeartbeatTimeout"/>(60s)内没收到<b>任何</b>帧 →
    /// 判僵死 → 强杀进程树 + 广播一次 Faulted。
    /// </summary>
    /// <remarks>
    /// <para><b>⚠️ 为什么"空闲时误杀"可以接受, 而"在飞调用挂起"不可接受</b>(这就是本判据的取舍依据):
    /// ① 空闲 Worker 被误判僵死的代价 = 白拉一个进程。按生命周期规则 L1(懒启动) + L5(只重拉那一个
    /// (目录,会话)), 上层会在下一次真的需要工具时重新 spawn, 用户侧无感;
    /// ② 而放着不杀的代价 = 一次 <c>tools/call</c> 永久挂起(<see cref="WorkerProtocol.ToolCallTimeout"/>
    /// 是无限超时, 读循环又因为管道没关而收不到 EOF)。用户点停止也只是"父进程放弃等待",
    /// Worker 进程树连同它占着的 git 写锁还在跑 —— 这正是本仓库反复强调的那类事故
    /// (停止按钮只停住主循环、子进程仍在跑)。
    /// 所以判据取「宁错杀勿挂起」。</para>
    ///
    /// <para>为什么是"任何帧"而不是"心跳帧": 见 <see cref="ReadLoopAsync"/> 的注释。
    /// 为什么是 60s(发送间隔 5s 的 12 倍): 留足余量吸收 GC / 磁盘 IO 抖动, 否则正常的暂停会被判成僵死。</para>
    /// </remarks>
    private async Task WatchLivenessAsync()
    {
        try
        {
            while (!_stopCts.IsCancellationRequested)
            {
                await Task.Delay(WorkerProtocol.HeartbeatInterval, _stopCts.Token).ConfigureAwait(false);

                var idleMs = Environment.TickCount64 - Interlocked.Read(ref _lastFrameTick);
                // HeartbeatTimeout 是 TimeSpan, 与 long 毫秒比较前统一换算(不要用隐式转换)。
                if (idleMs < (long)WorkerProtocol.HeartbeatTimeout.TotalMilliseconds)
                {
                    continue;
                }

                var seconds = idleMs / 1000;
                var pending = _core.PendingCount;
                Log.Warn(Category, $"{_logPrefix} Worker 心跳僵死: {seconds}s 内未收到任何帧(在飞请求 {pending} 个)");

                // 先杀再广播: 广播出去之后上层会立刻 DisposeAsync, 那时若进程还活着就要多花一个自退窗口
                KillQuietly(_process, "心跳僵死");
                SignalFaultOnce($"Worker 心跳僵死: {seconds}s 内未收到任何帧(在飞请求 {pending} 个)");
                return; // 判死一次即可; 后续由 Faulted → DisposeAsync 收尾
            }
        }
        catch (OperationCanceledException)
        {
            // 释放路径
        }
        catch (Exception ex)
        {
            // 看门狗自己出错不该让传输变得不可用; 但必须留痕, 否则表现为"看门狗其实没在跑"
            Log.Debug(Category, $"{_logPrefix} 心跳看门狗异常退出: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 唯一的故障出口: 翻 <see cref="IsConnected"/> + 完结全部在飞请求 + <b>至多一次</b>广播 <see cref="Faulted"/>。
    /// </summary>
    /// <remarks>
    /// <para>全部故障入口(读循环 finally / 心跳看门狗 / 写失败 / 握手校验失败)都汇到这里 ——
    /// 少一条入口就是给"永久挂起"留一个后门, 而 <c>ToolCallTimeout</c> 不会替你兜底。</para>
    ///
    /// <para><b>为什么"至多一次"</b>: 见类注释。上层按事件做记账(故障史、降级、加锁),
    /// 重复触发就是重复降级 + 重复加锁, 而故障原因只需要留一条 —— 第一条通常就是根因。</para>
    ///
    /// <para><b>为什么已释放时不再广播</b>: <see cref="DisposeAsync"/> 也会让读循环走到这里
    /// (取消令牌 → 读循环结束)。此刻连接是<b>我们自己</b>关掉的, 广播出去会让上层把一次干净收尾
    /// 记成"Worker 崩溃", 甚至在退出路径上触发一次无意义的重拉。在飞请求此时已由 DisposeAsync 完结过。</para>
    /// </remarks>
    private void SignalFaultOnce(string reason)
    {
        if (Interlocked.Exchange(ref _faultRaised, 1) != 0)
        {
            return;
        }

        _connected = false;

        // 内核侧同样有一次性闸门(它可能被本类之外的路径 Fault 过); 两道闸互不替代:
        // 内核那道保证"在飞请求只被完结一次", 本类这道保证"上层只被通知一次"
        _core.Fault(reason);

        if (Volatile.Read(ref _disposed) != 0)
        {
            Log.Debug(Category, $"{_logPrefix} 传输故障({reason}); 已释放, 不再广播");
            return;
        }

        Log.Warn(Category, $"{_logPrefix} 传输故障: {reason}");
        RaiseSafe(Faulted, reason);
    }

    /// <summary>
    /// 统一出口: 发请求 + 按超时/取消规则等响应 + 把传输层的故障语义补齐。
    /// </summary>
    /// <remarks>
    /// <b>为什么超时/取消判定完全委托 <see cref="ToolTransportContract.AwaitAsync"/></b>(它内部再用
    /// <see cref="WorkerRpcCore.RequestAsync"/>):「外部取消优先于超时」「无限时长不建 Task.Delay」
    /// 「判定只看外部 ct」这三条是 <c>IToolTransport</c> 对两个实现的共同约定, 写在共享辅助里才谈得上"逐字一致"。
    /// 这里只额外负责两件共享辅助管不到的事:
    /// ① <b>写失败即视为故障</b>(管道不会自己恢复, 不广播 Faulted 的话在飞调用只能等超时);
    /// ② <b>超时/对端 error 不视为故障</b>(前者是 Worker 只是慢, 后者是它明确回了一个失败 ——
    /// 连接都还活着, 标成故障会让上层白白换一个 Worker)。
    /// </remarks>
    private async Task<JsonNode?> ExchangeAsync(string method, JsonObject? parameters,
        TimeSpan timeout, CancellationToken ct, string displayName)
    {
        try
        {
            return await _core.RequestAsync(method, parameters, timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 铁律: 取消原样上抛, 绝不能降级成"工具执行失败"
        }
        catch (TimeoutException)
        {
            throw; // 不是故障: Worker 可能只是在跑一个很长的子代理
        }
        catch (WorkerRpcException)
        {
            throw; // 对端明确回了 error: 工具级失败, 由上层转成 ToolResult.Error
        }
        catch (ObjectDisposedException)
        {
            // 传输在等待期间被释放: 在飞请求已由 DisposeAsync 完结, 不是 Worker 的故障
            throw;
        }
        catch (Exception ex)
        {
            // 其余几乎都是写侧失败(IOException / 管道已关)
            SignalFaultOnce($"请求 {displayName} 发送失败: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 请求入口的统一闸门: 已释放抛 <see cref="ObjectDisposedException"/>,
    /// 未握手/已断线抛 <see cref="InvalidOperationException"/>(上层据此决定重拉或降级)。
    /// </summary>
    private void EnsureUsable(string operation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!_connected)
        {
            throw new InvalidOperationException($"{_logPrefix} Worker 连接不可用({operation}): 尚未完成握手或已断开");
        }
    }

    /// <summary>
    /// 释放: 完结在飞请求 → 关 stdin 让 Worker 自退 → 有界等待 → Kill(entireProcessTree) → 收尾。
    /// <b>幂等, 且绝不抛异常</b>(僵死/崩溃路径上也会走到这里, 释放动作本身不能失败)。
    /// </summary>
    /// <remarks>
    /// <para><b>顺序为什么不能改</b>:
    /// <list type="number">
    /// <item><b>先完结在飞请求</b>: 从这一刻起任何等待者都不可能再收到真实响应, 必须立刻给它们一个
    /// 确定答案(软失败), 否则引擎会挂在这里等一个永远不会来的结果。</item>
    /// <item><b>再关 stdin</b>: Worker 侧的"父进程没了就自杀"有两条路径(stdin EOF 与 ParentPid 探测),
    /// 关 stdin 是其中最确定的一条, 也是唯一不依赖 pid 探测实现细节的一条。</item>
    /// <item><b>等待期间刻意<b>不</b>停读循环</b>: 停了读循环, Worker 写 stdout 就会堵死, 于是它永远
    /// 等不到能退出的时机, 每次释放都要白等满 <see cref="WorkerProtocol.ShutdownTimeout"/>。
    /// 读循环读到 EOF 自行结束, 令牌在最后一步才取消。</item>
    /// <item><b>超时后 Kill(entireProcessTree: true)</b>: ⚠️ <b>必须</b>带 <c>entireProcessTree</c> ——
    /// Worker 进程树里挂着子代理 CLI 进程(Claude Code / opencode / codex…), 只杀 Worker 本体就会把
    /// 它们留成孤儿: 没人管、还占着工作目录的 git 写锁与网络连接。</item>
    /// </list></para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // 幂等: 断连与正常退出两条路径都会走到这里
        }

        _connected = false;

        // 1) 完结在飞请求(不广播 Faulted: 这是我们主动关的, 不是 Worker 崩了)
        try
        {
            _core.FailAllPending("Worker 传输已释放");
            _core.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(Category, $"{_logPrefix} 完结在飞请求时异常: {ex.GetType().Name}: {ex.Message}");
        }

        // 2) 关闭 stdin, 让 Worker 读到 EOF 后自行退出
        try
        {
            _process.StandardInput?.Close();
        }
        catch (Exception ex)
        {
            Log.Debug(Category, $"{_logPrefix} 关闭 Worker stdin 失败: {ex.GetType().Name}: {ex.Message}");
        }

        // 3) 有界等待自退
        try
        {
            using var waitCts = new CancellationTokenSource(WorkerProtocol.ShutdownTimeout);
            await _process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 4) 超时未退出: 强杀整棵进程树, 再给一次有界的等待让管道真正收尾
            KillQuietly(_process, "自退超时");

            try
            {
                using var killWaitCts = new CancellationTokenSource(KillWaitTimeout);
                await _process.WaitForExitAsync(killWaitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log.Warn(Category, $"{_logPrefix} Worker 进程在强杀后仍未退出, 放弃等待");
            }
            catch (Exception ex)
            {
                Log.Debug(Category, $"{_logPrefix} 等待 Worker 进程退出失败: {ex.GetType().Name}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Log.Debug(Category, $"{_logPrefix} 等待 Worker 进程退出异常: {ex.GetType().Name}: {ex.Message}");
        }

        // 5) 停三个后台循环(此刻 stdout 已 EOF, 它们本来也快结束了)
        // ⚠️ 刻意**不**释放 _stopCts: 三个后台任务仍在用它(Task.Delay(…, token) 在 CTS 已释放时会抛),
        // 随实例被 GC 回收即可 —— 与 McpStdioClient 刻意不释放 _disposedCts 是同一条理由。
        try
        {
            _stopCts.Cancel();
        }
        catch (Exception ex)
        {
            Log.Debug(Category, $"{_logPrefix} 取消后台循环时异常: {ex.GetType().Name}: {ex.Message}");
        }

        // 6) 释放句柄与写侧
        try
        {
            _process.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(Category, $"{_logPrefix} 释放 Worker 进程句柄失败: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            _writer.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(Category, $"{_logPrefix} 释放帧写入器失败: {ex.GetType().Name}: {ex.Message}");
        }

        Log.Debug(Category, $"{_logPrefix} 传输已释放");
    }

    // ── 载荷编解码(AOT: 只用源生成上下文) ─────────────────────────────────────────

    /// <summary>DTO → <c>params</c> 节点。</summary>
    /// <remarks>
    /// 走"先序列化成文本、再解析成节点"而不是 <c>SerializeToNode</c>: 两者结果等价, 但本仓其它地方
    /// (ChatService / UsageStatsService)一律用文本形态, 统一形状便于与日志里的原始帧对照;
    /// 代价是每次请求多一次解析, 对本协议的帧体积可忽略。
    /// <para>⚠️ <b>绝不能改用不带 <see cref="JsonTypeInfo"/> 的重载</b>: AOT 下会抛
    /// <see cref="InvalidOperationException"/>, 而本仓的 AOT 兼容性目前没有 CI 门禁
    /// (AGENTS.md 技术债 #1/#2), 只在真实发布时才暴露。</para>
    /// </remarks>
    private static JsonObject? ToParams<T>(T payload, JsonTypeInfo<T> typeInfo, string what) where T : class
    {
        try
        {
            // JsonNode.Parse 返回 JsonNode?, 但本仓所有 DTO 的根都是对象, 故收窄成 JsonObject。
            // 收窄失败说明协议 DTO 定义错了(不该出现), 让它在这里炸比把异常形状拖到对端更早暴露。
            return JsonNode.Parse(JsonSerializer.Serialize(payload, typeInfo)) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Worker {what} 序列化失败: {ex.Message}", ex);
        }
    }

    /// <summary>取 <c>result</c> 节点并反序列化成 DTO; 节点缺失即视为对端违约, 抛异常而不是返回默认值。</summary>
    private static T ReadPayload<T>(JsonNode? result, JsonTypeInfo<T> typeInfo, string what) where T : class
    {
        if (result is null)
        {
            throw new InvalidOperationException($"Worker 对 {what} 没有返回 result");
        }

        try
        {
            // 经文本往返: 形状与写侧完全对称, 且不依赖任何反射式重载
            return JsonSerializer.Deserialize(result.ToJsonString(), typeInfo)
                ?? throw new InvalidOperationException($"Worker 对 {what} 返回了 null");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Worker {what} 解析失败: {ex.Message}", ex);
        }
    }

    /// <summary>通知类载荷: 解析失败只记 Warn 并放弃该条通知, 绝不中断读循环。</summary>
    private static T? TryRead<T>(JsonObject frame, JsonTypeInfo<T> typeInfo, string what) where T : class
    {
        if (!frame.TryGetPropertyValue("params", out var paramsNode) || paramsNode is null)
        {
            Log.Warn(Category, $"Worker {what} 缺少 params 字段, 已忽略");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(paramsNode.ToJsonString(), typeInfo);
        }
        catch (JsonException ex)
        {
            Log.Warn(Category, $"Worker {what} 解析失败, 已忽略: {ex.Message}");
            return null;
        }
    }

    // ── 小工具 ────────────────────────────────────────────────────────────────────

    /// <summary>取帧的 method 字段(缺失/非字符串 → null)。</summary>
    private static string? ReadMethod(JsonObject frame)
    {
        if (!frame.TryGetPropertyValue("method", out var node) || node is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<string>(out var text) ? text : null;
    }

    /// <summary>逐订阅者投递(单个订阅者异常不影响其余订阅者, 也不影响读循环)。</summary>
    private void RaiseSafe<T>(Action<T>? handlers, T payload)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T>)handler)(payload);
            }
            catch (Exception ex)
            {
                Log.Warn(Category, ex, $"{_logPrefix} 事件订阅者处理异常");
            }
        }
    }

    /// <summary>强杀整棵进程树; 失败只记 Debug(它已经是补救动作, 失败不该再抛)。</summary>
    private static void KillQuietly(Process process, string reason)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(Category, $"强制结束 Worker 进程失败({reason}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>进程已经退出(或查不到)时给一句人类可读描述; 一切正常时返回 null。</summary>
    private string? TryDescribeEarlyExit()
    {
        try
        {
            return _process.HasExited
                ? $"Worker 进程已退出(退出码 {_process.ExitCode}), 握手未完成"
                : null;
        }
        catch (Exception ex)
        {
            return $"查询 Worker 进程状态失败: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>算出 Worker key; 身份字段缺一时退化为可读占位串(绝不因此让传输建立失败)。</summary>
    private static string SafeWorkerKey(WorkerHelloRequest hello)
    {
        if (string.IsNullOrWhiteSpace(hello.SessionId) || string.IsNullOrWhiteSpace(hello.WorkDir))
        {
            return "未标识";
        }

        try
        {
            return WorkerProtocol.MakeWorkerKey(hello.WorkDir, hello.SessionId);
        }
        catch (Exception ex)
        {
            // MakeWorkerKey 内部会 Normalize(路径); 路径异常不值得让整个传输建立失败
            return $"无效({ex.GetType().Name})";
        }
    }
}