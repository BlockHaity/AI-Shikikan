using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Git;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Personas;
using AIShikikan.Core.Services.Session;
using AIShikikan.Core.Services.Templates;
using AIShikikan.Core.Services.Tools;
using AIShikikan.Core.Services.Usage;

namespace AIShikikan.Core.Services.Engine;

/// <summary>事件归属上下文: 会话/回合/标题/分支, 由引擎在事件出口统一标注。</summary>
public sealed record EngineEventScope(string SessionId, string TurnId, string SessionTitle, string? Branch);

public abstract record AgentEngineEvent
{
    /// <summary>事件归属; 兼容旧构造(未标注)时为空。</summary>
    public EngineEventScope? Scope { get; set; }

    /// <summary>归属会话 ID(未标注为空串)。</summary>
    public string SessionId => Scope?.SessionId ?? string.Empty;

    /// <summary>归属回合 ID(未标注为空串)。</summary>
    public string TurnId => Scope?.TurnId ?? string.Empty;

    /// <summary>归属会话标题(未标注为空串)。</summary>
    public string SessionTitle => Scope?.SessionTitle ?? string.Empty;

    /// <summary>归属工作区分支(未标注为 null)。</summary>
    public string? Branch => Scope?.Branch;
}

public sealed record EngineTextDelta(string Text) : AgentEngineEvent;

public sealed record EngineThinkingDelta(string Thinking) : AgentEngineEvent;

public sealed record EngineToolStarted(string ToolCallId, string ToolName, string Arguments) : AgentEngineEvent;

public sealed record EngineToolOutput(string ToolCallId, string ToolName, string Line) : AgentEngineEvent;

public sealed record EngineToolFinished(string ToolCallId, string ToolName, ToolResult Result) : AgentEngineEvent;

public sealed record EngineApprovalRequested(
    string ToolCallId, string ToolName, string Arguments,
    TaskCompletionSource<bool> UserDecision) : AgentEngineEvent;

/// <summary>AI 通过 ask_user 工具向用户提问, 等待 GUI 回填回答(null 表示跳过/取消)。</summary>
public sealed record EngineQuestionRequested(
    string ToolCallId, string Question,
    TaskCompletionSource<string?> UserAnswer) : AgentEngineEvent;

public sealed record EngineDone(string? Content, string? Error) : AgentEngineEvent;

public sealed record EngineAssignmentChanged(Assignment Assignment) : AgentEngineEvent;

public sealed record EngineUsageRecorded(string Provider, string Model, ChatUsage Usage) : AgentEngineEvent;

/// <summary>上下文自动压缩完成事件(达到阈值后由引擎在发起请求前触发)。</summary>
public sealed record EngineContextCompacted : AgentEngineEvent;

/// <summary>回合被取消事件(会话级终态取消 / per-turn 取消 / 外部 ct 任一触发)。
/// 取消路径刻意不发出 EngineDone(原因见 RunTurnCoreAsync 的 catch 分支注释), 需要闭环的
/// 订阅者(如"把已生成内容落盘为截断消息")应订阅本事件, 不要等 EngineDone。</summary>
public sealed record EngineTurnCanceled : AgentEngineEvent;

public sealed class EngineOptions
{
    /// <summary>单回合内最大工具迭代轮数; 当前无外部配置方, 取下方默认值 10。</summary>
    public int MaxTurns { get; set; } = 10;

    /// <summary>免审批直跑全部工具; 当前无外部配置方(恒 false, 需审批工具仍逐个走审批事件)。</summary>
    public bool AutoApprove { get; set; }

    /// <summary>模型名; 未设置时由 LlmService 按 Provider 默认模型解析。</summary>
    public string? Model { get; set; }

    /// <summary>Provider Id; 为空时 LlmService 走默认 Provider。</summary>
    public string? ProviderId { get; set; }

    /// <summary>单次请求携带的历史消息条数上限; 当前无外部配置方, 取下方默认值 40。
    /// 实际切点由 FindToolSafeCutoff 在此基础上向前回退以保证 tool_calls 配对完整。</summary>
    public int MaxHistoryMessages { get; set; } = 40;

    /// <summary>追加到 system 提示词的额外指令; 当前无外部配置方(恒 null)。</summary>
    public string? SystemExtra { get; set; }

    /// <summary>思考深度; 由聊天页每回合开始前就地写入(UI 线程)。</summary>
    public ThinkingLevel Thinking { get; set; } = ThinkingLevel.Auto;

    /// <summary>会话工作目录; 由聊天页每回合开始前就地写入(UI 线程)。</summary>
    public string? WorkDir { get; set; }

    /// <summary>Plan 模式; 由聊天页与 CommanderRuntime.SetPlanMode 就地写入(UI 线程)。</summary>
    public bool IsPlanMode { get; set; }

    /// <summary>是否启用上下文自动压缩(达到阈值后在发起请求前压缩历史); 当前无外部配置方, 取下方默认值。</summary>
    public bool AutoCompactEnabled { get; set; } = true;

    /// <summary>自动压缩触发阈值(最近一次 input tokens / 上下文窗口大小), 默认 85%; 当前无外部配置方, 取下方默认值。</summary>
    public double AutoCompactThreshold { get; set; } = 0.85;

    /// <summary>模型上下文窗口无法解析时的兜底值(默认 128K, 与 ModelProfileService.DefaultContextTokens 一致)。
    /// 设 0 = 关闭兜底(等价于回到"未知模型不压缩"的旧行为)。</summary>
    public long FallbackContextTokens { get; set; } = 128_000;
}

/// <summary>对话引擎: 组装 system(人格 + Roster) → LLM → 工具(批准/只读/子代理) → 循环至完成。</summary>
public sealed class AgentEngine
{
    /// <summary>等待用户响应(工具审批 / ask_user 提问)的最长时间: 超过则按无响应处理,
    /// 避免 GUI 无订阅者或用户离开时引擎永久挂起。</summary>
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(5);

    /// <summary>本回合实际生效的模型名(回合开始时解析并缓存)。
    /// 工具上下文(子代理输出压缩)需与主对话同模型, 避免逐次工具调用重复推导。</summary>
    private string? _currentModel;

    private readonly LlmService _llm;
    private readonly ToolRegistry _registry;
    private readonly GitService _git;
    private readonly AssignmentManager _assignments;
    private readonly IReadOnlyList<Persona> _personas;
    private readonly IReadOnlyList<AgentTemplate> _templates;
    private readonly IReadOnlyList<CliAgentDefinition> _agents;
    private readonly string _workspaceRoot;
    private string? _personaText;
    private readonly EngineOptions _options;
    private readonly List<ChatTurnMessage> _conversation = [];
    private IReadOnlyList<AgentRosterEntry>? _rosterEntries;

    /// <summary>最近一次 LLM 调用的输入 token 数(即上下文占用), 供自动压缩阈值判断。</summary>
    private int _lastInputTokens;

    /// <summary>回合内不可变的选项快照(见 RunTurnAsync 的快照注释)。
    /// 做成 record struct: 12 个字段以内编译器按值传, 不会额外堆分配。</summary>
    private readonly record struct TurnOptions(
        int MaxTurns,
        bool AutoApprove,
        string? Model,
        string? ProviderId,
        int MaxHistoryMessages,
        string? SystemExtra,
        ThinkingLevel Thinking,
        string? WorkDir,
        bool IsPlanMode,
        bool AutoCompactEnabled,
        double AutoCompactThreshold,
        long FallbackContextTokens);

    // ---- 会话身份 / 事件路由 ----

    private readonly EngineEventHub? _hub;
    private readonly ISessionEngineHost? _host;
    private Action<AgentEngineEvent>? _localHandlers;
    private int _turnSeq;
    private string? _currentTurnId;

    /// <summary>会话级**终态**取消源: 取消后不再被重置, 该引擎无法再执行任何回合
    /// (回合入口 ThrowIfCancellationRequested 立即抛)。仅 CancelSession() 触发。</summary>
    private readonly CancellationTokenSource _sessionCts = new();

    /// <summary>当前回合的取消源: 每次 RunTurnAsync 开始时换新, 只影响本回合。
    /// 这样"停一个回合"不会像 CancelSession 那样把整台引擎废掉。</summary>
    private CancellationTokenSource _turnCts = new();

    /// <summary>事件归属上下文缓存: 流式期间每 token 一次 Raise, 原本每次 new 一个
    /// EngineEventScope, 叠加 GUI 侧 O(n²) 的 Markdown 重解析放大开销。</summary>
    private EngineEventScope? _scopeCache;

    /// <summary>归属会话 ID(每会话独立引擎)。</summary>
    public string SessionId { get; }

    /// <summary>归属会话标题(事件携带, 由注册表维护)。</summary>
    public string SessionTitle { get; private set; }

    /// <summary>会话当前绑定分支(回合开始时由协调器解析刷新)。</summary>
    public string? Branch { get; private set; }

    /// <summary>当前进行中的回合 ID(无回合时为 null)。</summary>
    public string? CurrentTurnId => _currentTurnId;

    public AgentEngine(
        LlmService llm,
        ToolRegistry registry,
        GitService git,
        AssignmentManager assignments,
        IReadOnlyList<Persona> personas,
        IReadOnlyList<AgentTemplate> templates,
        IReadOnlyList<CliAgentDefinition> agents,
        string workspaceRoot,
        string? personaText = null,
        EngineOptions? options = null,
        IReadOnlyList<AgentRosterEntry>? rosterEntries = null,
        string? sessionId = null,
        string? sessionTitle = null,
        ISessionEngineHost? host = null,
        EngineEventHub? hub = null)
    {
        _llm = llm;
        _registry = registry;
        _git = git;
        _assignments = assignments;
        _personas = personas;
        _templates = templates;
        _agents = agents;
        _workspaceRoot = workspaceRoot;
        _personaText = personaText;
        _rosterEntries = rosterEntries;
        _options = options ?? new EngineOptions();
        SessionId = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId;
        SessionTitle = sessionTitle ?? SessionId;
        _host = host;
        _hub = hub;
    }

    /// <summary>引擎事件: 存在事件 Hub 时订阅进入 Hub(按活动会话过滤投递), 否则为本引擎原始事件。</summary>
    public event Action<AgentEngineEvent>? OnEvent
    {
        add
        {
            if (value is null) return;
            if (_hub is not null) _hub.Subscribe(value);
            else _localHandlers += value;
        }
        remove
        {
            if (value is null) return;
            if (_hub is not null) _hub.Unsubscribe(value);
            else _localHandlers -= value;
        }
    }

    /// <summary>未过滤的原始事件出口(会话注册表订阅, 用于后台会话用量落盘等簿记)。</summary>
    internal event Action<AgentEngineEvent>? RawEvent;

    /// <summary>本引擎专属事件出口: 无条件触发, 不经 Hub 的"活动会话"过滤。
    /// 供需要严格绑定到某个引擎的订阅者使用 —— 走 OnEvent 时, 只要引擎带 Hub, 实际订阅的
    /// 就是全局 Hub, 切会话后旧会话的增量会被丢弃(后台会话流式时 UI 完全没有输出)。</summary>
    public event Action<AgentEngineEvent>? LocalEvent;

    /// <summary>事件出口: 标注归属上下文 → 原始订阅者 → 本引擎订阅者 → 本地订阅者 → Hub(过滤投递)。</summary>
    private void Raise(AgentEngineEvent e)
    {
        e.Scope ??= CurrentScope();
        RawEvent?.Invoke(e);
        LocalEvent?.Invoke(e);
        _localHandlers?.Invoke(e);
        _hub?.Publish(e);
    }

    private EngineEventScope CurrentScope()
    {
        var turnId = _currentTurnId ?? string.Empty;
        var cached = _scopeCache;
        // 命中条件里必须带上 SessionTitle: 它由注册表在首条消息后异步改写(标题自动生成),
        // 只比 (SessionId, TurnId, Branch) 三元组会让后续事件携带过期标题, 聊天卡片标题错乱。
        // 四元组全等才复用同一实例 —— record 不可变, 复用对外完全等价。
        if (cached is not null &&
            cached.SessionId == SessionId &&
            cached.TurnId == turnId &&
            cached.Branch == Branch &&
            cached.SessionTitle == SessionTitle)
        {
            return cached;
        }

        cached = new EngineEventScope(SessionId, turnId, SessionTitle, Branch);
        _scopeCache = cached;
        return cached;
    }

    /// <summary>更新会话标题/分支(注册表维护; 分支同时在回合开始时由协调器刷新)。</summary>
    public void SetSessionInfo(string? title, string? branch)
    {
        if (title is not null) SessionTitle = title;
        if (branch is not null) Branch = branch;
    }

    /// <summary>取消该会话的进行中与后续回合(会话删除/停止会话时调用)。
    /// 这是**终态**取消: _sessionCts 一旦取消就不再被重置, 此后该引擎的任何回合都会在入口
    /// 立即抛 OperationCanceledException, 只能靠删除并重建会话运行时来恢复。
    /// ⚠ 与 GUI 层 ChatPageViewModel 的停止按钮不是一回事 —— 那个走 GUI 自己的 _turnCts,
    /// 语义是"只停本回合"。Core 内两者不要混用。</summary>
    public void CancelSession()
    {
        CancelTurn(); // 顺手停掉进行中的回合, 使等待中的审批/ask_user 也立即醒来

        try
        {
            _sessionCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 引擎已释放: 忽略
        }
    }

    /// <summary>只取消当前进行中的回合, 不影响后续回合的可执行性。
    /// 每回合开始时会换一枚新的 _turnCts, 因此"排队中的下一回合"不会被本方法误伤。
    /// 需要让整个引擎作废时用 CancelSession()。</summary>
    public void CancelTurn()
    {
        var cts = Volatile.Read(ref _turnCts);
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 回合已结束并释放: 忽略
        }
    }

    public EngineOptions Options => _options;

    public AssignmentManager Assignments => _assignments;

    public string? PersonaText => _personaText;

    public IReadOnlyList<AgentRosterEntry>? RosterEntries => _rosterEntries;

    public void SetPersonaText(string? text)
    {
        _personaText = string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public void SetRosterEntries(IReadOnlyList<AgentRosterEntry>? entries)
    {
        _rosterEntries = entries;
    }

    public void ClearConversation()
    {
        _conversation.Clear();
        _lastInputTokens = 0;
    }

    /// <summary>解析模型上下文窗口大小: 模型设置手配值优先, 回退模型档案(API/内置);
    /// 仍拿不到(未收录的未知模型)时回退 <paramref name="fallback"/>, 不再返回 0。</summary>
    private long ResolveContextTokens(string model, ProviderConfig provider, long fallback)
    {
        var tokens = provider.GetContextTokens(model)
            ?? ModelProfileService.Resolve(model, provider.Id).ContextTokens;
        // 未知模型(既没在 providers.toml手配、/v1/models 也没收录)时档案 ContextTokens 为 0。
        // 若原样返回 0, 自动压缩的 `total > 0` 守卫恒不成立 → 压缩对未知模型彻底静默失效,
        // 而用户恰恰最可能在用没配过 models.toml 的新模型。这里必须兜底。
        return tokens > 0 ? tokens : fallback;
    }

    /// <summary>拍一份 EngineOptions 快照。逐字段读: 引用类型字段的读本身是原子的,
    /// 途中被 UI 线程改只会让个别字段取到新值, 不会拿到"引用与内容不匹配"的撕裂对象。</summary>
    private TurnOptions SnapshotOptions()
    {
        var o = _options;
        return new TurnOptions(
            o.MaxTurns, o.AutoApprove, o.Model, o.ProviderId, o.MaxHistoryMessages,
            o.SystemExtra, o.Thinking, o.WorkDir, o.IsPlanMode,
            o.AutoCompactEnabled, o.AutoCompactThreshold, o.FallbackContextTokens);
    }

    /// <summary>求"保留最后 <paramref name="takeLast"/> 条"的安全切点: 从 count-takeLast 起向前回退,
    /// 直到保留段首条同时满足——
    /// ① 自己不是 tool 结果(否则请求里出现孤立 tool 消息, OpenAI 直接 400);
    /// ② 前一条不是带 tool_calls 的 assistant(否则出现孤立 tool_use, Anthropic 400);
    /// ③ 自己不是 user(压缩摘要是以 user 角色插在保留段之前的, 连续 user 会被部分 API 拒)。
    /// 返回保留段的起始下标; 回退到 0 表示"整段都得带上"(病态历史下优先保证配对完整)。
    /// 压缩切点与请求切点共用本方法, 避免两处语义漂移。</summary>
    private static int FindToolSafeCutoff(IReadOnlyList<ChatTurnMessage> msgs, int takeLast)
    {
        if (takeLast <= 0 || msgs.Count <= takeLast) return 0;

        var from = msgs.Count - takeLast;
        while (from > 0)
        {
            var first = msgs[from];
            var prev = msgs[from - 1];
            var breaks = first.Role == ChatMsgRole.Tool
                         || first.Role == ChatMsgRole.User
                         || (prev.Role == ChatMsgRole.Assistant && prev.ToolCalls is { Count: > 0 });
            if (!breaks) break;
            from--;
        }

        return from;
    }

    /// <summary>压缩主对话上下文: 保留最近 KeepRecent 条消息, 其余历史交由 LLM 摘要为一条
    /// 上下文消息替换。返回是否发生压缩(历史不足以压缩时 false)。失败时保持原样不误删。
    /// providerId / model 为本回合快照里的实际取值; 外部手动触发压缩时留空, 回退读 EngineOptions。</summary>
    public async Task<bool> CompactConversationAsync(
        CancellationToken ct = default, string? providerId = null, string? model = null)
    {
        const int KeepRecent = 6;
        if (_conversation.Count <= KeepRecent + 1)
        {
            return false; // 历史太短, 无需压缩
        }

        // 边界回退: 保留段不得以 tool 结果 / user 开头, 也不得切断 assistant(tool_calls) 与其结果
        var keepFrom = FindToolSafeCutoff(_conversation, KeepRecent);
        if (keepFrom <= 0)
        {
            return false; // 全部属于最近交互, 无完整可压缩前缀
        }

        var toCompact = _conversation.Take(keepFrom).ToList();
        var effProviderId = providerId ?? _options.ProviderId;
        var provider = _llm.GetProvider(effProviderId);
        if (provider is null) return false;

        var effModel = model ?? _llm.ResolveModel(_options.Model, effProviderId);

        try
        {
            var sb = new StringBuilder();
            foreach (var m in toCompact)
            {
                var (label, body) = m.Role switch
                {
                    ChatMsgRole.User => ("用户", m.Content),
                    ChatMsgRole.Assistant when m.ToolCalls is { Count: > 0 } =>
                        ("助手", $"(调用工具: {string.Join(", ", m.ToolCalls.Select(t => t.Name))})"),
                    ChatMsgRole.Assistant => ("助手", m.Content),
                    ChatMsgRole.Tool => ("工具结果", m.Content),
                    _ => ("其他", m.Content)
                };
                // 摘要为纯文本: 图片不进入摘要, 但标注数量避免后续上下文误判"用户从未发图"
                if (m.Images is { Count: > 0 })
                {
                    body = string.IsNullOrWhiteSpace(body)
                        ? $"(发送了 {m.Images.Count} 张图片)"
                        : $"{body}\n(并发送了 {m.Images.Count} 张图片)";
                }

                if (string.IsNullOrWhiteSpace(body)) continue;
                sb.Append($"\n[{label}] {(body.Length > 4000 ? body[..4000] + "..." : body)}");
            }

            // 摘要请求自身也要防超长: 中段截断保留头尾
            var transcript = sb.ToString();
            if (transcript.Length > 24000)
            {
                transcript = transcript[..12000] + "\n...(中段过长已省略)...\n" + transcript[^6000..];
            }

            var request = new ChatRequest
            {
                Model = effModel,
                MaxTokens = 2048,
                Temperature = 0.1,
                System = """
                    你是对话历史压缩器。把给定的多轮对话历史压缩为一份结构化摘要, 供后续对话作为上下文继续。要求:
                    1. 保留: 用户的总体目标、已达成的关键结论、修改/创建的文件路径、工具执行结果要点、未决事项与约束;
                    2. 剔除: 寒暄、冗余过程、重复内容;
                    3. 用简洁的分点中文输出, 不要开场白, 不要臆造未出现的信息。
                    """,
                Messages =
                [
                    new ChatTurnMessage { Role = ChatMsgRole.User, Content = transcript }
                ]
            };

            var response = await _llm.GetClient(provider.Id).CompleteAsync(request, ct)
                .ConfigureAwait(false);
            if (response.IsError || string.IsNullOrWhiteSpace(response.Content))
            {
                Log.Warn("Engine", $"上下文压缩未生效: {response.Error ?? "空内容"}");
                return false;
            }

            // 用摘要替换被压缩的历史, 保留 keepFrom 之后的消息。
            // 摘要用 User 角色(部分 API 对开头的 assistant 容忍度反而更差), 因此切点已由
            // FindToolSafeCutoff 保证保留段首条不是 User, 不会拼出连续 user 消息。
            var summary = new ChatTurnMessage
            {
                Role = ChatMsgRole.User,
                Content = $"# 前情摘要(已压缩 {toCompact.Count} 条历史)\n{response.Content.Trim()}"
            };
            var recent = _conversation.Skip(keepFrom).ToList();
            _conversation.Clear();
            _conversation.Add(summary);
            _conversation.AddRange(recent);
            _lastInputTokens = 0; // 压缩后占用待下一次真实用量刷新, 避免连续误触发

            Log.Info("Engine", $"上下文已压缩: {toCompact.Count} 条 → 摘要 1 条, 保留最近 {recent.Count} 条");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Engine", ex, "上下文压缩失败, 保持原对话不变");
            return false;
        }
    }

    /// <summary>用 UI 会话消息重建引擎内部对话(删除/fork 编辑消息后调用, 保证 LLM 上下文一致)。
    /// 工具分段压缩为简短摘要并入助手回合文本; 图片分段作为多模态附件保留。
    ///
    /// 设计取舍(有意为之, 不是缺陷): 这里**刻意不重建** Tool 角色消息与 ToolCallId 配对 ——
    /// 历史里出现过的工具调用被压成 "[已执行工具 X: 截断结果]" 并并入助手回合文本, 之后 LLM
    /// 看不到原始的 tool_calls 结构。原因是: (1) 对话历史可能几十上百轮, 逐条重建 tool 配对
    /// 会让每次编辑/分叉都把上下文撑大数倍, 而这些结果对"接着聊"几乎无价值;
    /// (2) 重建出的配对若与实际工具 id 对不上, 反而更容易触发 API 校验错误。
    /// 代价是: fork/编辑后模型不再知道"我曾经调用过什么工具、参数是什么", 只知道结果摘要。</summary>
    public void RebuildConversation(IReadOnlyList<ChatMessage> messages)
    {
        _conversation.Clear();
        _lastInputTokens = 0; // 历史已重建, 旧的占用读数作废
        foreach (var m in messages)
        {
            var text = string.Join("\n", m.Segments
                .Where(s => s.Kind == MessageSegmentKind.Text)
                .Select(s => s.Content));

            var tools = m.Segments.Where(s => s.Kind == MessageSegmentKind.Tool && s.Tool is not null).ToList();
            if (tools.Count > 0)
            {
                var sb = new StringBuilder(text);
                foreach (var t in tools)
                {
                    var tool = t.Tool!;
                    sb.Append($"\n\n[已执行工具 {tool.Name}");
                    if (tool.IsError) sb.Append(" (失败)");
                    if (!string.IsNullOrWhiteSpace(tool.Result))
                    {
                        var r = tool.Result.Length > 1500 ? tool.Result[..1500] + "..." : tool.Result;
                        sb.Append($": {r}");
                    }

                    sb.Append(']');
                }

                text = sb.ToString();
            }

            var images = m.Segments
                .Where(s => s.Kind == MessageSegmentKind.Image && !string.IsNullOrEmpty(s.ImageData))
                .Select(s => new ChatImagePart { Base64Data = s.ImageData!, MimeType = s.ImageMimeType ?? "image/png" })
                .ToList();

            if (string.IsNullOrWhiteSpace(text) && images.Count == 0) continue;

            _conversation.Add(new ChatTurnMessage
            {
                Role = m.Role == MessageRole.User ? ChatMsgRole.User : ChatMsgRole.Assistant,
                Content = text,
                Images = images.Count > 0 ? images : null
            });
        }
    }

    public Task<string> RunTurnAsync(string userMessage, CancellationToken ct = default) =>
        RunTurnAsync(userMessage, null, ct);

    /// <summary>发起一轮对话; images 为用户消息附带的多模态图片(base64)。
    /// 回合开始前向工作区协调器申请执行权(同 worktree 跨分支已有活动/保留期直接拒绝, 不改动对话)。</summary>
    public async Task<string> RunTurnAsync(string userMessage, IReadOnlyList<ChatImagePart>? images, CancellationToken ct = default)
    {
        // 回合开始时快照 EngineOptions: GUI 在 UI 线程就地改写同一个可变对象(思考等级/工作目录/
        // Plan 模式), 引擎回合跑在池线程, 两边没有任何同步。ARM 弱内存序下没有发布屏障,
        // 引擎线程可能读到"写了一半"的组合(如 Thinking 已更新而 WorkDir 还是旧的)。
        // 快照后整个回合只用这一份, GUI 的中途改动从下一回合生效 —— 这也符合语义: 设置本就
        // 是"下一条消息生效", 不该影响已发出的那条。
        var opts = SnapshotOptions();

        var turnId = $"{SessionId}-{Interlocked.Increment(ref _turnSeq)}";
        WorkspaceActivity? activity = null;
        if (_host is not null)
        {
            if (!_host.TryBeginTurn(this, turnId, opts.WorkDir, out var blockedReason, out activity))
            {
                var blocked = $"⛔ 无法发送: {blockedReason}";
                Log.Warn("Engine", $"回合 {turnId} 被工作区协调器拒绝: {blockedReason}");
                Raise(new EngineDone(null, blocked));
                return blocked;
            }

            if (activity is not null)
            {
                Branch = activity.Key.Branch; // 事件归属携带最新分支
            }
        }

        _currentTurnId = turnId;
        // per-turn 取消源: 每回合换新, 让 CancelTurn() 只停本回合(与终态的 _sessionCts 分离)
        var turnCts = new CancellationTokenSource();
        Interlocked.Exchange(ref _turnCts, turnCts)?.Dispose();
        // 三个取消源合并: 会话级终态停止(手动停止会话) + 本回合停止(CancelTurn) + 外部 ct(排队中用户已停止)
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct, _sessionCts.Token, turnCts.Token);
        try
        {
            return await RunTurnCoreAsync(userMessage, images, linkedCts.Token, opts).ConfigureAwait(false);
        }
        finally
        {
            _currentTurnId = null;
            _host?.EndTurn(this, activity);
        }
    }

    /// <summary>回合主体: 组装提示词 → LLM 流式 → 工具循环(工作区执行权由调用方持有)。
    /// opts 是本回合的选项快照, 整个回合只读它, 不再回头读可变的 _options。</summary>
    private async Task<string> RunTurnCoreAsync(
        string userMessage, IReadOnlyList<ChatImagePart>? images, CancellationToken ct, TurnOptions opts)
    {
        // 连续用户消息合并为一条(继续输出场景), 避免部分 API 要求严格角色交替
        var last = _conversation.Count > 0 ? _conversation[^1] : null;
        if (last is { Role: ChatMsgRole.User })
        {
            last.Content += $"\n\n{userMessage}";
            if (images is { Count: > 0 })
            {
                last.Images ??= [];
                last.Images.AddRange(images);
            }
        }
        else
        {
            _conversation.Add(new ChatTurnMessage
            {
                Role = ChatMsgRole.User,
                Content = userMessage,
                Images = images is { Count: > 0 } ? images.ToList() : null
            });
        }

        try
        {
            var provider = _llm.GetProvider(opts.ProviderId);
            if (provider is null)
            {
                var msg = "未配置 Provider。请在 providers.toml 中添加(见 ConfigDir)或设置 OPENAI_API_KEY / ANTHROPIC_API_KEY。";
                Raise(new EngineDone(null, msg));
                return msg;
            }

            var model = _llm.ResolveModel(opts.Model, opts.ProviderId);
            // 缓存本回合实际生效的模型: 工具上下文(子代理输出压缩)需要同模型, 避免逐次工具调用重复解析
            _currentModel = model;
            var thinking = ResolveThinking(model, opts.ProviderId, opts.Thinking);
            var system = BuildSystemPrompt(thinking, opts);

            Log.Debug("LLM", $"回合开始: provider={opts.ProviderId ?? "默认"}, model={model}, thinking={thinking}");

            for (var turn = 0; turn < opts.MaxTurns; turn++)
            {
                ct.ThrowIfCancellationRequested();

                // 自动压缩: 上下文占用达到阈值(默认 85%)时, 发起请求前先压缩较早历史
                if (opts.AutoCompactEnabled && _lastInputTokens > 0)
                {
                    // 未知模型回退到 FallbackContextTokens; 用户显式设 0 时 total==0 → 关闭压缩
                    var total = ResolveContextTokens(model, provider, opts.FallbackContextTokens);
                    if (total > 0 && _lastInputTokens >= total * opts.AutoCompactThreshold)
                    {
                        if (await CompactConversationAsync(ct, opts.ProviderId, model).ConfigureAwait(false))
                        {
                            Raise(new EngineContextCompacted());
                        }
                    }
                }

                var request = new ChatRequest
                {
                    Model = model,
                    System = system,
                    MaxTokens = 4096,
                    Temperature = 0.2,
                    Tools = _registry.ToSpecs(),
                    Thinking = thinking,
                    // 切点必须配对安全: 无脑 TakeLast 会把 assistant(tool_calls) 与其 tool 结果切开,
                    // 发出孤立 tool 消息(OpenAI 400)/孤立 tool_use(Anthropic 400)
                    Messages = _conversation
                        .Skip(FindToolSafeCutoff(_conversation, opts.MaxHistoryMessages))
                        .ToList()
                };

                var client = _llm.GetClient(opts.ProviderId);
                ChatCompletionResult? response = null;

                await foreach (var sse in client.StreamAsync(request, ct).ConfigureAwait(false))
                {
                    switch (sse.Kind)
                    {
                        case StreamEventKind.TextDelta:
                            Raise(new EngineTextDelta(sse.Text ?? ""));
                            break;
                        case StreamEventKind.ThinkingDelta:
                            Raise(new EngineThinkingDelta(sse.Thinking ?? ""));
                            break;
                        case StreamEventKind.Error:
                            var sErr = $"流式错误: {sse.Error}";
                            Log.Warn("LLM", sErr);
                            Raise(new EngineDone(null, sErr));
                            return $"模型调用失败: {sErr}";
                        case StreamEventKind.Done:
                            response = sse.Final;
                            break;
                    }
                }

                response ??= new ChatCompletionResult { IsError = true, Error = "未收到模型响应" };

                if (response.Usage is { InputTokens: > 0 } or { OutputTokens: > 0 })
                {
                    _lastInputTokens = response.Usage.InputTokens;
                    Raise(new EngineUsageRecorded(provider.Id, model, response.Usage));
                }

                if (response.IsError)
                {
                    Log.Warn("LLM", $"响应错误: {response.Error}");
                    Raise(new EngineDone(null, response.Error));
                    return $"模型调用失败: {response.Error}";
                }

                if (response.ToolCalls is { Count: > 0 })
                {
                    _conversation.Add(new ChatTurnMessage
                    {
                        Role = ChatMsgRole.Assistant,
                        Content = string.Empty,
                        ToolCalls = response.ToolCalls
                    });

                    foreach (var call in response.ToolCalls)
                    {
                        try
                        {
                            var result = await ExecuteToolAsync(call, ct, opts);
                            _conversation.Add(new ChatTurnMessage
                            {
                                Role = ChatMsgRole.Tool,
                                Content = result,
                                ToolCallId = call.Id
                            });
                        }
                        catch (OperationCanceledException)
                        {
                            // 取消时补齐占位结果并闭环事件, 保持 tool_calls/tool 配对完整, 便于后续继续输出
                            Raise(new EngineToolFinished(call.Id, call.Name,
                                ToolResult.Error("用户中断了此工具调用。")));
                            _conversation.Add(new ChatTurnMessage
                            {
                                Role = ChatMsgRole.Tool,
                                Content = "用户中断了此工具调用。",
                                ToolCallId = call.Id
                            });
                            throw;
                        }
                    }

                    continue;
                }

                var content = response.Content ?? string.Empty;
                _conversation.Add(new ChatTurnMessage
                {
                    Role = ChatMsgRole.Assistant,
                    Content = content
                });

                Raise(new EngineDone(content, null));
                return content;
            }

            var stopMsg = $"工具迭代超过 {opts.MaxTurns} 轮, 已停止。";
            Raise(new EngineDone(null, stopMsg));
            return stopMsg;
        }
        catch (OperationCanceledException)
        {
            // 这里刻意**不**补发 EngineDone: GUI 依赖"取消时 await 抛 OperationCanceledException"这一既有
            // 契约来保留已生成内容、显示停止提示并开启"继续输出"(见 ChatPageViewModel 的 catch (OCE))。
            // 若在这里再补一个 Done, 订阅者会同时收到"正常收尾"信号, 可能把截断的助手消息当成功回合
            // 落库、或把停止提示顶掉。要闭环请用下面这个专用事件。
            Raise(new EngineTurnCanceled());
            throw;
        }
        catch (Exception ex)
        {
            var msg = $"发生错误: {ex.Message}";
            Raise(new EngineDone(null, msg));
            return msg;
        }
    }

    private async Task<string> ExecuteToolAsync(ToolCallData call, CancellationToken ct, TurnOptions opts)
    {
        Raise(new EngineToolStarted(call.Id, call.Name, call.Arguments));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (!_registry.TryGet(call.Name, out var tool))
        {
            var err = $"工具不存在: {call.Name}";
            Log.Warn("Engine", err);
            Raise(new EngineToolFinished(call.Id, call.Name, ToolResult.Error(err)));
            return $"工具调用失败: {err}";
        }

        if (tool.RequiresApproval && !opts.AutoApprove)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Raise(new EngineApprovalRequested(call.Id, call.Name, call.Arguments, tcs));
            bool approved;
            try
            {
                // 超时兜底: GUI 无订阅者(如后台会话)时不会有人回填决策, 必须自行兜底避免永久挂起
                approved = await tcs.Task.WaitAsync(ApprovalTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                var timedOut = $"工具 {call.Name} 的审批等待超时({ApprovalTimeout.TotalMinutes:0} 分钟无响应), 已按拒绝处理。请向用户说明并询问替代方案。";
                Log.Warn("Engine", timedOut);
                Raise(new EngineToolFinished(call.Id, call.Name, ToolResult.Error(timedOut)));
                return timedOut;
            }

            if (!approved)
            {
                var declined = $"用户拒绝了工具调用 {call.Name}。请向用户说明并询问替代方案。";
                Raise(new EngineToolFinished(call.Id, call.Name, ToolResult.Error(declined)));
                return declined;
            }
        }

        ToolResult result;
        try
        {
            JsonElement args;
            try
            {
                args = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments).RootElement.Clone();
            }
            catch (JsonException)
            {
                args = LlmJson.ParseArgs(call.Arguments);
            }

            var context = new ToolContext
            {
                WorkspaceRoot = _workspaceRoot,
                IsPlanMode = opts.IsPlanMode,
                // 归属会话: 工具侧的"按会话"状态(子代理压缩开关 / Plan 授权)必须按发起回合的会话查,
                // 不能读"当前活动会话" —— 后台会话跑子代理时用户切到别的会话会串味
                SessionId = SessionId,
                // 子代理输出压缩要与本回合同 provider/model, 否则用户切了非默认模型时压缩会走另一个模型
                ProviderId = opts.ProviderId,
                Model = _currentModel,
                OnToolOutput = line => Raise(new EngineToolOutput(call.Id, call.Name, line)),
                // ask_user 工具经此回调触达 GUI: 发事件 → 弹输入框 → 等待用户回答(取消随回合中断)
                // 同样加超时兜底: 无订阅者时不至于永久挂起
                // 超时(TimeoutException)不在引擎侧转文本, 而是交给 AskUserTool 自己接住并走
                // "用户未作答(已跳过)" 分支 —— 超时不是错误, 让 LLM 拿到异常文本会反复重问
                AskUser = async (question, askCt) =>
                {
                    var answerTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Raise(new EngineQuestionRequested(call.Id, question, answerTcs));
                    return await answerTcs.Task.WaitAsync(ApprovalTimeout, askCt);
                }
            };

            result = await tool.ExecuteAsync(args, context, ct);

            if (result.IsError)
            {
                Log.Warn("Engine", $"工具 {call.Name} 失败({sw.ElapsedMilliseconds}ms): {TruncateOneLine(result.Content)}");
            }
            else
            {
                Log.Info("Engine", $"工具 {call.Name} 完成 ({sw.ElapsedMilliseconds}ms)");
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("Engine", $"工具 {call.Name} 被用户中断");
            throw; // 用户终止需要立即上抛
        }
        catch (Exception ex)
        {
            Log.Error("Engine", ex, $"工具 {call.Name} 执行异常");
            result = ToolResult.Error($"工具执行异常: {ex.Message}");
        }

        Raise(new EngineToolFinished(call.Id, call.Name, result));
        return result.Content;

        static string TruncateOneLine(string s)
        {
            var line = s.Replace('\n', ' ').Replace('\r', ' ');
            return line.Length <= 160 ? line : line[..160] + "...";
        }
    }

    /// <summary>解析实际生效的思考等级: 自动档位解析为当前模型配置的最大思考等级。
    /// 读传入的 options 快照, 不读可变 _options(见 RunTurnAsync 的快照注释)。</summary>
    private ThinkingLevel ResolveThinking(string model, string? providerId, ThinkingLevel configured)
    {
        if (configured is not ThinkingLevel.Auto)
        {
            return configured;
        }

        return _llm.GetProvider(providerId)?.GetMaxThinking(model) ?? ThinkingLevel.Max;
    }

    private string BuildSystemPrompt(ThinkingLevel thinking, TurnOptions opts)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(_personaText))
        {
            parts.Add(_personaText);
        }

        // git 段落用会话工作目录解析(未指定时回退到全局工作区根): 让子代理看到自己实际所在的分支与脏状态
        var roster = RosterBuilder.Build(_agents, _personas, _templates,
            AgentConfigService.LoadUserFile().Rules,
            git: _git,
            workDir: string.IsNullOrWhiteSpace(opts.WorkDir) ? _workspaceRoot : opts.WorkDir,
            rosterEntries: _rosterEntries,
            enabled: true,
            planMode: opts.IsPlanMode);
        if (!string.IsNullOrWhiteSpace(roster))
        {
            parts.Add(roster);
        }

        if (!string.IsNullOrWhiteSpace(opts.SystemExtra))
        {
            parts.Add(opts.SystemExtra);
        }

        var directives = new List<string>
        {
            ThinkingLevels.Directive(thinking)
        };

        if (opts.IsPlanMode)
        {
            directives.Add("当前模式: Plan(计划)。分析需求、拆解任务、制定方案, 但不要直接执行代码修改。");
        }
        else
        {
            directives.Add("当前模式: Build(构建)。直接执行任务、编写代码、完成目标。");
        }

        if (!string.IsNullOrWhiteSpace(opts.WorkDir))
        {
            directives.Add($"工作目录: {opts.WorkDir}");
        }

        if (directives.Count > 0)
        {
            parts.Add(string.Join("\n", directives));
        }

        return parts.Count == 0
            ? "你是 AI-Shikikan 的指挥官, 负责分析需求、调用工具与子代理完成任务。"
            : string.Join("\n\n", parts);
    }
}
