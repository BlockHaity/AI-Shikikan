using System.Security.Cryptography;
using System.Text;
using AIShikikan.Core.Services.Session;

namespace AIShikikan.Core.Services.Worker;

/// <summary>
/// 独立 Worker 进程的协议常量: 方法名、版本号、超时预算与跨进程身份键。
/// 配套 DTO 见 <c>WorkerMessages.cs</c>(同命名空间), 源生成注册见
/// <c>AIShikikan.Core.Serialization.AppJsonContext</c>。
///
/// <para><b>传输形状</b>: 匿名管道(stdio) + NDJSON, 一行一帧, 复用 JSON-RPC 2.0 的
/// <c>jsonrpc/id/method/params/result/error</c> 骨架(与 <c>McpClientBase</c> 手写的 stdio
/// JSON-RPC 同构)。本文件只定义<b>契约常量</b>, 不含任何传输/序列化实现。</para>
///
/// <para><b>⚠️ 跨进程同版本契约(硬性)</b>: 本协议的所有 DTO 都走
/// <c>AppJsonContext</c> 序列化, 而该上下文<b>刻意不开 <c>UseStringEnumConverter</c></b>,
/// 枚举一律以<b>数字</b>落盘(例如 <c>Assignment.Status</c> 的 <c>SubagentStatus</c>)。
/// 数字枚举在两侧版本不一致时会静默错位(不抛异常, 只是解析成另一个状态), 因此:
/// <list type="bullet">
/// <item>握手时父进程必须下发并校验 <see cref="ProtocolVersion"/>; 不一致直接终止 Worker,
/// 绝不"先跑起来再说" —— 那会让枚举错位表现为 UI 上离奇的状态跳变, 极难定位;</item>
/// <item>DTO 的<b>字段增删是兼容的</b>(STJ 忽略未知字段、缺失字段取默认值),
/// 但<b>枚举成员的重排/删除/改名不兼容</b>; 任何改动都要抬 <see cref="ProtocolVersion"/>。</item>
/// </list>
/// 换言之: 新增可选字段不必抬版本, 改动任何跨进程出现的枚举必须抬。
/// </para>
///
/// <para><b>为什么复用 JSON-RPC 2.0 的形状而不是自定义二进制帧</b>: 帧本身要一行一条,
/// NDJSON 的换行边界就是帧边界, 与 JSON-RPC 的"一个请求一个 id 一个响应"天然契合;
/// 复用既有形状的收益是心智与调试工具(抓管道直接看)成本都最低, 且 AOT 友好 ——
/// 全程只有字符串切分 + 源生成 <c>JsonTypeInfo</c>, 不引入任何反射或新依赖。</para>
/// </summary>
public static class WorkerProtocol
{
    public const string JsonRpcVersion = "2.0";
    public const int ProtocolVersion = 1;

    // 父 → 子
    public const string MethodHello       = "worker/hello";
    public const string MethodToolsList   = "worker/tools/list";
    public const string MethodToolsSync   = "worker/tools/sync";
    public const string MethodToolsCall   = "worker/tools/call";
    public const string MethodPing        = "worker/ping";
    public const string MethodShutdown    = "worker/shutdown";

    // 子 → 父
    public const string NotifyToolOutput  = "notify/toolOutput";
    public const string NotifyAssignment  = "notify/assignment";
    public const string NotifyHeartbeat   = "notify/heartbeat";
    public const string NotifyCancel      = "notify/cancel";   // 父 → 子
    public const string RequestAskUser    = "request/askUser"; // 当前方案未启用(ask_user 留主进程), 保留协议位

    public static readonly TimeSpan HandshakeTimeout   = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ToolsListTimeout   = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan HeartbeatInterval  = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HeartbeatTimeout   = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ShutdownTimeout    = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan CancelTimeout      = TimeSpan.FromSeconds(5);

    /// <summary>工具执行超时: 刻意无限。</summary>
    /// <remarks>
    /// 子代理<b>合法</b>运行 30 分钟(<c>CliAgentDefinition.TimeoutMinutes</c> 默认 30,
    /// 上界 <c>MaxTimeoutMinutes</c> = 1440)。若沿用 MCP 的
    /// <c>McpClientBase.DefaultCallTimeout</c>(300s), 会把正常跑满上下文的长任务误杀成超时失败,
    /// 而且是<b>间歇性</b>的(只在任务偏长时炸), 极难与真实失败区分。
    /// 取消走独立的 <see cref="NotifyCancel"/> 通道 + <see cref="CancelTimeout"/>,
    /// 所以这里不需要时间兜底 —— 兜底本就该是"用户主动取消", 而不是"到点就杀"。
    /// </remarks>
    public static readonly TimeSpan ToolCallTimeout = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// 单行最大字符数: 8KB。沿用 <c>CliAgentRunner</c> 的既有单行上限(该处的
    /// <c>Truncate</c> 把子进程 stdout 的<b>每一行</b> 裁到 8KB 再上报)。
    ///
    /// <para><b>⚠️ 作用域只限"流式输出行", 绝不是协议帧上限</b>: 沿用的对象是"子进程输出流里的
    /// 一行", 对应本协议里 <c>WorkerToolOutputNotification.Line</c> 那种<b>增量进度</b>通知。
    /// 请求/响应帧(如 <c>WorkerToolCallResponse</c>, 内含整份文件内容与 <c>DetailJson</c>)
    /// 可以远超 8KB, 对它们套用本上限会把 <c>read_file</c> 的正常结果截成残缺内容 ——
    /// 表现为"文件读到一半就没了", 且没有任何错误提示。</para>
    /// </summary>
    public const int MaxLineChars = 8 * 1024;

    /// <summary>
    /// 跨进程身份键: 由工作目录 + 会话 Id 决定 Worker 实例的唯一键(16 位小写十六进制)。
    ///
    /// <para><b>为什么必须是哈希而不是拼串</b>: 该键要当进程表键、缓存键、可能还要进日志/文件名,
    /// 明文路径既过长又可能含非法文件名字符; SHA-256 前 8 字节(64 位)碰撞概率在本机规模下可忽略
    /// —— 与 <see cref="Git.GitCheckpointStore"/> 用 SHA-256 前 6 字节做仓库目录名同源。</para>
    ///
    /// <para><b>⚠️ 跨进程一致性约束(读这段前先看 <c>GitCheckpointStore.GetRepoHash</c> 的同类约束)</b>:
    /// 规范化<b>必须</b>与主进程算 key 时走<b>同一条路径</b>(都调本方法、都先
    /// <see cref="GitWorkspaceResolver.Normalize"/>), 否则 Worker 与主进程会算出两个不同的 key,
    /// 后果是<b>路由/缓存全部错位</b>: 父进程找不到自己 spawn 出来的 Worker(只能靠 pid 兜底孤儿),
    /// 而 Worker 收到的 <c>tools/call</c> 会被判给别的会话。
    /// 参照写法:<c>GitCheckpointStore.GetRepoHash</c> 对传入字符串<b>直接哈希、不做规范化</b>,
    /// 规范化被刻意上移到调用方(<c>GitWorkspaceContext.RepositoryRoot</c>)—— 即
    /// "哈希函数只吃已规范化的输入, 规范化责任在唯一上游"。本方法反其道而行(自己规范化),
    /// 代价是<b>多一个必须两端口径一致的隐式契约</b>; 换来的是任何一侧调用都不会漏掉规范化。
    /// 两种风格都可行, 但<b>绝不允许一半路径规范化、一半不规范化</b>。</para>
    ///
    /// <para><b>分隔符 "\n" 的已知理论瑕疵</b>: <see cref="GitWorkspaceResolver.Normalize"/>
    /// 在 Linux 上<b>不会</b>剔除路径里的换行符(换行是合法文件名字符), 所以理论上
    /// <c>workDir="/a\nb", session="c"</c> 与 <c>workDir="/a", session="b\nc"</c> 会撞出同一个 key。
    /// 现实中 <c>sessionId</c> 是 GUID/N 短串, 不含换行, 撞不上; 但若将来 sessionId 改成
    /// 用户可编辑的标题派生值, 必须先改成带长度前缀的拼接, 否则这里会变成静默串会话。</para>
    ///
    /// <para><b>参数约束</b>: 两个参数都必须非空 —— <c>sessionId</c> 参与身份计算,
    /// 缺失时若悄悄替换成占位符, 不同会话会共用同一个 Worker(而不是各自 spawn 一个),
    /// 表现为会话间工具状态串味。路径侧则交给 <c>Normalize</c> 自身的容错。
    /// 注意 <c>Normalize(null)</c> 会抛 <see cref="NullReferenceException"/>
    /// (它的 catch 分支里又调了一次 <c>path.Trim()</c>), 所以这里不做隐式兜底, 让调用方显式传对。</para>
    /// </summary>
    public static string MakeWorkerKey(string workDir, string sessionId)
    {
        // 规范化只做一次, 且与主进程调用本方法的路径完全一致(见上方一致性约束)
        var normalized = GitWorkspaceResolver.Normalize(workDir);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized + "\n" + sessionId));
        // 8 字节 = 16 个小写十六进制字符; Convert.ToHexStringLower 是 .NET 9+ API(仓库已用, 见 GitCheckpointStore)
        return Convert.ToHexStringLower(digest.AsSpan(0, 8));
    }
}