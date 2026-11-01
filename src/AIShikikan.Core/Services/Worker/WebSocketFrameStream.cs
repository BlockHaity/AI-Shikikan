using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

using AIShikikan.Core.Logging;

namespace AIShikikan.Core.Services.Worker;

/*
 * WebSocket 传输层: 把 RFC6455 帧还原成字节消息, 再把消息包成 Stream 交给协议层。
 *
 * 为什么手写帧编解码而不引 System.Net.WebSockets / HttpListener:
 *   1. 服务端这里只需要「一段字节 = 一条消息」这一件事, 不需要 WebSocket 的会话管理;
 *   2. HttpListener 在 Linux 上会拖平台差异, 而 Native AOT 与「零新依赖」是硬约束;
 *   3. 握手只校验 path / Upgrade / Sec-WebSocket-Key / Bearer token 四项, 用 NetworkStream
 *      逐字节读还能控制「不把第一帧读进 HTTP 头缓冲」这个细节 —— 一旦吞掉, 首帧会静默丢失,
 *      表现为握手成功但父进程的 worker/hello 永远等不到响应。
 *
 * 三层职责(都在本文件里, 因为只有一起看才能说清消息边界):
 *   ServerWebSocketChannel —— 只认帧: 握手 / mask 校验 / 分片拼接 / ping-pong / close;
 *   WebSocketFrameStream  —— 只认流: ReadAsync 直连通道, WriteAsync 一次写 = 一条消息;
 *   IWebSocketChannel     —— 两者之间的契约, 将来客户端侧复用同一形状时只需换实现。
 *
 * ⚠️ 消息边界与 NDJSON 的配合: WorkerFrameWriter 写一帧是「内容 + 换行」两次 WriteAsync,
 * 于是 WS 上会变成两条消息。对端若按「消息 = 一行」解析会漏掉换行, 必须像 WorkerFrameReader
 * 那样跨消息持有行缓冲。这里不替它粘合, 因为粘合会破坏「一次 WriteAsync 一条消息」的语义。
 */

/// <summary>
/// 一条 WebSocket 连接的最小抽象: 收一条消息、发一条消息。
/// </summary>
/// <remarks>
/// <para><b>ReceiveAsync 的返回值</b>: 本次写入 <c>destination</c> 的字节数, <b>0 表示对端已关闭</b>
/// (收到 close 帧或底层流 EOF)。同一条消息若大于 <c>destination</c>, 剩余字节留在实现内部,
/// 下一次调用继续返回<b>同一条消息</b>的剩余部分 —— 这是 <see cref="Stream"/> 语义的要求:
/// <see cref="WorkerFrameReader"/> 的读缓冲从 8KB 起按需扩容, 协议帧却可以远超 8KB,
/// 一次性投递会在第一条大帧上就失败。</para>
/// <para><b>SendAsync</b>: 整段 <c>data</c> 作为<b>一条</b>消息发送, 由实现负责加锁与分帧。</para>
/// </remarks>
public interface IWebSocketChannel : IDisposable
{
    /// <summary>收一条消息的一段; 返回 0 表示对端关闭。实现需支持任意大小的 destination。</summary>
    Task<int> ReceiveAsync(Memory<byte> destination, CancellationToken ct);

    /// <summary>把整段数据作为一条 WebSocket 消息发出。</summary>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
}

/// <summary>
/// 把 <see cref="IWebSocketChannel"/> 包成 <see cref="Stream"/>: 协议层(WorkerServer /
/// WorkerFrameReader / WorkerFrameWriter)完全不需要知道底下是 WebSocket 还是管道。
/// </summary>
/// <remarks>
/// <para><b>为什么写方向必须是「一次 WriteAsync 一条消息」而不是攒成一条</b>: 协议层本来就把
/// 「一帧一次写」当成原子语义(WorkerFrameWriter 自己持有写锁)。在这里再攒一次会引入额外的
/// 缓冲与刷新时机, 收益为零, 风险是「攒着的那一段永远发不出去」。</para>
/// <para><b>同步方法一律 NotSupported</b>: 协议层全程走异步路径(stdio 管道本来也不允许
/// 同步 Read/Write 阻塞 UI 线程)。留一个同步实现只会诱使后来者在读循环里同步等待。</para>
/// <para><b>Dispose 关闭通道</b>: 通道的 Dispose 只是「尽力发 close 帧」, 不关 NetworkStream
/// (socket 由 TcpClient 管), 所以这里贴一层 Dispose 不会连带关掉别人的连接。</para>
/// </remarks>
public sealed class WebSocketFrameStream : Stream
{
    private readonly IWebSocketChannel _channel;
    private int _disposed;

    public WebSocketFrameStream(IWebSocketChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException("WebSocket 消息流没有可寻址的长度");

    public override long Position
    {
        get => throw new NotSupportedException("WebSocket 消息流不支持定位");
        set => throw new NotSupportedException("WebSocket 消息流不支持定位");
    }

    public override void Flush()
    {
        // 每次写都已经整条消息发出并 Flush, 这里无事可做。
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("请使用 ReadAsync: WebSocket 读取是异步且可能长时间等待的");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("请使用 WriteAsync: WebSocket 写入是异步的");

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("WebSocket 消息流不支持定位");

    public override void SetLength(long value) =>
        throw new NotSupportedException("WebSocket 消息流不支持设置长度");

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _channel.ReceiveAsync(new Memory<byte>(buffer, offset, count), cancellationToken);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return new ValueTask<int>(_channel.ReceiveAsync(buffer, cancellationToken));
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _channel.SendAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return new ValueTask(_channel.SendAsync(buffer, cancellationToken));
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _channel.Dispose();
        }

        base.Dispose(disposing);
    }
}

/*
 * RFC6455 服务端帧编解码。
 *
 * 为什么校验 masked 而不是容忍未加掩码的帧: RFC6455 §5.1 要求客户端发出的每一帧都带掩码。
 * 放行未加掩码的帧等于允许「浏览器同源之外的、本不该连上来的实现」按自己方便的方式发帧,
 * 而且掩码是服务端唯一能确认「这是一个正常客户端」的低成本信号。
 *
 * 为什么单条消息上限 64MB: 协议帧本来就该小(一行 NDJSON 通常几十 KB), 64MB 足够容纳任何
 * 合法的 read_file 响应, 同时挡住「对端不停发分片」时的无界内存增长。超限抛
 * InvalidOperationException 而不是静默截断 —— 截断会表现为「文件读到一半就没了」。
 */
internal sealed class ServerWebSocketChannel : IWebSocketChannel
{
    private const string LogCategory = "Worker";

    /// <summary>握手请求头上限。正常握手连 1KB 都不到, 16KB 足够容纳任何代理附加的头。</summary>
    private const int MaxHeaderBytes = 16 * 1024;

    /// <summary>单条消息(含分片拼接后的总长度)上限。</summary>
    private const int MaxMessageBytes = 64 * 1024 * 1024;

    /// <summary>Dispose 时发 close 帧的预算。对端不读时不能把调用方钉死在这里。</summary>
    private static readonly TimeSpan CloseFrameBudget = TimeSpan.FromSeconds(1);

    private const int OpcodeContinuation = 0;
    private const int OpcodeText = 1;
    private const int OpcodeBinary = 2;
    private const int OpcodeClose = 8;
    private const int OpcodePing = 9;
    private const int OpcodePong = 10;

    /// <summary>握手 Accept 的固定拼接串(RFC6455 §4.2.2)。</summary>
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly NetworkStream _stream;

    /// <summary>写锁: 数据帧与 ping 应答 / close 帧可能来自不同任务, NetworkStream 不允许交错写。</summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>上一条消息没被 destination 一次吃完时的剩余字节(空表示无残留)。</summary>
    private ReadOnlyMemory<byte> _pending;

    /// <summary>对端已关闭(close 帧或 EOF)。此后 ReceiveAsync 直接返回 0, 不再碰底层流。</summary>
    private volatile bool _closed;

    private int _disposed;

    /// <param name="stream">已完成 HTTP 升级握手的连接流(其 socket 由 TcpClient 持有)。</param>
    public ServerWebSocketChannel(NetworkStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    // ───────────────────────── 接收 ─────────────────────────

    /// <summary>收一条消息的一段。0 = 对端关闭(close 帧 / EOF), 且此后恒为 0。</summary>
    public async Task<int> ReceiveAsync(Memory<byte> destination, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (destination.IsEmpty)
        {
            // Stream 契约允许 0 长度读立即返回; 调用方(WorkerFrameReader)不会传空缓冲。
            return 0;
        }

        if (_closed)
        {
            return 0;
        }

        if (!_pending.IsEmpty)
        {
            return DrainPending(destination);
        }

        while (true)
        {
            var message = await ReadMessageAsync(ct).ConfigureAwait(false);
            if (message is null)
            {
                _closed = true;
                return 0;
            }

            if (message.Length == 0)
            {
                // 空消息在 NDJSON 协议里没有意义; 若把它当成 0 返回, 调用方会误判为对端关闭。
                continue;
            }

            if (message.Length <= destination.Length)
            {
                message.AsSpan().CopyTo(destination.Span);
                return message.Length;
            }

            message.AsSpan(0, destination.Length).CopyTo(destination.Span);
            _pending = message.AsMemory(destination.Length);
            return destination.Length;
        }
    }

    /// <summary>把残留的消息字节写进 destination; 写完后清空残留。</summary>
    private int DrainPending(Memory<byte> destination)
    {
        var count = Math.Min(destination.Length, _pending.Length);
        _pending.Span.Slice(0, count).CopyTo(destination.Span);
        _pending = _pending.Slice(count);
        return count;
    }

    /// <summary>读一条完整消息。null = 对端关闭(已回 close 帧或读到 EOF)。</summary>
    private async Task<byte[]?> ReadMessageAsync(CancellationToken ct)
    {
        MemoryStream? assembling = null;
        var assemblingLength = 0;
        var fragmenting = false;

        while (true)
        {
            var header = await ReadFrameHeaderAsync(ct).ConfigureAwait(false);
            if (header is null)
            {
                return null;
            }

            var frame = header.Value;
            switch (frame.Opcode)
            {
                case OpcodeClose:
                {
                    var payload = await ReadPayloadAsync(frame, 0, ct).ConfigureAwait(false);
                    await SendCloseEchoAsync(payload, ct).ConfigureAwait(false);
                    Log.Debug(LogCategory, "收到 WebSocket close 帧, 已回 close 并结束接收");
                    return null;
                }

                case OpcodePing:
                {
                    var payload = await ReadPayloadAsync(frame, 0, ct).ConfigureAwait(false);
                    await SendFrameAsync(OpcodePong, payload, ct).ConfigureAwait(false);
                    continue;
                }

                case OpcodePong:
                    // pong 只是心跳应答, 本协议自身另有 notify/heartbeat, 这里丢掉即可。
                    await ReadPayloadAsync(frame, 0, ct).ConfigureAwait(false);
                    continue;

                case OpcodeText:
                case OpcodeBinary:
                {
                    if (fragmenting)
                    {
                        throw new InvalidOperationException(
                            "WebSocket 分片顺序非法: 上一个分片尚未以 continuation 收尾, 就收到了新的数据帧");
                    }

                    var payload = await ReadPayloadAsync(frame, 0, ct).ConfigureAwait(false);
                    if (frame.Fin)
                    {
                        return payload;
                    }

                    fragmenting = true;
                    assembling = new MemoryStream();
                    assembling.Write(payload);
                    assemblingLength = payload.Length;
                    continue;
                }

                case OpcodeContinuation:
                {
                    if (!fragmenting || assembling is null)
                    {
                        throw new InvalidOperationException(
                            "WebSocket 分片顺序非法: 收到 continuation, 但没有前导的数据帧");
                    }

                    var payload = await ReadPayloadAsync(frame, assemblingLength, ct).ConfigureAwait(false);
                    assemblingLength += payload.Length;
                    assembling.Write(payload);

                    if (!frame.Fin)
                    {
                        continue;
                    }

                    var message = assembling.ToArray();
                    assembling.Dispose();
                    return message;
                }

                default:
                    throw new InvalidOperationException($"WebSocket 帧 opcode {frame.Opcode} 非法");
            }
        }
    }

    /// <summary>读帧头。返回 null 表示「帧头之前对端就关了」, 与收到 close 等价。</summary>
    private async Task<FrameHeader?> ReadFrameHeaderAsync(CancellationToken ct)
    {
        var head = new byte[2];
        if (!await TryReadExactlyAsync(head, ct).ConfigureAwait(false))
        {
            return null;
        }

        var fin = (head[0] & 0x80) != 0;
        var opcode = head[0] & 0x0F;

        if ((head[0] & 0x70) != 0)
        {
            throw new InvalidOperationException("WebSocket 帧设置了保留位(RSV), 本服务端未协商任何扩展");
        }

        if ((head[1] & 0x80) == 0)
        {
            throw new InvalidOperationException(
                "客户端 WebSocket 帧缺少 mask 位: RFC6455 要求客户端发出的每一帧都必须加掩码");
        }

        var len7 = head[1] & 0x7F;
        long length;
        switch (len7)
        {
            case 126:
            {
                var ext = new byte[2];
                if (!await TryReadExactlyAsync(ext, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("WebSocket 帧在扩展长度中途结束");
                }

                length = BinaryPrimitives.ReadUInt16BigEndian(ext);
                break;
            }

            case 127:
            {
                var ext = new byte[8];
                if (!await TryReadExactlyAsync(ext, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("WebSocket 帧在扩展长度中途结束");
                }

                length = BinaryPrimitives.ReadInt64BigEndian(ext);
                if (length < 0)
                {
                    throw new InvalidOperationException("WebSocket 帧长度超出 RFC6455 允许范围(最高位为 1)");
                }

                break;
            }

            default:
                length = len7;
                break;
        }

        if (length > MaxMessageBytes)
        {
            throw new InvalidOperationException(
                $"WebSocket 单条消息超过上限 {MaxMessageBytes} 字节, 拒绝接收(疑似对端失控)");
        }

        // 上方已强制要求 masked, 所以这里一定有 4 字节掩码键。
        var mask = new byte[4];
        if (!await TryReadExactlyAsync(mask, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("WebSocket 帧在 mask 键中途结束");
        }

        return new FrameHeader(fin, opcode, length, mask);
    }

    /// <summary>读一帧载荷并就地解掩码。<paramref name="alreadyBuffered"/> 是此前已拼入的分片长度。</summary>
    private async Task<byte[]> ReadPayloadAsync(FrameHeader header, int alreadyBuffered, CancellationToken ct)
    {
        if (alreadyBuffered + header.Length > MaxMessageBytes)
        {
            throw new InvalidOperationException(
                $"WebSocket 单条消息超过上限 {MaxMessageBytes} 字节, 拒绝接收(疑似对端失控)");
        }

        var payload = new byte[(int)header.Length];
        if (payload.Length > 0 && !await TryReadExactlyAsync(payload, ct).ConfigureAwait(false))
        {
            throw new IOException("WebSocket 帧在载荷中途结束");
        }

        var mask = header.Mask;
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] ^= mask[i & 3];
        }

        return payload;
    }

    /// <summary>
    /// 读满 <paramref name="buffer"/>。返回 false 仅表示「一个字节都没读到就 EOF」;
    /// 读到一半才 EOF 属于坏帧, 抛异常。
    /// </summary>
    private async Task<bool> TryReadExactlyAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await _stream.ReadAsync(buffer.Slice(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new IOException("WebSocket 连接在帧中途关闭");
            }

            read += n;
        }

        return true;
    }

    // ───────────────────────── 发送 ─────────────────────────

    /// <summary>把整段数据作为一条 text 消息发出。</summary>
    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return SendFrameAsync(OpcodeText, data, ct);
    }

    /// <summary>回一条 close 帧; 失败只记 Debug —— close 是"尽力而为", 对端可能早已断开。</summary>
    private async Task SendCloseEchoAsync(byte[] payload, CancellationToken ct)
    {
        try
        {
            await SendFrameAsync(OpcodeClose, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(LogCategory, $"回 WebSocket close 帧失败(连接可能已断开): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>发一帧(服务端帧: FIN=1 且不加 mask)。</summary>
    /// <remarks>
    /// 写锁必须覆盖「帧头 + 载荷 + Flush」整段, 否则并发的两帧会在字节流里交错,
    /// 对端解析出的是两条都坏的帧 —— 而症状通常是偶发的"消息里混进半个 JSON"。
    /// </remarks>
    private async Task SendFrameAsync(int opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var header = BuildHeader(opcode, payload.Length);
            await _stream.WriteAsync(header.AsMemory(), ct).ConfigureAwait(false);
            if (!payload.IsEmpty)
            {
                await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
            }

            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>构造服务端帧头: FIN=1、不 mask、按 7/16/64 位选择长度编码。</summary>
    private static byte[] BuildHeader(int opcode, int length)
    {
        var header = length <= 125
            ? new byte[2]
            : length <= ushort.MaxValue
                ? new byte[4]
                : new byte[10];

        header[0] = (byte)(0x80 | opcode); // FIN=1
        if (length <= 125)
        {
            header[1] = (byte)length;
        }
        else if (length <= ushort.MaxValue)
        {
            header[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)length);
        }
        else
        {
            header[1] = 127;
            // 显式转 long: 否则重载会挑 WriteInt32BigEndian, 把长度写进高 4 字节, 帧直接失效。
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(2), (long)length);
        }

        return header;
    }

    /// <summary>
    /// 关闭: 尽力发 close 帧并置闸。**不关闭 NetworkStream** —— socket 的生命周期归 TcpClient,
    /// 这里关掉会让调用方在 Dispose 之后再也拿不到"对端已断开"的准确信号。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            // 对端不读时 WriteAsync 可能长时间不返回, 所以给整段 close 一个很短的预算。
            using var timeout = new CancellationTokenSource(CloseFrameBudget);
            SendFrameAsync(OpcodeClose, ReadOnlyMemory<byte>.Empty, timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // 关闭阶段的任何失败都不该冒泡: 调用方正在 finally 里收尾, 再抛会把真正的异常盖掉。
            Log.Debug(LogCategory, $"发送 WebSocket close 帧失败(连接可能已断开): {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ───────────────────────── HTTP 升级握手 ─────────────────────────

    /// <summary>
    /// 读取并校验 HTTP 升级请求, 成功时写出 101 响应。
    /// </summary>
    /// <remarks>
    /// <para><b>逐字节读是刻意的</b>: 用大缓冲一次读会把「CRLFCRLF 之后紧跟的第一条 WebSocket 帧」
    /// 一并读进手里, 而那部分字节无处归还给帧解码器 —— 表现为握手成功、首帧丢失,
    /// 结果是父进程的 worker/hello 永远等不到响应。</para>
    /// <para>失败时按 RFC 语义回 404(路径不符) / 400(不是合法升级请求) / 401(token 不符),
    /// 并返回 false, 由调用方关闭连接。所有错误响应都带 <c>Connection: close</c>。</para>
    /// </remarks>
    /// <param name="stream">已连接(尚未升级)的连接流。</param>
    /// <param name="expectedPath">期望的请求路径(不含 query, 如 <c>/worker</c>)。</param>
    /// <param name="requiredToken">必须匹配的 Bearer token; 空白视为无法鉴权, 一律拒绝。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<bool> TryUpgradeAsync(
        NetworkStream stream,
        string expectedPath,
        string requiredToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var expected = NormalizeRequestPath(expectedPath);
        var buffer = new byte[MaxHeaderBytes];
        var count = 0;

        // 只读到 CRLFCRLF 为止; 后面紧跟的字节必须是 WebSocket 帧, 一个都不能吞。
        while (true)
        {
            if (count >= buffer.Length)
            {
                Log.Warn(LogCategory, $"WebSocket 握手失败: 请求头超过 {MaxHeaderBytes} 字节仍未结束");
                await WriteHttpErrorAsync(stream, 400, "Bad Request", ct).ConfigureAwait(false);
                return false;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(count, 1), ct).ConfigureAwait(false);
            if (read == 0)
            {
                Log.Debug(LogCategory, "WebSocket 握手失败: 对端在请求头完成前关闭了连接");
                return false;
            }

            count += read;
            if (count >= 4
                && buffer[count - 4] == (byte)'\r'
                && buffer[count - 3] == (byte)'\n'
                && buffer[count - 2] == (byte)'\r'
                && buffer[count - 1] == (byte)'\n')
            {
                break;
            }
        }

        var text = Encoding.ASCII.GetString(buffer, 0, count);
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines.Length > 0 ? lines[0] : string.Empty;
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 3 || !string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn(LogCategory, $"WebSocket 握手失败: 请求行不是合法的 GET(\"{requestLine}\")");
            await WriteHttpErrorAsync(stream, 400, "Bad Request", ct).ConfigureAwait(false);
            return false;
        }

        var requestPath = ExtractRequestPath(parts[1]);
        if (!string.Equals(requestPath, expected, StringComparison.Ordinal))
        {
            Log.Warn(LogCategory, $"WebSocket 握手失败: 路径不符(请求={requestPath}, 期望={expected})");
            await WriteHttpErrorAsync(stream, 404, "Not Found", ct).ConfigureAwait(false);
            return false;
        }

        var headers = ParseHeaders(lines);
        var upgrade = headers.TryGetValue("Upgrade", out var upgradeValue) ? upgradeValue : null;
        if (upgrade is null || !upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn(LogCategory, "WebSocket 握手失败: 缺少 Upgrade: websocket 头");
            await WriteHttpErrorAsync(stream, 400, "Bad Request", ct).ConfigureAwait(false);
            return false;
        }

        var key = headers.TryGetValue("Sec-WebSocket-Key", out var keyValue) ? keyValue : null;
        if (string.IsNullOrWhiteSpace(key))
        {
            Log.Warn(LogCategory, "WebSocket 握手失败: 缺少 Sec-WebSocket-Key 头");
            await WriteHttpErrorAsync(stream, 400, "Bad Request", ct).ConfigureAwait(false);
            return false;
        }

        if (string.IsNullOrWhiteSpace(requiredToken))
        {
            Log.Warn(LogCategory, "WebSocket 握手失败: 服务端未配置 token, 拒绝所有连接");
            await WriteHttpErrorAsync(stream, 401, "Unauthorized", ct).ConfigureAwait(false);
            return false;
        }

        var authorization = headers.TryGetValue("Authorization", out var authValue) ? authValue : null;
        var expectedAuthorization = "Bearer " + requiredToken;
        if (!string.Equals(authorization, expectedAuthorization, StringComparison.Ordinal))
        {
            Log.Warn(LogCategory, "WebSocket 握手失败: Authorization 不是预期的 Bearer token");
            await WriteHttpErrorAsync(stream, 401, "Unauthorized", ct).ConfigureAwait(false);
            return false;
        }

        var accept = ComputeAccept(key.Trim());
        var response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n" +
            "\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        Log.Debug(LogCategory, $"WebSocket 握手成功: 路径={requestPath}");
        return true;
    }

    /// <summary>把请求头解析成大小写不敏感的字典; 同名头取最后一个。</summary>
    private static Dictionary<string, string> ParseHeaders(string[] lines)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            headers[name] = line[(colon + 1)..].Trim();
        }

        return headers;
    }

    /// <summary>取请求目标的路径部分: 去掉 query, 并把 absolute-form(<c>http://host/path</c>)归一成 path。</summary>
    private static string ExtractRequestPath(string target)
    {
        var path = target;
        var query = path.IndexOf('?');
        if (query >= 0)
        {
            path = path[..query];
        }

        var scheme = path.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var slash = path.IndexOf('/', scheme + 3);
            path = slash >= 0 ? path[slash..] : "/";
        }

        return path;
    }

    /// <summary>期望路径归一成以 <c>/</c> 开头; 空白回退到默认 <c>/worker</c>(与 WorkerConfig 默认一致)。</summary>
    private static string NormalizeRequestPath(string? path)
    {
        var value = string.IsNullOrWhiteSpace(path) ? "/worker" : path.Trim();
        return value.StartsWith('/') ? value : "/" + value;
    }

    /// <summary>Sec-WebSocket-Accept = Base64(SHA1(key + 固定 GUID))。</summary>
    private static string ComputeAccept(string key)
    {
        var digest = SHA1.HashData(Encoding.UTF8.GetBytes(key + WebSocketGuid));
        return Convert.ToBase64String(digest);
    }

    /// <summary>写一个 HTTP 错误响应。写失败一概吞掉: 此刻连接多半已经不可用。</summary>
    private static async Task WriteHttpErrorAsync(NetworkStream stream, int status, string reason, CancellationToken ct)
    {
        try
        {
            var response =
                $"HTTP/1.1 {status} {reason}\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                "Content-Length: 0\r\n" +
                "Connection: close\r\n" +
                "\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(LogCategory, $"写 WebSocket 握手错误响应失败({status}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>解析后的一帧头。</summary>
    private readonly record struct FrameHeader(bool Fin, int Opcode, long Length, byte[] Mask);
}
