namespace AIShikikan.Core.Services.Mcp;

using System.Text.Json.Serialization;
using AIShikikan.Core.Serialization;

/// <summary>单个 MCP 服务器定义(stdio / http / sse 传输)。</summary>
public class McpServerDefinition
{
    /// <summary>唯一 ID(小写标识, 用于内部路由与工具前缀)。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名称(可中文)。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>传输方式: "stdio"(默认, 子进程) | "http"(Streamable HTTP) | "sse"(HTTP+Server-Sent Events)。</summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>http/sse 传输的服务器 URL(stdio 时忽略)。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>可执行命令(npx / uvx / node / python 等, 仅 stdio 传输)。</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>命令参数列表(仅 stdio 传输)。</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>额外环境变量(与父进程环境合并后传给子进程, 仅 stdio 传输)。</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>额外 HTTP 请求头(仅 http/sse 传输, TOML 里是内联表)。
    /// 会原样附加到每次 HTTP 请求; 需要 Bearer 时也可直接写 "Authorization" = "Bearer ..."。
    /// 注: <see cref="ResolveHttpHeaders"/> 是给传输层(McpHttpClient)准备的统一取值入口,
    /// 在它接入之前本字段只落盘、不生效。</summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>Bearer Token 便捷字段(仅 http/sse, stdio 忽略)。非空时等价于
    /// "Authorization: Bearer &lt;token&gt;"; 若 <see cref="Headers"/> 里已显式给出 Authorization 则不覆盖。</summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>合并出本次 HTTP 请求应携带的请求头: <see cref="Headers"/> 原样,
    /// 再按 <see cref="Token"/> 补 Authorization(仅在未显式指定时)。
    /// 传输层统一从这里取值, 免得各处各写一套拼装逻辑。Token 为空时直接返回原字典(零拷贝)。</summary>
    public IReadOnlyDictionary<string, string> ResolveHttpHeaders()
    {
        if (string.IsNullOrWhiteSpace(Token))
        {
            return Headers;
        }

        var merged = new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase);
        if (!merged.ContainsKey("Authorization"))
        {
            merged["Authorization"] = $"Bearer {Token.Trim()}";
        }

        return merged;
    }
}

/// <summary>mcp-servers.toml 文件模型。</summary>
public class McpConfigFile
{
    public List<McpServerDefinition> Servers { get; set; } = [];
}

/// <summary>MCP 服务器配置持久化(TOML, AOT 源生成序列化)。</summary>
public static class McpConfigService
{
    public const string McpServersResource = DefaultConfig.McpServersResource;

    private static readonly object Sync = new();
    private static IReadOnlyList<McpServerDefinition>? _cache;

    public static void EnsureDefaultExists()
    {
        DefaultConfig.WriteIfMissing(AppPaths.McpServersPath, McpServersResource);
    }

    /// <summary>加载全部 MCP 服务器定义(带缓存)。</summary>
    public static IReadOnlyList<McpServerDefinition> LoadAll()
    {
        lock (Sync)
        {
            if (_cache is not null)
            {
                return _cache;
            }

            EnsureDefaultExists();
            _cache = [];
            // 主文件为空 / 非法 TOML 时自动回退 .bak, 避免缓存里落进空配置后被写回覆盖
            McpConfigFile? parsed = null;
            if (AtomicFile.TryReadText(AppPaths.McpServersPath, out var content, text => TryParse(text, out parsed))
                && parsed is not null)
            {
                _cache = parsed.Servers;
            }

            return _cache;
        }
    }

    public static void Save(McpConfigFile file)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            // 原子写入(tmp -> 刷盘 -> rename): 避免写入中断留下半截 mcp-servers.toml
            AtomicFile.TryWriteAllText(AppPaths.McpServersPath, TomlBridge.Serialize(file), "mcp-servers.toml");
            _cache = file.Servers.ToList();
        }
    }

    public static McpConfigFile LoadUserFile()
    {
        // Upsert/Remove 走这里: 解析失败返回空对象会被 Save 写回, 故主文件损坏时回退 .bak
        McpConfigFile? parsed = null;
        if (AtomicFile.TryReadText(AppPaths.McpServersPath, out var content, text => TryParse(text, out parsed))
            && parsed is not null)
        {
            return parsed;
        }

        return new McpConfigFile();
    }

    /// <summary>尝试解析 mcp-servers.toml 内容; 解析异常视为不可用(触发 .bak 回退)。</summary>
    private static bool TryParse(string content, out McpConfigFile? file)
    {
        try
        {
            file = TomlBridge.Deserialize<McpConfigFile>(content);
            return file is not null;
        }
        catch
        {
            file = null;
            return false;
        }
    }

    /// <summary>新增或更新一个服务器定义(按 Id 幂等)。</summary>
    public static void Upsert(McpServerDefinition definition)
    {
        var file = LoadUserFile();
        var idx = file.Servers.FindIndex(s =>
            string.Equals(s.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            file.Servers[idx] = definition;
        }
        else
        {
            file.Servers.Add(definition);
        }

        Save(file);
    }

    public static void Remove(string id)
    {
        var file = LoadUserFile();
        file.Servers.RemoveAll(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        Save(file);
    }

    /// <summary>清空缓存(文件被外部修改后调用); 与其它方法一样纳入 Sync 保护。</summary>
    public static void Refresh()
    {
        lock (Sync)
        {
            _cache = null;
        }
    }
}
