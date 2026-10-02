using System.Text.Json;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;

namespace AIShikikan.Core.Services.Tools;

public class ToolResult
{
    public bool IsError { get; set; }
    public string Content { get; set; } = string.Empty;

    /// <summary>关联的 git 检查点步骤 ID, UI 据此提供回滚按钮。
    /// 目前只有 git_create_checkpoint 会赋值; 子代理工具恒为 null —— 子代理在当前分支就地工作,
    /// 回滚入口是"每条用户消息"的检查点, 与单个子代理无关(旧 ac/&lt;stepId&gt; 分支机制已废弃)。</summary>
    public string? StepId { get; set; }

    /// <summary>结构化卡片展示数据(按工具类型渲染专属卡体); 为空时卡片回退到通用文本展示。</summary>
    public ToolCardDetail? Detail { get; set; }

    public static ToolResult Ok(string content) => new() { Content = content };

    public static ToolResult Error(string message) => new() { IsError = true, Content = message };
}

public class ToolContext
{
    public required string WorkspaceRoot { get; init; }

    /// <summary>主对话是否处于 Plan 模式(决定子代理是否以 plan_args 启动)。</summary>
    public bool IsPlanMode { get; init; }

    /// <summary>
    /// 当前回合使用的 Provider Id(引擎注入)。
    /// 供需要"与本回合同模型"的子流程使用(如子代理输出压缩), 避免走到全局默认 provider 上。
    /// ⚠️ 已知: GUI 的 provider/model 选择写进全局 providers.toml, <c>EngineOptions.ProviderId</c>
    /// 至今零生产调用方, 因此本字段实际恒为 null —— 压缩仍会回退到 <c>LlmService.ActiveProvider</c>。
    /// 接线"引擎侧模型快照"语义后本字段才会真正生效, 届时调用方无需改动。
    /// </summary>
    public string? ProviderId { get; init; }

    /// <summary>
    /// 当前回合实际生效的模型名(引擎注入, 已解析)。
    /// 供子代理输出压缩等场景复用, 避免各处重复推导导致与主对话模型不一致。
    /// ⚠️ 同 <see cref="ProviderId"/>: <c>EngineOptions.Model</c> 无生产调用方, 本字段实际为引擎
    /// 从全局配置解析出的默认模型; 若要"严格跟随本回合选择", 需先接上引擎侧快照。
    /// </summary>
    public string? Model { get; init; }

    /// <summary>执行本工具的会话 Id(引擎注入)。
    /// 会话级子代理配置(Roster: Plan 授权 / 输出压缩开关)必须按发起本回合的会话读取,
    /// 不能读"当前活动会话" —— 后台会话同时跑时两者不是同一个。
    /// 为 null/空时调用方需自行回退(见 AgentExecutor.RosterOf)。</summary>
    public string? SessionId { get; init; }

    /// <summary>子代理实时输出回调(UI 订阅)。</summary>
    public Action<string>? OnToolOutput { get; init; }

    /// <summary>向用户反问并等待回答的回调(由引擎注入, 返回 null 表示用户未作答/取消)。</summary>
    public Func<string, CancellationToken, Task<string?>>? AskUser { get; init; }
}

public interface ITool
{
    string Name { get; }

    string Description { get; }

    JsonElement Parameters { get; }

    bool RequiresApproval { get; }

    Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default);
}

public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public void Register(ITool tool)
    {
        lock (_gate)
        {
            // 字典是 OrdinalIgnoreCase: 两个仅大小写不同的名字(如 run_Foo 与 run_foo、
            // MCP 桥接名 mcp_<server>_<tool> 与同名 run_<agent>)会被静默覆盖,
            // 表现为"某个工具莫名消失/被顶替", 且没有任何线索。至少要留一条告警。
            // 仍然覆盖而非抛异常: 覆盖是既有语义(右侧栏/Plan 切换靠它重建工具集),
            // 抛异常会让重建流程整体失败。
            if (_tools.TryGetValue(tool.Name, out var existing) && !ReferenceEquals(existing, tool))
            {
                Log.Warn("Tools",
                    $"工具名冲突: \"{tool.Name}\" 已注册({existing.GetType().Name}), " +
                    $"被本次注册({tool.GetType().Name})覆盖; 工具总数 {_tools.Count}");
            }

            _tools[tool.Name] = tool;
        }
    }

    /// <summary>按条件注销一组工具(MCP 刷新/右侧栏关闭时重建)。</summary>
    public int UnregisterWhere(Func<ITool, bool> predicate)
    {
        lock (_gate)
        {
            var names = _tools.Where(kv => predicate(kv.Value)).Select(kv => kv.Key).ToList();
            foreach (var n in names)
            {
                _tools.Remove(n);
            }

            return names.Count;
        }
    }

    public bool TryGet(string name, out ITool tool)
    {
        lock (_gate)
        {
            return _tools.TryGetValue(name, out tool!);
        }
    }

    public ITool Get(string name)
    {
        lock (_gate)
        {
            return _tools.TryGetValue(name, out var tool)
                ? tool
                : throw new KeyNotFoundException($"未知工具: {name}");
        }
    }

    public IReadOnlyList<ITool> All
    {
        get
        {
            lock (_gate)
            {
                return _tools.Values.ToList();
            }
        }
    }

    /// <summary>导出全部工具的 LLM 声明(每回合随请求全量重发)。
    /// ⚠️ 已知问题(技术债 #10): 没有任何上限或裁剪 —— Agent 数量增长时 run_&lt;id&gt; 工具会
    /// 挤占上下文并降低工具选择准确率, 请求体也会按工具数线性膨胀。
    /// 刻意不在此处加限制: 裁剪工具集会直接改变 LLM 可用能力, 属于产品决策
    /// (可选方向: 按任务类型/会话开关裁剪工具集, 或把多个子代理合并成"按描述自动路由"的单入口)。</summary>
    public List<Llm.ToolSpec> ToSpecs()
    {
        lock (_gate)
        {
            return _tools.Values.Select(t => new Llm.ToolSpec
            {
                Name = t.Name,
                Description = t.Description,
                Parameters = t.Parameters
            }).ToList();
        }
    }
}

public static class ToolSchema
{
    public static JsonElement Json(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}