namespace AIShikikan.Cli.Tui.Services.Commands;

/// <summary>命令处理器公共工具: 文本截断等纯函数(UI 输出统一走 <see cref="AIShikikan.Cli.Tui.Ui.IUiOutput"/>)。</summary>
public static class CommandUi
{
    public static string Shorten(string? s, int max = 60)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        return s.Length <= max ? s : s[..max] + "...";
    }

    public static string Truncate(string s, int len) => s.Length <= len ? s : s[..len] + "...";
}
