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

    /// <summary>把 Persona 构建为带 YAML frontmatter 的 Markdown 文本, 系统提示词作为正文;
    /// Plan/Build 模式专属提示词以 HTML 注释段追加在正文末尾(不渲染、不影响通用部分)。</summary>
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
        if (!string.IsNullOrWhiteSpace(persona.PlanPrompt))
        {
            content += $"\n\n{PlanMarker}\n{persona.PlanPrompt.Trim()}\n{SectionEnd}";
        }

        if (!string.IsNullOrWhiteSpace(persona.BuildPrompt))
        {
            content += $"\n\n{BuildMarker}\n{persona.BuildPrompt.Trim()}\n{SectionEnd}";
        }

        return BuildFrontmatter(frontmatter) + content;
    }

    // ---- 模式专属提示词段(HTML 注释标记, Markdown 渲染时不可见) ----

    private const string PlanMarker = "<!-- persona:plan -->";
    private const string BuildMarker = "<!-- persona:build -->";
    private const string SectionEnd = "<!-- /persona -->";

    /// <summary>从正文中抽取 Plan/Build 模式专属段, 返回去除段后的通用正文与两段内容。</summary>
    public static (string GeneralBody, string PlanPrompt, string BuildPrompt) ExtractModeSections(string body)
    {
        var plan = ExtractSection(body, PlanMarker);
        var build = ExtractSection(body, BuildMarker);
        var general = RemoveSection(body, PlanMarker);
        general = RemoveSection(general, BuildMarker);
        return (general.Trim(), plan, build);
    }

    private static string ExtractSection(string body, string marker)
    {
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        // 结束边界: 结束标记与另一段起始标记取最先出现者, 防止缺失结束标记时吞并后段
        var contentStart = start + marker.Length;
        var end = NearestIndex(body, contentStart, SectionEnd, PlanMarker, BuildMarker);
        return end < 0
            ? body[contentStart..].Trim()
            : body[contentStart..end].Trim();
    }

    private static int NearestIndex(string body, int from, params string[] markers)
    {
        var nearest = -1;
        foreach (var m in markers)
        {
            var i = body.IndexOf(m, from, StringComparison.Ordinal);
            if (i >= 0 && (nearest < 0 || i < nearest)) nearest = i;
        }

        return nearest;
    }

    private static string RemoveSection(string body, string marker)
    {
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return body;

        var end = body.IndexOf(SectionEnd, start + marker.Length, StringComparison.Ordinal);
        return end < 0
            ? body[..start].TrimEnd()
            : (body[..start] + body[(end + SectionEnd.Length)..]).Trim();
    }
}
