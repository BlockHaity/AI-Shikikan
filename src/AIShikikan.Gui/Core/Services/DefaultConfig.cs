namespace AIShikikan.Core.Services;

/// <summary>读取程序集内嵌的默认配置文件(TOML), 用于首次启动时初始化用户配置。</summary>
public static class DefaultConfig
{
    public const string ProvidersResource = "AIShikikan.Core.DefaultConfig.providers.toml";
    public const string AgentsResource = "AIShikikan.Core.DefaultConfig.agents.toml";

    /// <summary>读取内嵌默认配置文本; 资源缺失时返回 null。</summary>
    public static string? Load(string resourceName)
    {
        var assembly = typeof(DefaultConfig).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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