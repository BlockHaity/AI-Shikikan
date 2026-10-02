using AIShikikan.Core.Logging;
using AIShikikan.Core.Services.Worker;

namespace AIShikikan.Worker;

/*
 * AIShikikan.Worker —— 独立 Worker 进程的入口。
 *
 * ⚠ 绝对不要在这里碰 AIShikikan.Gui: Worker 是 headless 子进程, 引用 GUI 会把 Avalonia
 *   依赖面拖进来, 也会隐式依赖 GUI 的程序集名(卫星资源 en/AIShikikan.Gui.resources.dll 由它推出)。
 *   与之对应的另一条禁令: **不要调用 CommanderRuntime.Boot()** —— 理由见 WorkerSlimHost.cs
 *   文件头的详细展开(写配置 / 扫盘 / 后台 spawn MCP 子进程)。
 *
 * ⚠ 不要照搬 GUI 的 FixupLinuxImeEnvironment(): Worker 没有窗口, 不涉及输入法;
 *   那个函数会在环境变量全缺失时 Process.Start 遍历 /proc 去探测 fcitx/ibus 守护进程,
 *   在这里纯属无谓开销(而且这段逻辑只对 Avalonia 的 X11 IME 生效)。
 *
 * ⚠ stdout 的用途: 本进程正常运行时**不往 stdout 写任何东西**(日志走文件, 见下);
 *   只有 --help 会写 stdout, 因为那条路径立即退出、且不可能有协议帧在飞。
 *   保留这份克制是为了将来若改用「stdin/stdout 重定向」当传输通道(架构文档 §3.2 里的
 *   候选之一)时不必先来清理污染。
 */
internal static class Program
{
    private const string Category = "Worker";

    /// <summary>
    /// 退出码约定: 0 正常 / 2 参数错 / 1 未处理异常。
    /// <para>刻意给「参数错」一个独立码: 父进程 spawn 后会等握手, 参数错意味着这个子进程
    /// 永远不会有响应帧, 与「Worker 起来但执行失败」必须能被区分开。</para>
    /// </summary>
    private const int ExitOk = 0;
    private const int ExitInternalError = 1;
    private const int ExitBadArguments = 2;

    public static async Task<int> Main(string[] args)
    {
        // 启动第一件事: 打进程标签, 再初始化日志。
        // 顺序不能反 —— Log.Initialize 会起后台消费线程, 标签必须在它之前定下来,
        // 否则最早那几行日志会顶着默认标签 "gui" 落盘, 而 GUI 与 Worker 共写同一个
        // 日志文件(交错后没有来源信息就只能靠猜)。
        Log.SetProcessLabel("worker");
        Log.Initialize();
        HookGlobalExceptions();

        try
        {
            var parsed = ParseArgs(args);
            if (parsed.ShowHelp)
            {
                Console.Out.WriteLine(Usage);
                return Shutdown(ExitOk, "收到 --help, 打印用法后退出");
            }

            if (parsed.Failure is { } failure)
            {
                Log.Error(Category, failure);
                Console.Error.WriteLine(failure);
                Console.Error.WriteLine();
                Console.Error.WriteLine(Usage);
                return Shutdown(ExitBadArguments, "参数错误, 退出");
            }

            // workDir 必须先 GetFullPath 一次: 工具侧的相对路径解析、git 的 -C、以及
            // MakeWorkerKey 的规范化都假设拿到的是绝对路径。
            // 路径本身非法(非法字符/过长/空)算「参数错」而不是「内部错误」, 所以在这里
            // 就转成退出码 2, 别让它掉到外层 catch 变成 1 —— 两者的排障方向完全不同。
            string workDir;
            try
            {
                workDir = Path.GetFullPath(parsed.WorkDir!);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                var msg = $"--workdir 不是合法路径(\"{parsed.WorkDir}\"): {ex.Message}";
                Log.Error(Category, msg);
                Console.Error.WriteLine(msg);
                return Shutdown(ExitBadArguments, "参数错误, 退出");
            }

            var sessionId = parsed.SessionId ?? string.Empty;

            // 会话 Id 缺失不致命(手工启动调试时很常见), 但要留痕: 它参与 WorkerKey 计算,
            // 空值会让这个实例被路由到"空会话"上。
            if (string.IsNullOrEmpty(sessionId))
            {
                Log.Warn(Category, "未提供 --session; 该 Worker 将以空会话身份运行(通常意味着手工启动)");
            }

            // --key 没给就现算。两条路径必须落在同一个函数上(WorkerProtocol.MakeWorkerKey),
            // 否则两侧算出两个 key, 父进程就认不出自己派生的实例(只能靠 pid 兜底孤儿)。
            var workerKey = string.IsNullOrWhiteSpace(parsed.Key)
                ? WorkerProtocol.MakeWorkerKey(workDir, sessionId)
                : parsed.Key!.Trim();
            var keyFromParent = !string.IsNullOrWhiteSpace(parsed.Key);

            Log.Info(Category,
                $"Worker 启动: pid={Environment.ProcessId}, 会话={sessionId}, 工作目录={workDir}, " +
                $"父进程={parsed.ParentPid}, WorkerKey={workerKey}({(keyFromParent ? "来自 --key" : "本进程现算")})");

            var options = new WorkerHostOptions
            {
                SessionId = sessionId,
                WorkDir = workDir,
                // 启动阶段手里只有 --workdir; 真正的工作树根由 hello 里的 WorkspaceRoot 接管
                // (它是父进程 IWorkspaceResolver 的解析结果, 执行权与检查点目录都按它建键)。
                WorkspaceRoot = workDir,
                ParentPid = parsed.ParentPid,
                WorkerKey = workerKey
            };

            var host = new WorkerSlimHost(options);
            Log.Info(Category, $"工具集装配完成: {host.SnapshotTools().Count} 个工具");

            using var shutdown = new CancellationTokenSource();
            // Ctrl+C: 取消而不是直接杀进程, 好让 WorkerServer 有机会走完在飞调用的收尾
            // (发失败响应/写完最后一帧)。真正没人收的场合靠父进程关闭管道 → 读循环 EOF。
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true; // 阻止 CLR 直接终止进程
                shutdown.Cancel();
            };

            // ── 唯一的集成接缝 ──
            // WorkerServer(位于 AIShikikan.Core/Services/Worker/)负责:
            //   · 接管下面这两个标准流、跑 NDJSON 读循环、握手(worker/hello)、派发 tools/call;
            //   · 握手成功后调 host.BindHello(hello)、每次 tools/sync 后调 host.ApplyToolsSync(sync);
            //   · 把 host.OutputSink 接到自己的写锁上(流式输出必须串行且有序)。
            //
            // ⚠ 为什么用 Console.OpenStandardInput/Output 而不是 TextReader/TextWriter:
            //   父进程侧走的是 Process.RedirectStandardInput/Output, 对端就是这两个裸流。
            //   用 Console.OpenStandardXxx() 直接拿字节流, 才能控制 UTF-8 **不发 BOM**
            //   (WorkerFrameWriter 自己编码, 不经过 TextWriter 的编码器状态)。
            //   ⚠ 切勿用 Console.In/Out: 它们是同步 TextReader, 且在重定向场景下带自己的缓冲,
            //   会与帧层的按行读取打架。
            var server = new WorkerServer(host);
            // 流式输出的出口: 工具侧 ToolContext.OnToolOutput 只入队, 由 WorkerServer 串行写出。
            // 必须在 RunAsync 之前挂上 —— RunAsync 一开跑就可能有输出行到达。
            host.OutputSink = server.EmitToolOutput;
            await server.RunAsync(
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                shutdown.Token).ConfigureAwait(false);

            return Shutdown(ExitOk, "Worker 正常退出(管道已关闭)");
        }
        catch (Exception ex)
        {
            Log.Error(Category, ex, "Worker 致命错误");
            Console.Error.WriteLine($"Worker 致命错误: {ex}");
            return Shutdown(ExitInternalError, "因致命错误退出");
        }
    }

    /// <summary>
    /// 统一的退出收尾: 记最后一条日志, 然后 Flush。
    /// </summary>
    /// <remarks>
    /// ⚠⚠ <b>顺序陷阱(全仓通用)</b>: <c>Log.Flush()</c> 会把内部 <c>_initialized</c> 置回
    /// false, 于是此后所有 <c>Log.*</c> 都被<b>静默丢弃</b>(不报错、不打提示)。
    /// 所以 Flush 必须是退出路径的<b>最后一步</b>: 之后不要再打任何日志, 包括
    /// 全局异常钩子里的 Error 与尚在收尾的后台任务日志。
    /// 本方法把这层顺序固化成一个入口, 就是为了不再出现"退出信息恰好丢在 Flush 之后"。
    /// </remarks>
    private static int Shutdown(int exitCode, string finalMessage)
    {
        Log.Info(Category, finalMessage);
        Log.Flush();
        return exitCode;
    }

    /// <summary>挂接全局异常钩子: 未观察的任务异常落盘(Warn)后 Observed 掉。</summary>
    /// <remarks>
    /// <c>AppDomain.UnhandledException</c> 刻意不挂: 未处理异常发生时进程正在被终止,
    /// 此时打的日志极可能与 <c>Log.Flush()</c> 竞争, 反而把最后一批日志搅乱;
    /// Main 里的 catch 已经覆盖了真正会走正常退出路径的异常。
    /// </remarks>
    private static void HookGlobalExceptions()
        => TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warn(Category, e.Exception, "未观察的任务异常");
            e.SetObserved();
        };

    // ─────────────────────────── 参数解析 ───────────────────────────

    /// <summary>解析结果。失败信息是给 <c>--workdir</c> 缺失这类硬错误用的可读文案。</summary>
    private sealed class ParsedArgs
    {
        public bool ShowHelp { get; init; }
        public string? Failure { get; init; }
        public string? SessionId { get; init; }
        public string? WorkDir { get; init; }
        public string? Key { get; init; }
        public int ParentPid { get; init; }
    }

    /// <summary>
    /// 逐项解析命令行(不拼字符串命令行, 也不做任何 shell 相关的拼装 —— 参数数组天然免疫
    /// 引号/空格/注入问题)。
    /// </summary>
    /// <remarks>
    /// <para><b>未知参数一律忽略并记 Warn, 不因未知参数退出失败</b>: 新旧版本主/子进程混搭
    /// 是常态(用户升级了 GUI 但 <c>worker/</c> 子目录里还是旧 Worker, 或反过来),
    /// 此时新加的 <c>--key</c> / 传输管道句柄参数会被旧版本无视。让进程因为一个看不懂的参数
    /// 就起不来, 等于把"版本错配"变成"功能完全不可用", 而降级路径(InlineTransport)本就存在。</para>
    /// <para>同理, 管道句柄类参数由 WorkerServer 侧解析, 本入口不认识也不关心, 只留一条
    /// Warn 便于对齐两端的命令行契约。</para>
    /// </remarks>
    private static ParsedArgs ParseArgs(string[] args)
    {
        // --help 单独预扫: 它必须能在任何位置生效, 且优先级高于一切(带错参数也想看用法)。
        foreach (var a in args)
        {
            if (a is "-h" or "--help" or "help")
            {
                return new ParsedArgs { ShowHelp = true };
            }
        }

        string? sessionId = null;
        string? workDir = null;
        string? key = null;
        var parentPid = 0;
        var failure = (string?)null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--session":
                    sessionId = TakeValue(args, ref i, "--session", ref failure);
                    break;

                case "--workdir":
                    workDir = TakeValue(args, ref i, "--workdir", ref failure);
                    break;

                case "--parent-pid":
                {
                    var raw = TakeValue(args, ref i, "--parent-pid", ref failure);
                    if (raw is null)
                    {
                        break;
                    }

                    // 解析失败保留 0(= 不启用孤儿自检), 不升级成致命错误:
                    // 父进程 pid 只用于"父进程没了就自我了断"这条保险, 读不出来时少一道保险,
                    // 好过整个 Worker 起不来。
                    if (int.TryParse(raw, out var parsedPid) && parsedPid > 0)
                    {
                        parentPid = parsedPid;
                    }
                    else
                    {
                        Log.Warn(Category, $"--parent-pid 不是合法的正整数(\"{raw}\"), 按 0 处理(不启用孤儿自检)");
                    }

                    break;
                }

                case "--key":
                    key = TakeValue(args, ref i, "--key", ref failure);
                    break;

                default:
                    Log.Warn(Category, $"忽略未知参数: {arg}");
                    break;
            }
        }

        if (failure is null && string.IsNullOrWhiteSpace(workDir))
        {
            failure = "缺少必需参数 --workdir <绝对路径>(工具的相对路径解析与 git 上下文都以它为准)";
        }

        return new ParsedArgs
        {
            Failure = failure,
            SessionId = sessionId,
            WorkDir = workDir,
            Key = key,
            ParentPid = parentPid
        };
    }

    /// <summary>取 <c>--flag</c> 后面跟的值; 缺失时记错误(首个错误胜出, 避免刷屏)。</summary>
    private static string? TakeValue(string[] args, ref int index, string flag, ref string? failure)
    {
        if (index + 1 >= args.Length)
        {
            if (failure is null)
            {
                failure = $"参数 {flag} 缺少取值";
            }

            return null;
        }

        return args[++index];
    }

    private const string Usage = """
        AIShikikan.Worker — AI-Shikikan 独立 Worker 进程

        由主进程 spawn, 不要手工启动(手工启动只能做参数解析自检, 因为没有管道就没有协议)。

        用法:
          AIShikikan.Worker --session <会话Id> --workdir <绝对路径> [--parent-pid <pid>] [--key <hex>]

        参数:
          --session <id>    宿主会话 Id。参与 WorkerKey 计算, 必须与主进程算 key 时逐字相同。
          --workdir <path>  本会话工作目录(绝对路径)。文件工具的相对路径解析与 git 上下文都以它为准。
                            [必填, 缺失时以退出码 2 退出]
          --parent-pid <n>  父进程 pid。用于孤儿自检(父进程没了就自我了断, 免得留下占着
                            工作目录 git 写锁的孤儿进程)。缺省/非法值 = 0 = 不启用。
          --key <hex>       主进程算好的 WorkerKey(16 位十六进制)。不给则本进程用
                            WorkerProtocol.MakeWorkerKey(workDir, sessionId) 现算。
          -h, --help       打印本帮助并以退出码 0 退出。

        未知参数一律忽略(记 Warn): 新旧版本主/子进程混搭是常态, 不该因此起不来。

        退出码: 0 正常 / 1 未处理异常 / 2 参数错
        """;
}