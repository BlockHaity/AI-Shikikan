using System.IO;

using AIShikikan.Core.Logging;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Worker;

/*
 * worker.toml —— Worker 传输方式的唯一配置来源(GUI 与 Worker 共读)。
 *
 * 设计前提(用户明确要求):
 *   · 本地**始终**用 stdio 派生子进程跑 Worker, 不再回退到主进程内联执行;
 *   · Worker 可独立运行, 通过 --websocket / worker.toml 开启 WebSocket 服务,
 *     把 Worker 所在的项目根目录当作远程文件夹暴露出去;
 *   · 远程模式下 Agent CLI 由远端 Worker 提供, 主进程只负责协议与工具桥接。
 *
 * 配置示例(放在 AppPaths.ConfigDir/worker.toml):
 *
 *   mode = "websocket"
 *   url = "ws://127.0.0.1:8765/worker"   # 客户端连接地址; 留空时由 listen+path 推导
 *   token = "change-me"                 # 远程模式**必填**, 两侧必须一致
 *   listen = "127.0.0.1:8765"           # 服务端监听地址
 *   path = "/worker"                    # 服务端 URL 路径
 *   root = "/srv/project"               # 服务端允许访问的项目根目录; 留空时用启动目录
 *   workdir = "/srv/project"            # 客户端请求的远程工作目录; 留空时用 root
 *
 * 环境变量可覆盖同名配置(优先级高于文件), 便于脚本与容器场景:
 *   AISHIKIKAN_WORKER_MODE / _URL / _TOKEN / _LISTEN / _ROOT / _WORKDIR
 */

/// <summary>worker.toml 的强类型模型(TomlBridge 以 snake_case 落盘)。</summary>
public sealed class WorkerConfig
{
    /// <summary><c>stdio</c>(默认)或 <c>websocket</c>。大小写不敏感。</summary>
    public string Mode { get; set; } = "stdio";

    /// <summary>客户端连接的完整地址(如 <c>ws://127.0.0.1:8765/worker</c>)。仅远程模式使用。</summary>
    public string? Url { get; set; }

    /// <summary>共享鉴权令牌。<b>远程模式必填</b>, 客户端以 <c>Authorization: Bearer</c> 发送。</summary>
    public string? Token { get; set; }

    /// <summary>服务端监听地址 <c>host:port</c>(默认仅回环)。</summary>
    public string Listen { get; set; } = "127.0.0.1:8765";

    /// <summary>服务端 URL 路径(默认 <c>/worker</c>)。</summary>
    public string Path { get; set; } = "/worker";

    /// <summary>服务端允许访问的项目根目录。留空时取 Worker 进程启动目录。</summary>
    public string? Root { get; set; }

    /// <summary>客户端请求的远程工作目录。留空时取 <see cref="Root"/>。</summary>
    public string? WorkDir { get; set; }

    /// <summary>是否远程(WebSocket)模式。也允许通过 <see cref="Url"/> 隐式开启。</summary>
    public bool IsWebSocket =>
        string.Equals((Mode ?? string.Empty).Trim(), "websocket", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(Url);

    /// <summary>客户端实际请求的远程目录(未配置时为 null, 交给调用方回退到本机工作目录)。</summary>
    public string? EffectiveRemoteWorkDir =>
        string.IsNullOrWhiteSpace(WorkDir) ? NullIfBlank(Root) : WorkDir.Trim();

    /// <summary>
    /// 客户端连接地址: 显式 <see cref="Url"/> 优先; 否则由 <see cref="Listen"/> + <see cref="Path"/> 推导
    /// (监听 <c>0.0.0.0</c> 时改写为 <c>127.0.0.1</c>, 本机客户端可直连)。
    /// </summary>
    public string? ResolveClientUrl()
    {
        if (!string.IsNullOrWhiteSpace(Url))
        {
            return Url.Trim();
        }

        var listen = string.IsNullOrWhiteSpace(Listen) ? "127.0.0.1:8765" : Listen.Trim();
        var separator = listen.LastIndexOf(':');
        if (separator <= 0 || separator == listen.Length - 1)
        {
            return null;
        }

        var host = listen[..separator].Trim();
        var port = listen[(separator + 1)..].Trim();
        if (host is "0.0.0.0" or "*")
        {
            host = "127.0.0.1";
        }

        var path = NormalizePath(Path);
        return $"ws://{host}:{port}{path}";
    }

    /// <summary>把空白字段收成 null, 并把 path 规范成以 <c>/</c> 开头的形式。</summary>
    public WorkerConfig Normalize()
    {
        Mode = string.IsNullOrWhiteSpace(Mode) ? "stdio" : Mode.Trim();
        Url = NullIfBlank(Url);
        Token = NullIfBlank(Token);
        Listen = string.IsNullOrWhiteSpace(Listen) ? "127.0.0.1:8765" : Listen.Trim();
        Path = NormalizePath(Path);
        Root = NullIfBlank(Root);
        WorkDir = NullIfBlank(WorkDir);
        return this;
    }

    private static string NormalizePath(string? path)
    {
        var value = NullIfBlank(path) ?? "/worker";
        return value.StartsWith('/') ? value : "/" + value;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>worker.toml 的加载入口。文件缺失或损坏时退回默认(stdio), 并留下 Warn。</summary>
public static class WorkerConfigService
{
    private const string Category = "Worker";

    public const string EnvMode = "AISHIKIKAN_WORKER_MODE";
    public const string EnvUrl = "AISHIKIKAN_WORKER_URL";
    public const string EnvToken = "AISHIKIKAN_WORKER_TOKEN";
    public const string EnvListen = "AISHIKIKAN_WORKER_LISTEN";
    public const string EnvRoot = "AISHIKIKAN_WORKER_ROOT";
    public const string EnvWorkDir = "AISHIKIKAN_WORKER_WORKDIR";

    /// <summary>默认配置路径(与 providers.toml 等并列在 ConfigDir)。</summary>
    public static string DefaultPath => AppPaths.WorkerConfigPath;

    /// <summary>
    /// 读取配置。刻意**不写默认文件**: Worker 是可能被频繁拉起的子进程, 不该在启动路径上写用户配置。
    /// </summary>
    public static WorkerConfig Load(string? path = null)
    {
        var file = path ?? DefaultPath;
        var config = new WorkerConfig();

        if (File.Exists(file))
        {
            try
            {
                var parsed = TomlBridge.Deserialize<WorkerConfig>(File.ReadAllText(file));
                if (parsed is not null)
                {
                    config = parsed;
                }
            }
            catch (Exception ex)
            {
                Log.Warn(Category, ex, $"读取 {file} 失败, 按 stdio 模式运行");
            }
        }

        ApplyEnvironment(config);
        return config.Normalize();
    }

    /// <summary>把环境变量覆盖进配置(空值不覆盖)。</summary>
    public static void ApplyEnvironment(WorkerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.Mode = Environment.GetEnvironmentVariable(EnvMode) ?? config.Mode;
        config.Url = Environment.GetEnvironmentVariable(EnvUrl) ?? config.Url;
        config.Token = Environment.GetEnvironmentVariable(EnvToken) ?? config.Token;
        config.Listen = Environment.GetEnvironmentVariable(EnvListen) ?? config.Listen;
        config.Root = Environment.GetEnvironmentVariable(EnvRoot) ?? config.Root;
        config.WorkDir = Environment.GetEnvironmentVariable(EnvWorkDir) ?? config.WorkDir;
    }
}
