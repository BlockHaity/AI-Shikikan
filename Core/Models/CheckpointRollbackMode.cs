namespace AIShikikan.Core.Models;

/// <summary>检查点回滚方式。</summary>
public enum CheckpointRollbackMode
{
    /// <summary>硬重置: git reset --hard 到检查点 commit(仅允许祖先、工作区干净)。</summary>
    ResetHard,

    /// <summary>反向提交: git revert --no-commit <checkpoint>..HEAD 再提交一个还原 commit(仅允许祖先、工作区干净)。</summary>
    Revert
}