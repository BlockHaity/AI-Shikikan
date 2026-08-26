namespace AIShikikan.Cli.Tui.Services.Commands;

/// <summary>命令处理器接口: 以 "/" 前缀命令为单位, 每个处理器负责一组相关命令,
/// 便于按领域拆分维护。</summary>
public interface ICommandHandler
{
    /// <summary>是否能处理该命令名(不含 "/", 已小写)。</summary>
    bool CanHandle(string command);

    /// <summary>处理命令。返回 true 表示命令已消费(不再分发)。
    /// 无法处理时应返回 false(交给调用方提示未知命令)。</summary>
    bool TryHandle(string command, string args);

    /// <summary>帮助信息行: (命令, 说明)。</summary>
    IEnumerable<(string Command, string Help)> HelpRows { get; }
}
