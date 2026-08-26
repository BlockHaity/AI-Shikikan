using Terminal.Gui.Drivers;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace AIShikikan.Cli.Tui.Ui;

/// <summary>底部输入栏: 上下 ─ 围边 + ❯ 前缀 + 输入框。
/// ↑/↓ 翻历史, Esc 清空。
/// 输入框(<see cref="Field"/>)直接挂载在窗口上而非本视图内: 命令补全的弹出列表
/// 以输入框所在容器为参照计算位置与尺寸, 挂在 3 行高的本视图内会被挤压成一行;
/// 挂到窗口后弹出列表才能完整显示在输入框上方。</summary>
public class InputBar : View
{
    private readonly TextField _field;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private string _draft = string.Empty;

    /// <summary>用户提交一行输入(已 trim)。</summary>
    public event Action<string>? Submitted;

    public InputBar()
    {
        // 焦点链要求: 子视图可聚焦时, 其父链各层也须 CanFocus
        CanFocus = true;

        _field = new TextField
        {
            // 坐标相对窗口: 输入框位于底部边框之上(窗口第 AnchorEnd(3) 行)
            X = 3,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(),
            Height = 1
        };
        _field.BorderStyle = LineStyle.None;
        _field.Accepted += OnFieldAccepted;
        _field.KeyDown += OnFieldKeyDown;

        var prefix = new Label { Text = "❯ ", X = 0, Y = 1 };
        Add(prefix);
    }

    /// <summary>输入框(由外部添加到窗口, 以便命令补全弹出列表在全屏范围内渲染)。</summary>
    public TextField Field => _field;

    public string Value => _field.Value;

    public void SetFocusToField() => _field.SetFocus();

    /// <summary>配置 "/" 命令自动补全: 输入时在输入框上方弹出候选列表,
    /// ↑/↓ 选择, Enter 确认, Esc 关闭, 弹出期间不触发历史翻页与清空。
    /// 注意: 不能替换 <see cref="TextField.Autocomplete"/> 实例 —— TextField 在自身 Initialized
    /// 事件中会给 Autocomplete 设置 HostControl 并把 popup 加入父容器; 若在字段已有
    /// SuperView 后再替换, 新对象会在父容器 EndInit 枚举子视图期间被加入,
    /// 触发 "Collection was modified" 崩溃。因此这里只配置 TextField 自带实例。</summary>
    public void ConfigureAutocomplete(ISuggestionGenerator generator)
    {
        if (_field.Autocomplete is not TextFieldAutocomplete existing)
        {
            return;
        }

        existing.SuggestionGenerator = generator;
        existing.MaxWidth = 56;
        existing.MaxHeight = 8;
    }

    /// <summary>绘制上下 ─ 围边(用终端默认色, 遵循终端背景)。</summary>
    protected override bool OnDrawingContent(DrawContext context)
    {
        var width = Viewport.Width;
        if (width > 0)
        {
            SetAttribute(new Attribute(Color.None, Color.None));
            AddStr(0, 0, new string('─', width));
            AddStr(0, 2, new string('─', width));
        }

        return true;
    }

    private void OnFieldAccepted(object? sender, CommandEventArgs e)
    {
        var input = _field.Value.Trim();
        if (string.IsNullOrEmpty(input))
        {
            _field.Value = string.Empty;
            return;
        }

        _history.Add(input);
        _historyIndex = _history.Count;
        _field.Value = string.Empty;
        Submitted?.Invoke(input);
    }

    private void OnFieldKeyDown(object? sender, Key key)
    {
        // 命令补全有候选时, ↑/↓/Enter/Esc 交给 autocomplete(移动选择/确认/关闭),
        // 不触发历史翻页与清空; 候选关闭(Close 会清空 Suggestions)后恢复默认行为
        if (_field.Autocomplete is { Suggestions.Count: > 0 })
        {
            return;
        }

        switch (key.KeyCode)
        {
            case KeyCode.CursorUp:
                if (_history.Count > 0)
                {
                    // 首次上翻(索引 -1)或从底部(索引 >= 数量)时跳到最后一条
                    if (_historyIndex >= _history.Count || _historyIndex < 0)
                    {
                        _draft = _field.Value;
                        _historyIndex = _history.Count - 1;
                    }
                    else if (_historyIndex > 0)
                    {
                        _historyIndex--;
                    }

                    _field.Value = _history[_historyIndex];
                    key.Handled = true;
                }

                break;

            case KeyCode.CursorDown:
                if (_historyIndex >= 0 && _historyIndex < _history.Count - 1)
                {
                    _historyIndex++;
                    _field.Value = _history[_historyIndex];
                }
                else if (_historyIndex == _history.Count - 1)
                {
                    _historyIndex = _history.Count;
                    _field.Value = _draft;
                }

                key.Handled = true;
                break;

            case KeyCode.Esc when _field.Value.Length > 0:
                _field.Value = string.Empty;
                key.Handled = true;
                break;
        }
    }
}
