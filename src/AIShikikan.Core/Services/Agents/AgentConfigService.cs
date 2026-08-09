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

    /// <summary>内置 + 用户自定义合并; 用户定义按 Id 覆盖内置。</summary>
    public static IReadOnlyList<CliAgentDefinition> LoadAll()
    {
        lock (Sync)
        {
            if (_cache is not null)
            {
                return _cache;
            }

            var file = LoadUserFile();
            var map = new Dictionary<string, CliAgentDefinition>(StringComparer.OrdinalIgnoreCase);

            foreach (var def in BuiltinCliAgents.All)
            {
                map[def.Id] = def;
            }

            foreach (var def in file.Agents)
            {
                if (string.IsNullOrWhiteSpace(def.Id))
                {
                    continue;
                }

                map[def.Id] = def;
            }

            _cache = map.Values.ToList();
            return _cache;
        }
    }

    public static AgentConfigFile LoadUserFile()
    {
        try
        {
            if (File.Exists(AppPaths.AgentsPath))
            {
                var content = File.ReadAllText(AppPaths.AgentsPath);
                var file = TomlBridge.Deserialize<AgentConfigFile>(content);
                if (file is not null)
                {
                    return file;
                }
            }

            // 兼容旧 JSON 配置: 若 TOML 不存在, 回退读取 agents.json
            var legacyPath = Path.ChangeExtension(AppPaths.AgentsPath, ".json");
            if (File.Exists(legacyPath))
            {
                var file = JsonSerializer.Deserialize(
                    File.ReadAllText(legacyPath), AppJsonContext.Default.AgentConfigFile);
                if (file is not null)
                {
                    return file;
                }
            }
        }
        catch
        {
        }

        return new AgentConfigFile();
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
