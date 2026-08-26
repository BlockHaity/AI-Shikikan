using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace AIShikikan.Cli.Tui.Ui;

/// <summary>单行 markup 标签: 按 run 着色绘制, 支持多行文本(逐行绘制)。</summary>
public class MarkupLabelView : View
{
    private List<List<MarkupRun>> _lines = [];

    public MarkupLabelView()
    {
        CanFocus = false;
    }

    public void SetMarkup(string markup)
    {
        _lines = MarkupConverter.ParseLines(markup);
        SetNeedsDraw();
    }

    public void SetText(string text)
    {
        _lines = MarkupConverter.ParseLines(MarkupEscape.Escape(text));
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext context)
    {
        var y = 0;
        foreach (var line in _lines)
        {
            if (y >= Viewport.Height) break;
            var x = 0;
            foreach (var run in line)
            {
                if (x >= Viewport.Width) break;
                SetAttribute(run.Attribute);
                AddStr(x, y, run.Text);
                x += run.Text.Length;
            }

            y++;
        }

        return true;
    }
}
