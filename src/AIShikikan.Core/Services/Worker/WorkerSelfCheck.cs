using System.Diagnostics;
using System.Text.Json;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;
using AIShikikan.Core.Serialization;
using AIShikikan.Core.Services.Git;
using AIShikikan.Core.Services.Runtime;
using AIShikikan.Core.Services.Session;
using AIShikikan.Core.Services.Tools;

namespace AIShikikan.Core.Services.Worker;

/// <summary>Worker 自检结果: 失败项 + 需要让用户看见的说明(跳过项 / 已知口径差)。
///
/// <para><b>为什么分成两栏</b>: <c>Failures</c> 是"坏了"(退出码非零), <c>Notes</c> 是"没坏但你该知道"
/// (管道自检因未构建产物而跳过、大小写折叠口径差)。把两者混在一个列表里会让 doctor 把
/// "Worker 没构建"报成红叉, 而那恰恰是本方案允许的正常降级态; 反过来只报失败又会静悄悄吞掉
/// 「这次根本没验到跨进程那条路」这个关键事实 —— 而它正是用户最需要知道的一句。</para>
/// </summary>
public sealed record WorkerSelfCheckResult(IReadOnlyList<string> Failures, IReadOnlyList<string> Notes)
{
    /// <summary>是否全通(空失败清单)。</summary>
    public bool Ok => Failures.Count == 0;
}

/// <summary>
/// Worker 方案的端到端自检: 协议编解码 + 工具执行 + 取消传播 + 跨进程往返 + 身份键一致性
/// + <b>进程池生命周期(空闲回收)</b>。
///
/// <para><b>为什么必须有它(以及为什么必须是"真跨进程"的)</b>: 本仓无测试(技术债 #1), 现有的验证手段只有
/// <c>WorkspaceExecutionCoordinator.SelfCheck()</c> 那种纯内存自检, 而 Worker 这条路的每一处失效都是
/// <b>静默</b>的 —— 判别符与 <c>[JsonDerivedType]</c> 脱节表现为"整份会话读不出来", 取消不传播表现为
/// "停止按钮只停住主循环而子进程还在跑", <c>MakeWorkerKey</c> 两侧口径不一致表现为"父进程认不出自己 spawn 的
/// Worker"。内联路径跑通<b>不能</b>证明管道路径跑通, 因为两者只在 <c>IToolTransport</c> 之下等价。
/// 所以这里刻意让管道那一段在 <c>WorkerLocator</c> 命中时<b>真的起一个进程</b>跑完握手→同步→调用→关闭。</para>
///
/// <para><b>副作用纪律(本类的底线)</b>:
/// <list type="bullet">
/// <item>一切读写都发生在 <c>Path.GetTempPath()</c> 下的<b>本次运行专属临时目录</b>里, 绝不碰用户的仓库;
/// 目录在 <c>finally</c> 里删掉(带重试, 抗 Worker 崩溃残留的短暂占用)。</item>
/// <item>不写 <c>~/.config</c> / <c>~/.local/share</c> 下的任何用户文件: 唯一的例外是
/// <c>GitCheckpointStore</c> 构造期那次幂等 mkdir(且 doctor 早于本自检就已建过同一个目录)。</item>
/// <item>除 Worker 子进程外不 spawn 任何进程: 因此工作区解析走 <see cref="SelfCheckResolver"/>(纯内存)
/// 而非 <c>GitWorkspaceResolver</c>(那会起 2 个 git 进程), 也不碰任何 git 写操作。</item>
/// <item><see cref="Run"/> <b>绝不抛异常</b>: doctor 是诊断入口, 自检自己崩掉会把 doctor 一起顶掉,
/// 用户既看不到 Worker 的问题也看不到 doctor 的问题 —— 两头都丢。</item>
/// </list></para>
///
/// <para><b>为什么内联那一段自己实现 <c>InlineToolExecutor</c> 而不复用 Worker 侧那份</b>:
/// <c>AIShikikan.Worker</c> 不被 Core 引用(见 <c>worker-architecture.md</c> §7.1), Core 里拿不到
/// <c>WorkerSlimHost</c>。这里按同一份契约重写一遍 —— 也正因为是两份实现, "未知工具必须软失败"
/// 这条契约才真的被验证了一次, 而不是被同一个实现自证。</para>
/// </summary>
public static class WorkerSelfCheck
{
    /// <summary>自检覆盖的场景组数。doctor 的输出文案引用本常量, 不要在调用方写死数字。</summary>
    public const int ScenarioCount = 12;

    /// <summary>日志 category(与 Worker 其余部分一致)。</summary>
    private const string Category = "Worker";

    /// <summary>自检用的会话 Id。它参与 <see cref="WorkerProtocol.MakeWorkerKey"/>, 固定值便于事后复现。</summary>
    private const string SelfCheckSessionId = "worker-selfcheck";

    /// <summary>临时目录里的探针文件名(固定名, 临时<b>目录</b>才是一次性的)。</summary>
    private const string ProbeFileName = "selfcheck-probe.txt";

    /// <summary>read_file 的入参(裸 JSON)。路径走相对形式, 顺带验证「相对工作区解析」这条约定。</summary>
    private const string ProbeArguments = """{"path":"selfcheck-probe.txt"}""";

    /// <summary>探针文件内容。刻意混入中文 / 双引号 / 反斜杠 / 花括号 / 尖括号: 这五样各自对应一种
    /// 跨进程编码事故(UTF-8、JSON 字符串转义、"帧内不得有裸换行"纪律的对象)。</summary>
    private static readonly string[] ProbeLines =
    [
        "第一行: 中文 + 双引号 \"quoted\" + 反斜杠 \\",
        "第二行: JSON 片段 {\"a\":1} 与尖括号 <tag>",
        "第三行: 结束标记 worker-selfcheck",
    ];

    /// <summary>探针文件的完整内容(与 <see cref="ProbeLines"/> 一一对应的换行拼接)。</summary>
    private static readonly string ProbeBody = string.Join("\n", ProbeLines);

    /// <summary>等待某个"自检内的异步条件"的上限。仅用于自检自身的防挂死兜底, 不参与被测语义。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>等待 Worker 进程退出的上限(自检自己的闸门; 传输层另有 <c>ShutdownTimeout</c>)。</summary>
    private static readonly TimeSpan ExitProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>S8–S11 用的空闲阈值。生产默认是 1 分钟, 自检里换成 200ms —— 否则每组场景都要真等一分钟。</summary>
    private static readonly TimeSpan IdleProbeTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>S8–S10 / S12 里「等空闲超时真的过去」的时长。</summary>
    /// <remarks>
    /// 取阈值的 3 倍: <c>Task.Delay</c> 只可能偏晚(那正是我们要的方向), 不会偏早, 因此 3 倍裕量足够;
    /// 而裕量太小会在机器繁忙时随机假失败 —— 一条会随机红的断言等于没有断言。
    /// </remarks>
    private static readonly TimeSpan IdleProbeWait = TimeSpan.FromMilliseconds(600);

    /// <summary>S11 需要的最小静置时长。<see cref="Environment.TickCount64"/> 是<b>毫秒</b>分辨率:
    /// 不先睡够, <see cref="WorkerClient.Touch"/> 写进去的值可能与前一次读数完全相同, 断言随机假失败。</summary>
    private static readonly TimeSpan TouchProbeWait = TimeSpan.FromMilliseconds(30);

    // ───────────────────────────── 公开入口 ─────────────────────────────

    /// <summary>跑一遍自检, 只返回失败项(空列表 = 全通)。</summary>
    /// <remarks>与 <c>WorkspaceExecutionCoordinator.SelfCheck()</c> 同款签名, 便于 doctor 统一处理。
    /// 需要"跳过说明"时请用 <see cref="RunDetailed"/>。</remarks>
    public static IReadOnlyList<string> Run(CancellationToken ct = default) => RunDetailed(ct).Failures;

    /// <summary>跑一遍自检, 返回失败项 + 说明项。<b>绝不抛异常。</b></summary>
    public static WorkerSelfCheckResult RunDetailed(CancellationToken ct = default)
    {
        try
        {
            // 刻意同步阻塞: doctor 是控制台入口, 没有 await 的位置。
            // GetAwaiter().GetResult() 会把异常原样抛出(不包 AggregateException), 故下面的分类 catch 才成立。
            return RunCoreAsync(ct).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return new WorkerSelfCheckResult(["自检被调用方取消(未跑完)"], []);
        }
        catch (Exception ex)
        {
            return new WorkerSelfCheckResult([$"自检执行中断({ex.GetType().Name}): {ex.Message}"], []);
        }
    }

    // ───────────────────────────── 编排 ─────────────────────────────

    private static async Task<WorkerSelfCheckResult> RunCoreAsync(CancellationToken ct)
    {
        var failures = new List<string>();
        var notes = new List<string>();

        // ⚠ 临时目录是本自检唯一的写入面: 工具执行、探针文件、Worker 子进程的工作目录全在这里。
        // 放在 Path.GetTempPath 下而不是用户仓库里, 是为了让"最坏情况写坏东西"也只损坏一个可随时删掉的目录。
        var workDir = Path.Combine(Path.GetTempPath(),
            "aishikikan-worker-selfcheck-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(workDir);
            await File.WriteAllTextAsync(Path.Combine(workDir, ProbeFileName), ProbeBody, ct).ConfigureAwait(false);

            // ⚠ 只注册固定工具(含 5 个 git_*): 本自检**只调用** read_file, 那些 git 工具在临时非 git 目录下
            // 会返回友好错误, 因此"清单里存在 git 工具"这件事不影响任何断言, 却能让 S1 顺带验到
            // 「工具集形态与生产完全一致」(含 RequiresApproval / 多行 ParametersJson)。
            // 关键约束: 绝不调用它们 —— 本自检不跑任何 git 操作, 连只读的都不跑。
            var registry = new ToolRegistry();
            foreach (var tool in AgentToolFactory.CreateCoreTools(new GitService(new GitCheckpointStore())))
            {
                registry.Register(tool);
            }

            var ctx = new ToolContext { WorkspaceRoot = workDir, SessionId = SelfCheckSessionId };

            var (probe, s1) = await CheckInlineFlowAsync(registry, ctx, ct).ConfigureAwait(false);
            failures.AddRange(s1);
            failures.AddRange(await CheckUnknownToolAsync(registry, ctx, ct).ConfigureAwait(false));
            failures.AddRange(await CheckCancellationAsync(registry, ctx, ct).ConfigureAwait(false));
            failures.AddRange(await CheckPipeAsync(workDir, notes, ct).ConfigureAwait(false));
            failures.AddRange(CheckCardCodec(probe));
            failures.AddRange(CheckWorkerKey(workDir, notes));
            failures.AddRange(await CheckPoolAsync(registry, workDir, ct).ConfigureAwait(false));
            failures.AddRange(await CheckIdleReclaimAsync(registry, workDir, ct).ConfigureAwait(false));
            failures.AddRange(await CheckInFlightNoReclaimAsync(registry, workDir, ct).ConfigureAwait(false));
            failures.AddRange(await CheckTurnActiveNoReclaimAsync(registry, workDir, ct).ConfigureAwait(false));
            failures.AddRange(await CheckActivityRefreshAsync(registry, workDir, ct).ConfigureAwait(false));
            failures.AddRange(await CheckIdleTimeoutDisabledAsync(registry, workDir, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            failures.Add("自检被调用方取消(未跑完)");
        }
        catch (Exception ex)
        {
            // 走到这里说明某一段自检本身坏了(如临时目录建不出来、工具集构造失败)。
            // 仍然收敛成一条失败项, 绝不让它冒出去把 doctor 顶掉。
            failures.Add($"自检执行中断({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }

        if (failures.Count > 0)
        {
            Log.Warn(Category, $"Worker 自检未通过({failures.Count} 项): {string.Join(" | ", failures)}");
        }
        else
        {
            Log.Info(Category, notes.Count == 0
                ? $"Worker 自检通过({ScenarioCount} 组场景)"
                : $"Worker 自检通过({ScenarioCount} 组场景, 说明 {notes.Count} 条): {string.Join(" | ", notes)}");
        }

        return new WorkerSelfCheckResult(failures, notes);
    }

    // ── S1: 内联传输全流程(不需要 Worker 产物, 永远能跑) ──────────────────────

    /// <summary>S1 的产物, 供 S5 复用(避免第二处重新造一份 detail 再验一次编解码)。</summary>
    private sealed record InlineProbe(WorkerToolCallResponse? Response, FileReadDetail? Decoded);

    private static async Task<(InlineProbe Probe, List<string> Failures)> CheckInlineFlowAsync(
        ToolRegistry registry, ToolContext ctx, CancellationToken ct)
    {
        var failures = new List<string>();

        void Check(bool ok, string name)
        {
            if (!ok) failures.Add(name);
        }

        await using var transport = CreateInline(registry, ctx);

        // 1.1 握手: 内联模式没有对端, 但仍必须给出一份可用的身份, 否则上层无法把它与真实 Worker 区分开。
        var hello = await transport.HandshakeAsync(
            new WorkerHelloRequest
            {
                ProtocolVersion = WorkerProtocol.ProtocolVersion,
                SessionId = SelfCheckSessionId,
                WorkDir = ctx.WorkspaceRoot,
                WorkspaceRoot = ctx.WorkspaceRoot
            },
            ct).ConfigureAwait(false);

        Check(hello.Ok, "S1 内联握手应返回 Ok=true");
        Check(hello.ProtocolVersion == WorkerProtocol.ProtocolVersion, "S1 内联握手应回显当前协议版本");
        // WorkerPid 必须填本进程: 内联模式下 Worker 确实就是这个进程, 填别的值会让排障时
        // "日志里的 pid 与 ps 对不上"把人带向错误方向。
        Check(hello.WorkerPid == Environment.ProcessId, "S1 内联握手的 WorkerPid 应为本进程 pid(诚实降级)");

        // 1.2 工具集同步: 内联侧只是把本地注册表原样吐出(sync 请求里的开关只对 Worker 侧有意义),
        // 但"声明能被完整吐出来"这件事必须验: ParametersJson 是多行原始字符串, 一旦某天改成
        // 不再转义就嵌套, 症状是"偶发 JsonException", 极难复现。
        var tools = await transport.SyncToolsAsync(
            new WorkerToolsSyncRequest { CoreTools = true, SubagentVisible = false, PlanMode = false },
            ct).ConfigureAwait(false);

        Check(tools.Tools.Count > 0, "S1 内联同步的工具清单不应为空");
        Check(tools.Tools.Any(t => t.Name == "read_file"), "S1 内联同步的工具清单应包含 read_file");
        Check(tools.Tools.All(t => !string.IsNullOrWhiteSpace(t.Description)), "S1 工具描述不应为空(否则 LLM 无从选型)");

        foreach (var descriptor in tools.Tools)
        {
            // 多行 schema 必须能被当作 JSON 字符串字段往返(见 WorkerMessages 的编帧纪律第 1 条)
            if (string.IsNullOrWhiteSpace(descriptor.ParametersJson))
            {
                failures.Add($"S1 工具 {descriptor.Name} 的 ParametersJson 为空");
                continue;
            }

            if (!IsJsonObject(descriptor.ParametersJson))
            {
                failures.Add($"S1 工具 {descriptor.Name} 的 ParametersJson 不是合法 JSON 对象(多行 schema 必须嵌套转义)");
            }
        }

        // 1.3 工具调用: 内容正确 + 卡片详情按具体类型切出来
        var response = await transport.CallToolAsync(
            new WorkerToolCallRequest
            {
                CallId = NewCallId(),
                Name = "read_file",
                Arguments = ProbeArguments,
                SessionId = SelfCheckSessionId
            },
            ct).ConfigureAwait(false);

        Check(!response.IsError, $"S1 read_file 不应失败({response.Content})");
        Check(response.Content.Contains(ProbeLines[0], StringComparison.Ordinal),
            "S1 read_file 的文本结果应包含探针首行");
        Check(response.DetailType == "fileRead", $"S1 卡片判别符应为 fileRead(实际 {response.DetailType ?? "null"})");
        Check(!string.IsNullOrWhiteSpace(response.DetailJson), "S1 卡片详情 JSON 不应为空");
        Check(response.DetailJson?.Contains('\n') == true,
            "S1 卡片详情应含裸换行(AppJsonContext 开了 WriteIndented) —— 这正是它必须嵌套成 JSON 字符串字段的原因");

        // 1.4 关闭语义: 释放后所有请求入口都必须抛 ObjectDisposedException,
        // 否则"已释放的传输继续干活"会比抛异常更难排查。
        await transport.ShutdownAsync(ct).ConfigureAwait(false);
        await transport.DisposeAsync().ConfigureAwait(false);
        Check(ThrowsObjectDisposed(() => transport.SyncToolsAsync(
                new WorkerToolsSyncRequest { CoreTools = true }, ct)),
            "S1 释放后的传输应拒绝新请求(抛 ObjectDisposedException)");
        Check(ThrowsObjectDisposed(() => transport.CallToolAsync(
                new WorkerToolCallRequest { CallId = NewCallId(), Name = "read_file" }, ct)),
            "S1 释放后的传输应拒绝工具调用(抛 ObjectDisposedException)");
        Check(transport.IsConnected,
            "S1 释放后 IsConnected 刻意保持 true(内联没有可重连的对端, 翻 false 只会诱发注定失败的重试)");

        FileReadDetail? decoded = null;
        if (response.DetailType is not null && response.DetailJson is not null)
        {
            try
            {
                decoded = ToolCardDetailCodec.Deserialize(response.DetailType, response.DetailJson) as FileReadDetail;
            }
            catch (Exception ex)
            {
                failures.Add($"S1 卡片详情解码失败({ex.GetType().Name}: {ex.Message})");
            }
        }

        return (new InlineProbe(response, decoded), failures);
    }

    // ── S2: 未知工具名 → 软失败, 不得抛异常 ────────────────────────────────────

    private static async Task<List<string>> CheckUnknownToolAsync(
        ToolRegistry registry, ToolContext ctx, CancellationToken ct)
    {
        var failures = new List<string>();

        await using var transport = CreateInline(registry, ctx);
        const string unknownName = "selfcheck__no_such_tool";

        WorkerToolCallResponse? unknown = null;
        try
        {
            unknown = await transport.CallToolAsync(
                new WorkerToolCallRequest { CallId = NewCallId(), Name = unknownName, Arguments = "{}" },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ⚠ 这是本场景要抓的核心失效: "对端违约"抛出去会被上层当成**传输故障**,
            // 于是引擎把一个 Worker 判成断线并降级/重拉 —— 实际只是这次调用拼错了一个名字。
            failures.Add($"S2 未知工具抛出了异常({ex.GetType().Name}: {ex.Message}), 应软失败为 IsError=true");
        }

        if (unknown is not null)
        {
            if (!unknown.IsError)
            {
                failures.Add("S2 未知工具必须返回 IsError=true(软失败), 不得被当成成功");
            }

            if (!unknown.Content.Contains(unknownName, StringComparison.Ordinal))
            {
                failures.Add("S2 未知工具的错误文案应写明是哪个工具名(否则 LLM 无法自我纠正)");
            }
        }

        // 软失败的另一半: 一次违约不得毒化后续调用。若这里也失败, 说明失败被当成了断线。
        var after = await transport.CallToolAsync(
            new WorkerToolCallRequest
            {
                CallId = NewCallId(),
                Name = "read_file",
                Arguments = ProbeArguments,
                SessionId = SelfCheckSessionId
            },
            ct).ConfigureAwait(false);

        if (after.IsError)
        {
            failures.Add($"S2 未知工具调用之后, 同一条连接上的 read_file 应当仍可用(实际: {after.Content})");
        }

        return failures;
    }

    // ── S3: 取消传播(全仓铁律的验证点) ────────────────────────────────────────

    private static async Task<List<string>> CheckCancellationAsync(
        ToolRegistry registry, ToolContext ctx, CancellationToken ct)
    {
        var failures = new List<string>();

        // 用一个"永远阻塞到被取消"的工具: 它能区分两种实现差异 ——
        // ① 令牌直连到工具(正确的那个), ② 只放弃等待而工具仍在跑(那个 bug)。
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        InlineToolExecutor blocking = async (_, token) =>
        {
            entered.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                observed.TrySetResult(true);
                throw;
            }

            return new WorkerToolCallResponse { Content = "自检: 阻塞工具不应返回" };
        };

        await using var transport = CreateInline(registry, ctx, blocking);

        using var cts = new CancellationTokenSource();
        var pending = transport.CallToolAsync(
            new WorkerToolCallRequest { CallId = NewCallId(), Name = "selfcheck_blocking" }, cts.Token);

        if (await Task.WhenAny(entered.Task, Task.Delay(ProbeTimeout, ct)).ConfigureAwait(false) != entered.Task)
        {
            failures.Add("S3 阻塞工具没有被执行(自检自身的等待超时)");
            return failures;
        }

        cts.Cancel();

        try
        {
            await pending.ConfigureAwait(false);
            failures.Add("S3 取消后调用不应正常返回(一个无限阻塞的工具只可能被取消终结)");
        }
        catch (OperationCanceledException)
        {
            // 期望路径: 取消以 OCE 结束, 绝不能降级成别的异常或 ToolResult.Error。
        }
        catch (Exception ex)
        {
            failures.Add($"S3 取消以 {ex.GetType().Name} 结束, 应为 OperationCanceledException: {ex.Message}");
        }

        // ⚠ 比"调用方以 OCE 结束"更强的一条: 令牌必须**直连到真正干活的那段代码**。
        // 只放弃等待(把 Task 扔在后台继续跑)也能让上面那条断言通过, 但那正是"停止按钮只停住主循环、
        // 子代理进程继续跑"的事故形态, 所以这里额外验证执行器真的观察到了取消。
        if (await Task.WhenAny(observed.Task, Task.Delay(ProbeTimeout, ct)).ConfigureAwait(false) != observed.Task)
        {
            failures.Add("S3 取消令牌没有直连到工具执行(执行器未观察到取消) —— 这正是「停止按钮只停住主循环、子代理进程继续跑」的事故形态");
        }

        // 取消是一次调用的事故, 不是连接的事故: 之后同一条连接必须照常可用。
        return failures;
    }

    // ── S4: 管道传输(仅当 Worker 可执行文件可定位; 未命中则跳过) ────────────────

    private static async Task<List<string>> CheckPipeAsync(
        string workDir, List<string> notes, CancellationToken ct)
    {
        var failures = new List<string>();

        var location = WorkerLocator.Locate();
        if (!location.Found)
        {
            // 跳过不是失败: "没构建 Worker 产物"是本方案允许的降级态(工具照常在主进程跑)。
            // 但必须让用户知道**这次没验到跨进程那条路** —— 那才是最需要被看见的缺口。
            notes.Add($"管道自检已跳过(未定位到 Worker 产物, 走进程内降级): {location.Source} — {location.Detail}");
            return failures;
        }

        var exe = location.ExecutablePath!;
        var hello = new WorkerHelloRequest
        {
            ProtocolVersion = WorkerProtocol.ProtocolVersion,
            SessionId = SelfCheckSessionId,
            WorkDir = workDir,
            WorkspaceRoot = workDir,
            ParentPid = Environment.ProcessId
        };

        IToolTransport? transport = null;
        var workerPid = 0;
        var disposed = false;

        try
        {
            // Start 失败会抛 InvalidOperationException, 由下面的 catch 收敛成一条失败项。
            var pipe = PipeTransport.Start(exe, hello);
            transport = pipe;

            // ⚠ 刻意**不断言** Start 之后 IsConnected 为 true: 该属性的文档契约是
            // 「握手成功且此后没有断线」, Start 不含握手, 所以此刻为 false 是**正确**的。
            // 而且严格更安全: 若它在握手前就报 true, WorkerProxyTool 的前置检查会放行调用,
            // Worker 回一个含糊的「尚未完成 worker/hello」, 比「传输不可用」难排查得多。
            // 这里只断言「不是已断线的终态」——用 Faulted 未触发来间接确认。
            var response = await pipe.HandshakeAsync(hello, ct).ConfigureAwait(false);
            workerPid = response.WorkerPid;

            // 握手之后必须为 true: 这是 WorkerPool 把句柄发给代理工具前的最后一道状态。
            if (!pipe.IsConnected)
            {
                failures.Add("S4 握手成功后 IsConnected 应为 true");
            }

            if (!response.Ok) failures.Add($"S4 握手应返回 Ok=true(实际: {response.Error})");
            if (response.WorkerPid <= 0) failures.Add("S4 握手上报的 WorkerPid 应为正数");
            if (response.ProtocolVersion != WorkerProtocol.ProtocolVersion)
            {
                // 协议里的枚举以数字跨进程传递, 版本错位是静默的 —— 宁可当场断开。
                failures.Add($"S4 协议版本不符: Worker={response.ProtocolVersion}, 本进程={WorkerProtocol.ProtocolVersion}");
            }

            var tools = await pipe.SyncToolsAsync(
                new WorkerToolsSyncRequest { CoreTools = true, SubagentVisible = false, PlanMode = false },
                ct).ConfigureAwait(false);

            if (tools.Tools.Count == 0)
            {
                failures.Add("S4 Worker 侧工具清单为空(CoreTools=true 时至少应有固定工具)");
            }

            if (!tools.Tools.Any(t => t.Name == "read_file"))
            {
                failures.Add("S4 Worker 侧工具清单缺少 read_file");
            }

            foreach (var descriptor in tools.Tools.Where(t => t.Name == "read_file"))
            {
                if (!IsJsonObject(descriptor.ParametersJson))
                {
                    failures.Add("S4 read_file 的 ParametersJson 跨进程后不是合法 JSON 对象(多行 schema 未正确嵌套转义)");
                }
            }

            var read = await pipe.CallToolAsync(
                new WorkerToolCallRequest
                {
                    CallId = NewCallId(),
                    Name = "read_file",
                    Arguments = ProbeArguments,
                    SessionId = SelfCheckSessionId
                },
                ct).ConfigureAwait(false);

            if (read.IsError)
            {
                failures.Add($"S4 跨进程 read_file 失败: {read.Content}");
            }
            else if (!read.Content.Contains(ProbeLines[0], StringComparison.Ordinal))
            {
                failures.Add("S4 跨进程 read_file 的内容不是探针首行(UTF-8 或路径基准有问题)");
            }

            if (read.DetailType != "fileRead")
            {
                failures.Add($"S4 跨进程卡片判别符应为 fileRead(实际 {read.DetailType ?? "null"})");
            }
            else if (ToolCardDetailCodec.Deserialize(read.DetailType, read.DetailJson) is not FileReadDetail decoded
                     || decoded.TotalLines != ProbeLines.Length)
            {
                failures.Add("S4 跨进程卡片详情解码后类型或行数不对");
            }

            // 跨进程的"对端违约"同样必须软失败: 抛出去会让父进程侧把它当成 Worker 崩溃。
            WorkerToolCallResponse? unknown = null;
            try
            {
                unknown = await pipe.CallToolAsync(
                    new WorkerToolCallRequest
                    {
                        CallId = NewCallId(),
                        Name = "selfcheck__no_such_tool",
                        Arguments = "{}",
                        SessionId = SelfCheckSessionId
                    },
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add($"S4 未知工具调用抛出异常({ex.GetType().Name}: {ex.Message}), 应软失败为 IsError=true");
            }

            if (unknown is not null && !unknown.IsError)
            {
                failures.Add("S4 未知工具必须返回 IsError=true(不得被当成成功)");
            }

            // 一次软失败不得被误判成断线, 否则一次拼错工具名就会把整个 Worker 换掉。
            if (!pipe.IsConnected)
            {
                failures.Add("S4 未知工具软失败后连接仍应可用(软失败不得被当成传输故障)");
            }

            await pipe.ShutdownAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ⚠ 管道段整体兜住: Worker 起不来 / 版本不符 / 管道崩了, 都是**一条失败项**,
            // 绝不能让"环境里没装 Worker"这种正常状态把整个 doctor 搞崩(那会连累其它检查项的输出)。
            failures.Add($"S4 管道自检异常({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            if (transport is not null)
            {
                try
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                    disposed = true;
                }
                catch (Exception ex)
                {
                    failures.Add($"S4 释放管道传输失败({ex.GetType().Name}: {ex.Message})");
                }
            }

            // ⚠ 退出断言只做一次, 且**先于**探活轮询: DisposeAsync 内部已经"关 stdin → 有界等待 → 强杀 → 再等"
            // (见 PipeTransport.DisposeAsync), 到这里进程理应已退出, 探活通常一次就过。
            // 反过来(先轮询再 Dispose)会造出一个纯属自检噪音的长轮询窗口。
            if (disposed && workerPid > 0)
            {
                if (!await WaitProcessExitAsync(workerPid, ct).ConfigureAwait(false))
                {
                    failures.Add($"S4 关闭后 Worker 进程(pid={workerPid})仍未退出(残留进程会一直占着工作目录)");
                }
            }
        }

        return failures;
    }

    // ── S5: 跨进程卡片编解码(复用 S1 的产物) ───────────────────────────────────

    private static List<string> CheckCardCodec(InlineProbe probe)
    {
        var failures = new List<string>();

        if (probe.Response is null)
        {
            failures.Add("S5 缺少 S1 的产物, 无法验证卡片编解码");
            return failures;
        }

        var response = probe.Response;

        // 5.1 判别符 → 具体类型 → 载荷, 三者必须自洽。
        if (probe.Decoded is null)
        {
            failures.Add("S5 卡片详情未能还原为 FileReadDetail");
            return failures;
        }

        if (!string.Equals(probe.Decoded.Content, ProbeBody, StringComparison.Ordinal))
        {
            failures.Add("S5 卡片详情的内容与探针不一致(行数/换行处理有偏差)");
        }

        if (probe.Decoded.TotalLines != ProbeLines.Length)
        {
            failures.Add($"S5 卡片详情的总行数应为 {ProbeLines.Length}(实际 {probe.Decoded.TotalLines})");
        }

        if (probe.Decoded.Truncated)
        {
            failures.Add("S5 三行探针文件不应被标记为截断");
        }

        // 5.2 DTO 往返: 详情载荷是"多行 JSON 塞进字符串字段", 必须验证它在真实的
        // 源生成序列化下原样往返 —— 这是 NDJSON 一行一帧纪律成立的前提。
        string json;
        try
        {
            json = JsonSerializer.Serialize(response, AppJsonContext.Default.WorkerToolCallResponse);
        }
        catch (Exception ex)
        {
            failures.Add($"S5 响应 DTO 序列化失败({ex.GetType().Name}: {ex.Message})");
            return failures;
        }

        if (!json.Contains("\\n", StringComparison.Ordinal))
        {
            failures.Add("S5 序列化结果里找不到转义换行 —— 多行载荷必须作为 JSON 字符串字段嵌套, 否则帧会被读行端切成两半");
        }

        WorkerToolCallResponse? back = null;
        try
        {
            back = JsonSerializer.Deserialize(json, AppJsonContext.Default.WorkerToolCallResponse) as WorkerToolCallResponse;
        }
        catch (Exception ex)
        {
            failures.Add($"S5 响应 DTO 反序列化失败({ex.GetType().Name}: {ex.Message})");
        }

        if (back is null)
        {
            failures.Add("S5 响应 DTO 往返后为 null");
            return failures;
        }

        if (back.DetailType != response.DetailType || back.DetailJson != response.DetailJson)
        {
            failures.Add("S5 响应 DTO 往返后判别符或详情载荷发生漂移");
        }

        // 5.3 未知判别符必须当场炸(契约违例), 否则卡片会"退化但看不出错"。
        try
        {
            ToolCardDetailCodec.Deserialize("selfcheck__unknown_detail", "{}");
            failures.Add("S5 未知判别符应当抛异常(契约违例必须当场暴露, 不能静默降级)");
        }
        catch (InvalidOperationException)
        {
            // 期望路径
        }
        catch (Exception ex)
        {
            failures.Add($"S5 未知判别符应抛 InvalidOperationException, 实际 {ex.GetType().Name}");
        }

        // 5.4 无详情是合法形态(null ⇔ null), 不能被误判成失败。
        if (ToolCardDetailCodec.Deserialize(null, null) is not null)
        {
            failures.Add("S5 无详情(null/null)应还原为 null");
        }

        return failures;
    }

    // ── S6: 跨进程身份键一致性 ───────────────────────────────────────────────

    private static List<string> CheckWorkerKey(string workDir, List<string> notes)
    {
        var failures = new List<string>();

        string key;
        try
        {
            key = WorkerProtocol.MakeWorkerKey(workDir, SelfCheckSessionId);
        }
        catch (Exception ex)
        {
            failures.Add($"S6 MakeWorkerKey 抛异常({ex.GetType().Name}: {ex.Message})");
            return failures;
        }

        if (key != WorkerProtocol.MakeWorkerKey(workDir, SelfCheckSessionId))
        {
            failures.Add("S6 MakeWorkerKey 对同一输入不稳定");
        }

        if (key.Length != 16 || key.Any(c => !char.IsAsciiDigit(c) && (c < 'a' || c > 'f')))
        {
            failures.Add($"S6 WorkerKey 应为 16 位小写十六进制(实际 \"{key}\")");
        }

        if (key == WorkerProtocol.MakeWorkerKey(workDir, SelfCheckSessionId + "-other"))
        {
            failures.Add("S6 不同会话必须得到不同的 WorkerKey(否则会话间会共用同一个 Worker)");
        }

        // 6.1 尾分隔符与 . / .. 归一: 这两条是 MakeWorkerKey 内部 Normalize 的既定语义,
        // 且 WorkerHelloRequest.WorkDir 的文档明确承诺"两端带不带尾斜杠算出同一个 key"。
        if (key != WorkerProtocol.MakeWorkerKey(TrailingSeparator(workDir), SelfCheckSessionId))
        {
            failures.Add("S6 带尾分隔符的 workDir 应算同一个 WorkerKey(Normalize 未收敛尾分隔符)");
        }

        var dotted = Path.Combine(workDir, "sub", "..");
        if (key != WorkerProtocol.MakeWorkerKey(dotted, SelfCheckSessionId))
        {
            failures.Add("S6 含 . / .. 的 workDir 应算同一个 WorkerKey(Normalize 未做路径归一)");
        }

        // 6.2 大小写: **不硬编码平台语义**, 而是向 WorkerDirectoryGroup.PathComparer 实测该平台
        // 是否折叠大小写, 再按实测结论给断言。
        //
        // 关键事实(读代码得出, 见 WorkspaceResolver.Normalize): Normalize 只做 GetFullPath + 去尾分隔符,
        // **不折叠大小写**; 而分组字典的键比较在 Windows/macOS 上是 OrdinalIgnoreCase。
        // 于是两个口径天然不一致: 同一目录的大小写变体会落进同一个组(同一 sessionId 复用同一槽位,
        // 只会 spawn 一次), 但算出两个不同的 workerKey。后果仅限诊断/日志前缀(该 key 不参与寻址),
        // 所以判为"说明"而不是失败 —— 但它必须被记下来: 将来若有人改成"按 workerKey 建索引",
        // 这条口径差就会立刻变成"认不出自己的 Worker"。
        var caseFoldedGrouping = WorkerDirectoryGroup.PathComparer.Equals(
            "\u0001SelfCheckKeyProbe\u0001", "\u0001selfcheckkeyprobe\u0001");

        var swapped = SwapFirstLetterCase(workDir);
        if (swapped is null)
        {
            notes.Add("未能构造 workDir 的大小写变体(路径里没有 ASCII 字母), 跳过大小写口径检查");
            return failures;
        }

        var sameKey = key == WorkerProtocol.MakeWorkerKey(swapped, SelfCheckSessionId);
        if (caseFoldedGrouping && !sameKey)
        {
            notes.Add("已知口径差: 本平台分组比较器折叠大小写, 而 MakeWorkerKey 只经 Normalize(不折叠), " +
                      "同一目录的大小写变体会得到不同的 worker key —— 该 key 仅用于诊断/日志前缀(寻址走组内槽位), 故不影响功能");
        }
        else if (!caseFoldedGrouping && sameKey)
        {
            failures.Add("S6 大小写敏感平台上, 大小写不同的目录算出了同一个 WorkerKey(不同会话会共用同一个 Worker)");
        }

        return failures;
    }

    // ── S7: 进程池降级路径(不 spawn 任何进程) ─────────────────────────────────

    private static async Task<List<string>> CheckPoolAsync(
        ToolRegistry registry, string workDir, CancellationToken ct)
    {
        var failures = new List<string>();

        void Check(bool ok, string name)
        {
            if (!ok) failures.Add(name);
        }

        // PipeEnabled=false 是池自带的"分步验证"开关(SpawnAsync 第一句就是它):
        // 走这条路径可以在**不起任何进程**的前提下把「工厂委托 → Acquire → 降级记账 → GetHealth → Release」
        // 整条链验掉。它验证的正是整个方案的最后一道兜底: Worker 不可用时工具必须照常能跑,
        // 且降级必须可见(WorkerPool 的核心承诺)。
        IToolTransport InlineFactory(string sessionId, string dir, string workspaceRoot,
            string? commanderPersonaText, IReadOnlyList<AgentRosterEntry>? rosterEntries, bool planMode)
            => CreateInline(registry, new ToolContext
            {
                WorkspaceRoot = dir,
                SessionId = sessionId,
                IsPlanMode = planMode,
                CommanderPersonaText = commanderPersonaText,
                RosterEntries = rosterEntries
            });

        var pool = new WorkerPool(
            // ⚠ 必须用纯内存解析器: 生产实现 GitWorkspaceResolver 会起 2 个 git 进程,
            // 而自检的副作用纪律禁止 spawn 除 Worker 之外的进程(临时目录也不是 git 仓库, 解析没有意义)。
            new SelfCheckResolver(),
            InlineFactory,
            // liveSessionProvider 传 null → 不起对账后台循环(见 WorkerPool.StartReconcileLoop),
            // 于是 DisposeAsync 之后不留任何线程。
            liveSessionProvider: null,
            options: new WorkerPoolOptions { PipeEnabled = false });

        try
        {
            var client = await pool.AcquireAsync(
                SelfCheckSessionId, workDir, null, null, false, ct).ConfigureAwait(false);

            // 池的纪律: Acquire 除调用方取消外不抛任何异常, 返回的句柄一定是"可用的"。
            Check(client.IsConnected, "S7 管道关闭时 Acquire 必须返回可用的内联句柄");
            Check(client.WorkerKey == WorkerProtocol.MakeWorkerKey(workDir, SelfCheckSessionId),
                "S7 内联句柄的 WorkerKey 必须与 MakeWorkerKey(workDir, sessionId) 同源");

            var health = pool.GetHealth();
            var entry = health.Entries.FirstOrDefault(e => e.SessionId == SelfCheckSessionId);
            Check(entry is not null, "S7 GetHealth 应列出该会话的槽位");
            if (entry is not null)
            {
                Check(entry.Mode == WorkerMode.Inline,
                    $"S7 槽位模式应为 Inline(实际 {entry.Mode}) —— 降级必须可见");
                Check(!string.IsNullOrWhiteSpace(entry.LastSpawnError),
                    "S7 降级必须留痕: LastSpawnError 不应为空, 否则用户只会看到「最近变慢了」");
                Check(entry.WorkerKey == client.WorkerKey, "S7 健康快照里的 WorkerKey 应与句柄一致");
            }

            // 降级路径也必须能真的干活: 否则"Worker 不可用 → 工具照常"只是一句注释。
            var tools = await pool.SyncToolsAsync(client, true, false, false, null, ct).ConfigureAwait(false);
            Check(tools.Tools.Any(t => t.Name == "read_file"), "S7 降级路径的工具清单应包含 read_file");

            var call = await client.CallToolAsync(
                new WorkerToolCallRequest
                {
                    CallId = NewCallId(),
                    Name = "read_file",
                    Arguments = ProbeArguments,
                    SessionId = SelfCheckSessionId
                },
                ct).ConfigureAwait(false);

            Check(!call.IsError && call.Content.Contains(ProbeLines[0], StringComparison.Ordinal),
                $"S7 降级路径的 read_file 应正常返回探针内容(实际: {call.Content})");

            Check(pool.GroupCount == 1, "S7 持有会话时应有 1 个目录组");

            // L3: 引用计数归零 → 摘除该目录的全部 Worker(此处是内联句柄, 但记账必须归零)。
            pool.Release(SelfCheckSessionId, workDir);
            Check(pool.GroupCount == 0, "S7 Release 之后目录组应被摘除(L3)");
        }
        catch (OperationCanceledException)
        {
            throw; // 调用方取消原样上抛, 由 RunCoreAsync 收敛
        }
        catch (Exception ex)
        {
            // 池的纪律是"生命周期代码不抛"; 真抛了就是缺陷, 但仍收敛成一条失败项。
            failures.Add($"S7 进程池自检异常({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            await pool.DisposeAsync().ConfigureAwait(false);
        }

        return failures;
    }

    // ── S8: 空闲回收(只摘句柄, 保留槽位与目录组) ──────────────────────────────

    /// <summary>
    /// S8: 连续空闲超过阈值后, 该 (目录, 会话) 的 Worker <b>句柄</b>应被摘下,
    /// 而<b>槽位与目录组必须留着</b>(会话还在, 下次 Acquire 走懒重建)。
    /// </summary>
    private static async Task<List<string>> CheckIdleReclaimAsync(
        ToolRegistry registry, string workDir, CancellationToken ct)
    {
        var failures = new List<string>();
        var workerKey = WorkerProtocol.MakeWorkerKey(workDir, SelfCheckSessionId);

        void Check(bool ok, string name)
        {
            if (!ok) failures.Add(name);
        }

        await using var pool = CreateIdleProbePool(registry, IdleProbeTimeout);

        try
        {
            var client = await pool.AcquireAsync(SelfCheckSessionId, workDir, null, null, false, ct)
                .ConfigureAwait(false);

            // 前提断言: 下面每一条"应当被回收"的判断都以"此刻确实空闲"为前提。
            // 前提不成立时它们会**假通过**(忙着的句柄当然不会被回收), 所以必须先把前提钉住。
            Check(client.ActiveCallCount == 0 && !client.IsBusy,
                "S8 刚 Acquire 到的句柄应当是空闲的(前提不成立时本组断言全是假通过)");
            Check(ReferenceEquals(FindPoolClient(pool, workerKey), client),
                "S8 Acquire 之后槽位应持有刚拿到的那个句柄(否则后面验的不是它)");

            await Task.Delay(IdleProbeWait, ct).ConfigureAwait(false);
            await pool.ReconcileNowAsync(ct).ConfigureAwait(false);

            // 8.1 挡住"空闲回收根本没接线": 症状是每开一个会话就常驻一份独立运行时,
            //     而所有报表(健康快照/状态栏)都完全正常, 没有任何报错。
            Check(FindPoolClient(pool, workerKey) is null,
                "S8 空闲超过阈值并对账后, 槽位里的 Worker 句柄应被摘下(否则空闲回收完全没生效: 每开一个会话白养一份运行时)");

            var entry = pool.GetHealth().Entries.FirstOrDefault(e => e.SessionId == SelfCheckSessionId);

            // 8.2 ⚠ 槽位必须还在 —— 这是"槽位级空闲回收"与 Reconcile("会话已不存在 → 摘整个槽位")的
            //     关键区别。槽位被误删的后果是连锁的: 会话被当成不存在 → 组计数归零 → 组被 L3 退役 →
            //     下次 Acquire 重新挂载并新建一个组 —— 而"目录里明明还有会话"这件事在任何报表上
            //     都看不出来, 只会表现为 Worker 莫名其妙地反复重启。
            Check(entry is not null,
                "S8 空闲回收只应摘 Worker 句柄, 不应摘掉会话槽位(条目消失说明与 Reconcile 的「会话已不存在」混为一谈, 会连带把目录组误退役)");

            if (entry is not null)
            {
                Check(!entry.IsConnected, "S8 回收之后健康快照里的 IsConnected 应为 false");

                // 空闲回收只该摘句柄, 不该动降级痕迹。这两条与传输类型无关, 是"可见性"底线:
                // 抹掉它们之后, 一个**永久降级**(工具一直在主进程里跑)的会话会在空闲回收后
                // 翻成「未连接」, 用户与 doctor 都无从知道真相。
                Check(!string.IsNullOrWhiteSpace(entry.LastSpawnError),
                    "S8 空闲回收不得抹掉降级原因 LastSpawnError(抹掉后用户只剩「未连接」, 看不到工具其实一直在主进程里跑)");

                // ⚠ 本组跑的是 PipeEnabled=false 的槽位, 它**本来就**是 Degraded, 所以句柄被摘后
                //   Mode 仍是 Inline; 生产里被空闲回收的是**已连上**的管道句柄, 那时 Mode 是 NotConnected
                //   —— 两者都只表示"此刻没有存活实例"。这条断言真正挡的是: 空闲回收顺手把 Degraded 清掉,
                //   于是永久降级的会话也跟着翻成 NotConnected(降级可见性被抹掉)。
                Check(entry.Mode == WorkerMode.Inline,
                    "S8 空闲回收不得抹掉「已降级」标记(否则永久降级的会话在空闲回收后会被谎报为「未连接」)");
            }

            Check(pool.GroupCount == 1, "S8 空闲回收不得退役目录组(会话还在, 组归零即误退役)");

            // 8.3 回收之后再 Acquire: 必须"懒重建"出一个可用句柄, 且仍落进**同一个**组。
            //     这条同时挡住"组被摘掉后重建出第二个组"(GroupCount 变 2)与"回收之后再也拿不到句柄"。
            var again = await pool.AcquireAsync(SelfCheckSessionId, workDir, null, null, false, ct)
                .ConfigureAwait(false);
            Check(again.IsConnected, "S8 回收之后再次 Acquire 必须拿到可用句柄(空闲回收后是懒重建, 不是失效)");
            Check(pool.GroupCount == 1,
                "S8 回收后重新 Acquire 仍应落进同一个目录组(变成 2 说明组被误退役后又重建了一个)");

            // 8.4 幂等: 对账是常驻循环(默认 15s 一轮), 重复调用必须无害。
            //     少这两句的话, "重复对账把目录组摘掉"这类缺陷会一路活到生产才炸。
            await pool.ReconcileNowAsync(ct).ConfigureAwait(false);
            await pool.ReconcileNowAsync(ct).ConfigureAwait(false);
            Check(pool.GroupCount == 1, "S8 重复对账应幂等(不应把目录组摘掉)");
            Check(pool.GetHealth().Entries.Any(e => e.SessionId == SelfCheckSessionId),
                "S8 重复对账之后槽位应仍在(幂等的另一半)");
        }
        catch (OperationCanceledException)
        {
            throw; // 调用方取消原样上抛, 由 RunCoreAsync 收敛
        }
        catch (Exception ex)
        {
            failures.Add($"S8 空闲回收自检异常({ex.GetType().Name}): {ex.Message}");
        }

        return failures;
    }

    // ── S9: 有在飞调用时绝不回收(正确性底线) ──────────────────────────────────

    /// <summary>
    /// S9: 有<b>在飞工具调用</b>时, 空闲回收必须跳过该槽位。本组是整个空闲回收功能的正确性底线,
    /// 不是性能项。
    /// </summary>
    private static async Task<List<string>> CheckInFlightNoReclaimAsync(
        ToolRegistry registry, string workDir, CancellationToken ct)
    {
        var failures = new List<string>();
        var workerKey = WorkerProtocol.MakeWorkerKey(workDir, SelfCheckSessionId);

        void Check(bool ok, string name)
        {
            if (!ok) failures.Add(name);
        }

        // ⚠⚠ 构造"在飞调用"的方式(本组最容易写错的地方, 复核请看这里):
        //   把内联传输的**执行器**换成一道受控的门 —— 被调用即说明 WorkerClient.CallToolAsync
        //   已经进到传输层(NotifyCallStarted 已执行), 而它 await 的那个 Task 由 release 把闸,
        //   于是这次调用真的**悬在半空**直到我们放行。
        //   为什么必须真悬空: 空闲判据读的是 client.ActiveCallCount, 调用一旦返回计数就归零,
        //   那时它与"空闲"不可区分 —— 断言会假通过。而生产事故(子代理合法跑 30 分钟, 在第 1 分钟
        //   被当成空闲杀掉)恰恰**只**发生在悬空的那段时间里, 悬空之外复现不出来。
        //   为什么能塞进内联传输: IdleProbeTimeout 场景里 PipeEnabled=false, Acquire 必然走
        //   CreateInlineClient → 用的就是下面这个 executor 委托(池的降级路径本来就靠它执行工具)。
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WorkerToolCallResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        InlineToolExecutor gated = async (_, _) =>
        {
            entered.TrySetResult(true);
            return await release.Task.ConfigureAwait(false);
        };

        await using var pool = CreateIdleProbePool(registry, IdleProbeTimeout, gated);
        Task<WorkerToolCallResponse>? pending = null;

        try
        {
            var client = await pool.AcquireAsync(SelfCheckSessionId, workDir, null, null, false, ct)
                .ConfigureAwait(false);

            // 刻意不 await: 这就是"在飞"本身。
            pending = client.CallToolAsync(
                new WorkerToolCallRequest
                {
                    CallId = NewCallId(),
                    Name = "read_file",
                    Arguments = ProbeArguments,
                    SessionId = SelfCheckSessionId
                },
                ct);

            // 等执行器真的进了门再往下走: 否则"在飞"可能只是"我调了方法", 而 NotifyCallStarted
            // 还没执行 —— 那一刻 ActiveCallCount 仍是 0, 后面整组断言都会假通过。
            if (await Task.WhenAny(entered.Task, Task.Delay(ProbeTimeout, ct)).ConfigureAwait(false) != entered.Task)
            {
                failures.Add("S9 受控执行器没有被进入(在飞状态没成立, 本组断言无意义)");
                return failures;
            }

            Check(client.ActiveCallCount == 1,
                $"S9 在飞调用期间 ActiveCallCount 应为 1(实际 {client.ActiveCallCount})");
            Check(client.IsBusy, "S9 在飞调用期间 IsBusy 应为 true");

            await Task.Delay(IdleProbeWait, ct).ConfigureAwait(false);
            await pool.ReconcileNowAsync(ct).ConfigureAwait(false);

            // ⚠ 失败即事故, 文案里写清后果: 一个合法跑 30 分钟的子代理在第 1 分钟被当成空闲杀掉 →
            //   Worker 被关 → 子代理进程树变孤儿(没人再给它发 cancel) → 它持有的仓库 git 写锁
            //   要拖到超时才释放, 而用户在界面上只看到"这一回合莫名其妙停了"。
            Check(ReferenceEquals(FindPoolClient(pool, workerKey), client),
                "S9 有在飞工具调用时 Worker 被当成空闲回收了 —— 后果是子代理进程变孤儿, 且它持有的 git 写锁要拖到超时才释放");
            Check(client.IsBusy,
                "S9 对账之后这次调用仍应在飞(计数被提前清零会让 IsBusy 这个判据整体失效)");
        }
        catch (OperationCanceledException)
        {
            throw; // 调用方取消原样上抛, 由 RunCoreAsync 收敛
        }
        catch (Exception ex)
        {
            failures.Add($"S9 在飞调用自检异常({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            // ⚠ 必须无条件收尾: InlineTransport.DisposeAsync **刻意不打断**在飞调用(它没有那些调用的
            //   句柄, 见它的 remarks), 于是没人放闸的话 pending 会永远挂着 —— 既会拖住后续场景,
            //   也会把"自检卡住"变成下一次 doctor 的症状。顺序也重要: 先放闸再让 await using 释放池。
            release.TrySetResult(new WorkerToolCallResponse { Content = "S9 收尾: 放行在飞调用" });

            var call = pending;
            if (call is not null)
            {
                try
                {
                    // 用 Task.WhenAny 而不是直接 await: 自检的收尾自己也要有闸门,
                    // 免得"执行器没真正挂在那道门上"这类实现变更把 doctor 永久挂住。
                    if (await Task.WhenAny(call, Task.Delay(ProbeTimeout, CancellationToken.None)).ConfigureAwait(false) != call)
                    {
                        failures.Add("S9 放闸后在飞调用仍未结束(自检自身的收尾失败, 会污染后续场景)");
                    }
                    else
                    {
                        _ = await call.ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    // 收尾失败不算"被测逻辑失败"(调用方取消 / 传输已释放都会走到这里), 但也不静默: 留一条 Debug。
                    Log.Debug(Category, $"S9 收尾时在飞调用以 {ex.GetType().Name} 结束: {ex.Message}");
                }
            }
        }

        return failures;
    }

    // ── S10: 回合进行中不回收(且置 false 后必须真的能回收) ──────────────────────

    /// <summary>
    /// S10: <c>MarkTurnActive(sessionId, workDir, true)</c> 期间不参与空闲回收;
    /// 置回 <c>false</c> 之后<b>必须</b>恢复可回收。
    /// </summary>
    private static async Task<List<string>> CheckTurnActiveNoReclaimAsync(
        ToolRegistry registry, string workDir, CancellationToken ct)
    {
        var failures = new List<string>();
        var workerKey = WorkerProtocol.MakeWorkerKey(workDir, SelfCheckSessionId);

        void Check(bool ok, string name)
        {
            if (!ok) failures.Add(name);
        }

        await using var pool = CreateIdleProbePool(registry, IdleProbeTimeout);

        try
        {
            var client = await pool.AcquireAsync(SelfCheckSessionId, workDir, null, null, false, ct)
                .ConfigureAwait(false);

            pool.MarkTurnActive(SelfCheckSessionId, workDir, true);

            await Task.Delay(IdleProbeWait, ct).ConfigureAwait(false);
            await pool.ReconcileNowAsync(ct).ConfigureAwait(false);

            // 挡住"回合进行中也回收": 回合的 LLM 流式阶段恒定"没有在飞调用",
            // 那一刻这个 Worker 正被本回合独占使用 —— 被回收等于让下一个工具调用多付一次重建,
            // 更糟的是把这一回合正在用的句柄从槽位上摘走。
            Check(ReferenceEquals(FindPoolClient(pool, workerKey), client),
                "S10 回合进行中不得回收 —— 否则正在流式输出答案的这一回合会在两次工具调用之间的空档被摘掉 Worker");

            // ⚠ 这半段不能省, 否则本场景会**假通过**: 把 MarkTurnActive 整个改成 no-op 也能过上面那条,
            //   而生产里真实的事故方向恰恰相反 —— 调用方漏置 active:false(没写 try/finally),
            //   症状是"有的会话的 Worker 永远关不掉"。只有验证 false 真的解锁, 才能发现它。
            pool.MarkTurnActive(SelfCheckSessionId, workDir, false);

            await Task.Delay(IdleProbeWait, ct).ConfigureAwait(false);
            await pool.ReconcileNowAsync(ct).ConfigureAwait(false);

            Check(FindPoolClient(pool, workerKey) is null,
                "S10 MarkTurnActive(false) 之后必须真的能被空闲回收(不回收说明 false 从未生效: 生产里漏置 false 的会话其 Worker 将永远常驻)");
            Check(pool.GroupCount == 1, "S10 回收不应退役目录组(会话还在)");
        }
        catch (OperationCanceledException)
        {
            throw; // 调用方取消原样上抛, 由 RunCoreAsync 收敛
        }
        catch (Exception ex)
        {
            failures.Add($"S10 回合活跃标记自检异常({ex.GetType().Name}): {ex.Message}");
        }

        return failures;
    }

    // ── S11: 活动时间的刷新入口(实时输出走的就是它) ────────────────────────────

    /// <summary>S11: <see cref="WorkerClient.Touch"/> 必须真的刷新 <c>LastActivityTicks</c>。</summary>
    private static async Task<List<string>> CheckActivityRefreshAsync(
        ToolRegistry registry, string workDir, CancellationToken ct)
    {
        var failures = new List<string>();

        void Check(bool ok, string name)
        {
            if (!ok) failures.Add(name);
        }

        await using var pool = CreateIdleProbePool(registry, IdleProbeTimeout);

        try
        {
            var client = await pool.AcquireAsync(SelfCheckSessionId, workDir, null, null, false, ct)
                .ConfigureAwait(false);

            var before = client.LastActivityTicks;
            await Task.Delay(TouchProbeWait, ct).ConfigureAwait(false);

            // 前提: 这段时间里没有任何路径**应该**动活动时间(GetHealth / 对账都不动句柄)。
            //     若它自己变了, "Touch 有没有生效"就无从判断 —— 明确报出来而不是让下面那条随机红。
            Check(client.LastActivityTicks == before,
                "S11 静置期间活动时间不应自行变化(变了说明有别的路径在偷偷 Touch, 本条断言的前提不成立)");

            // ⚠ 为什么用 Touch 而不是去触发 ToolOutputReceived: 内联传输**刻意**不发 toolOutput 通知
            //   (见 InlineTransport 的「事件恒不触发」), 公开 API 里也没有 raise 的口子;
            //   而 WorkerClient.OnTransportToolOutput 的第一件事就是调 Touch —— 同一个方法。
            //   所以这里验的正是那条路径上的写入动作本身。
            client.Touch();

            Check(client.LastActivityTicks > before,
                "S11 Touch() 必须刷新 LastActivityTicks —— 实时输出到达时走的正是它(WorkerClient.OnTransportToolOutput); 断了它, 正在吐输出的 Worker 会在下一次吐字之前被当成空闲回收");
        }
        catch (OperationCanceledException)
        {
            throw; // 调用方取消原样上抛, 由 RunCoreAsync 收敛
        }
        catch (Exception ex)
        {
            failures.Add($"S11 活动时间刷新自检异常({ex.GetType().Name}): {ex.Message}");
        }

        return failures;
    }

    // ── S12: 空闲阈值关闭时行为不变 ───────────────────────────────────────────

    /// <summary>S12: <c>DirectoryIdleTimeout = TimeSpan.Zero</c> 时不得回收(退回「只有 L3 回收」)。</summary>
    private static async Task<List<string>> CheckIdleTimeoutDisabledAsync(
        ToolRegistry registry, string workDir, CancellationToken ct)
    {
        var failures = new List<string>();
        var workerKey = WorkerProtocol.MakeWorkerKey(workDir, SelfCheckSessionId);

        void Check(bool ok, string name)
        {
            if (!ok) failures.Add(name);
        }

        await using var pool = CreateIdleProbePool(registry, TimeSpan.Zero);

        try
        {
            var client = await pool.AcquireAsync(SelfCheckSessionId, workDir, null, null, false, ct)
                .ConfigureAwait(false);

            await Task.Delay(IdleProbeWait, ct).ConfigureAwait(false);
            await pool.ReconcileNowAsync(ct).ConfigureAwait(false);

            // 挡住"关不掉的开关": 该项是用户可见的开关(置零 = 完全关闭), 而对账循环本身照跑,
            // 于是"阈值判断写错成永远成立"会表现为关不掉 —— 一个没人能自己绕开的常驻进程。
            Check(ReferenceEquals(FindPoolClient(pool, workerKey), client),
                "S12 DirectoryIdleTimeout=TimeSpan.Zero 时不得回收(该项是开关: 关掉后必须回到「只有 L3 回收」的改造前语义)");
            Check(pool.GroupCount == 1, "S12 阈值关闭时目录组不应被摘掉");
        }
        catch (OperationCanceledException)
        {
            throw; // 调用方取消原样上抛, 由 RunCoreAsync 收敛
        }
        catch (Exception ex)
        {
            failures.Add($"S12 阈值关闭自检异常({ex.GetType().Name}): {ex.Message}");
        }

        return failures;
    }

    // ───────────────────────────── 内联侧的对端实现 ─────────────────────────────

    /// <summary>造一个内联传输。<paramref name="executor"/> 为 null 时用标准实现(跑本地注册表)。</summary>
    private static IToolTransport CreateInline(
        ToolRegistry? registry, ToolContext ctx, InlineToolExecutor? executor = null)
        => new InlineTransport(
            executor ?? ((request, token) => ExecuteInlineAsync(registry!, ctx, request, token)),
            (_, _) => Task.FromResult(new WorkerToolsResponse { Tools = ListTools(registry!) }));

    /// <summary>
    /// 标准内联执行器: 与 <c>AIShikikan.Worker</c> 的 <c>WorkerSlimHost.ExecuteAsync</c> 是<b>两份实现</b>
    /// (Core 不引用 Worker, 见 worker-architecture.md §7.1), 契约逐条对齐:
    /// 未知工具 / 参数非法 → <c>IsError=true</c>; 取消 → 原样上抛; 其余异常 → 收成 <c>IsError</c>。
    /// </summary>
    private static async Task<WorkerToolCallResponse> ExecuteInlineAsync(
        ToolRegistry registry, ToolContext ctx, WorkerToolCallRequest request, CancellationToken ct)
    {
        if (!registry.TryGet(request.Name, out var tool))
        {
            return new WorkerToolCallResponse
            {
                IsError = true,
                Content = $"未知工具: {request.Name}(当前工具数 {registry.All.Count})"
            };
        }

        JsonElement args;
        try
        {
            // Clone(): RootElement 是寄生在 JsonDocument 上的视图, 不 Clone 会在工具读到时抛 ObjectDisposedException。
            args = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(request.Arguments) ? "{}" : request.Arguments).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new WorkerToolCallResponse { IsError = true, Content = $"工具参数不是合法 JSON: {ex.Message}" };
        }

        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(args, ctx, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ⚠ 必须排在 catch(Exception) 之前(全仓铁律), 否则取消会被说成失败。
            throw;
        }
        catch (Exception ex)
        {
            return new WorkerToolCallResponse { IsError = true, Content = $"工具执行异常: {ex.Message}" };
        }

        return ToResponse(result);
    }

    /// <summary>把 <see cref="ToolResult"/> 映射成协议响应(与 WorkerSlimHost.BuildResponse 同构)。</summary>
    private static WorkerToolCallResponse ToResponse(ToolResult result)
    {
        var response = new WorkerToolCallResponse
        {
            Content = result.Content,
            IsError = result.IsError,
            StepId = result.StepId
        };

        if (result.Detail is null) return response;

        try
        {
            response.DetailType = ToolCardDetailCodec.GetTypeName(result.Detail);
            response.DetailJson = ToolCardDetailCodec.Serialize(result.Detail);
        }
        catch (Exception)
        {
            // 判别符与 [JsonDerivedType] 脱节时降级为通用文本卡, 而不是让整个会话读不出来。
            response.DetailType = null;
            response.DetailJson = null;
        }

        return response;
    }

    /// <summary>把本地注册表导出成协议声明。</summary>
    private static List<WorkerToolDescriptor> ListTools(ToolRegistry registry) =>
        registry.All.Select(t => new WorkerToolDescriptor
        {
            Name = t.Name,
            Description = t.Description,
            // 刻意转成字符串并保留原始排版: 跨进程形态就是字符串, 且多行必须靠它嵌套转义。
            ParametersJson = t.Parameters.ValueKind == JsonValueKind.Undefined ? "{}" : t.Parameters.GetRawText(),
            RequiresApproval = t.RequiresApproval,
            // ⚠ Kind / RequiresGitWrite 刻意留空: 分类规则的真源在 WorkerServer.BuildDescriptors,
            // 这里复制第二份必然漂移(症状是"某个工具分类显示错了"这种极难归因的小事),
            // 而本自检只需要证明"声明能被完整往返"。
            Kind = string.Empty,
            RequiresGitWrite = false
        }).ToList();

    // ───────────────────────────── 小工具 ─────────────────────────────

    /// <summary>
    /// 造一个「专测空闲回收」的池: 内联传输(不 spawn 任何进程)、给定的空闲阈值、
    /// <b>手动</b>对账(关掉后台循环, 让每一轮回收都由场景显式触发)。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么必须给 <c>liveSessionProvider</c>(S7 传的是 null)</b>:
    /// <see cref="WorkerPool.ReconcileNowAsync"/> 拿不到比对基准时<b>静默跳过整轮</b>(它宁可漏回收也不误回收),
    /// 于是本组场景会全部"跑完且全绿", 却什么都没验到 —— 这是最坏的一种覆盖: 看起来有断言,
    /// 实际是空转。所以这里给的集合里含本会话: "会话还在"这条前提被显式固定,
    /// 场景测的才是空闲回收, 而不是 <c>Reconcile</c> 那条「会话已不存在 → 摘整个槽位」的路径。</para>
    ///
    /// <para><b>为什么关掉后台对账循环</b>(<c>ReconcileInterval = TimeSpan.Zero</c>): 生产里节拍会被
    /// 空闲阈值拉细到「阈值/4」(本组 200ms → 撞 2s 下界), 那个后台循环会在断言中途抢先回收,
    /// 于是"是谁触发的"变成竞态、失败无法复现。这里让每一轮回收都只由 <c>ReconcileNowAsync</c> 驱动。
    /// 代价: 这个池不会自动回收"已消失的会话", 而本组场景不测那条, 所以没有覆盖损失。</para>
    ///
    /// <para><b>为什么用内联传输也等于覆盖了管道的判据</b>: <c>DetachIdleClients</c> 只读槽位字段与句柄的
    /// 活动度(<c>IsBusy</c> / <c>LastActivityTicks</c>), 与传输类型无关; 真实管道那一侧由 S4 单独覆盖
    /// (它验的是握手/编解码/退出, 不是空闲判据)。同时本自检的副作用纪律禁止在这几组场景里
    /// 额外 spawn 进程。</para>
    /// </remarks>
    private static WorkerPool CreateIdleProbePool(
        ToolRegistry registry, TimeSpan idleTimeout, InlineToolExecutor? executor = null)
    {
        IToolTransport InlineFactory(string sessionId, string dir, string workspaceRoot,
            string? commanderPersonaText, IReadOnlyList<AgentRosterEntry>? rosterEntries, bool planMode)
            => CreateInline(registry, new ToolContext
            {
                WorkspaceRoot = dir,
                SessionId = sessionId,
                IsPlanMode = planMode,
                CommanderPersonaText = commanderPersonaText,
                RosterEntries = rosterEntries
            }, executor);

        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SelfCheckSessionId };

        return new WorkerPool(
            // 同 S7: 必须用纯内存解析器(生产实现会起 2 个 git 进程)。
            new SelfCheckResolver(),
            InlineFactory,
            liveSessionProvider: () => live,
            options: new WorkerPoolOptions
            {
                PipeEnabled = false,
                DirectoryIdleTimeout = idleTimeout,
                ReconcileInterval = TimeSpan.Zero
            });
    }

    /// <summary>在池当前持有的句柄里按 worker key 找; 返回 null = 该槽位的 <c>Client</c> 已是 null。</summary>
    /// <remarks>刻意用 <see cref="WorkerPool.EnumerateClients"/> 而不是 <c>GetHealth</c>: 后者是纯数据快照,
    /// 按设计就不持句柄, 只能间接推断; 前者直接回答"槽位里此刻有没有句柄", 而且支持<b>引用相等</b>断言 ——
    /// 「还是原来那个句柄」比「有个句柄」强得多(被换成一个新句柄同样意味着当前这次调用被打断了)。</remarks>
    private static WorkerClient? FindPoolClient(WorkerPool pool, string workerKey)
    {
        foreach (var client in pool.EnumerateClients())
        {
            if (string.Equals(client.WorkerKey, workerKey, StringComparison.Ordinal)) return client;
        }

        return null;
    }

    /// <summary>自检用固定解析器: 目录即工作区根、无分支。纯内存, 不起任何进程。</summary>
    private sealed class SelfCheckResolver : IWorkspaceResolver
    {
        public string ResolveWorkTreeRoot(string dir) => GitWorkspaceResolver.Normalize(dir);

        public string? ResolveBranch(string dir) => null;
    }

    private static string NewCallId() => Guid.NewGuid().ToString("N")[..16];

    private static string TrailingSeparator(string path) => path + Path.DirectorySeparatorChar;

    private static bool IsJsonObject(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>同步抛与异步 fault 都算; 返回 false 表示"没抛 ObjectDisposedException"。</summary>
    private static bool ThrowsObjectDisposed(Func<Task> action)
    {
        try
        {
            action().GetAwaiter().GetResult();
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }

        return false;
    }

    /// <summary>把路径里最后一个 ASCII 字母的大小写翻转; 找不到字母时返回 null。</summary>
    private static string? SwapFirstLetterCase(string path)
    {
        for (var i = path.Length - 1; i >= 0; i--)
        {
            var c = path[i];
            if (c is >= 'a' and <= 'z') return path[..i] + (char)(c - 32) + path[(i + 1)..];
            if (c is >= 'A' and <= 'Z') return path[..i] + (char)(c + 32) + path[(i + 1)..];
        }

        return null;
    }

    /// <summary>轮询等某个 pid 上不再有 Worker 进程; 返回 true 表示"已退出"。
    /// 进程名的身份校验见 <see cref="IsWorkerAlive"/>, 本方法只负责等待。</summary>
    private static async Task<bool> WaitProcessExitAsync(int pid, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < ExitProbeTimeout)
        {
            if (!IsWorkerAlive(pid)) return true;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return !IsWorkerAlive(pid);
    }

    /// <summary>
    /// 该 pid 上是否还活着 Worker 进程。
    ///
    /// <para><b>为什么必须校验进程名</b>: pid 会被系统回收复用, 在自检的几秒窗口里理论上可能撞上一个
    /// 与 Worker 无关的进程, 那会报出一条"Worker 没退出"的假故障。只按进程名前缀判断是对那个窗口的
    /// 廉价防御; 前缀只取 10 个字符是因为 Linux 的 <c>/proc/&lt;pid&gt;/stat</c> comm 字段被截断到 15 字符,
    /// <c>AIShikikan.Worker</c>(16 字符)在 Linux 上会被读成 <c>AIShikikan.Worke</c>。</para>
    /// </summary>
    private static bool IsWorkerAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.StartsWith(
                WorkerLocator.WorkerExecutableStem[..10], StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false; // 进程已退出
        }
        catch (InvalidOperationException)
        {
            return false; // 已退出但句柄状态异常
        }
    }

    /// <summary>
    /// 删临时目录, 带重试。
    ///
    /// <para><b>为什么要重试</b>: Worker 子进程崩溃时可能还攥着目录里的文件句柄, 第一次删除会抛
    /// IOException/UnauthorizedAccessException。这属于"正常收尾不完全", 不该让自检整体失败 ——
    /// 但也绝不能静默: 连续失败会留一个孤儿目录, 所以最后一次失败记 Debug, 由日志承接。</para>
    /// </summary>
    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(80 * (attempt + 1));
            }
            catch (Exception ex)
            {
                Log.Debug(Category, $"清理自检临时目录失败({ex.GetType().Name}: {ex.Message}): {path}");
                return;
            }
        }

        Log.Debug(Category, $"自检临时目录未能清理, 可能有残留进程占用: {path}");
    }
}