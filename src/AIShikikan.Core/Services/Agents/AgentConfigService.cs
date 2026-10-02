using System.Text.Json;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Agents;

public class AgentConfigFile
{
    public List<CliAgentDefinition> Agents { get; set; } = [];

    /// <summary>Roster 分派规则描述(自定义), 会注入 {rules} 占位符。</summary>
    public string Rules { get; set; } = string.Empty;
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

    /// <summary>读取用户 Agent 配置文件**原样**。
    ///
    /// <para>与 <see cref="LoadAll"/> 的区别(这是有意为之, 不要顺手"统一"成同一套缓存):
    /// <list type="bullet">
    /// <item><see cref="LoadAll"/> 返回不可变的 Agent 列表快照, 可以缓存, 且只缓存 Agents 段。</item>
    /// <item>本方法返回**可变的完整配置对象**(含 <see cref="AgentConfigFile.Rules"/>),
    /// 并被 SaveUserAgent / RemoveUserAgent / SaveRules 当作"读-改-写"的基线使用。
    /// 一旦缓存, 这些保存路径就会把快照写回文件, 外部改动(用户手改 agents.toml)会被静默吞掉。</item>
    /// <item>AgentEngine 每回合也只为读 Rules 调用一次本方法——因此这里选择"多读一次盘"
    /// 换取保存路径的正确性。</item>
    /// </list></para></summary>
    public static AgentConfigFile LoadUserFile()
    {
        if (File.Exists(AppPaths.AgentsPath))
        {
            // 主文件为空 / 非法 TOML 时自动回退 .bak, 避免"加载返回空配置 -> 下次写回覆盖原始数据"
            AgentConfigFile? parsed = null;
            if (AtomicFile.TryReadText(AppPaths.AgentsPath, out var content, text => TryParse(text, out parsed))
                && parsed is not null)
            {
                // 用户文件为准: 即使删光了 Agent 也保持原样, 不恢复默认
                return parsed;
            }

            // 文件存在但解析失败且无可用备份: 不覆盖用户文件, 按空配置运行
            Log.Warn("Config", $"读取 {AppPaths.AgentsPath} 失败且无可用备份, 按空配置运行");
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
            catch (Exception ex)
            {
                Log.Warn("Config", ex, $"解析旧版 {legacyPath} 失败");
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
        catch (Exception ex)
        {
            Log.Warn("Config", ex, $"读取 {AppPaths.AgentsPath} 失败");
            return new AgentConfigFile();
        }
    }

    /// <summary>按 id 查 Agent 定义(agents 为空时用 LoadAll 的缓存)。
    /// 有意同时匹配 Id 与 Name: LLM 在 Roster 里看到的往往是 Name("Claude Code")而不是 id("claude"),
    /// 两者都可直接作为 agentId 传入, 大小写不敏感。调用方应尽量自行传入 agents 快照避免重复查表。</summary>
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

    /// <summary>丢弃 <see cref="_cache"/>, 下次 <see cref="LoadAll"/> 重新读盘。
    /// 加锁与 LoadAll/SaveFile 的 Sync 纪律保持一致(单引用下已安全, 此处只求行为统一)。</summary>
    public static void Refresh()
    {
        lock (Sync)
        {
            _cache = null;
        }
    }

    /// <summary>尝试解析 agents.toml 内容; 解析异常视为不可用(触发 .bak 回退)。</summary>
    private static bool TryParse(string content, out AgentConfigFile? file)
    {
        try
        {
            file = TomlBridge.Deserialize<AgentConfigFile>(content);
            return file is not null;
        }
        catch
        {
            file = null;
            return false;
        }
    }

    private static void SaveFile(AgentConfigFile file)
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        // 原子写入(tmp -> 刷盘 -> rename): 避免写入中断留下半截 agents.toml
        AtomicFile.TryWriteAllText(AppPaths.AgentsPath, TomlBridge.Serialize(file), "agents.toml");
        // 失效合并缓存: 写盘是唯一的真相来源, 之后必须重新读而不是继续用旧快照
        lock (Sync)
        {
            _cache = null;
        }
    }
}
