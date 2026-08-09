using System.Text.Json.Serialization;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Personas;

public class Persona
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public PersonaKind Kind { get; set; } = PersonaKind.Expert;
    public string Description { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;

    [JsonIgnore]
    public string Display => string.IsNullOrEmpty(Name) ? Id : Name;
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
            catch
            {
            }
        }

        // 兼容旧 TOML 格式
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
            catch
            {
            }
        }

        return list;
    }

    /// <summary>从 Markdown(YAML frontmatter) 文件解析 Persona, 正文作为系统提示词。</summary>
    private static Persona? LoadMarkdown(string file)
    {
        var content = File.ReadAllText(file);
        var (frontmatter, body) = YamlFrontmatterParser.Parse(content);

        if (!frontmatter.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var persona = new Persona
        {
            Id = id,
            Name = frontmatter.GetValueOrDefault("name", id),
            Kind = Enum.TryParse(frontmatter.GetValueOrDefault("kind"), true, out PersonaKind kind)
                ? kind : PersonaKind.Expert,
            Description = frontmatter.GetValueOrDefault("description", string.Empty),
            SystemPrompt = string.IsNullOrWhiteSpace(body)
                ? frontmatter.GetValueOrDefault("systemPrompt", string.Empty)
                : body.Trim()
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
        File.WriteAllText(path, YamlFrontmatterParser.Build(persona));
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

                persona = new Persona
                {
                    Id = id,
                    Name = frontmatter.GetValueOrDefault("name", id),
                    Kind = Enum.TryParse(frontmatter.GetValueOrDefault("kind"), true, out PersonaKind kind)
                        ? kind : PersonaKind.Expert,
                    Description = frontmatter.GetValueOrDefault("description", string.Empty),
                    SystemPrompt = string.IsNullOrWhiteSpace(body)
                        ? frontmatter.GetValueOrDefault("systemPrompt", string.Empty)
                        : body.Trim()
                };
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
