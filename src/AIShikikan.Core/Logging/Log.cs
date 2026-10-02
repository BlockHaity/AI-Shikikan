using System.IO;
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
/// - 分类约定: Boot / Engine / LLM / Agent / Git / MCP / Config / Session / UI。
/// </summary>
public static class Log
{
    private const int RetentionDays = 7;
    private static readonly object Gate = new();
    private static Channel<string>? _channel;
    private static LogLevel _minLevel = LogLevel.Info;
    private static bool _initialized;

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

        var line = $"{DateTime.Now:HH:mm:ss.fff} [{LevelTag(level)}] [{category}] {message}";
        System.Diagnostics.Debug.WriteLine(line); // IDE/调试器可见

        _channel?.Writer.TryWrite(line);
    }

    /// <summary>后台消费: 按日打开文件追加写, 批量 flush。</summary>
    private static async Task ConsumeLoopAsync()
    {
        StreamWriter? writer = null;
        var currentDate = string.Empty;

        try
        {
            var reader = (_channel ?? throw new InvalidOperationException()).Reader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var line))
                {
                    var date = DateTime.Now.ToString("yyyyMMdd");
                    if (writer is not null && date != currentDate)
                    {
                        // 跨天滚动: 关闭旧文件
                        await writer.FlushAsync().ConfigureAwait(false);
                        writer.Dispose();
                        writer = null;
                    }

                    writer ??= new StreamWriter(
                        new FileStream(Path.Combine(AppPaths.LogDir, $"ai-shikikan-{date}.log"),
                            FileMode.Append, FileAccess.Write, FileShare.Read),
                        System.Text.Encoding.UTF8)
                    { AutoFlush = false };
                    currentDate = date;

                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // 静默: 日志失败不影响业务
        }
        finally
        {
            writer?.Dispose();
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
