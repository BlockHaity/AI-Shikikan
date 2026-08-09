using System.Text.Json;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Agents;

public class AgentConfigFile
{
    public List<CliAgentDefinition> Agents { get; set; } = [];

    /// <summary>Roster 分派规则描述(自定义), 会注入 {rules} 占位符。</summary>
    public string Rules { get; set; } = string.Empty;

    /// <summary>是否向对话注入 Agent Roster。</summary>
    public bool RosterEnabled { get; set; } = true;
}

public static class AgentConfigService
{
    private static readonly object Sync = new();
    private static IReadOnlyList<CliAgentDefinition>? _cache;

    /// <summary>首次启动时生成默认 Agent 定义(仅当用户文件不存在)。</summary>
    public static void EnsureDefaultExists()
    {
        DefaultConfig.WriteIfMissing(AppPaths.AgentsPath, DefaultConfig.AgentsResource);
    }

    /// <summary>加载用户 Agent 定义; 无任何配置文件时初始化默认配置。</summary>
    public static IReadOnlyList<CliAgentDefinition> LoadAll()
    {
        lock (Sync)
        {
            if (_cache is not null)
            {
                return _cache;
            }

            var file = LoadUserFile();
            _cache = file.Agents.ToList();
            return _cache;
        }
    }

    public static AgentConfigFile LoadUserFile()
    {
        if (File.Exists(AppPaths.AgentsPath))
        {
            try
            {
                var content = File.ReadAllText(AppPaths.AgentsPath);
                var file = TomlBridge.Deserialize<AgentConfigFile>(content);
                if (file is not null)
                {
                    // 用户文件为准: 即使删光了 Agent 也保持原样, 不恢复默认
                    return file;
                }
            }
            catch
            {
            }

            // 文件存在但解析失败: 不覆盖用户文件, 按空配置运行
            return new AgentConfigFile();
        }

        // 兼容旧 JSON 配置: 若 TOML 不存在, 回退读取 agents.json
        var legacyPath = Path.ChangeExtension(AppPaths.AgentsPath, ".json");
        if (File.Exists(legacyPath))
        {
            try
            {
                var file = JsonSerializer.Deserialize(
                    File.ReadAllText(legacyPath), AppJsonContext.Default.AgentConfigFile);
                if (file is not null)
                {
                    return file;
                }
            }
            catch
            {
            }

            return new AgentConfigFile();
        }

        // 首次启动: 生成默认配置文件后再读取
        EnsureDefaultExists();
        try
        {
            var content = File.ReadAllText(AppPaths.AgentsPath);
            return TomlBridge.Deserialize<AgentConfigFile>(content) ?? new AgentConfigFile();
        }
        catch
        {
            return new AgentConfigFile();
        }
    }

    public static CliAgentDefinition? Find(string? id, IReadOnlyList<CliAgentDefinition>? agents = null)
    {
        var list = agents ?? LoadAll();
        return list.FirstOrDefault(a =>
            string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Name, id, StringComparison.OrdinalIgnoreCase));
    }

    public static void SaveUserAgent(CliAgentDefinition definition)
    {
        var file = LoadUserFile();
        var idx = file.Agents.FindIndex(a =>
            string.Equals(a.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            file.Agents[idx] = definition;
        }
        else
        {
            file.Agents.Add(definition);
        }

        SaveFile(file);
    }

    public static void RemoveUserAgent(string id)
    {
        var file = LoadUserFile();
        file.Agents.RemoveAll(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        SaveFile(file);
    }

    public static void SaveRules(string rules)
    {
        var file = LoadUserFile();
        file.Rules = rules;
        SaveFile(file);
    }

    public static void SaveRosterEnabled(bool enabled)
    {
        var file = LoadUserFile();
        file.RosterEnabled = enabled;
        SaveFile(file);
    }

    public static void Refresh() => _cache = null;

    private static void SaveFile(AgentConfigFile file)
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllText(AppPaths.AgentsPath, TomlBridge.Serialize(file));
        _cache = null;
    }
}
