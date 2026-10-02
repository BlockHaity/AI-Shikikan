using System.Text.Json.Serialization;

namespace AIShikikan.Core.Models;

/// <summary>工具卡片结构化展示数据: 供 UI 按工具类型渲染专属卡体, 随 ToolSegment 持久化。</summary>
///
/// <para><b>⚠️ 新增派生类型必须登记在这里, 否则字段会被静默丢掉(不会报错)。</b>
/// <see cref="JsonPolymorphicAttribute.UnknownDerivedTypeHandling"/> 默认是
/// <c>FallBackToBaseType</c>: 运行期类型不在这份清单里时, STJ 会<b>按基类契约</b>序列化 ——
/// 不写判别符、不抛异常。而 <see cref="ToolCardDetail"/> 自身没有任何可序列化属性, 结果就是
/// <c>"detail": {}</c>, 派生类的字段在写盘那一刻就没了。
/// 反向读同样炸: <c>{}</c> 里没有 <c>$type</c>, STJ 只能去实例化基类, 而基类是 abstract,
/// 于是抛 <c>JsonException</c>, 整个 <c>ChatSession</c> 反序列化失败(ChatService.EnsureLoaded
/// 只能落到 .bak 回退, 再失败就标记 IsCorrupted 并锁死写回)。</para>
///
/// <para><b>历史成因(勿重犯)</b>: <see cref="CheckpointDetail"/> 当初把
/// <c>[JsonDerivedType(typeof(CheckpointDetail), "checkpoint")]</c> 写在了<b>它自己</b>身上。
/// 派生类型清单不继承, 自登记只对"把 CheckpointDetail 当声明类型直接序列化"生效,
/// 对 <c>ToolSegment.Detail</c>(声明类型是 ToolCardDetail)这条路径完全无效。</para>
///
/// <para><b>判别符字符串是已落盘数据的一部分, 不可改</b>: <c>sessions/{id}.json</c> 里
/// <c>ToolSegment.Detail.$type</c> 就是这些字符串, 改名会让历史会话读不出来。
/// 现值: fileRead / dirList / glob / grep / subagents / checkpoint。</para>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(FileReadDetail), "fileRead")]
[JsonDerivedType(typeof(DirectoryListDetail), "dirList")]
[JsonDerivedType(typeof(GlobDetail), "glob")]
[JsonDerivedType(typeof(GrepDetail), "grep")]
[JsonDerivedType(typeof(SubagentsDetail), "subagents")]
[JsonDerivedType(typeof(CheckpointDetail), "checkpoint")]
public abstract class ToolCardDetail
{
}

/// <summary>read_file: 完整文件路径 + 原始内容(保留格式与缩进)。</summary>
public class FileReadDetail : ToolCardDetail
{
    /// <summary>文件完整路径。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>文件内容(原始文本, 未加行号, 保留缩进)。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>文件总行数。</summary>
    public int TotalLines { get; set; }

    /// <summary>显示起始行(1 起)。</summary>
    public int StartLine { get; set; } = 1;

    /// <summary>本次显示的行数。</summary>
    public int LinesShown { get; set; }

    /// <summary>是否未显示全部内容。</summary>
    public bool Truncated { get; set; }
}

/// <summary>list_directory: 目录条目列表。</summary>
public class DirectoryListDetail : ToolCardDetail
{
    public string Path { get; set; } = string.Empty;

    public bool Recursive { get; set; }

    public List<DirectoryEntry> Entries { get; set; } = [];

    /// <summary>是否因超过上限而截断。</summary>
    public bool Truncated { get; set; }
}

/// <summary>目录列表条目: 名称/类型/大小/修改时间。</summary>
public class DirectoryEntry
{
    public string Name { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public long SizeBytes { get; set; }

    public DateTime ModifiedAt { get; set; }

    /// <summary>递归列出时的缩进层级。</summary>
    public int Depth { get; set; }

    [JsonIgnore]
    public string DisplayName => new string(' ', Depth * 2) + Name;

    [JsonIgnore]
    public string ModifiedText => ModifiedAt.ToString("MM-dd HH:mm");

    [JsonIgnore]
    public string SizeText => IsDirectory ? string.Empty : $"{SizeBytes / 1024}KB";
}

/// <summary>glob: 查询模式 + 匹配文件路径。</summary>
public class GlobDetail : ToolCardDetail
{
    public string Pattern { get; set; } = string.Empty;

    public List<FileMatchEntry> Matches { get; set; } = [];

    public bool Truncated { get; set; }
}

/// <summary>匹配到的文件路径。</summary>
public class FileMatchEntry
{
    public string Path { get; set; } = string.Empty;
}

/// <summary>grep: 查询条件 + 匹配(文件路径/行号/匹配行上下文)。</summary>
public class GrepDetail : ToolCardDetail
{
    public string Pattern { get; set; } = string.Empty;

    public bool CaseSensitive { get; set; }

    public List<GrepMatchEntry> Matches { get; set; } = [];

    public bool Truncated { get; set; }
}

/// <summary>单条 grep 匹配。</summary>
public class GrepMatchEntry
{
    public string Path { get; set; } = string.Empty;

    public int Line { get; set; }

    /// <summary>匹配行上下文内容(原始文本, 保留缩进, 超长截断)。</summary>
    public string Text { get; set; } = string.Empty;
}

/// <summary>run_subagents / run_&lt;agent&gt;: 每个子 agent 一个独立结果框。</summary>
public class SubagentsDetail : ToolCardDetail
{
    public List<SubagentResultEntry> Subagents { get; set; } = [];
}

/// <summary>单个子 agent 的执行结果。</summary>
public class SubagentResultEntry
{
    public string AgentId { get; set; } = string.Empty;

    public string AgentName { get; set; } = string.Empty;

    public int ExitCode { get; set; }

    public int ElapsedSeconds { get; set; }

    public bool IsError { get; set; }

    public bool TimedOut { get; set; }

    /// <summary>输出结果(已压缩/截断的文本)。</summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>关联检查点 —— 已废弃, 恒为 null。
    /// 子代理统一在当前分支就地工作, 不再为单个子代理建检查点(旧的 ac/&lt;stepId&gt; 分支机制已下线),
    /// 回滚入口是"每条用户消息"的检查点卡片。保留字段仅为兼容历史会话 JSON(已写盘的数据里带这个键)。</summary>
    [Obsolete("子代理检查点机制已废弃, 恒为 null; 回滚请用每条用户消息的检查点。")]
    public string? StepId { get; set; }

    [JsonIgnore]
    public bool IsSuccess => !IsError && !TimedOut;

    [JsonIgnore]
    public string MetaText => $"exit {ExitCode} · {ElapsedSeconds}s";
}
