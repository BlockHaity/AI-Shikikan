namespace AIShikikan.Core.Services;

using AIShikikan.Core.Logging;

/// <summary>读取程序集内嵌的默认配置文件(TOML), 用于首次启动时初始化用户配置。</summary>
public static class DefaultConfig
{
    public const string ProvidersResource = "AIShikikan.Core.DefaultConfig.providers.toml";
    public const string AgentsResource = "AIShikikan.Core.DefaultConfig.agents.toml";
    public const string McpServersResource = "AIShikikan.Core.DefaultConfig.mcp-servers.toml";

    /// <summary>读取内嵌默认配置文本; 资源缺失时返回 null(带警告日志)。</summary>
    public static string? Load(string resourceName)
    {
        var assembly = typeof(DefaultConfig).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is not null)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        // 兜底: 清单名与预期不一致(如根命名空间不同)时, 按 .DefaultConfig.<文件名> 后缀匹配
        var match = assembly.GetManifestResourceNames().FirstOrDefault(n =>
            n.EndsWith(GetSuffix(resourceName), StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            Log.Warn("Config", $"内嵌资源名 {resourceName} 未命中, 使用清单名 {match}");
            using var fallback = assembly.GetManifestResourceStream(match);
            if (fallback is null)
            {
                return null;
            }

            using var fallbackReader = new StreamReader(fallback);
            return fallbackReader.ReadToEnd();
        }

        Log.Warn("Config", $"内嵌资源缺失: {resourceName}");
        return null;
    }

    /// <summary>取 .DefaultConfig.<文件名> 形式的后缀(如 .DefaultConfig.agents.toml)。</summary>
    private static string GetSuffix(string resourceName)
    {
        const string marker = ".DefaultConfig.";
        var idx = resourceName.LastIndexOf(marker, StringComparison.Ordinal);
        var file = idx >= 0 ? resourceName[(idx + marker.Length)..] : resourceName;
        return marker + file;
    }

    /// <summary>将默认配置写出到用户路径; 仅在目标文件不存在时写入, 已存在则跳过。</summary>
    public static bool WriteIfMissing(string path, string resourceName)
    {
        if (File.Exists(path))
        {
            return false;
        }

        var content = Load(resourceName);
        if (content is null)
        {
            return false;
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, content);
        return true;
    }
}