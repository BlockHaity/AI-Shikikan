namespace AIShikikan.Core.Services.Mcp;

using AIShikikan.Core.Logging;
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

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>mcp-servers.toml 文件模型。</summary>
public class McpConfigFile
{
    public List<McpServerDefinition> Servers { get; set; } = [];
}

/// <summary>MCP 服务器配置持久化(TOML, AOT 源生成序列化)。</summary>
public static class McpConfigService
{
    public const string McpServersResource = "AIShikikan.Core.DefaultConfig.mcp-servers.toml";

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
            try
            {
                var content = File.ReadAllText(AppPaths.McpServersPath);
                var file = TomlBridge.Deserialize<McpConfigFile>(content);
                _cache = file?.Servers ?? [];
            }
            catch (Exception ex)
            {
                Log.Warn("Config", ex, $"解析 {AppPaths.McpServersPath} 失败");
                _cache = [];
            }

            return _cache;
        }
    }

    public static void Save(McpConfigFile file)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            File.WriteAllText(AppPaths.McpServersPath, TomlBridge.Serialize(file));
            _cache = file.Servers.ToList();
        }
    }

    public static McpConfigFile LoadUserFile()
    {
        try
        {
            var content = File.ReadAllText(AppPaths.McpServersPath);
            return TomlBridge.Deserialize<McpConfigFile>(content) ?? new McpConfigFile();
        }
        catch (Exception ex)
        {
            Log.Warn("Config", ex, $"读取 {AppPaths.McpServersPath} 失败");
            return new McpConfigFile();
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

    public static void Refresh() => _cache = null;
}
