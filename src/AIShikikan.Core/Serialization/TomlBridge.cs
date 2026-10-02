using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Mcp;
using AIShikikan.Core.Services.Personas;
using AIShikikan.Core.Services.Templates;
using AIShikikan.Core.Services.Usage;
using Tomlyn;
using Tomlyn.Model;

namespace AIShikikan.Core.Serialization;

/// <summary>配置文件使用的类型集合, 通过 TOML 桥接层读写。
/// 命名策略为 snake_case + 字符串枚举, 与 TOML 惯例保持一致。</summary>
///
/// <para><b>与 <see cref="AppJsonContext"/> 的差异是刻意的, 不要"顺手统一"</b>:
/// 本上下文用 <c>SnakeCaseLower</c> 属性名 + <c>UseStringEnumConverter</c>(枚举落盘为
/// <c>"open_ai"</c> 这类可读字符串); 数据文件上下文用 <c>CamelCase</c> + 默认数字枚举。
/// 改成一致会让已存盘的 users' 配置与数据文件全部读不出 —— 统一必须配一次数据迁移。</para>
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
[JsonSerializable(typeof(McpConfigFile))]
[JsonSerializable(typeof(McpServerDefinition))]
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
            // 数组分两类(勿改):
            //  ① 元素全是对象/字典 → TomlTableArray, Tomlyn 渲染成 [[table]] 数组表。
            //     这是 "Servers = [{...}]" 这类列表配置必须的形态: TOML 的数组表是
            //     [[servers]] 重复块, 写成内联数组在 Tomlyn 读回时也拿不到稳定结构。
            //  ② 其他(含混合、空数组、基础类型) → TomlArray, 渲染成 [ ... ]。
            //     注意 Dictionary<string, string>(如 McpServerDefinition.Headers/Env)
            //     走的是上面的 JsonObject 分支而非这里, 落盘是内联表 { k = "v" },
            //     与数组分支互不影响 —— 字典加字段不需要动这段判断。
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

            // DateTime / Guid 等其它类型一律按字符串处理。
            // 判据说明: 上面 int 的判断放在 long 之后是必要的 —— TryGetValue<long> 对
            // 值为 int 的 JsonValue 也返回 true, 顺序反了会让所有整数都落成 long。
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
