using Terminal.Gui.Drawing;

namespace AIShikikan.Cli.Tui.Ui;

/// <summary>透明配色: 前景/背景均用终端默认色(Color.None → ANSI 39/49m 复位),
/// 让 TUI 完全遵循终端背景; 选中/焦点角色用加粗区分。</summary>
public static class UiTheme
{
    private static readonly Attribute TransparentAttr = new(Color.None, Color.None);
    private static readonly Attribute TransparentBold = new(Color.None, Color.None, TextStyle.Bold);

    /// <summary>全透明配色方案(所有角色透明, 焦点/激活加粗)。</summary>
    public static readonly Scheme Transparent = new()
    {
        Normal = TransparentAttr,
        Focus = TransparentBold,
        HotNormal = TransparentAttr,
        HotFocus = TransparentBold,
        Active = TransparentBold,
        HotActive = TransparentBold,
        Highlight = TransparentAttr,
        Editable = TransparentAttr,
        ReadOnly = TransparentAttr,
        Disabled = TransparentAttr,
        Code = TransparentAttr,
        CodeComment = TransparentAttr,
        CodeKeyword = TransparentAttr,
        CodeString = TransparentAttr,
        CodeNumber = TransparentAttr,
        CodeOperator = TransparentAttr,
        CodeType = TransparentAttr,
        CodePreprocessor = TransparentAttr,
        CodeIdentifier = TransparentAttr,
        CodeConstant = TransparentAttr,
        CodePunctuation = TransparentAttr,
        CodeFunctionName = TransparentAttr,
        CodeAttribute = TransparentAttr
    };
}
