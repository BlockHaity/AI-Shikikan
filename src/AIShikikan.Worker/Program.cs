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
 *   候选之一)时不必先来清理污染。WebSocket 模式同样遵守: 服务起在本进程内, stdout 依旧干净。
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
                return BadArguments(failure, showUsage: true);
            }

            // ── 配置: 先读 worker.toml(含环境变量覆盖), 再让命令行显式值压过它 ──
            // 顺序不能反 —— 配置文件是"这台机器"的长期意图, 命令行是"这一次启动"的意图。
            // WorkerConfigService.Load 刻意**不写**默认文件: Worker 是可能被频繁拉起的
            // 子进程, 不该在启动路径上写用户配置。
            var configPath = parsed.ConfigPath?.Trim();
            if (configPath is not null && !File.Exists(configPath))
            {
                // 显式路径不存在不升级成失败: 手工启动时"先跑起来看日志"比"因缺配置起不来"
                // 更有用, 而 Load 对缺失文件本来就是按默认(stdio)返回。
                Log.Warn(Category, $"--config 指定的配置文件不存在: {configPath}; 改按默认配置运行");
            }

            var config = WorkerConfigService.Load(configPath);
            ApplyCommandLineOverrides(config, parsed);
            config.Normalize();

            Log.Info(Category,
                $"Worker 配置: 来源={configPath ?? WorkerConfigService.DefaultPath}, " +
                $"模式={(config.IsWebSocket ? "websocket" : "stdio")}, " +
                $"监听={config.Listen}, 路径={config.Path}");

            using var shutdown = new CancellationTokenSource();
            // Ctrl+C: 取消而不是直接杀进程, 好让两种服务端都有机会走完在飞调用的收尾
            // (发失败响应/写完最后一帧/放开监听的 socket)。stdio 模式真正没人收的场合
            // 靠父进程关闭管道 → 读循环 EOF。
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true; // 阻止 CLR 直接终止进程
                shutdown.Cancel();
            };

            return config.IsWebSocket
                ? await RunWebSocketAsync(config, parsed, shutdown.Token).ConfigureAwait(false)
                : await RunStdioAsync(config, parsed, shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(Category, ex, "Worker 致命错误");
            Console.Error.WriteLine($"Worker 致命错误: {ex}");
            return Shutdown(ExitInternalError, "因致命错误退出");
        }
    }

    /// <summary>
    /// WebSocket 模式: 把 Worker 作为独立服务常驻, 并把 <c>root</c> 暴露成远程项目目录。
    /// </summary>
    /// <remarks>
    /// ⚠ 这条路径**不要求 --workdir**: 独立运行的典型姿势就是站在项目根目录里直接
    /// <c>./AIShikikan.Worker --websocket</c>, 此时工作目录就是进程启动目录。
    /// </remarks>
    private static async Task<int> RunWebSocketAsync(WorkerConfig config, ParsedArgs parsed, CancellationToken ct)
    {
        // --session/--key/--parent-pid 是 stdio 派生的身份参数; 远程模式下每次连接都会用
        // hello 覆盖会话与工作目录, 不提示一下会让排障的人以为它们生效了。
        if (!string.IsNullOrWhiteSpace(parsed.SessionId)
            || !string.IsNullOrWhiteSpace(parsed.Key)
            || parsed.ParentPid > 0)
        {
            Log.Warn(Category, "WebSocket 模式下 --session/--key/--parent-pid 不生效: 远程身份由每次连接的 hello 覆盖");
        }

        // 默认工作目录的回退链: CLI --workdir → 配置里的远程目录(EffectiveRemoteWorkDir 已含
        // root 回退) → 进程启动目录。第一步必须 GetFullPath: 服务端做 hello 越界校验、
        // 工具侧解析相对路径都假设拿到的是绝对路径。
        var cliWorkDir = string.IsNullOrWhiteSpace(parsed.WorkDir) ? null : parsed.WorkDir.Trim();
        var workDirRaw = cliWorkDir
            ?? config.EffectiveRemoteWorkDir
            ?? Environment.CurrentDirectory;

        string defaultWorkDir;
        try
        {
            defaultWorkDir = Path.GetFullPath(workDirRaw);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return BadArguments($"工作目录不是合法路径(\"{workDirRaw}\"): {ex.Message}");
        }

        // root: 显式配置优先, 缺省把默认工作目录当作对外暴露的根。
        var rootRaw = config.Root ?? defaultWorkDir;
        string root;
        try
        {
            root = Path.GetFullPath(rootRaw);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return BadArguments($"root 不是合法路径(\"{rootRaw}\"): {ex.Message}");
        }

        // root 存在性与 token 是远程服务的两道硬门槛: 不存在的根会在第一次 hello 时
        // 变成一个无从解释的越界拒绝, 而没有 token 的对外监听等于把本机文件系统裸奔出去。
        if (!Directory.Exists(root))
        {
            return BadArguments($"WebSocket 模式的 root 不存在或不是目录: {root}");
        }

        if (string.IsNullOrWhiteSpace(config.Token))
        {
            return BadArguments("WebSocket 模式必须配置 token(远程访问需要鉴权): 用 --token 或 worker.toml 的 token");
        }

        // 把规范化后的绝对根写回配置: 服务端按它做目录越界校验, 相对路径会在不同
        // 工作目录下解析出不同结果。
        config.Root = root;

        Log.Info(Category,
            $"Worker 以 WebSocket 服务模式启动: pid={Environment.ProcessId}, root={root}, " +
            $"默认工作目录={defaultWorkDir}, 路径={config.Path}, 监听={config.Listen}");

        var cancelled = false;
        // ⚠ await using 必须包在独立作用域里: 资源释放发生在作用域结束处, 也就是 Shutdown()
        // 之前 —— 否则释放期间打的日志会落在 Log.Flush() 之后(见 Shutdown 的顺序陷阱)。
        await using (var server = WorkerWebSocketServer.Start(
                         config,
                         options => new WorkerSlimHost(options),
                         defaultWorkDir))
        {
            Log.Info(Category,
                $"WebSocket 服务已监听: {server.EndPoint}, 客户端可连接 {config.ResolveClientUrl() ?? "(未推导)"}");

            try
            {
                await server.RunAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                cancelled = true;
            }
        }

        return Shutdown(ExitOk, cancelled ? "收到取消信号, WebSocket 服务已停止" : "WebSocket 服务已停止, Worker 退出");
    }

    /// <summary>
    /// stdio 模式(默认): 由 GUI 派生的子进程, 接管 stdin/stdout 上的 NDJSON 帧。
    /// </summary>
    private static async Task<int> RunStdioAsync(WorkerConfig config, ParsedArgs parsed, CancellationToken ct)
    {
        // 工作目录: 命令行优先, 其次 worker.toml 的 workdir。两者都没有才算参数错 ——
        // 这条路径由 GUI 派生, GUI 一定会传 --workdir, 缺了说明是版本错配或手工启动。
        var cliWorkDir = string.IsNullOrWhiteSpace(parsed.WorkDir) ? null : parsed.WorkDir.Trim();
        var workDirRaw = cliWorkDir
            ?? (string.IsNullOrWhiteSpace(config.WorkDir) ? null : config.WorkDir.Trim());

        if (workDirRaw is null)
        {
            return BadArguments(
                "缺少工作目录: 请提供 --workdir <绝对路径>, 或在 worker.toml 里配置 workdir",
                showUsage: true);
        }

        // workDir 必须先 GetFullPath 一次: 工具侧的相对路径解析、git 的 -C、以及
        // MakeWorkerKey 的规范化都假设拿到的是绝对路径。
        // 路径本身非法(非法字符/过长/空)算「参数错」而不是「内部错误」, 所以在这里
        // 就转成退出码 2, 别让它掉到外层 catch 变成 1 —— 两者的排障方向完全不同。
        string workDir;
        try
        {
            workDir = Path.GetFullPath(workDirRaw);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return BadArguments($"--workdir 不是合法路径(\"{workDirRaw}\"): {ex.Message}");
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
            ct).ConfigureAwait(false);

        return Shutdown(ExitOk, "Worker 正常退出(管道已关闭)");
    }

    /// <summary>把命令行显式给出的值覆盖到配置对象上(命令行优先于 worker.toml 与环境变量)。</summary>
    private static void ApplyCommandLineOverrides(WorkerConfig config, ParsedArgs parsed)
    {
        // 只覆盖**显式给出**的项: 没写的参数一律保持配置文件/环境变量里的值。
        if (parsed.WebSocket)
        {
            config.Mode = "websocket";
        }

        if (!string.IsNullOrWhiteSpace(parsed.Listen))
        {
            config.Listen = parsed.Listen.Trim();
        }

        if (!string.IsNullOrWhiteSpace(parsed.UrlPath))
        {
            config.Path = parsed.UrlPath.Trim();
        }

        if (!string.IsNullOrWhiteSpace(parsed.Token))
        {
            config.Token = parsed.Token.Trim();
        }

        if (!string.IsNullOrWhiteSpace(parsed.Root))
        {
            config.Root = parsed.Root.Trim();
        }

        // ⚠ --workdir 不写回 config.WorkDir: 后者是"客户端请求的远程目录"这一语义,
        // 而 --workdir 在两条路径上的回退顺序不同(stdio 走 config.WorkDir, WebSocket 走
        // EffectiveRemoteWorkDir), 分开处理更清楚。
    }

    /// <summary>参数错误的统一出口: 记日志 + 写 stderr(必要时附用法), 然后以退出码 2 结束。</summary>
    private static int BadArguments(string message, bool showUsage = false)
    {
        Log.Error(Category, message);
        Console.Error.WriteLine(message);
        if (showUsage)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
        }

        return Shutdown(ExitBadArguments, "参数错误, 退出");
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

    /// <summary>解析结果。失败信息是给「参数缺取值」这类硬错误用的可读文案。</summary>
    private sealed class ParsedArgs
    {
        public bool ShowHelp { get; init; }
        public string? Failure { get; init; }
        public bool WebSocket { get; init; }
        public string? ConfigPath { get; init; }
        public string? SessionId { get; init; }
        public string? WorkDir { get; init; }
        public string? Key { get; init; }
        public int ParentPid { get; init; }
        public string? Listen { get; init; }
        public string? UrlPath { get; init; }
        public string? Token { get; init; }
        public string? Root { get; init; }
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

        string? configPath = null;
        string? sessionId = null;
        string? workDir = null;
        string? key = null;
        string? listen = null;
        string? urlPath = null;
        string? token = null;
        string? root = null;
        var webSocket = false;
        var parentPid = 0;
        var failure = (string?)null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--config":
                    configPath = TakeValue(args, ref i, "--config", ref failure);
                    break;

                case "--websocket":
                    // 纯开关, 不取值: 取值形态("--websocket false")会让命令行的语义变得可疑。
                    webSocket = true;
                    break;

                case "--listen":
                    listen = TakeValue(args, ref i, "--listen", ref failure);
                    break;

                case "--path":
                    urlPath = TakeValue(args, ref i, "--path", ref failure);
                    break;

                case "--token":
                    token = TakeValue(args, ref i, "--token", ref failure);
                    break;

                case "--root":
                    root = TakeValue(args, ref i, "--root", ref failure);
                    break;

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

        // ⚠ 这里刻意**不**校验 --workdir: WebSocket 模式不要求它(项目根目录即工作目录),
        // 所以"缺工作目录"的判断放在 RunStdioAsync 里, 只对 stdio 路径算参数错。

        return new ParsedArgs
        {
            Failure = failure,
            WebSocket = webSocket,
            ConfigPath = configPath,
            SessionId = sessionId,
            WorkDir = workDir,
            Key = key,
            ParentPid = parentPid,
            Listen = listen,
            UrlPath = urlPath,
            Token = token,
            Root = root
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

        两种运行方式:
          · stdio(默认): 由 GUI 主进程 spawn, 通过 stdin/stdout 上的 NDJSON 帧通信。
            GUI 会自动按这种方式派生 Worker; 本地**不会**回退到主进程内联执行, 因此手工启动
            stdio 模式只能做参数解析自检(没有管道就没有协议)。
          · websocket(--websocket 或 worker.toml 的 mode = "websocket"/url): 作为独立服务
            常驻运行, 把 root 目录暴露成远程项目文件夹, 供远端 GUI 连接。

        用法:
          AIShikikan.Worker --session <会话Id> --workdir <绝对路径> [--parent-pid <pid>] [--key <hex>]
          AIShikikan.Worker --websocket [--listen <host:port>] [--path <url路径>]
                                    [--token <token>] [--root <目录>] [--config <worker.toml>]

        参数:
          --config <path>   指定 worker.toml。缺省用配置目录下的 worker.toml; 文件不存在时
                            按默认配置(stdio)运行, 且**不会**写出配置文件。
          --websocket       强制以 WebSocket 服务模式运行(等价于 mode = "websocket")。
          --listen <addr>   监听地址 host:port, 默认 127.0.0.1:8765(仅回环)。
          --path <path>     URL 路径, 默认 /worker, 会自动补前导斜杠。
          --token <token>   共享鉴权令牌。WebSocket 模式**必填**, 两端必须逐字一致。
          --root <dir>      允许远程访问的项目根目录, 必须存在且是目录; 缺省时取默认工作目录。
          --session <id>    宿主会话 Id。参与 WorkerKey 计算, 必须与主进程算 key 时逐字相同。
          --workdir <path>  工作目录(绝对路径)。stdio 模式必填; WebSocket 模式可省略, 缺省
                            依次回退到配置里的 workdir/root → 本进程启动目录。
          --parent-pid <n>  父进程 pid。用于孤儿自检(父进程没了就自我了断, 免得留下占着
                            工作目录 git 写锁的孤儿进程)。缺省/非法值 = 0 = 不启用。
          --key <hex>       主进程算好的 WorkerKey(16 位十六进制)。不给则本进程用
                            WorkerProtocol.MakeWorkerKey(workDir, sessionId) 现算。
          -h, --help       打印本帮助并以退出码 0 退出。

        worker.toml 示例(位于配置目录; 环境变量覆盖同名配置):
          mode = "websocket"
          url = "ws://127.0.0.1:8765/worker"   # 客户端连接地址; 留空时由 listen + path 推导
          token = "change-me"                  # 远程模式必填
          listen = "127.0.0.1:8765"            # 服务端监听地址
          path = "/worker"                     # 服务端 URL 路径
          root = "/srv/project"                # 允许访问的项目根; 留空时用进程启动目录
          workdir = "/srv/project"             # 客户端请求的远程目录; 留空时用 root
        环境变量: AISHIKIKAN_WORKER_MODE / _URL / _TOKEN / _LISTEN / _ROOT / _WORKDIR

        未知参数一律忽略(记 Warn): 新旧版本主/子进程混搭是常态, 不该因此起不来。

        退出码: 0 正常 / 1 未处理异常 / 2 参数错
        """;
}
