using System.IO;
using System.Threading;
using System.Threading.Channels;

namespace AIShikikan.Core.Logging;

/// <summary>日志级别。</summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4
}

/// <summary>
/// 轻量日志门面(Native AOT 兼容, 零依赖):
/// - 输出到 <see cref="AppPaths.LogDir"/> 下按日滚动文件 yyyyMMdd.log, 自动清理 7 天前旧日志;
/// - 写文件经 Channel 后台消费, 绝不阻塞引擎/UI 线程; 进程退出时 Flush;
/// - 级别默认 Debug 构建为 Debug、Release 为 Info, 可用环境变量 AISHIKIKAN_LOG_LEVEL 覆盖(trace/debug/info/warn/error);
/// - 分类约定: Boot / Engine / LLM / Agent / Git / MCP / Config / Session / UI;
/// - 行内带 [pid:xxx 标签] 进程标识: GUI 与 Worker 共写同一个日志文件, 交错后没有来源信息就只能靠猜。
///
/// 多进程共写同一文件靠两条: <c>FileMode.Append</c> 提供 <c>O_APPEND</c>, 保证每行原子追加不会被别的进程写花;
/// <c>FileShare.ReadWrite</c> 则让每个进程都「允许别人读写」——Windows 上 <see cref="FileStream"/> 走 <c>CreateFile</c>
/// 的共享模式检查, 谁独占写权限, 后开的进程直接 <see cref="IOException"/>; Unix 上 .NET 只在
/// <c>FileShare.None</c> 时加 flock, <c>FileShare.Read</c> 不加锁所以侥幸能两个 fd 都打开, 但那是 Unix 独有的巧合,
/// 不能拿来当跨平台语义。
/// </summary>
public static class Log
{
    private const int RetentionDays = 7;

    /// <summary>writer 打开/写入失败后的首次重试退避。</summary>
    private const int WriterBackoffInitialMs = 250;

    /// <summary>
    /// 重试退避上限。刻意压在 <see cref="Flush(int)"/> 默认超时(1500ms)之下, 保证 Flush 等待窗口里
    /// 至少还能再试一两次 —— 否则最后一批日志会因为「正在退避中」被白白丢掉。
    /// </summary>
    private const int WriterBackoffMaxMs = 1000;

    /// <summary>
    /// 连续故障期间的诊断重复间隔。写 stderr 只在「刚进入故障」和「每满一分钟」时输出,
    /// 既能让人知道日志断了, 又不会把控制台刷爆(此时文件日志本身已经不可用, stderr 是唯一的提示渠道)。
    /// </summary>
    private const long WriterFailureRemindIntervalMs = 60_000;

    /// <summary>未显式调用 <see cref="SetProcessLabel"/> 时的默认标签(即主进程 GUI)。</summary>
    private const string DefaultProcessLabel = "gui";

    /// <summary>
    /// 标签长度上限。标签来自调用方(未来可能是配置/命令行), 不夹紧的话一个超长字符串
    /// 会在每一行都重复一遍, 把日志文件撑爆且污染排障时的对齐阅读。
    /// </summary>
    private const int MaxProcessLabelLength = 32;

    private static readonly object Gate = new();

    /// <summary>进程内不变, 但按行取值比每次访问 <see cref="Environment.ProcessId"/> 便宜。</summary>
    private static readonly int ProcessId = Environment.ProcessId;

    private static Channel<string>? _channel;
    private static LogLevel _minLevel = LogLevel.Info;
    private static bool _initialized;

    /// <summary>人类可读标签; 读写都走 <see cref="Volatile"/>, 避免日志线程读到撕裂的引用。</summary>
    private static string _processLabel = DefaultProcessLabel;

    /// <summary>
    /// 当前进程的人类可读标签(默认 <c>gui</c>), 会出现在每行日志的 <c>[pid:xxx 标签]</c> 里。
    /// 未来 Worker 进程应在 <see cref="Initialize"/> 之后尽早设为 <c>worker</c> 等。
    /// </summary>
    public static string ProcessLabel => Volatile.Read(ref _processLabel);

    /// <summary>
    /// 设置进程标签。空白回退为默认值, 超长按 <see cref="MaxProcessLabelLength"/> 截断 ——
    /// 因为标签在每行都出现, 必须有界。
    /// </summary>
    public static void SetProcessLabel(string? label)
    {
        var normalized = label?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            normalized = DefaultProcessLabel;
        }

        if (normalized.Length > MaxProcessLabelLength)
        {
            normalized = normalized[..MaxProcessLabelLength];
        }

        // 引用本身是不可变的(字符串赋值即原子), Volatile 只保证跨线程的可见性/顺序,
        // 这样 Initialize 前设置也能被后台消费者线程看到。
        Volatile.Write(ref _processLabel, normalized);
    }

    /// <summary>初始化(幂等)。应在进程入口尽早调用。</summary>
    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            _minLevel = ResolveMinLevel();
#if DEBUG
            _minLevel = _minLevel < LogLevel.Debug ? LogLevel.Debug : _minLevel;
#endif

            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                CleanupOldLogs();
                _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
                {
                    SingleReader = true,
                    FullMode = BoundedChannelFullMode.DropOldest // 日志洪峰时丢弃最旧的, 不拖垮业务
                });
                _ = Task.Run(ConsumeLoopAsync);
            }
            catch
            {
                // 日志系统自身故障不能影响启动
            }

            _initialized = true;
        }
    }

    public static void Trace(string category, string message) => Write(LogLevel.Trace, category, message);
    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message);
    public static void Info(string category, string message) => Write(LogLevel.Info, category, message);
    public static void Warn(string category, string message) => Write(LogLevel.Warn, category, message);
    public static void Error(string category, string message) => Write(LogLevel.Error, category, message);

    /// <summary>记录异常(含类型名与堆栈, 供 Error/Warn 级别使用)。</summary>
    public static void Warn(string category, Exception ex, string? message = null)
        => Write(LogLevel.Warn, category, FormatException(ex, message));

    public static void Error(string category, Exception ex, string? message = null)
        => Write(LogLevel.Error, category, FormatException(ex, message));

    /// <summary>写入即时消息并等待落盘(仅 doctor/崩溃等关键路径使用)。</summary>
    /// <remarks>
    /// ⚠️ 陷阱: 本方法会把 <see cref="_initialized"/> 置回 false, 于是 <see cref="Write"/> 从此静默 return,
    /// 直到有人再次调用 <see cref="Initialize"/>。也就是说 <c>Flush()</c> 之后写的任何日志(包括全局异常钩子里的
    /// Error、以及尚在收尾的后台任务日志)都会被丢弃且无任何提示。
    /// Worker 的退出流程必须把 Flush 放在退出路径的「最后一步」; 如果 Worker 还要在同一进程里复活/重启,
    /// Flush 之后必须重新 <see cref="Initialize"/> 一次(幂等, 会重建 channel 与消费循环)。
    /// </remarks>
    public static void Flush(int millisecondsTimeout = 1500)
    {
        var channel = _channel;
        if (channel is null || !channel.Writer.TryComplete())
        {
            return;
        }

        // 消费循环完成即代表全部落盘
        channel.Reader.Completion.Wait(millisecondsTimeout);

        lock (Gate)
        {
            _channel = null;
            _initialized = false; // 允许后续重新初始化(如 doctor 后继续运行)
        }
    }

    private static void Write(LogLevel level, string category, string message)
    {
        if (!_initialized || level < _minLevel)
        {
            return;
        }

        // [pid:<pid> <标签>] 是多进程共享日志文件的排障依据: 文件名按日滚动、所有进程 append 到同一份,
        // 行内若不带来源, GUI 与 Worker 的日志交错后无法归属。pid 用 Environment.ProcessId(AOT 安全, 无需 DI/反射)。
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{LevelTag(level)}] [pid:{ProcessId} {ProcessLabel}] [{category}] {message}";
        System.Diagnostics.Debug.WriteLine(line); // IDE/调试器可见

        _channel?.Writer.TryWrite(line);
    }

    /// <summary>
    /// 消费者线程私有的 writer 状态。
    /// 为什么独立成类而不是几个局部变量: 打开 / 写入 / 重建 / 退避这四件事都只发生在唯一的消费者线程上,
    /// 不需要加锁, 但「退避截止时间」「连续失败次数」必须跨多次循环迭代存活。
    /// </summary>
    private sealed class LogWriterState
    {
        /// <summary>当前打开的 writer; null 表示尚未打开 / 已被丢弃 / 上次打开失败。</summary>
        public StreamWriter? Writer;

        /// <summary><see cref="Writer"/> 绑定的日期(yyyyMMdd), 用于跨天滚动判断; 空串表示未绑定。</summary>
        public string Date = string.Empty;

        /// <summary>下一次允许尝试打开的 <see cref="Environment.TickCount64"/> 时刻; 0 表示可以立即尝试。</summary>
        public long RetryAfterTick;

        /// <summary>连续失败次数(退避用); 成功打开一次即清零。</summary>
        public int ConsecutiveFailures;

        /// <summary>最近一次失败的时刻; 0 表示当前没有处于故障态(用于诊断节流与「已恢复」提示)。</summary>
        public long LastFailureTick;
    }

    /// <summary>
    /// 后台消费: 按日打开文件追加写, 每行写完立即 Flush。
    ///
    /// ⚠️ 这个循环的生命周期必须等于进程的生命周期 —— 它一旦退出就再也没有人重启它:
    /// <see cref="_channel"/> 仍在、<c>_initialized</c> 仍为 true, <see cref="Write"/> 会继续 TryWrite 进有界 channel,
    /// 直到 4096 条填满后被 DropOldest 静默丢弃。表现是「进程整个生命周期一行日志都不落盘, 且没有任何提示」。
    /// <br/>
    /// 所以这里的铁律是: <b>打开失败、写失败都只影响当前这一个 writer, 一律就地吞掉并重建, 异常绝不允许冒泡到这里以外</b>。
    /// 之前把 <c>new FileStream(...)</c> 包在一个 <c>catch { }</c> 里恰好踩中了这个坑:
    /// Windows 上后开的进程(与 GUI 共用日志文件的 Worker)因共享模式被拒 → IOException → 整个循环退出 → 静默丢日志。
    /// </summary>
    private static async Task ConsumeLoopAsync()
    {
        var state = new LogWriterState();

        try
        {
            var reader = (_channel ?? throw new InvalidOperationException()).Reader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var line))
                {
                    var date = DateTime.Now.ToString("yyyyMMdd");

                    // 跨天滚动: 关掉昨天的文件。关旧文件失败(磁盘满等)不能影响继续写今天的新文件。
                    if (state.Writer is not null && date != state.Date)
                    {
                        CloseWriter(state);
                        // 换了一个文件名就是一次全新的机会, 不该被上一次的失败退避拖住。
                        state.RetryAfterTick = 0;
                        state.ConsecutiveFailures = 0;
                    }

                    // 打开失败返回 null → 丢弃本行, 继续消费下一行(这才是循环存活的意义所在)。
                    // 用局部变量承接而不是直接读 state.Writer: 避免依赖编译器对字段的 null 状态追踪。
                    var writer = state.Writer ?? OpenWriter(date, state);
                    if (writer is null)
                    {
                        continue;
                    }

                    // 写失败同样只是丢一行 + 丢 writer, 下一行重建。
                    if (!await WriteLineAsync(writer, line, state).ConfigureAwait(false))
                    {
                        continue;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 兜底: 能走到这里说明出问题的不是某个 writer 而是消费循环本身(例如 channel 被 faulted)。
            // 此时文件日志已经不可用(没有消费者了), 唯一能用的诊断渠道是 stderr。
            WriteDiagnostic($"日志消费循环异常退出, 本进程后续日志将全部丢失: {ex}");
        }
        finally
        {
            CloseWriter(state);
        }
    }

    /// <summary>
    /// 打开当日日志文件(append + 多进程共享)。返回 null 表示本次尝试失败:
    /// 不抛异常, 只把诊断打到 stderr 并设置退避截止时间, 由调用方丢掉本行继续消费。
    /// </summary>
    private static StreamWriter? OpenWriter(string date, LogWriterState state)
    {
        var now = Environment.TickCount64;
        if (state.RetryAfterTick != 0 && now < state.RetryAfterTick)
        {
            // 退避窗口内不再尝试。否则「有日志就重试」会变成 syscall 风暴:
            // 缓冲区里堆着 4096 行时, 会连续做 4096 次注定失败的 open, CPU 和 IO 全被日志系统吃掉。
            return null;
        }

        try
        {
            var stream = new FileStream(
                Path.Combine(AppPaths.LogDir, $"ai-shikikan-{date}.log"),
                FileMode.Append,
                FileAccess.Write,
                // 必须是 ReadWrite 而不是 Read: Windows 上 FileStream 走 CreateFile 的共享模式检查,
                // FileShare.Read 的语义是「我允许别人只读打开, 但任何写/删除都会被拒」。
                // GUI 与 Worker 都是 FileAccess.Write + FileShare.Read 打开同一个文件时,
                // 后开的进程直接 IOException, 而那个 IOException 会杀掉消费循环(见 ConsumeLoopAsync 的说明)。
                // 两个进程都要能写, 所以谁都不得独占写权限。
                FileShare.ReadWrite);

            var writer = new StreamWriter(stream, System.Text.Encoding.UTF8) { AutoFlush = false };
            state.Writer = writer;
            state.Date = date;
            state.ConsecutiveFailures = 0;

            if (state.LastFailureTick != 0)
            {
                // 断过又通了, 明确告诉使用者「这段空白是日志系统的问题, 不是业务没输出」。
                WriteDiagnostic($"日志文件已恢复: ai-shikikan-{date}.log(故障期间丢弃的行不可恢复)");
                state.LastFailureTick = 0;
            }

            return writer;
        }
        catch (Exception ex)
        {
            state.Writer = null; // 保证不残留半开的句柄
            state.Date = string.Empty;
            NoteWriterFailure("打开", $"ai-shikikan-{date}.log", ex, state, now);
            return null;
        }
    }

    /// <summary>
    /// 写一行并立刻落盘。返回 false 表示失败, 调用方必须丢掉 writer(下一行重建)。
    /// </summary>
    private static async Task<bool> WriteLineAsync(StreamWriter writer, string line, LogWriterState state)
    {
        try
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
            // 既有语义: AutoFlush = false, 每行写完显式 Flush。不改成批量 flush 是为了不动落盘时机 —
            // 崩溃时宁可多几次 IO, 也别丢掉已写出的日志。
            await writer.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // 写失败(磁盘满 / 句柄失效 / Windows 上被独占式打开者顶掉)与打开失败同等级:
            // 丢 writer + 退避, 循环必须活着。
            NoteWriterFailure("写入", state.Date, ex, state, Environment.TickCount64);
            CloseWriter(state);
            return false;
        }
    }

    /// <summary>
    /// 关闭并丢弃当前 writer。flush 失败也吞掉 —— 走到这里说明这个句柄本来就要被丢掉了,
    /// flush 成功与否不影响下一个 writer。
    /// <br/>
    /// 同时清空 <see cref="LogWriterState.Date"/>: 让调用方下次一定重新走 <see cref="OpenWriter"/>,
    /// 而不是误判成「今天已经打开过了」而复用一个已经被丢弃的 writer。
    /// </summary>
    private static void CloseWriter(LogWriterState state)
    {
        var writer = state.Writer;
        state.Writer = null;
        state.Date = string.Empty;

        if (writer is null)
        {
            return;
        }

        try
        {
            writer.Flush();
        }
        catch
        {
            // 见方法说明
        }

        try
        {
            writer.Dispose();
        }
        catch
        {
            // 同上
        }
    }

    /// <summary>
    /// 记录一次 writer 故障: 记连续失败次数、按指数退避设置下次可重试时刻、按时间节流输出 stderr 诊断。
    /// 三件事缺一不可 —— 不退避会打爆 CPU, 不节流会把 stderr 刷爆, 不诊断则等于「静默丢日志」的原罪。
    /// </summary>
    private static void NoteWriterFailure(string action, string target, Exception ex, LogWriterState state, long now)
    {
        state.ConsecutiveFailures++;

        var shift = Math.Clamp(state.ConsecutiveFailures - 1, 0, 10);
        var delay = Math.Min(WriterBackoffInitialMs << shift, WriterBackoffMaxMs);
        state.RetryAfterTick = now + delay;

        // 首次故障立即报, 之后每 WriterFailureRemindIntervalMs 再报一次, 避免长时故障刷爆控制台。
        if (state.LastFailureTick == 0 || now - state.LastFailureTick >= WriterFailureRemindIntervalMs)
        {
            WriteDiagnostic(
                $"{action}日志文件失败({target}, 连续第 {state.ConsecutiveFailures} 次, 已丢弃本行并将在 {delay}ms 后重试): " +
                $"{ex.GetType().Name}: {ex.Message}");
        }

        state.LastFailureTick = now;
    }

    /// <summary>
    /// 日志系统自身出故障时的唯一诊断出口。此时 <see cref="Write"/> 不可用(没有消费者),
    /// 所以只能写 stderr —— 它至少不会依赖那个已经坏掉的文件。
    /// </summary>
    private static void WriteDiagnostic(string message)
    {
        try
        {
            Console.Error.WriteLine($"[AIShikikan.Log] [pid:{ProcessId} {ProcessLabel}] {message}");
        }
        catch
        {
            // stderr 也可能不可用(重定向到已关闭的管道 / 无控制台)。到这一步就只能放弃, 不能再抛。
        }
    }

    private static LogLevel ResolveMinLevel()
    {
        var raw = Environment.GetEnvironmentVariable("AISHIKIKAN_LOG_LEVEL");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw.Trim().ToLowerInvariant() switch
            {
                "trace" => LogLevel.Trace,
                "debug" => LogLevel.Debug,
                "info" => LogLevel.Info,
                "warn" or "warning" => LogLevel.Warn,
                "error" => LogLevel.Error,
                _ => LogLevel.Info
            };
        }

#if DEBUG
        return LogLevel.Debug;
#else
        return LogLevel.Info;
#endif
    }

    private static void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(AppPaths.LogDir, "ai-shikikan-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
        }
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        _ => "ERR"
    };

    private static string FormatException(Exception ex, string? message) =>
        $"{(string.IsNullOrEmpty(message) ? ex.GetType().Name : message)}: {ex.Message}{Environment.NewLine}{ex.StackTrace}";
}
