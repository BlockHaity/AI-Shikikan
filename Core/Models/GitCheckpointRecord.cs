using System.Text.Json.Serialization;

namespace AIShikikan.Core.Models;

/// <summary>Git 检查点记录: 绑定会话与仓库上下文的不可变标记, 供回滚/分叉使用。</summary>
public class GitCheckpointRecord
{
    /// <summary>唯一 ID(短 GUID)。</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>仓库根目录(包含 .git 的目录)。</summary>
    public string RepositoryRoot { get; init; } = string.Empty;

    /// <summary>工作目录(用户发送消息时的 cwd, 可能是子目录)。</summary>
    public string WorkDir { get; init; } = string.Empty;

    /// <summary>创建检查点时的分支名。</summary>
    public string BranchName { get; init; } = string.Empty;

    /// <summary>检查点对应的 commit SHA(完整 40 字符)。</summary>
    public string CommitSha { get; init; } = string.Empty;

    /// <summary>轻量标签名: ai-shikikan/checkpoint/{Id}。</summary>
    public string TagName { get; init; } = string.Empty;

    /// <summary>关联的会话 ID。)</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>对话截断位置: 该检查点创建时会话已有的消息索引(0-based)。用于 Fork 时复制会话前缀。</summary>
    public int ConversationCutoff { get; init; }

    /// <summary>检查点来源。)</summary>
    public GitCheckpointSource Source { get; init; } = GitCheckpointSource.AutoUserMessage;

    /// <summary>用户可读标签(如 "用户消息 #3", "AI 提交: 修复登录 bug")。)</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>创建时间。)</summary>
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    /// <summary>最后一次回滚方式(用于审计/显示)。</summary>
    public CheckpointRollbackMode? LastRollbackMode { get; set; }

    /// <summary>最后一次回滚时间。)</summary>
    public DateTime? LastRollbackAt { get; set; }

    /// <summary>最后一次回滚后的 commit SHA(若回滚成功)。</summary>
    public string? LastRollbackCommitSha { get; set; }

    /// <summary>显示用的短 commit (前 8 字符)。</summary>
    [JsonIgnore]
    public string ShortSha => CommitSha.Length >= 8 ? CommitSha[..8] : CommitSha;

    /// <summary>显示用的创建时间文本。)</summary>
    [JsonIgnore]
    public string CreatedText => CreatedAt.ToString("MM-dd HH:mm:ss");

    /// <summary>是否已被回滚过(任一方式)。)</summary>
    [JsonIgnore]
    public bool HasBeenRolledBack => LastRollbackAt.HasValue;
}