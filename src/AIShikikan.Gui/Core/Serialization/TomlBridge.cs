using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Personas;
using AIShikikan.Core.Services.Templates;
using AIShikikan.Core.Services.Usage;
using Tomlyn;
using Tomlyn.Model;

namespace AIShikikan.Core.Serialization;

/// <summary>配置文件使用的类型集合, 通过 TOML 桥接层读写。
/// 命名策略为 snake_case + 字符串枚举, 与 TOML 惯例保持一致。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(LlmSettings))]
[JsonSerializable(typeof(ProviderConfig))]
[JsonSerializable(typeof(AgentConfigFile))]
[JsonSerializable(typeof(CliAgentDefinition))]
[JsonSerializable(typeof(Persona))]
[JsonSerializable(typeof(AgentTemplate))]
[JsonSerializable(typeof(ThemeService.Preferences))]
[JsonSerializable(typeof(ModelConfigFile))]
[JsonSerializable(typeof(ModelPriceConfig))]
internal sealed partial class TomlJsonContext : JsonSerializerContext;

/// <summary>JSON ↔ TOML 双向桥接: 利用 System.Text.Json 源生成(与 AOT/裁剪兼容)
/// 完成强类型对象 ⇄ JsonNode 的转换, 再由 Tomlyn 在 JsonNode ⇄ TomlTable 间转换。
/// 全程不依赖反射, 保证 Native AOT 下可正常读写配置文件。</summary>
public static class TomlBridge
{
    /// <summary>把配置对象序列化为 TOML 文本。</summary>
    public static string Serialize<T>(T value)
    {
        var jsonTypeInfo = TomlJsonContext.Default.GetTypeInfo(typeof(T))!;
        var node = JsonSerializer.SerializeToNode(value, jsonTypeInfo);
        var table = ToTomlTable((JsonObject)node!);
        return Toml.FromModel(table);
    }

    /// <summary>从 TOML 文本反序列化为配置对象。</summary>
    public static T? Deserialize<T>(string content)
    {
        var table = Toml.ToModel(content);
        var node = ToJsonObject(table);
        var jsonTypeInfo = TomlJsonContext.Default.GetTypeInfo(typeof(T))!;
        return (T?)JsonSerializer.Deserialize(node, jsonTypeInfo);
    }

    private static TomlTable ToTomlTable(JsonObject obj)
    {
        var table = new TomlTable();
        foreach (var kv in obj)
        {
            if (kv.Value is null)
            {
                continue;
            }

            var converted = ToTomlValue(kv.Value);
            if (converted is not null)
            {
                table[kv.Key] = converted;
            }
        }

        return table;
    }

    private static object? ToTomlValue(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            return ToTomlTable(obj);
        }

        if (node is JsonArray arr)
        {
            var items = arr.Select(ToTomlValue).ToList();
            // 对象数组 → [[table]]; 基本类型数组 → [ ... ]
            if (items.Count > 0 && items.All(i => i is TomlTable))
            {
                var array = new TomlTableArray();
                foreach (var item in items)
                {
                    array.Add((TomlTable)item!);
                }

                return array;
            }

            var tomlArray = new TomlArray();
            foreach (var item in items)
            {
                tomlArray.Add(item);
            }

            return tomlArray;
        }

        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s))
            {
                return s;
            }

            if (jv.TryGetValue<bool>(out var b))
            {
                return b;
            }

            if (jv.TryGetValue<long>(out var l))
            {
                return l;
            }

            if (jv.TryGetValue<double>(out var d))
            {
                return d;
            }

            if (jv.TryGetValue<int>(out var i))
            {
                return i;
            }

            // DateTime / Guid 等其它类型一律按字符串处理
            return jv.ToJsonString().Trim('"');
        }

        return null;
    }

    private static JsonObject ToJsonObject(TomlTable table)
    {
        var obj = new JsonObject();
        foreach (var kv in table)
        {
            obj[kv.Key] = ToJsonNode(kv.Value);
        }

        return obj;
    }

    private static JsonNode? ToJsonNode(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case TomlTableArray tableArray:
            {
                var arr = new JsonArray();
                foreach (var item in tableArray)
                {
                    arr.Add((JsonNode)ToJsonObject(item));
                }

                return arr;
            }

            case TomlTable table:
                return ToJsonObject(table);

            case TomlArray array:
            {
                var arr = new JsonArray();
                foreach (var item in array)
                {
                    arr.Add((JsonNode)ToJsonValue(item!));
                }

                return arr;
            }

            default:
                return ToJsonValue(value!);
        }
    }

    private static JsonValue ToJsonValue(object value) => value switch
    {
        string s => JsonValue.Create(s)!,
        bool b => JsonValue.Create(b)!,
        long l => JsonValue.Create(l)!,
        double d => JsonValue.Create(d)!,
        _ => JsonValue.Create(value.ToString() ?? string.Empty)!
    };
}
