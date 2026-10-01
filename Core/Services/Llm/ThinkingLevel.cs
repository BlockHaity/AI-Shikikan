namespace AIShikikan.Core.Services.Llm;

/// <summary>思考深度等级。Off 显式关闭思考, Auto 表示由模型/系统自动决定, 其余为内置档位。</summary>
public enum ThinkingLevel
{
    Off = -1,
    Auto = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    XHigh = 4,
    Max = 5
}

public static class ThinkingLevels
{
    public static string ToConfigString(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => "off",
        ThinkingLevel.Auto => "auto",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.XHigh => "xhigh",
        _ => "max"
    };

    public static bool TryParse(string? s, out ThinkingLevel level)
    {
        level = ThinkingLevel.Auto;
        switch (s?.Trim().ToLowerInvariant())
        {
            case "off" or "none" or "disabled":
                level = ThinkingLevel.Off;
                return true;
            case "auto":
                level = ThinkingLevel.Auto;
                return true;
            case "low":
                level = ThinkingLevel.Low;
                return true;
            case "medium" or "med":
                level = ThinkingLevel.Medium;
                return true;
            case "high":
                level = ThinkingLevel.High;
                return true;
            case "xhigh" or "x-high":
                level = ThinkingLevel.XHigh;
                return true;
            case "max" or "full":
                level = ThinkingLevel.Max;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 思考档位的稳定语义标识(off/auto/low/medium/high/xhigh/max), 与 <see cref="ToConfigString"/> 取值一致。
    /// 刻意不返回展示文案: Core 不能反向引用 GUI 的 Strings, 而在这里硬编码中文会让英文界面
    /// 的思考档位菜单直接显示中文。GUI 侧应拿这个标识去映射本地化文案
    /// (调用点: Views/Converters.cs 的 ThinkingLevelToTextConverter、ViewModels/ChatPageViewModel.SelectedThinkingText)。
    /// </summary>
    public static string DisplayName(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => "off",
        ThinkingLevel.Auto => "auto",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.XHigh => "xhigh",
        _ => "max"
    };

    /// <summary>注入系统提示词的思考深度指令。</summary>
    public static string Directive(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => "思考深度: 关闭。不要展开长篇分析, 直接给出最终答案。",
        ThinkingLevel.Low => "思考深度: 低。直接回答, 无需深入分析。",
        ThinkingLevel.Medium => "思考深度: 中。适度分析后回答。",
        ThinkingLevel.High => "思考深度: 高。进行深入、全面的分析后再回答。",
        ThinkingLevel.XHigh => "思考深度: 极高。进行详尽、多角度的深度分析后再回答。",
        ThinkingLevel.Max => "思考深度: 满。穷尽可能的分析路径, 给出最完整的回答。",
        _ => "思考深度: 自动。根据任务复杂度自动决定分析深度。"
    };

    /// <summary>通过模型名判断其是否具备推理/思考能力(决定是否发送 reasoning_effort 参数)。</summary>
    public static bool IsReasoningModel(string modelId)
    {
        var id = modelId.ToLowerInvariant();
        if (id.Contains("reason") || id.Contains("deepseek-r1"))
        {
            return true;
        }

        if (id.Contains("o1") || id.Contains("o3") || id.Contains("o4"))
        {
            return true;
        }

        if (id.Contains("gpt-5") || id.Contains("gpt-o") || id.Contains("thinking"))
        {
            return true;
        }

        return false;
    }
}
