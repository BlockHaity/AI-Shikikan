using System.Text;
using AIShikikan.Core.Services.Personas;
using YamlDotNet.RepresentationModel;

namespace AIShikikan.Core.Serialization;

/// <summary>解析 Markdown 文件中的 YAML frontmatter。</summary>
public static class YamlFrontmatterParser
{
    /// <summary>把 Markdown(YAML frontmatter) 解析为键值集合与正文。</summary>
    public static (Dictionary<string, string> Frontmatter, string Body) Parse(string markdown)
    {
        var frontmatter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                    var value = entry.Value switch
                    {
                        YamlScalarNode scalar => scalar.Value ?? string.Empty,
                        YamlMappingNode map => string.Join("\n", map.Children.Select(
                            kv => $"{kv.Key}: {kv.Value}")),
                        YamlSequenceNode seq => string.Join("\n", seq.Children.Select(
                            c => c is YamlScalarNode s ? s.Value ?? string.Empty : c.ToString())),
                        _ => entry.Value.ToString() ?? string.Empty
                    };

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

    /// <summary>把键值集合序列化为 YAML frontmatter 块(含前后分隔符与空行)。多行值用 \n 转义。</summary>
    public static string BuildFrontmatter(IReadOnlyDictionary<string, string> values)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        foreach (var kv in values)
        {
            var value = kv.Value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "")
                .Replace("\n", "\\n");
            sb.AppendLine($"{kv.Key}: \"{value}\"");
        }

        sb.AppendLine("---");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>把 Persona 构建为带 YAML frontmatter 的 Markdown 文本, 系统提示词作为正文。</summary>
    public static string Build(Persona persona, string? body = null)
    {
        var frontmatter = new Dictionary<string, string>
        {
            { "id", persona.Id },
            { "name", persona.Name },
            { "kind", persona.Kind.ToString() },
            { "description", persona.Description }
        };

        var content = body ?? persona.SystemPrompt;
        return BuildFrontmatter(frontmatter) + content;
    }
}
