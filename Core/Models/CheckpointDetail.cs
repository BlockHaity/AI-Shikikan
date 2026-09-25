using System.Text.Json.Serialization;

namespace AIShikikan.Core.Models;

/// <summary>检查点卡片详情: 供 UI 持久化渲染检查点卡片, 随 ToolSegment 持久化(继承 ToolCardDetail 复用多态序列化)。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(CheckpointDetail), "checkpoint")]
public class CheckpointDetail : ToolCardDetail
{
    /// <summary>检查点记录 ID。)</summary>
    public string CheckpointId { get; set; } = string.Empty;

    /// <summary>检查点标签(用户可读)。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>短 commit SHA(前 8 字符)。</summary>
    public string ShortSha { get; set; } = string.Empty;

    /// <summary>完整 commit SHA。)</summary>
    public string FullSha { get; set; } = string.Empty;

    /// <summary>分支名。)</summary>
    public string BranchName { get; set; } = string.Empty;

    /// <summary>工作目录(相对仓库根的相对路径, 或绝对路径)。</summary>
    public string WorkDir { get; set; } = string.Empty;

    /// <summary>检查点来源。)</summary>
    public GitCheckpointSource Source { get; set; } = GitCheckpointSource.AutoUserMessage;

    /// <summary>创建时间。)</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>关联的会话 ID。)</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>对话截断索引(用于 Fork 复制)。</summary>
    public int ConversationCutoff { get; set; }

    /// <summary>最后回滚方式。)</summary>
    public CheckpointRollbackMode? LastRollbackMode { get; set; }

    /// <summary>最后回滚时间。)</summary>
    public DateTime? LastRollbackAt { get; set; }

    /// <summary>最后回滚后的 commit SHA。)</summary>
    public string? LastRollbackCommitSha { get; set; }

    /// <summary>显示用的创建时间文本。)</summary>
    [JsonIgnore]
    public string CreatedText => CreatedAt.ToString("MM-dd HH:mm:ss");

    /// <summary>来源显示文本。)</summary>
    [JsonIgnore]
    public string SourceText => Source switch
    {
        GitCheckpointSource.AutoUserMessage => "自动(用户消息)",
        GitCheckpointSource.AiTool => "AI 工具",
        GitCheckpointSource.Manual => "手动",
        _ => "未知"
    };

    /// <summary>是否已回滚过。)</summary>
    [JsonIgnore]
    public bool HasBeenRolledBack => LastRollbackAt.HasValue;
}