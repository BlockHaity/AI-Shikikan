namespace AIShikikan.Core.Services.Worker;

/// <summary>
/// Worker 进程/连接的启动身份(命令行或 hello 给定, 进程生命周期内不变)。
/// <para>放在 Core 而不是 Worker 项目: stdio 入口与 WebSocket 服务端都要构造它, 而后者位于 Core。</para>
/// </summary>
public sealed class WorkerHostOptions
{
    /// <summary>宿主会话 Id。stdio 模式来自 <c>--session</c>; 远程模式由 hello 覆盖。</summary>
    public required string SessionId { get; init; }

    /// <summary>工具的相对路径解析与 git 上下文锚点。远程模式下由 hello 覆盖(且必须落在 <see cref="RemoteRoot"/> 内)。</summary>
    public required string WorkDir { get; init; }

    /// <summary>工作树根。stdio 模式启动时用 <see cref="WorkDir"/> 顶替, 握手后由 hello 接管。</summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>父进程 pid(0 = 未知)。仅诊断与孤儿自检用; 远程模式恒为 0。</summary>
    public required int ParentPid { get; init; }

    /// <summary>WorkerKey(远程模式下按 root + 空会话计算, 仅用于日志)。</summary>
    public required string WorkerKey { get; init; }

    /// <summary>
    /// 远程模式允许访问的项目根目录。非 null 即表示这是 WebSocket 服务端连接:
    /// hello 里的 WorkDir 必须落在此目录内, 且该目录本身就是 Worker 的工作目录。
    /// </summary>
    public string? RemoteRoot { get; init; }

    /// <summary>是否为远程(WebSocket)连接。</summary>
    public bool IsRemote => !string.IsNullOrEmpty(RemoteRoot);
}
