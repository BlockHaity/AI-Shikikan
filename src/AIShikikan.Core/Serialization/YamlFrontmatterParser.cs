using YamlDotNet.RepresentationModel;
using AIShikikan.Core.Services.Personas;

namespace AIShikikan.Core.Serialization;

/// <summary>解析 Markdown 文件中的 YAML frontmatter。</summary>
public static class YamlFrontmatterParser
{
    public static (Dictionary<string, string> Frontmatter, string Body) Parse(string markdown)
    {
        var frontmatter = new Dictionary<string, string>();
        var body = markdown;

        const string delimiter = "---";
        var firstIndex = markdown.IndexOf(delimiter, StringComparison.Ordinal);
        if (firstIndex < 0) return (frontmatter, markdown);

        var secondIndex = markdown.IndexOf(delimiter, firstIndex + delimiter.Length, StringComparison.Ordinal);
        if (secondIndex < 0) return (frontmatter, markdown);

        var yamlContent = markdown[(firstIndex + delimiter.Length)..secondIndex].Trim();
        body = markdown[(secondIndex + delimiter.Length)..].TrimStart();

        if (string.IsNullOrWhiteSpace(yamlContent)) return (frontmatter, body);

        try
        {
            var yaml = new YamlStream();
            yaml.Load(new StringReader(yamlContent));
            var root = yaml.Documents[0].RootNode as YamlMappingNode;
            if (root != null)
            {
                foreach (var entry in root.Children)
                {
                    var key = entry.Key.ToString();
                    var value = entry.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        frontmatter[key] = value ?? string.Empty;
                    }
                }
            }
        }
        catch
        {
            // 如果 YAML 解析失败, 返回空 frontmatter
        }

        return (frontmatter, body);
    }

    public static string BuildFrontmatter(Dictionary<string, string> values)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        foreach (var kv in values)
        {
            var value = kv.Value.Replace("\n", "\n  ");
            sb.AppendLine($"{kv.Key}: \"{value}\"");
        }
        sb.AppendLine("---");
        sb.AppendLine();
        return sb.ToString();
    }

    public static string Build(Persona persona, string body)
    {
        var frontmatter = new Dictionary<string, string>
        {
            { "id", persona.Id },
            { "name", persona.Name },
            { "kind", persona.Kind.ToString() },
            { "description", persona.Description },
            { "systemPrompt", persona.SystemPrompt }
        };
        return BuildFrontmatter(frontmatter) + body;
    }
}
