/*
 * Worker NDJSON 帧编解码 + JSON-RPC 请求/响应配对内核。
 *
 * ── 为什么这三个类型要住在同一个文件里 ──
 *   父进程侧(PipeTransport)与子进程侧(WorkerServer)都必须"写一行一帧、读一行一帧、
 *   按 id 配对请求与响应"。这三件事里任何一处两边口径不同, 症状都是**静默**的:
 *   写侧漏了换行 → 对端永远收不到这一帧(表现为无限等待);
 *   读侧跳过了合法帧 → 请求永久挂起;
 *   id 归一化口径不同 → 响应配不上等待者(表现为"Worker 不响应", 而日志里干干净净)。
 *   放一个文件里是为了让"两份实现"永远来自同一段文本。
 *
 * ── 序列化栈(AOT 硬约束) ──
 *   全程只用 JsonNode / JsonObject / JsonTypeInfo(源生成), 不碰任何反射式重载:
 *     JsonSerializer.Serialize(obj)            → 禁止(AOT 下抛 InvalidOperationException)
 *     JsonSerializer.Serialize(x, JsonTypeInfo) → 唯一允许的写法
 *   帧本身一律 JsonNode: JSON-RPC 信封(jsonrpc/id/method/params/result/error)没有 DTO,
 *   刻意不建模 —— DTO 只覆盖 params/result 的载荷(见 WorkerMessages.cs 的文件头说明)。
 *
 * ── 编帧纪律(违反任何一条都不会编译失败, 只会偶发失败) ──
 *   1. 一行一帧, 帧内不得有裸换行。
 *      ParametersJson / Arguments / DetailJson 三个字段的值天然含换行(多行 JSON),
 *      所以它们必须是**JSON 字符串字段**(嵌套一层), 由 STJ 转义成 \n;
 *      绝不能把它们的多行 JSON 直接拼进帧里 —— 读行端会把它切成两半,
 *      表现是"偶发 JsonException", 且只在参数恰好多行时才发生, 极难复现。
 *   2. ⚠️ **MaxLineChars 绝不套到协议帧上**。
 *      WorkerProtocol.MaxLineChars(8KB) 的作用域只有"子进程输出流的一行",
 *      即 WorkerToolOutputNotification.Line。请求/响应帧可以远超 8KB
 *      (WorkerToolCallResponse 内含整份文件内容与 DetailJson, 后者因 WriteIndented 还会膨胀 2~3 倍),
 *      套上去会把 read_file 的正常结果截成残缺内容, 且**没有任何错误提示**。
 *      因此本文件的读侧没有任何按长截断的逻辑, 见 ReadAsync。
 */

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIShikikan.Core.Logging;

namespace AIShikikan.Core.Services.Worker;

/// <summary>NDJSON 帧写入: 一行一个 JSON 对象, UTF-8 无 BOM, 写侧串行化。</summary>
/// <remarks>
/// <para><b>为什么不用 <c>StreamWriter</c></b>: <c>StreamWriter</c> 自带字符缓冲与编码器状态,
/// 一旦发生异常(管道写失败)它会留在"内部有未刷出的数据"的状态, 而管道已经是坏的,
/// 补刷只会再抛一次; 且它的默认编码可能带 BOM(见 <see cref="Utf8NoBom"/> 的说明)。
/// 直接 <c>WriteAsync(byte[])</c> + 手动补 '\n' 是"写一次就一定落地"的最短路径。</para>
///
/// <para><b>为什么写侧必须串行</b>: <see cref="System.IO.Pipes"/> 的管道流<b>非线程安全</b>,
/// 两个线程同时写会让字节交错, 产出的是一帧被劈成两半的"合法 JSON"之外的垃圾 ——
/// 对端要么抛解析异常, 要么把半帧当别的请求配错 id。而本仓确实会并发写:
/// <c>run_subagents</c> 一次并发刷多个子代理的输出通知, 主线程同时还在发取消/心跳应答。
/// 所以这里用<b>实例级</b> <see cref="SemaphoreSlim"/>(照 McpStdioClient 的 _writeLock),
/// 不能是 static 的全局锁 —— 多个 Worker 实例各写各的管道, 全局锁会让一个卡住的 Worker
/// 把其它会话一起钉住。</para>
///
/// <para><b>⚠️ 实例生命周期与流的所有权分离</b>: 本类<b>不拥有</b>传入的流, 也不负责关它 ——
/// 关管道是传输层 <c>DisposeAsync</c> 的职责(要按"关 stdin → 等自退 → Kill → Dispose 进程"的顺序走,
/// 不是本类能独立完成的)。因此 <see cref="Dispose"/> 只关闸不关流。</para>
/// </remarks>
public sealed class WorkerFrameWriter : IDisposable
{
    /// <summary>
    /// 显式 UTF-8 且<b>不发 BOM</b>。
    /// <para>为什么要在代码里写死: BOM 是 U+FEFF, 它是合法字符, 混进帧里会让首行以
    /// "\uFEFF{" 开头 —— 严格按 '{' 判首字符的读侧会把它当成 banner 丢掉, 于是**第一帧永远收不到**。
    /// 现实中 Worker 侧若用 <c>StreamWriter</c>(默认 <c>Encoding.UTF8</c> 带 BOM)就会踩中。
    /// 这里的 <c>GetBytes</c> 路径本来就不会产生 BOM, 但把"不发 BOM"写进类型名里是为了
    /// 让后来者在换成 StreamWriter 时立刻看到这条要求。</para>
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>帧分隔符。刻意用 "\n" 而不是 Environment.NewLine: 帧边界是协议约定, 与平台无关。</summary>
    private static readonly byte[] NewlineBytes = "\n"u8.ToArray();

    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private int _disposed;

    /// <param name="output">协议输出流(匿名管道父侧写子 stdin / 子侧写父 stdout)。必须可写。</param>
    public WorkerFrameWriter(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("输出流不可写", nameof(output));
        }

        _output = output;
    }

    /// <summary>写一帧: <c>{...}\n</c>, 并等到数据真的交给操作系统。</summary>
    /// <remarks>
    /// <para><b>锁外只做序列化</b>: <c>ToJsonString()</c> 对一个含整份文件内容的响应会分配几 MB,
    /// 持锁做这些会让并发的取消通知(小帧, 但很急)排在后面 —— 而"取消很急"正是本协议的常态。</para>
    ///
    /// <para><b>⚠️ 不做长度上限</b>: 见文件头纪律第 2 条。协议帧可以很大, 截断即等于静默损坏。</para>
    /// </remarks>
    public async Task WriteAsync(JsonObject frame, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(frame);
        ct.ThrowIfCancellationRequested();

        // 帧文本: JsonNode 自带的 ToJsonString 是"紧凑单行"输出(不套 AppJsonContext 的 WriteIndented),
        // 这正是 NDJSON 要的形状。嵌套的字符串字段里的换行由 STJ 转义成 \n, 因此整帧仍是一行。
        var text = frame.ToJsonString();

        // 锁外完成编码(纯 CPU, 与流无关)
        var payload = Utf8NoBom.GetBytes(text);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 二次检查: 可能在等锁期间被 Dispose(例如用户正在退出)
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            await _output.WriteAsync(payload.AsMemory(), ct).ConfigureAwait(false);
            await _output.WriteAsync(NewlineBytes.AsMemory(), ct).ConfigureAwait(false);
            // 管道是内核缓冲, 不 Flush 的话数据可能还在用户态缓冲里;
            // "以为发出去了其实没发"在管道上表现为对端无限等待, 没有超时兜底。
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>关闸: 之后任何 <see cref="WriteAsync"/> 抛 <see cref="ObjectDisposedException"/>。</summary>
    /// <remarks>
    /// <para><b>刻意不关流、不释放 <see cref="SemaphoreSlim"/></b>, 理由照抄
    /// <c>McpStdioClient.DisposeAsync</c> 的注释: DisposeAsync 很可能在仍有并发
    /// <c>WriteAsync</c> 持着写锁时被调用, 此时 Dispose 掉信号量会让那些在途操作抛
    /// <see cref="ObjectDisposedException"/> 砸到调用方脸上。本类从未取过
    /// <c>AvailableWaitHandle</c>, 不释放是安全的; 它随实例被 GC 回收, 生命周期与实例相同。</para>
    ///
    /// <para><b>为什么要关闸</b>: "已释放的传输继续接受写入"比抛异常更难排查 ——
    /// 写进一条已关闭的管道得到的是 <c>ObjectDisposedException</c>/<c>IOException</c>,
    /// 发生在很远的调用栈上, 看不出根因是"这个传输早就没了"。</para>
    /// </remarks>
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }
}

/// <summary>NDJSON 帧读取: 逐行, 跳过空行与不以 '{' 开头的行(容忍对端 banner)。</summary>
/// <remarks>
/// <para><b>为什么"跳过"而不是"报错"</b>: 对端是另一个进程, 它完全可能在往 stdout 写第一帧之前
/// 先打印一行运行时 banner(Java/.NET 的 native 库加载信息、第三方 CLI 的启动提示)。
/// 因为本协议用的是**重定向后的 stdout**, 这些字节与协议帧混在同一条流里。
/// 一行以 '{' 开头却解析失败才是真异常(记 Debug); 而 banner、空行、以及任何不以 '{' 开头的行
/// 都是"正常噪声", 报错只会把一次偶发的启动提示变成连接故障。</para>
///
/// <para><b>⚠️ 逐帧不设长度上限</b>(理由见文件头纪律第 2 条): 这里只有一道
/// <see cref="LineBuffer.MaxFrameBytes"/> 的病态兜底(对端永不写换行时防止内存无界增长),
/// 它的作用是"让传输层进入崩溃处理路径并留下明确日志", 而不是协议限制。</para>
///
/// <para><b>⚠️ 约定: 同一个流同一时刻只能有一个读循环</b>(父侧一个 PipeTransport、
/// 子侧一个 WorkerServer, 都满足)。见 <see cref="LineBuffer"/> 为什么用
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> 挂状态。</para>
/// </remarks>
public static class WorkerFrameReader
{
    /// <summary>日志 category(与 Worker 其余部分一致)。</summary>
    private const string Category = "Worker";

    /// <summary>进日志的行预览最多这么长, 免得一条 banner/坏帧把日志文件刷爆。</summary>
    private const int MaxLoggedChars = 200;

    /// <summary>
    /// 每个流一份读缓冲(未完成的半行)。挂在 <see cref="ConditionalWeakTable{TKey,TValue}"/> 上
    /// 而不是字段/参数上, 是为了让 <see cref="ReadAsync(Stream, CancellationToken)"/> 保持
    /// 「静态 + 传流」的签名(两侧共用、无需各自持有状态), 同时又保住跨调用的缓冲:
    /// 每次 new 一个 <c>StreamReader</c> 会把"已经读进自己缓冲、但还没到换行"的那部分字节丢掉 ——
    /// 在 NDJSON 上表现为随机丢帧, 而且**只在帧大于读缓冲时发生**(4KB 以上), 极难复现。
    /// 用 CWT 挂表则天然随流一起被回收, 不产生静态集合泄漏。
    /// </summary>
    private static readonly ConditionalWeakTable<Stream, LineBuffer> Buffers = new();

    /// <summary>读一帧。对端 EOF 返回 null。</summary>
    /// <remarks>
    /// <para>返回 null 只有两种情况: 对端 EOF(进程退出/管道关闭), 或读到最后一个没有换行结尾的残帧
    /// 已处理完毕。本方法<b>不会</b>因为"这一行是垃圾"而返回 null —— 垃圾行被内部跳过并记 Debug。</para>
    ///
    /// <para>EOF 时会把缓冲区里剩下的内容当作最后一帧处理(去掉换行): 对端被 <c>kill -9</c> 时
    /// 很可能正好写在半行上, 丢掉它等于让上层看不到"最后那条已经成功执行的工具输出"。</para>
    /// </remarks>
    public static async Task<JsonObject?> ReadAsync(Stream input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var buffer = Buffers.GetValue(input, static _ => new LineBuffer());

        while (true)
        {
            if (buffer.TryTakeLine(out var line))
            {
                var frame = TryParseFrame(line, out var reason);
                if (frame is not null)
                {
                    return frame;
                }

                if (!string.IsNullOrEmpty(reason))
                {
                    // 只记 Debug 且不中断循环: 一行坏帧不该带走整条连接
                    Log.Debug(Category, reason);
                }

                continue;
            }

            if (buffer.Eof)
            {
                // 最后一帧没有换行结尾(常见于对端被强杀): 取出残留再收工
                var rest = buffer.TakeRemainder();
                if (rest is null)
                {
                    return null;
                }

                var frame = TryParseFrame(rest, out var reason);
                if (!string.IsNullOrEmpty(reason))
                {
                    Log.Debug(Category, reason);
                }

                return frame;
            }

            var read = await input.ReadAsync(buffer.Free, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                buffer.Eof = true;
                continue;
            }

            buffer.Commit(read);
        }
    }

    /// <summary>解析一行(已是字符串)。非 JSON 对象返回 null 而不是抛。</summary>
    /// <remarks>
    /// <b>为什么"返回 null"而不是抛异常</b>: 调用方(读循环)对"这一行不是帧"的正确反应是
    /// 跳过并继续读 —— 抛异常会让 banner 直接变成连接故障。
    /// 需要区分"banner(静默)"与"坏帧(记 Debug)"时, 由 <see cref="ReadAsync"/> 内部的
    /// <see cref="TryParseFrame"/> 负责, 本方法只回答"能不能解析出一个 JSON 对象"。
    /// </remarks>
    public static JsonObject? ParseLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var trimmed = TrimFramePrefix(line.AsSpan());
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            return null;
        }

        // 有前导空白/BOM 时才截一次: 常态(帧以 '{' 开头)零拷贝零分配
        var text = trimmed.Length == line.Length ? line : trimmed.ToString();
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 行 → 帧, 并给出"为什么被丢掉"(null/空表示无需记日志)。
    /// </summary>
    private static JsonObject? TryParseFrame(string? line, out string? reason)
    {
        reason = null;
        if (line is null)
        {
            return null;
        }

        var trimmed = TrimFramePrefix(line.AsSpan());
        if (trimmed.Length == 0)
        {
            return null; // 空行 / 纯空白: 常态噪声, 静默
        }

        if (trimmed[0] != '{')
        {
            // banner 之类: 静默跳过(见类注释「为什么跳过而不是报错」), 但截短后留一条 Trace 便于排障
            Log.Trace(Category, $"忽略非协议行: {Preview(line)}");
            return null;
        }

        var frame = ParseLine(line);
        if (frame is null)
        {
            reason = $"Worker 收到以 '{{' 开头却无法解析的行, 已跳过(不中断读循环): {Preview(line)}";
        }

        return frame;
    }

    /// <summary>
    /// 剥掉一行开头的空白与 UTF-8 BOM。
    /// <para>两者都要容忍: 空白是"对面用了个带缩进的格式化输出"的常态, BOM 则是本仓自己
    /// <c>StreamWriter</c> 的默认行为(见 <see cref="WorkerFrameWriter"/> 的 Utf8NoBom 说明)。
    /// 不剥 BOM 的后果是<b>第一帧被当成 banner 丢掉</b> —— 表现为"握手永远超时"且日志干净。</para>
    /// </summary>
    private static ReadOnlySpan<char> TrimFramePrefix(ReadOnlySpan<char> line)
    {
        var span = line.TrimStart();
        if (span.Length > 0 && span[0] == '\uFEFF')
        {
            span = span[1..].TrimStart();
        }

        return span;
    }

    /// <summary>给日志用的行预览(截断 + 压成单行, 免得一条日志变成十几行)。</summary>
    private static string Preview(string line)
    {
        var flat = line.ReplaceLineEndings(" ").Trim();
        return flat.Length <= MaxLoggedChars
            ? flat
            : string.Concat(flat[..MaxLoggedChars], $"…(共 {line.Length} 字符)");
    }

    /// <summary>
    /// 一个流的读缓冲 + 跨调用的残留字节。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么自己管字节而不用 <c>StreamReader</c></b>: 缓冲必须跨 <c>ReadAsync</c> 调用存活,
    /// 而对外签名是静态的(只收流)。<c>StreamReader</c> 的缓冲是它的私有字段, 静态方法拿不到。</para>
    ///
    /// <para><b>为什么不用 1 字节读</b>(那是最省事的无状态实现): 管道上每字节一次 <c>read(2)</c>,
    /// 一个 1MB 的 <c>read_file</c> 响应就是百万次系统调用, 足以让"读一个文件"变成秒级操作。
    /// 8KB 起、按需翻倍的分块读把这个代价压到可忽略。</para>
    /// </remarks>
    private sealed class LineBuffer
    {
        /// <summary>初始容量 8KB: 绝大多数帧(请求/通知)远小于此, 一次读就够。</summary>
        private const int InitialCapacity = 8 * 1024;

        /// <summary>每次至少腾出的空闲空间。太小会让"大帧"退化成大量小读。</summary>
        private const int MinReadChunk = 4 * 1024;

        /// <summary>
        /// 病态兜底(256MB): 对端连一个换行都不写时的内存上限。
        /// <para>⚠️ 这<b>不是协议限制</b>: 合法帧可以远大于 8KB(见文件头纪律第 2 条),
        /// 正常情况下永远碰不到这条线。碰到它说明对端要么坏了要么在刷垃圾,
        /// 此时抛 <see cref="InvalidDataException"/> 让上层走崩溃处理(明确日志 + 完结在飞请求),
        /// 比"悄悄吃掉 256MB 内存"好。</para>
        /// </summary>
        public const int MaxFrameBytes = 256 * 1024 * 1024;

        private byte[] _bytes = [];
        private int _start;
        private int _end;
        private bool _bomChecked;

        /// <summary>对端是否已 EOF。</summary>
        public bool Eof { get; set; }

        /// <summary>当前可读入的空闲区(长度 = 容量 - 已填充)。</summary>
        public Memory<byte> Free => _bytes.AsMemory(_end);

        /// <summary>记录一次读入的字节数(并顺手剥掉开头的 UTF-8 BOM)。</summary>
        public void Commit(int count)
        {
            if (count <= 0)
            {
                return;
            }

            if (!_bomChecked)
            {
                _bomChecked = true;

                // 只在首读就拿到全部 3 字节时才剥: 管道读要么拿到数据要么阻塞,
                // 真出现"只读到 1~2 字节的 BOM"时留给 TrimFramePrefix 兜底, 不引入半截状态机。
                if (count >= 3 && _bytes[_start] == 0xEF && _bytes[_start + 1] == 0xBB && _bytes[_start + 2] == 0xBF)
                {
                    _start += 3;
                }
            }

            _end += count;
        }

        /// <summary>取出一个已完成的行(不含行尾); 没有则先腾挪/扩容并返回 false。</summary>
        public bool TryTakeLine(out string? line)
        {
            line = null;

            var index = _bytes.AsSpan(_start, _end - _start).IndexOf((byte)'\n');
            if (index < 0)
            {
                if (_end - _start >= MaxFrameBytes)
                {
                    throw new InvalidDataException(
                        $"Worker 帧超过 {MaxFrameBytes} 字节仍未遇到换行: 判定为对端失控(而不是合法大帧), 断开");
                }

                Compact();
                EnsureRoom();
                return false;
            }

            var length = index;

            // 容忍 CRLF: Windows 上写文本文件/某些运行时会带 \r, 留着会让 JSON 解析失败
            if (length > 0 && _bytes[_start + length - 1] == (byte)'\r')
            {
                length--;
            }

            line = Utf8.GetString(_bytes, _start, length);
            _start += index + 1;
            Compact();
            return true;
        }

        /// <summary>EOF 时取走缓冲里剩下的内容(最后一帧可能没有换行结尾); 没有则返回 null。</summary>
        public string? TakeRemainder()
        {
            var length = _end - _start;
            if (length <= 0)
            {
                return null;
            }

            var rest = Utf8.GetString(_bytes, _start, length);
            _start = _end;
            Compact();
            return rest;
        }

        /// <summary>把未消费字节挪到头部(消费完时什么都不做)。</summary>
        private void Compact()
        {
            if (_start == 0)
            {
                return;
            }

            var remaining = _end - _start;
            if (remaining > 0)
            {
                Buffer.BlockCopy(_bytes, _start, _bytes, 0, remaining);
            }

            _start = 0;
            _end = remaining;
        }

        /// <summary>保证至少还有 <see cref="MinReadChunk"/> 的空闲空间(不足则按 2 倍扩容)。</summary>
        private void EnsureRoom()
        {
            // Compact 之后未消费字节一定在头部, 所以直接 Array.Resize(它会拷贝 [0.._end))
            if (_bytes.Length - _end >= MinReadChunk)
            {
                return;
            }

            var used = _end;
            var capacity = Math.Max(_bytes.Length == 0 ? InitialCapacity : _bytes.Length * 2L, used + (long)MinReadChunk);
            if (capacity > MaxFrameBytes)
            {
                capacity = MaxFrameBytes;
            }

            Array.Resize(ref _bytes, (int)capacity);
        }

        /// <summary>解码用编码: <c>Encoding.UTF8</c> 的 <c>GetString</c> 不涉及 BOM, 只需容忍非法字节。</summary>
        private static readonly UTF8Encoding Utf8 =
            new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    }
}

/// <summary>
/// 对端回了 JSON-RPC <c>error</c> 帧时抛出的异常(由 <see cref="WorkerRpcCore.DispatchResponse"/> 抛出)。
/// </summary>
/// <remarks>
/// <para><b>为什么要有这个类型, 而不是直接抛 <see cref="InvalidOperationException"/></b>(McpClientBase 的做法):
/// 传输层必须能区分「对端明确回了一个 error」(连接可能是好的, 只是这次调用失败) 与
/// 「写侧失败/连接已死」(必须广播 <see cref="IToolTransport.Faulted"/>) —— 两者的上层动作完全相反:
/// 前者照常按工具级失败返回给 LLM, 后者要换一条路(重拉 Worker / 降级到内联)。
/// 靠 <c>ex.Message</c> 的前缀来区分这种语义是典型的脆弱做法, 一个文案改动就会静默改变故障分类。</para>
///
/// <para><b>刻意继承 <see cref="InvalidOperationException"/></b>: 保持与既有调用方
/// (McpClientBase 时代的"请求失败 = InvalidOperationException")的 catch 兼容性,
/// 同时给新代码一个精确的类型判据。</para>
/// </remarks>
public sealed class WorkerRpcException : InvalidOperationException
{
    public WorkerRpcException(string message, int? code, JsonNode? data) : base(message)
    {
        Code = code;
        ErrorData = data;
    }

    /// <summary>JSON-RPC 错误码(如 -32601 方法未找到); 对端没给或不是整数时为 null。</summary>
    public int? Code { get; }

    /// <summary>原始 <c>error.data</c> 节点(可含诊断细节), 无则 null。</summary>
    /// <remarks>
    /// 刻意<b>不叫</b> <c>Data</c> 而叫 <c>ErrorData</c>: <see cref="Exception.Data"/> 是 .NET
    /// 自带的 IDictionary(异常自定义数据), 与 JSON-RPC 的 <c>error.data</c> 完全是两回事。
    /// 同名会让读代码的人(含未来的自己)分不清手里那个字典是谁的。
    /// </remarks>
    public JsonNode? ErrorData { get; }
}

/// <summary>请求/响应配对内核(两侧共用): id 分配、Pending 表、超时/取消、故障时完结在飞请求。</summary>
/// <remarks>
/// <para><b>为什么不把配对逻辑塞进 <c>McpClientBase</c></b>: 那是一个"实现 MCP 客户端"的基类,
/// 而本协议只是与它同构(MCP 也是 stdio + JSON-RPC), 契约内容完全不同(方法名、载荷、超时预算)。
/// 强行共用会让 MCP 侧被迫理解 Worker 的帧, 反之亦然。共用的只有"形状", 形状本身就是本类。</para>
///
/// <para><b>超时/取消语义完全委托 <see cref="ToolTransportContract.AwaitAsync"/></b>:
/// 这是刻意的 —— <c>IToolTransport</c> 的实现约定第 1 条要求"两个实现逐字一致",
/// 而超时/取消恰好是最容易各写各的(ExternalCancelFirst、无限时长不建 Task.Delay、
/// 判定只看外部 ct 这三条)。自己重写一遍就等于放弃这条约定。</para>
///
/// <para><b>本类不碰流</b>: 帧怎么写出去由构造时注入的 <c>transmit</c> 委托决定,
/// 于是同一份内核既能跑在父进程的管道上, 也能跑在子进程侧(以及将来的任何传输)上。</para>
/// </remarks>
public sealed class WorkerRpcCore : IDisposable
{
    /// <summary>日志 category(与 Worker 其余部分一致)。</summary>
    private const string Category = "Worker";

    private readonly Func<JsonObject, CancellationToken, Task> _transmit;
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _pending = new(StringComparer.Ordinal);

    private long _nextId;
    private int _faultRaised;
    private int _disposed;

    /// <summary>收到一帧 <c>result</c>/<c>error</c> 响应时广播(帧原样, 供 doctor 自检与调试旁挂)。</summary>
    /// <remarks>
    /// 配对本身<b>不依赖</b>本事件(见 <see cref="DispatchResponse"/>), 它只是可观测性出口。
    /// 逐订阅者 try/catch: 一个订阅者抛异常不能把读循环带走。
    /// </remarks>
    public event Action<JsonObject>? ResponseReceived;

    /// <summary>连接彻底不可用(一次性)。见 <see cref="Fault"/>。</summary>
    public event Action<string>? Faulted;

    /// <param name="transmit">把一帧写出去的委托(父侧写子 stdin / 子侧写父 stdout)。
    /// 它拿到的是<b>已按超时规则合并过</b>的令牌, 因此可以在内部直接 await。</param>
    public WorkerRpcCore(Func<JsonObject, CancellationToken, Task> transmit)
    {
        _transmit = transmit ?? throw new ArgumentNullException(nameof(transmit));
    }

    /// <summary>在飞请求数(诊断用: 故障时非 0 说明有东西被"永远挂起"了)。</summary>
    public int PendingCount
    {
        get { lock (_gate) { return _pending.Count; } }
    }

    /// <summary>这个时长是否表示「无限」。</summary>
    /// <remarks>
    /// 委托 <see cref="ToolTransportContract.IsUnbounded"/> 而不是自己写一遍:
    /// "哪些值算无限"是<b>共享契约</b>(<c>Task.Delay</c> 对超过 <c>uint.MaxValue</c> 毫秒直接抛,
    /// <c>CancelAfter</c> 只放行 -1ms), 两处各写一份必然漂移, 而漂移的代价是运行期崩溃。
    /// </remarks>
    public bool IsUnbounded(TimeSpan timeout) => ToolTransportContract.IsUnbounded(timeout);

    /// <summary>发一个请求并等它的响应。</summary>
    /// <param name="method">方法名(见 <see cref="WorkerProtocol"/> 的方法常量)。</param>
    /// <param name="parameters">params 载荷; null 表示不带 <c>params</c> 字段。</param>
    /// <param name="timeout">超时; 用 <see cref="WorkerProtocol.ToolCallTimeout"/>(无限)时由取消与崩溃兜底。</param>
    /// <param name="ct">外部取消令牌。<b>用户取消的语义优先于超时</b>(见 <see cref="ToolTransportContract"/> 类注释)。</param>
    /// <returns>响应帧里的 <c>result</c> 节点(可为空); 对端回 <c>error</c> 时抛 <see cref="WorkerRpcException"/>。</returns>
    public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters,
        TimeSpan timeout, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrEmpty(method);

        // 把方法名里的 "worker/" 去掉, 让超时文案读起来是「Worker tools/sync 超时(15s)」而不是叠两遍前缀
        var what = method.StartsWith("worker/", StringComparison.Ordinal) ? method["worker/".Length..] : method;

        // ⚠️ "登记挂起表 → 写帧 → 返回 TCS 任务"三件事必须都在 start 内部一次完成:
        // 这样"响应比登记先到"的竞态在物理上不可能发生(McpClientBase 同款理由)。
        // 超时/取消的判定完全由共享辅助做 —— 本方法不掺和。
        // T 推导成 JsonNode?(result 可缺失), 由 AwaitAsync 的 `class?` 约束接住。
        return await ToolTransportContract.AwaitAsync(
            token => ExchangeAsync(method, parameters, token), timeout, ct, what).ConfigureAwait(false);
    }

    /// <summary>传输层收到一帧<b>响应</b>时调用: 按 id 配对给等待方。</summary>
    /// <remarks>
    /// <para>为什么"没有等待者"要记 <b>Warn</b> 而不静默丢(照 <c>McpClientBase.DispatchMessage</c>):
    /// 静默丢的两种来源是「超时/取消之后的迟到响应」(正常) 与「两侧 id 口径不一致」(bug)。
    /// 后者的症状是"Worker 从来不响应", 而日志里干干净净 —— 这是最难查的一类故障,
    /// 一条 Warn 就能把它从"正常"里摘出来。响应太多不至于刷屏: 一次故障最多多出「在飞数」条。</para>
    ///
    /// <para><b>带 <c>method</c> 的帧不是响应</b>(那是请求), 这里只记 Warn 交回传输层处理 ——
    /// 请求必须由「知道该回什么」的那一侧应答, 内核没有这个立场。</para>
    /// </remarks>
    public void DispatchResponse(JsonObject frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.TryGetPropertyValue("method", out _))
        {
            Log.Warn(Category, $"Worker 收到带 method 的帧却当响应派发(id={frame["id"]?.ToJsonString() ?? "(无)"}), 已忽略");
            return;
        }

        frame.TryGetPropertyValue("id", out var idNode);
        var key = NormalizeRpcId(idNode);
        if (key is null)
        {
            Log.Warn(Category, "Worker 收到既无 id 也无 method 的响应帧, 已忽略");
            return;
        }

        var hasResult = frame.TryGetPropertyValue("result", out var resultNode);
        var hasError = frame.TryGetPropertyValue("error", out var errorNode);

        TaskCompletionSource<JsonNode?>? waiter;
        lock (_gate)
        {
            _pending.Remove(key, out waiter);
        }

        if (waiter is null)
        {
            Log.Warn(Category,
                $"Worker 响应没有对应的等待者, 已丢弃(id={key}); 常见于超时/取消后的迟到响应, " +
                "若持续出现则说明两侧的 id 口径不一致");
        }
        else if (hasError)
        {
            waiter.TrySetException(BuildRpcException(errorNode));
        }
        else if (hasResult)
        {
            waiter.TrySetResult(resultNode);
        }
        else
        {
            Log.Warn(Category, $"Worker 响应既无 result 也无 error(id={key})");
            waiter.TrySetException(new InvalidOperationException($"Worker 响应既无 result 也无 error(id={key})"));
        }

        if (hasResult || hasError)
        {
            RaiseSafe(ResponseReceived, frame);
        }
    }

    /// <summary>把在飞请求全部完成为错误。<b>传输层释放时也走这里, 但不触发 <see cref="Faulted"/>。</b></summary>
    /// <remarks>
    /// <para><b>⚠️ 必须先在锁内取快照再遍历</b>: 一边遍历 <c>Pending.Values</c> 一边让响应派发路径
    /// 摘除条目会抛 <see cref="InvalidOperationException"/>, 而那恰好发生在"处理崩溃"的时刻 ——
    /// 会把崩溃处理本身再崩一次, 于是既没人在飞请求被完结、也没人看到 Faulted。
    /// 快照遍历由 <see cref="ToolTransportContract.FailAllPending{T}"/> 提供(两个实现共用同一份)。</para>
    /// </remarks>
    public void FailAllPending(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);

        List<TaskCompletionSource<JsonNode?>> snapshot;
        lock (_gate)
        {
            // 快照 + 清表: 清表让后来的 DispatchResponse 一律走"没有等待者"的 Warn 分支,
            // 不会出现"完结了却还被摘走"的错位。
            snapshot = [.. _pending.Values];
            _pending.Clear();
        }

        ToolTransportContract.FailAllPending(snapshot, reason);
    }

    /// <summary>宣布连接彻底不可用: 完结全部在飞请求 + <b>至多一次</b>广播 <see cref="Faulted"/>。</summary>
    /// <remarks>
    /// <para><b>为什么与 <see cref="FailAllPending"/> 分成两个方法</b>: "把在飞请求完结"和
    /// "广播连接已死"是两件事。<b>正常释放</b>(父进程退出、会话被删、WorkerManager 收工)只需要前者 ——
    /// 此刻若广播 <see cref="Faulted"/>, 上层会当成"Worker 崩了"而去重拉/降级/记故障,
    /// 于是一次干净的收尾产出一条假崩溃记录, 还可能在退出路径上触发一次无意义的重拉尝试。</para>
    /// </remarks>
    public void Fault(string reason)
    {
        FailAllPending(reason);

        if (Interlocked.Exchange(ref _faultRaised, 1) != 0)
        {
            return; // 至多一次: 上层按事件做计数/加锁, 重复触发就是重复降级
        }

        Log.Warn(Category, $"Worker 连接不可用: {reason}");
        RaiseSafe(Faulted, reason);
    }

    /// <summary>释放: 完结在飞请求并关闸。<b>不触发 <see cref="Faulted"/></b>, 理由见 <see cref="Fault"/>。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        FailAllPending("Worker RPC 内核已释放");
    }

    /// <summary>登记 id → 写帧 → 等响应。配对与超时/取消的判定都在这一个方法里闭环。</summary>
    private async Task<JsonNode?> ExchangeAsync(string method, JsonObject? parameters, CancellationToken token)
    {
        var waiter = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 让"没人观察的异常"变成已观察: 取消竞速(AwaitAsync 的哨兵先完成)之后本方法会被放弃,
        // 而迟到的 error 响应仍会 TrySetException 到这个 TCS 上 —— 无人观察的 faulted Task
        // 会在别的线程上触发 UnobservedTaskException, 炸出一条与本处毫无关系的噪声。
        ObserveQuietly(waiter.Task);

        var frame = new JsonObject
        {
            ["jsonrpc"] = WorkerProtocol.JsonRpcVersion,
            ["method"] = method,
        };

        if (parameters is not null)
        {
            frame["params"] = parameters;
        }

        string key;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            // 单调 long 足够: 同一进程生命周期内发出的请求数不可能接近 long.MaxValue,
            // 而随机 id 需要额外依赖(且对"两侧按顺序对齐"的排障没帮助)。
            frame["id"] = ++_nextId;

            // ⚠️ 键必须与 DispatchResponse 侧走同一个归一化函数(McpClientBase 同款理由):
            // JSON-RPC 允许 id 是数字或字符串, 而对端回显的类型可能与我们发出时不同。
            key = NormalizeRpcId(frame["id"])!;
            _pending[key] = waiter;
        }

        try
        {
            // 写失败(管道已断)会在这里抛; 此时挂起表里那条必须摘掉, 否则永远等不到任何人
            await _transmit(frame, token).ConfigureAwait(false);
            return await waiter.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                // id 单调递增 ⇒ 这个键只可能登记过本请求一次, 直接摘掉即可
                // (超时/写失败已提前返回时它是唯一一条; 正常完成时早已被 DispatchResponse 摘走)
                _pending.Remove(key);
            }

            // 无人在等(超时/写失败已提前返回)时也要终结, 免得这个 Task 永远悬着
            waiter.TrySetCanceled();
        }
    }

    /// <summary>
    /// JSON-RPC 的 id 归一化为字符串键。请求与响应<b>共用</b>这一个实现 ——
    /// 两处各写一份是"响应永远配不上等待者"这类故障最常见的成因。
    /// </summary>
    private static string? NormalizeRpcId(JsonNode? idNode)
    {
        if (idNode is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var number))
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<string>(out var text) && text.Length > 0)
        {
            return text;
        }

        // 兜底: 少数实现会把 id 写成别的标量类型, 用其字面文本当键至少不会漏配对
        var raw = value.ToString();
        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    /// <summary>把 <c>error</c> 载荷翻译成异常。code 与 data 一并带上, 排障时不必回去抓包。</summary>
    private static WorkerRpcException BuildRpcException(JsonNode? errorNode)
    {
        if (errorNode is JsonObject error)
        {
            int? code = error.TryGetPropertyValue("code", out var codeNode) && codeNode is JsonValue cv &&
                         cv.TryGetValue<int>(out var codeValue)
                ? codeValue
                : null;

            var message = error.TryGetPropertyValue("message", out var messageNode) &&
                          messageNode is JsonValue mv && mv.TryGetValue<string>(out var text)
                ? text
                : "(无 message)";

            var data = error.TryGetPropertyValue("data", out var dataNode) ? dataNode : null;
            var dataText = data is null ? string.Empty : $"; data={data.ToJsonString()}";

            return new WorkerRpcException(
                $"Worker 返回错误(code={(code?.ToString(CultureInfo.InvariantCulture)) ?? "?"}): {message}{dataText}",
                code,
                data);
        }

        // error 不是对象(对端犯协议错): 照样给调用方一个异常, 绝不让它静默变成"空结果"
        return new WorkerRpcException(
            $"Worker 返回了非对象的 error 载荷: {errorNode?.ToJsonString() ?? "(空)"}", null, null);
    }

    /// <summary>逐订阅者投递(单个订阅者异常不影响其余订阅者与投递方)。</summary>
    private static void RaiseSafe<T>(Action<T>? handlers, T payload)
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
                Log.Warn(Category, ex, "Worker RPC 内核事件订阅者处理异常");
            }
        }
    }

    /// <summary>
    /// 给任务挂一个"把异常读掉"的续体。
    /// <para>只读不吞: 正常 await 的那条路径照样拿到异常; 这里只是不让"已被放弃"的那条路径
    /// 触发 <see cref="TaskScheduler.UnobservedTaskException"/>。</para>
    /// </summary>
    private static void ObserveQuietly(Task task) =>
        _ = task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}