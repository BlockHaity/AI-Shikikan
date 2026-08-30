using System.Text.Json.Serialization;

namespace AIShikikan.Core.Models;

/// <summary>工具卡片结构化展示数据: 供 UI 按工具类型渲染专属卡体, 随 ToolSegment 持久化。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(FileReadDetail), "fileRead")]
[JsonDerivedType(typeof(DirectoryListDetail), "dirList")]
[JsonDerivedType(typeof(GlobDetail), "glob")]
[JsonDerivedType(typeof(GrepDetail), "grep")]
[JsonDerivedType(typeof(SubagentsDetail), "subagents")]
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

    /// <summary>关联检查点。</summary>
    public string? StepId { get; set; }

    [JsonIgnore]
    public bool IsSuccess => !IsError && !TimedOut;

    [JsonIgnore]
    public string MetaText => $"exit {ExitCode} · {ElapsedSeconds}s";
}
