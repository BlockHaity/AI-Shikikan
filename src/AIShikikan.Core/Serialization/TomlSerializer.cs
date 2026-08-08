using System.Collections;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace AIShikikan.Core.Serialization;

/// <summary>TOML 序列化辅助类, 用于 providers.toml 和 agents.toml。</summary>
public static class TomlSerializer
{
    public static string Serialize<T>(T obj)
    {
        var sb = new StringBuilder();
        var type = obj.GetType();

        foreach (var prop in type.GetProperties())
        {
            var value = prop.GetValue(obj);
            if (value == null) continue;

            var key = ToTomlKey(prop.Name);

            if (value is IList list)
            {
                foreach (var item in list)
                {
                    if (item == null) continue;
                    var itemTable = SerializeObjectToToml(item);
                    sb.AppendLine($"[[{key}]]");
                    sb.Append(itemTable);
                    sb.AppendLine();
                }
            }
            else if (value is IDictionary dict)
            {
                sb.AppendLine($"[{key}]");
                foreach (DictionaryEntry entry in dict)
                {
                    if (entry.Value == null) continue;
                    sb.AppendLine($"{ToTomlKey(entry.Key?.ToString() ?? "")} = {FormatTomlValue(entry.Value)}");
                }
            }
            else
            {
                sb.AppendLine($"{key} = {FormatTomlValue(value)}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static T Deserialize<T>(string content)
    {
        var table = Toml.ToModel(content);
        var dict = TableToDictionary(table);
        return DictionaryToObject<T>(dict);
    }

    private static string SerializeObjectToToml(object obj)
    {
        var sb = new StringBuilder();
        var type = obj.GetType();
        foreach (var prop in type.GetProperties())
        {
            var value = prop.GetValue(obj);
            if (value == null) continue;
            var key = ToTomlKey(prop.Name);
            sb.AppendLine($"{key} = {FormatTomlValue(value)}");
        }
        return sb.ToString();
    }

    private static string FormatTomlValue(object value)
    {
        return value switch
        {
            string s => $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
            bool b => b.ToString().ToLowerInvariant(),
            Enum e => $"\"{e}\"",
            _ => value.ToString() ?? string.Empty
        };
    }

    private static Dictionary<string, object> TableToDictionary(TomlTable table)
    {
        var dict = new Dictionary<string, object>();
        foreach (var kv in table)
        {
            dict[ToCSharpName(kv.Key)] = TomlValueToObj(kv.Value);
        }
        return dict;
    }

    private static object TomlValueToObj(TomlTable value)
    {
        if (value == null) return null!;
        
        // 检查是否是数组
        if (value.ContainsKey("__array"))
        {
            return value.Cast<KeyValuePair<string, TomlTable>>()
                .Select(kv => TomlValueToObj(kv.Value))
                .ToList();
        }
        
        return TableToDictionary(value);
    }

    private static T DictionaryToObject<T>(Dictionary<string, object> dict)
    {
        var type = typeof(T);
        var obj = Activator.CreateInstance(type);
        foreach (var prop in type.GetProperties())
        {
            if (!dict.TryGetValue(ToTomlKey(prop.Name), out var value)) continue;
            if (value == null || value is string s && string.IsNullOrEmpty(s)) continue;

            var target = prop.PropertyType;
            var converted = ConvertValue(value, target);
            if (converted != null)
            {
                prop.SetValue(obj, converted);
            }
        }
        return (T)obj!;
    }

    private static object? ConvertValue(object value, Type targetType)
    {
        return value switch
        {
            string s when targetType == typeof(bool) => bool.Parse(s),
            string s when targetType == typeof(int) => int.Parse(s),
            string s when targetType.IsEnum => Enum.Parse(targetType, s, true),
            Dictionary<string, object> dict when targetType == typeof(Dictionary<string, string>) =>
                dict.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty),
            List<object> list when targetType == typeof(List<string>) =>
                list.Cast<string>().ToList(),
            _ => value
        };
    }

    private static string ToTomlKey(string propertyName)
    {
        var sb = new StringBuilder();
        foreach (var c in propertyName)
        {
            if (char.IsUpper(c)) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string ToCSharpName(string tomlKey)
    {
        var parts = tomlKey.Split('_');
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length > 0)
            {
                sb.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1) sb.Append(part[1..]);
            }
        }
        return sb.ToString();
    }
}
