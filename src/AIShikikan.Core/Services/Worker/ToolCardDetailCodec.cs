using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AIShikikan.Core.Models;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Worker;

/// <summary>
/// <c>ToolResult.Detail</c>(<see cref="ToolCardDetail"/>)的跨进程编解码器: 判别符字符串 ↔ 具体类型的
/// <see cref="JsonTypeInfo"/>。供 <c>AIShikikan.Worker</c> 独立进程协议使用。
/// </summary>
///
/// <para><b>为什么需要这一层</b>: <see cref="ToolCardDetail"/> 是抽象基类 + <c>[JsonPolymorphic]</c>,
/// 多态分派在 AOT 下只能靠<b>源生成上下文里那份具体的 <see cref="JsonTypeInfo"/></b>。
/// 跨进程序列化时又不能像进程内那样把对象图整个丢给 STJ(没有共享类型系统, 也不能反射式序列化),
/// 所以协议里显式带一个判别符字符串, 由本类把它和 <see cref="JsonTypeInfo"/> 对上号。</para>
///
/// <para><b>为什么映射表手写, 不用反射扫 <c>[JsonDerivedType]</c></b>:
/// 一是 AOT 下反射读特性/类型信息不可靠, 裁剪后可能拿不到(甚至直接抛), 而
/// <see cref="AppJsonContext"/> 生成的 <see cref="JsonTypeInfo"/> 才是唯一真源;
/// 二是手写表能被 <see cref="SelfCheck"/> 交叉验证, 反射扫出来的东西没法自证。</para>
///
/// <para><b>为什么序列化必须传具体类型的 <see cref="JsonTypeInfo"/></b>: 按声明类型(抽象基类)序列化
/// 只会写出 <c>{}</c>。这正是历史上 <see cref="CheckpointDetail"/> 的事故 —— 它曾把多态特性错挂在自己身上
/// (派生类型清单不继承), 基类清单漏登记, 卡片字段静默全丢, 反向读回时 <c>{}</c> 无判别符 → 实例化抽象基类
/// 失败 → 抛异常 → 会话被判损坏 → 此后永久拒绝写回。</para>
///
/// <para><b>为什么 <see cref="Deserialize"/> 对未知判别符抛异常而不是返回 null</b>: 判别符是跨进程协议
/// 契约的一部分, 静默返回 null 会让工具卡片"退化但看不出错", 排查时只剩"卡片怎么没内容了"。
/// 契约违例必须当场炸。返回 null 只保留给"调用方明确表示没有结构化详情"(判别符本身为空)这一种语义。</para>
///
/// <para><b>新增派生类型的正确顺序</b>:
/// (1) 在 <see cref="ToolCardDetail"/> 上补 <c>[JsonDerivedType(typeof(XDetail), "x")</c>;
/// (2) 在 <see cref="AppJsonContext"/> 上补 <c>[JsonSerializable(typeof(XDetail))]</c>;
/// (3) 在本类三张表(<see cref="KnownTypeNames"/> / 判别符→<see cref="JsonTypeInfo"/> / 最小实例工厂)同步登记;
/// (4) 跑 <c>doctor</c> 让 <see cref="SelfCheck"/> 验证。判别符字符串不可改 —— 它已经落在历史会话的
/// <c>ToolSegment.Detail.$type</c> 里了。</para>
public static class ToolCardDetailCodec
{
    /// <summary>全部已知判别符(与 <see cref="ToolCardDetail"/> 上的 <c>[JsonDerivedType]</c> 清单一一对应)。</summary>
    /// <remarks>
    /// 刻意写成手写字面量而不是从判别符表导出: 只有"两个独立来源"做数量/键集合比对, 才能发现
    /// 有人加了一处忘了另一处(见 <see cref="SelfCheck"/>)。
    /// </remarks>
    private static readonly string[] KnownTypeNameList =
    [
        "fileRead",
        "dirList",
        "glob",
        "grep",
        "subagents",
        "checkpoint",
    ];

    /// <summary>对外只读视图(避免调用方强转回 <see cref="KnownTypeNameList"/> 后改到内部数组)。</summary>
    private static readonly IReadOnlyList<string> KnownTypeNamesView = Array.AsReadOnly(KnownTypeNameList);

    /// <summary>判别符 → 该派生类型自己的 <see cref="JsonTypeInfo"/>(取自源生成上下文)。</summary>
    /// <remarks>
    /// 用 <see cref="StringComparer.Ordinal"/>: 判别符是协议字节, 大小写不同就是不同判别符,
    /// 不能容忍"差不多等于"。
    /// </remarks>
    private static readonly Dictionary<string, JsonTypeInfo> TypeMap = new(StringComparer.Ordinal)
    {
        ["fileRead"] = AppJsonContext.Default.FileReadDetail,
        ["dirList"] = AppJsonContext.Default.DirectoryListDetail,
        ["glob"] = AppJsonContext.Default.GlobDetail,
        ["grep"] = AppJsonContext.Default.GrepDetail,
        ["subagents"] = AppJsonContext.Default.SubagentsDetail,
        ["checkpoint"] = AppJsonContext.Default.CheckpointDetail,
    };

    /// <summary>判别符 → 最小实例工厂, 仅供 <see cref="SelfCheck"/> 往返自检使用。</summary>
    /// <remarks>
    /// 它同时充当"派生类型真源清单": <see cref="SelfCheck"/> 用它造实例, 于是
    /// "判别符 → 类型"与"类型 → 判别符"两张映射必须自洽, 互换错位立刻暴露。
    /// </remarks>
    private static readonly Dictionary<string, Func<ToolCardDetail>> MinimalFactories = new(StringComparer.Ordinal)
    {
        ["fileRead"] = static () => new FileReadDetail(),
        ["dirList"] = static () => new DirectoryListDetail(),
        ["glob"] = static () => new GlobDetail(),
        ["grep"] = static () => new GrepDetail(),
        ["subagents"] = static () => new SubagentsDetail(),
        ["checkpoint"] = static () => new CheckpointDetail(),
    };

    /// <summary>全部已知判别符(与 <see cref="ToolCardDetail"/> 上的 <c>[JsonDerivedType]</c> 清单一一对应)。</summary>
    public static IReadOnlyList<string> KnownTypeNames => KnownTypeNamesView;

    /// <summary>取判别符; detail 为 null 时返回 null。</summary>
    /// <exception cref="InvalidOperationException">
    /// 运行时类型不在映射表里(说明有人新增了派生类型却忘了同步本表与
    /// <see cref="ToolCardDetail"/> 的 <c>[JsonDerivedType]</c>)。
    /// </exception>
    public static string? GetTypeName(ToolCardDetail? detail)
    {
        if (detail is null) return null;

        // 反向查表(运行时类型 → 判别符)。刻意遍历而不是再建一张 Type → name 表:
        // 少一张手工表就少一处可能脱节的地方, 6 项的线性扫描成本可忽略。
        foreach (var (name, info) in TypeMap)
        {
            if (info.Type == detail.GetType()) return name;
        }

        throw new InvalidOperationException(
            $"{detail.GetType().Name} 未登记在 ToolCardDetailCodec 中; " +
            "请在 ToolCardDetail 上补 [JsonDerivedType]、在 AppJsonContext 上补 [JsonSerializable], " +
            "并同步登记本类的 KnownTypeNames / 判别符映射表 / 最小实例工厂三处。");
    }

    /// <summary>
    /// 按<b>具体类型</b>序列化成 JSON 字符串(不是按声明类型 —— 声明类型是抽象基类, 按它写只会得到 <c>{}</c>)。
    /// detail 为 null 时返回 null。
    /// </summary>
    /// <exception cref="InvalidOperationException">运行时类型不在映射表里(同 <see cref="GetTypeName"/>)。</exception>
    public static string? Serialize(ToolCardDetail? detail)
    {
        if (detail is null) return null;

        var info = ResolveByType(detail.GetType());

        // 关键: 传具体类型的 JsonTypeInfo。写成 JsonSerializer.Serialize(detail) 会在 AOT 下直接不可用,
        // 写成 Serialize(detail, AppJsonContext.Default.ToolCardDetail) 会静默写出 {}。
        return JsonSerializer.Serialize(detail, info);
    }

    /// <summary>
    /// 由判别符 + JSON 字符串还原。typeName 为 null/空白(调用方明确表示"本工具没有结构化详情"),
    /// 或 json 为 null/空白时返回 null; <b>typeName 非空但未知时抛 <see cref="InvalidOperationException"/></b>。
    /// </summary>
    /// <remarks>
    /// 校验顺序是"先判别符、后载荷": 未知判别符属于协议契约违例(必须炸), 空载荷只是"这次没带详情"。
    /// </remarks>
    /// <exception cref="InvalidOperationException">判别符未知, 或载荷里的运行期类型与判别符对不上。</exception>
    public static ToolCardDetail? Deserialize(string? typeName, string? json)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return null;

        var info = ResolveByName(typeName);

        if (string.IsNullOrWhiteSpace(json)) return null;

        // 非泛型 JsonTypeInfo 重载的返回类型是 object(源生成上下文不暴露泛型 TypeInfo 强转),
        // 故此处显式收窄回基类 —— info 指向的类型均派生自 ToolCardDetail, 不会真的 InvalidCastException。
        if (JsonSerializer.Deserialize(json, info) is not ToolCardDetail result) return null;

        // 正常情况下 info 指向的是非多态的具体类型元数据, 载荷里的 $type 只会被当成无关属性忽略,
        // 返回值必然就是 info.Type。这一步是给"有人又把 [JsonPolymorphic] 错挂到派生类型自己身上"
        // (即 CheckpointDetail 那次事故)留的护栏: 那种情况下载荷里的 $type 会把对象分派成别的派生类型,
        // 而判别符说的是另一个 —— 宁可当场炸, 也别把内容错配的卡片交给 UI。
        if (result.GetType() != info.Type)
        {
            throw new InvalidOperationException(
                $"判别符 \"{typeName}\"({info.Type!.Name}) 与载荷的实际类型 {result.GetType().Name} 不一致; " +
                "请检查该派生类型上是否被错挂了 [JsonPolymorphic]/[JsonDerivedType]。");
        }

        return result;
    }

    /// <summary>供 doctor/自检用: 把每个已知类型都空实例化并往返一次, 返回失败清单(空 = 全通)。</summary>
    /// <remarks>
    /// 刻意只比较 <c>GetType()</c>、不反射读属性 —— 自检也要能在 Native AOT 下跑。
    /// </remarks>
    public static IReadOnlyList<string> SelfCheck()
    {
        var failures = new List<string>();

        // ---- 第 1 组: 三张手写表的键集合必须一致 ----
        // 这一组抓的是"漏更新": 加了 [JsonDerivedType] 却忘了本表(或忘了工厂表/已知判别符清单)。
        if (KnownTypeNameList.Length != TypeMap.Count)
        {
            failures.Add(
                $"表里有 {TypeMap.Count} 项, 已知判别符清单里有 {KnownTypeNameList.Length} 项, " +
                "请核对 ToolCardDetail 的 [JsonDerivedType] 清单。");
        }

        if (MinimalFactories.Count != TypeMap.Count)
        {
            failures.Add(
                $"判别符表里有 {TypeMap.Count} 项, 最小实例工厂表里有 {MinimalFactories.Count} 项; " +
                "请核对 ToolCardDetail 的 [JsonDerivedType] 清单。");
        }

        foreach (var name in KnownTypeNameList)
        {
            if (!TypeMap.ContainsKey(name)) failures.Add($"{name}: 已知判别符未出现在判别符映射表中");
            if (!MinimalFactories.ContainsKey(name)) failures.Add($"{name}: 已知判别符未出现在最小实例工厂表中");
        }

        foreach (var name in TypeMap.Keys)
        {
            if (!MinimalFactories.ContainsKey(name)) failures.Add($"{name}: 判别符映射表缺少对应的最小实例工厂");
        }

        // ---- 第 2 组: 逐判别符往返 + 与基类多态契约交叉验证 ----
        foreach (var (name, info) in TypeMap)
        {
            if (!MinimalFactories.TryGetValue(name, out var factory))
            {
                failures.Add($"{name}: 缺少最小实例工厂, 跳过往返");
                continue;
            }

            var expected = info.Type!;
            var minimal = factory();

            // 2.1 正向映射: 类型 → 判别符。"fileRead" 指向 SubagentsDetail 这类错位会被这里抓到。
            string? actualName;
            try
            {
                actualName = GetTypeName(minimal);
            }
            catch (InvalidOperationException ex)
            {
                failures.Add($"{name}: 取判别符失败({ex.Message})");
                continue;
            }

            if (actualName != name)
            {
                failures.Add($"{name}: 判别符映射错位, {expected.Name} 的判别符实际是 \"{actualName ?? "null"}\"");
                continue;
            }

            // 2.2 编码 → 解码往返。用具体类型的 JsonTypeInfo, 类型必须原样回来。
            string? json;
            try
            {
                json = Serialize(minimal);
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: 序列化失败({ex.GetType().Name}: {ex.Message})");
                continue;
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                failures.Add($"{name}: 序列化结果为空");
                continue;
            }

            // {} 意味着按抽象基类契约写出了 —— 派生字段静默全丢, 与历史事故同一形态。
            if (json.Trim() == "{}")
            {
                failures.Add($"{name}: 序列化结果为 {{}}, 疑似按声明类型(抽象基类)写出, 派生字段会静默全丢");
                continue;
            }

            ToolCardDetail? back;
            try
            {
                back = Deserialize(name, json);
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: 反序列化失败({ex.GetType().Name}: {ex.Message})");
                continue;
            }

            if (back is null)
            {
                failures.Add($"{name}: 往返后为 null");
                continue;
            }

            if (back.GetType() != expected)
            {
                failures.Add($"{name}: 往返后类型为 {back.GetType().Name}");
                continue;
            }

            // 2.3 判别符字符串与基类多态契约对齐。
            // 用"本表的判别符"伪造一份只含 $type 的载荷, 交给基类的多态契约分派:
            // 派出的类型必须与本表登记的一致。这样即便有人把 [JsonDerivedType] 错挂到派生类型自己身上
            // (派生类型清单不继承 → 基类契约里根本没这个判别符), 也会在这里暴露,
            // 而不是等到读历史会话时抛"无法实例化抽象基类"。
            try
            {
                var probe = JsonSerializer.Deserialize(
                    "{\"$type\":\"" + name + "\"}", AppJsonContext.Default.ToolCardDetail);

                if (probe is null)
                {
                    failures.Add($"{name}: 基类多态契约按 $type 分派得到 null");
                }
                else if (probe.GetType() != expected)
                {
                    failures.Add(
                        $"{name}: 基类多态契约按 $type=\"{name}\" 分派到 {probe.GetType().Name}, " +
                        $"与本表登记的 {expected.Name} 不一致, 请核对 ToolCardDetail 的 [JsonDerivedType]");
                }
            }
            catch (Exception ex)
            {
                failures.Add(
                    $"{name}: 用 $type=\"{name}\" 走基类多态契约失败({ex.GetType().Name}: {ex.Message}); " +
                    "请核对 ToolCardDetail 的 [JsonDerivedType] 清单");
            }
        }

        return failures;
    }

    /// <summary>按判别符查 <see cref="JsonTypeInfo"/>; 未知判别符抛异常(契约违例, 不静默降级)。</summary>
    private static JsonTypeInfo ResolveByName(string typeName)
    {
        if (TypeMap.TryGetValue(typeName, out var info)) return info;

        throw new InvalidOperationException(
            $"未知判别符 \"{typeName}\"; 已知判别符: {string.Join(" / ", KnownTypeNameList)}。");
    }

    /// <summary>按运行时具体类型查 <see cref="JsonTypeInfo"/>; 未登记抛异常。</summary>
    private static JsonTypeInfo ResolveByType(Type type)
    {
        foreach (var info in TypeMap.Values)
        {
            if (info.Type == type) return info;
        }

        throw new InvalidOperationException(
            $"{type.Name} 未登记在 ToolCardDetailCodec 中; " +
            "请在 ToolCardDetail 上补 [JsonDerivedType]、在 AppJsonContext 上补 [JsonSerializable], " +
            "并同步登记本类的 KnownTypeNames / 判别符映射表 / 最小实例工厂三处。");
    }
}