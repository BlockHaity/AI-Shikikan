namespace AIShikikan.Core.Models;

/// <summary>检查点创建来源。</summary>
public enum GitCheckpointSource
{
    /// <summary>用户消息发送前自动标记(对话检查点)。</summary>
    AutoUserMessage,

    /// <summary>AI 工具显式调用 CommitFiles/MarkCheckpoint 创建。</summary>
    AiTool,

    /// <summary>用户手动在 Git 面板创建。</summary>
    Manual
}