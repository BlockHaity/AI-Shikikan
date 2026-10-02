using System.Text.Json.Serialization;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Personas;

public class Persona
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public PersonaKind Kind { get; set; } = PersonaKind.Expert;
    public string Description { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>Plan 模式专属提示词(追加在通用 SystemPrompt 之后, 不影响通用部分)。</summary>
    public string PlanPrompt { get; set; } = string.Empty;

    /// <summary>Build 模式专属提示词(追加在通用 SystemPrompt 之后, 不影响通用部分)。</summary>
    public string BuildPrompt { get; set; } = string.Empty;

    [JsonIgnore]
    public string Display => string.IsNullOrEmpty(Name) ? Id : Name;

    /// <summary>按当前模式解析生效提示词: 通用正文 + 模式专属段(缺省回退 frontmatter 的 planPrompt/buildPrompt 键)。</summary>
    public string ResolveForMode(bool planMode)
    {
        var mode = planMode ? PlanPrompt : BuildPrompt;
        if (string.IsNullOrWhiteSpace(mode))
        {
            return SystemPrompt;
        }

        return string.IsNullOrWhiteSpace(SystemPrompt)
            ? mode.Trim()
            : $"{SystemPrompt.Trim()}\n\n{(planMode ? "# Plan 模式专属指令" : "# Build 模式专属指令")}\n{mode.Trim()}";
    }
}

public enum PersonaKind
{
    Expert,
    Roleplay
}

public static class PersonaService
{
    public static IReadOnlyList<Persona> LoadAll()
    {
        var list = new List<Persona>();
        Directory.CreateDirectory(AppPaths.PersonasDir);

        // 主格式: Markdown(YAML frontmatter)
        foreach (var file in Directory.GetFiles(AppPaths.PersonasDir, "*.md"))
        {
            try
            {
                var persona = LoadMarkdown(file);
                if (persona is not null)
                {
                    list.Add(persona);
                }
            }
            catch (Exception ex)
            {
                // 人格是用户手写的, 静默丢弃会让"专家凭空消失"无从排查
                Log.Warn("Persona", ex, $"人格解析失败(已跳过): {file}");
            }
        }

        // 兼容旧 TOML 格式。刻意裸读: 这是老版本一次性写出的迁移文件, 从未经过 AtomicFile,
        // 不存在 .toml.bak 可回退, 走 TryReadText 只会多一次无用的 validate 解析。
        foreach (var file in Directory.GetFiles(AppPaths.PersonasDir, "*.toml"))
        {
            try
            {
                var persona = TomlBridge.Deserialize<Persona>(File.ReadAllText(file));
                if (persona is null || string.IsNullOrWhiteSpace(persona.Id))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(persona.Name))
                {
                    persona.Name = persona.Id;
                }

                if (!string.IsNullOrWhiteSpace(persona.Name))
                {
                    list.Add(persona);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Persona", ex, $"旧 TOML 人格解析失败(已跳过): {file}");
            }
        }

        return list;
    }

    /// <summary>从 Markdown(YAML frontmatter) 文件解析 Persona, 正文作为系统提示词。</summary>
    private static Persona? LoadMarkdown(string file)
    {
        // 主文件为空 / 缺 id(frontmatter 解析失败)时自动回退 .bak, 避免人格被静默丢弃
        if (!AtomicFile.TryReadText(file, out var content, HasId))
        {
            return null;
        }

        var (frontmatter, body) = YamlFrontmatterParser.Parse(content);

        if (!frontmatter.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return FromFrontmatter(id, frontmatter, body);
    }

    /// <summary>校验 Markdown 是否含有可用的 id(与 LoadMarkdown 的成功条件一致)。</summary>
    private static bool HasId(string content)
    {
        var (frontmatter, _) = YamlFrontmatterParser.Parse(content);
        return frontmatter.TryGetValue("id", out var id) && !string.IsNullOrWhiteSpace(id);
    }

    /// <summary>由 frontmatter 与原始正文构建 Persona(加载 .md / 导入 .md 共用);
    /// 正文中的 persona:plan/build 注释段解析为模式专属提示词, 不污染通用正文。</summary>
    private static Persona? FromFrontmatter(
        string id, Dictionary<string, string> frontmatter, string rawBody)
    {
        var (general, planBody, buildBody) = YamlFrontmatterParser.ExtractModeSections(rawBody);

        var persona = new Persona
        {
            Id = id,
            Name = frontmatter.GetValueOrDefault("name", id),
            Kind = Enum.TryParse(frontmatter.GetValueOrDefault("kind"), true, out PersonaKind kind)
                ? kind : PersonaKind.Expert,
            Description = frontmatter.GetValueOrDefault("description", string.Empty),
            SystemPrompt = string.IsNullOrWhiteSpace(general)
                ? frontmatter.GetValueOrDefault("systemPrompt", string.Empty)
                : general,
            // 模式段优先取正文注释段, 无段时回退 frontmatter 键(两种写法都支持)
            PlanPrompt = !string.IsNullOrWhiteSpace(planBody)
                ? planBody
                : frontmatter.GetValueOrDefault("planPrompt", string.Empty),
            BuildPrompt = !string.IsNullOrWhiteSpace(buildBody)
                ? buildBody
                : frontmatter.GetValueOrDefault("buildPrompt", string.Empty)
        };

        return string.IsNullOrWhiteSpace(persona.Name) ? null : persona;
    }

    public static Persona? Find(string? idOrName, IReadOnlyList<Persona> personas) =>
        personas.FirstOrDefault(p =>
            string.Equals(p.Id, idOrName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase));

    public static void Save(Persona persona)
    {
        Directory.CreateDirectory(AppPaths.PersonasDir);
        var path = Path.Combine(AppPaths.PersonasDir, $"{persona.Id}.md");
        // 原子写入(tmp -> 刷盘 -> rename): 避免写入中断留下半截 .md 导致人格被静默丢弃。
        // 附属的 .bak 不会被 LoadAll 的 "*.md" 扫描匹配到, 不会重复加载。
        AtomicFile.TryWriteAllText(path, YamlFrontmatterParser.Build(persona), $"persona:{persona.Id}.md");
    }

    /// <summary>从外部文件导入专家/人格, 支持 .md(YAML frontmatter, 推荐)、.json(旧格式兼容), 校验后保存到配置目录。</summary>
    public static (Persona? Persona, string? Error) ImportFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return (null, $"文件不存在: {path}");
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        Persona persona;

        try
        {
            if (ext == ".md")
            {
                var content = File.ReadAllText(path);
                var (frontmatter, body) = YamlFrontmatterParser.Parse(content);

                if (!frontmatter.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
                {
                    return (null, "缺少 id 字段, 无法导入。");
                }

                persona = FromFrontmatter(id, frontmatter, body)
                          ?? new Persona(); // name 为空时由下方校验给出提示
            }
            else if (ext == ".json")
            {
                // 兼容旧 JSON 格式
                persona = System.Text.Json.JsonSerializer.Deserialize(
                    File.ReadAllText(path), AppJsonContext.Default.Persona)
                    ?? throw new InvalidDataException("文件内容不是有效的专家文件。");
            }
            else
            {
                return (null, $"不支持的文件格式: {ext}(仅支持 .md / .json)。");
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"解析失败: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(persona.Name))
        {
            return (null, "缺少 name 字段, 无法导入。");
        }

        if (string.IsNullOrWhiteSpace(persona.SystemPrompt))
        {
            return (null, "缺少 systemPrompt 字段, 无法导入。");
        }

        persona.Id = EnsureUniqueId(persona.Id, AppPaths.PersonasDir);
        Save(persona);
        return (persona, null);
    }

    private static string EnsureUniqueId(string id, string dir)
    {
        var file = Path.Combine(dir, $"{id}.md");
        if (!File.Exists(file))
        {
            return id;
        }

        var n = 2;
        while (File.Exists(Path.Combine(dir, $"{id}-{n}.md")))
        {
            n++;
        }

        return $"{id}-{n}";
    }

    public static void WriteSampleFiles()
    {
        if (Directory.Exists(AppPaths.PersonasDir) && Directory.GetFiles(AppPaths.PersonasDir).Length > 0)
        {
            return;
        }

        Save(new Persona
        {
            Id = "general-expert",
            Name = "通用专家",
            Kind = PersonaKind.Expert,
            Description = "通用领域的资深专家，严谨、结构化的输出",
            SystemPrompt = """
                你是一位经验丰富的通用领域专家。在回答/完成任务时请遵循：
                1. 先理解需求背景与目标
                2. 采用结构化的方式输出（步骤、要点、结论）
                3. 明确指出不确定性与风险
                4. 重要的论断附上理由
                """
        });

        Save(new Persona
        {
            Id = "senior-architect",
            Name = "资深架构师",
            Kind = PersonaKind.Expert,
            Description = "软件架构设计专家，关注可维护性、可扩展性与技术选型",
            SystemPrompt = """
                你是一位资深软件架构师。请遵循：
                 - 评估方案时权衡: 可维护性 > 可扩展性 > 实现速度
                 - 优先推荐经过验证的成熟方案
                 - 对新技术保持谨慎，明确给出取舍
                 - 输出包含: 架构概览、关键决策(ADR)、风险与缓解
                """
        });

        Save(new Persona
        {
            Id = "roleplay-tutor",
            Name = "苏格拉底式导师",
            Kind = PersonaKind.Roleplay,
            Description = "通过提问引导学习的角色。不直接给答案，用启发式提问",
            SystemPrompt = """
                你扮演一位苏格拉底式教学导师。风格:
                 - 不直接给出答案，用层层递进的提问引导对方自己找到结论
                 - 每次只提出一个问题
                 - 肯定对方正确的推理，用追问纠正错误假设
                """
        });
    }
}
