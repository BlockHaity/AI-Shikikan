using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCommander.Core.Services.Personas;

public class Persona
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static IReadOnlyList<Persona> LoadAll()
    {
        var list = new List<Persona>();
        Directory.CreateDirectory(AppPaths.PersonasDir);
        foreach (var file in Directory.GetFiles(AppPaths.PersonasDir, "*.json"))
        {
            try
            {
                var persona = JsonSerializer.Deserialize<Persona>(File.ReadAllText(file), JsonOptions);
                if (persona is not null && !string.IsNullOrWhiteSpace(persona.Name))
                {
                    if (string.IsNullOrEmpty(persona.Id))
                    {
                        persona.Id = Path.GetFileNameWithoutExtension(file);
                    }

                    list.Add(persona);
                }
            }
            catch
            {
            }
        }

        return list;
    }

    public static Persona? Find(string? idOrName, IReadOnlyList<Persona> personas) =>
        personas.FirstOrDefault(p =>
            string.Equals(p.Id, idOrName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase));

    public static void Save(Persona persona)
    {
        Directory.CreateDirectory(AppPaths.PersonasDir);
        var file = Path.Combine(AppPaths.PersonasDir, $"{persona.Id}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(persona, JsonOptions));
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