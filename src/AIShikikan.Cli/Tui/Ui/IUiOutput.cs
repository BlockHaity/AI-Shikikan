using AIShikikan.Cli.Models;

namespace AIShikikan.Cli.Tui.Ui;

/// <summary>命令处理器与引擎事件的 UI 输出抽象: 日志渲染 + 模态交互。
/// 实现为 Terminal.Gui 聊天日志与对话框; 由 TUI 与命令层共用。</summary>
public interface IUiOutput
{
    /// <summary>追加一行 markup 文本(可含换行)。</summary>
    void Write(string markup);

    /// <summary>追加一条带角色标识的聊天消息(icon/颜色按角色, 内容自动转义)。</summary>
    void Message(MessageRole role, string content);

    /// <summary>追加一条成功提示(绿色, 自动转义)。</summary>
    void Ok(string text);

    /// <summary>追加一条错误提示(红色, 自动转义)。</summary>
    void Error(string text);

    /// <summary>追加一条灰色辅助提示(自动转义)。</summary>
    void Hint(string text);

    /// <summary>追加带边框的面板(标题 + 多行 markup 正文)。</summary>
    void Panel(string title, string bodyMarkup);

    /// <summary>追加对齐的表格(等宽文本, 支持单行 markup 单元格)。</summary>
    void Table(string[] headers, IEnumerable<IReadOnlyList<string>> rows);

    /// <summary>清空日志。</summary>
    void Clear();

    /// <summary>滚动到日志底部(命令输出后调用)。</summary>
    void ScrollToBottom();

    /// <summary>模态确认对话框。返回 true 表示确认。可在任意线程调用(自动编组到 UI 线程并阻塞)。</summary>
    bool ConfirmModal(string title, string message, string yesLabel = "是", string noLabel = "否");

    /// <summary>模态多选对话框(按钮即选项)。返回选中下标, 取消返回 -1。可在任意线程调用。</summary>
    int SelectModal(string title, string message, string[] choices);

    /// <summary>模态只读文本对话框(用于查看 diff、分派输出等)。可在任意线程调用。</summary>
    void ShowTextModal(string title, string text);
}
