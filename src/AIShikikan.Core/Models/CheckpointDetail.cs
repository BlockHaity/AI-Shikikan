using System.Text.Json.Serialization;

namespace AIShikikan.Core.Models;

/// <summary>检查点卡片详情: 供 UI 持久化渲染检查点卡片, 随 ToolSegment 持久化(继承 ToolCardDetail 复用多态序列化)。</summary>
/// <remarks>
/// <para><b>本类型刻意不挂任何 <c>[JsonPolymorphic]</c> / <c>[JsonDerivedType]</c> 特性</b>。
/// STJ 的多态分派清单只读<b>属性声明类型</b>上的状态, 而 <c>ToolSegment.Detail</c> 的声明类型是
/// 基类 <see cref="ToolCardDetail"/> —— 派生类型自己身上的声明<b>完全无效</b>(派生类型清单不继承)。</para>
/// <para>历史事故: 这里曾经挂着 <c>[JsonPolymorphic]</c> + 指向自己的 <c>[JsonDerivedType]</c>,
/// 而基类清单里漏了本类型, 看起来"已登记"实则未生效。后果是 <c>git_create_checkpoint</c> 的卡片
/// 序列化时按基类契约写出 <c>"detail": {}</c>(13 个字段静默全丢, 不抛异常); 反向读回时
/// <c>{}</c> 无判别符 → 走"无判别符即声明类型"分支 → 实例化 abstract 基类失败 → 抛异常
/// → <c>ChatService.EnsureLoaded</c> 判为损坏 → 回退 <c>.bak</c>(同一份坏数据)→
/// 该会话<b>此后永久拒绝写回</b>(列表里能看到、点开全空)。</para>
/// <para>正确登记位置在 <see cref="ToolCardDetail"/> 上的
/// <c>[JsonDerivedType(typeof(CheckpointDetail), "checkpoint")]</c>。新增派生类型时同理:
/// 只改基类清单, 别在派生类型上自我登记。</para>
/// </remarks>
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

    /// <summary>对话截断索引(用于 Fork 复制)。语义同 <see cref="GitCheckpointRecord.ConversationCutoff"/>。</summary>
    /// <remarks>
    /// 特别地 <see cref="int.MaxValue"/> 表示"保留全部对话"(AI 工具创建检查点时写不),
    /// 只能用于 <c>cutoff &lt; Messages.Count</c> 这类比较, 不可做加减运算(会溢出成负数而被当成清空)。
    /// </remarks>
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