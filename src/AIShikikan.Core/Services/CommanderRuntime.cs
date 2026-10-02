using System.Text.Json;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;
using AIShikikan.Core.Serialization;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Git;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Mcp;
using AIShikikan.Core.Services.Personas;
using AIShikikan.Core.Services.Runtime;
using AIShikikan.Core.Services.Session;
using AIShikikan.Core.Services.Templates;
using AIShikikan.Core.Services.Tools;
using AIShikikan.Core.Services.Usage;
using AIShikikan.Core.Services.Worker;

namespace AIShikikan.Core.Services;

/// <summary>应用启动外观: 初始化配置目录、示例文件、所有服务与工具集, 供 CLI / GUI 复用。</summary>
public sealed class CommanderRuntime
{
    public static CommanderRuntime Instance { get; private set; } = null!;

    public required string WorkspaceRoot { get; init; }
    public required LlmService Llm { get; init; }

    /// <summary>显式工作区上下文的 Commit/tag 检查点服务。</summary>
    public required GitService GitService { get; init; }

    public required GitCheckpointStore Checkpoints { get; init; }
    public required AssignmentManager Assignments { get; init; }
    public required ToolRegistry Registry { get; init; }
    public required SessionRuntimeRegistry Sessions { get; init; }
    public required WorkspaceExecutionCoordinator Coordinator { get; init; }
    public required McpService Mcp { get; init; }

    /// <summary>Worker 进程池(每个 (工作目录 × 会话) 一个独立进程)。</summary>
    /// <remarks>
    /// 刻意<b>不是</b> <c>required init</c>: 它必须在 Registry 装配完成<b>之后</b>构造 ——
    /// 降级用的内联传输工厂要读 Registry(工具集) 与 <see cref="BuildToolScope"/>(人格/roster/Plan 授权),
    /// 塞不进 <see cref="Boot"/> 的对象初始化器(那里 Registry 还是空的)。
    /// <para>⚠ 与 <see cref="Workers"/> 有关的两条后续接线(不在本文件内):
    /// ① 会话结束 → <c>Workers.Release(sessionId, workDir)</c>;
    /// ② 主进程退出 → <c>Workers.ShutdownAllAsync()</c>。两者目前都靠池内每 60s 的对账兜底
    /// (L3), 而对账的基准集合由本文件在 <see cref="Boot"/> 里注入。</para>
    /// </remarks>
    public WorkerPool Workers { get; set; } = null!;

    public required IReadOnlyList<Persona> Personas { get; init; }
    public required IReadOnlyList<AgentTemplate> Templates { get; init; }
    public required IReadOnlyList<CliAgentDefinition> Agents { get; init; }

    /// <summary>当前活动会话引擎(兼容访问: GUI 现有调用点等价于 Sessions.ActiveEngine)。</summary>
    public AgentEngine Engine => Sessions.ActiveEngine;

    public string? CurrentPersonaText { get; private set; }

    /// <summary>当前指挥官人格(按 Plan/Build 模式解析出生效提示词)。</summary>
    private Persona? _commanderPersona;

    /// <summary>按当前模式重新解析指挥官人格提示词并同步到全部会话引擎。</summary>
    ///
    /// <para><b>末尾重建子代理工具是必需的</b>: <see cref="BuildToolScope"/> 把
    /// <c>CommanderPersonaText</c> 快照进 scope, 而子代理工具在构造时持有 scope。
    /// 不重建的话子代理会一直带旧人格(原先工具是实时读单例, 所以没有这个问题)。</para>
    /// </summary>
    private void RefreshCommanderPersonaText()
    {
        var text = _commanderPersona?.ResolveForMode(_isPlanMode);
        CurrentPersonaText = string.IsNullOrWhiteSpace(text) ? null : text;
        ApplyPersonaTextToSessions();
        RebuildSubagentTools();
    }

    /// <summary>指挥官人格是全局设置: 同步到所有已创建的会话引擎(下一回合生效)。</summary>
    private void ApplyPersonaTextToSessions()
    {
        foreach (var rt in Sessions.All)
        {
            rt.Engine.SetPersonaText(CurrentPersonaText);
        }
    }

    /// <summary>当前活动会话的 Roster 快照(未选定会话时为空列表)。</summary>
    ///
    /// <para><b>语义边界: 本属性是「读活动会话」的旧语义, 新代码不要用它判会话级开关</b>。
    /// 后台会话 B 的子代理在用户切到会话 A 后会读到 A 的压缩开关与 Plan 授权,
    /// 即 Plan 授权可跨会话串味(既会误拒也会误放)。会话身份现在可从
    /// <c>ToolContext.SessionId</c>(引擎已注入)取得, 故会话级判定应改用
    /// <see cref="GetRosterEntriesFor"/> / 带 sessionId 的
    /// <see cref="IsAgentRegisteredInPlanMode(CliAgentDefinition, string?)"/>,
    /// 本属性只保留给「确实是全局/当前会话视角」的调用点(如无会话身份的自检、UI 侧展示)。</para>
    ///
    /// <para><b>另一处待决策</b>: 未选定会话(Active 为 null)时本属性返回空表,
    /// 于是无 sessionId 的 <see cref="IsAgentRegisteredInPlanMode(CliAgentDefinition)"/> 把所有 Agent
    /// 判为未授权, Plan 模式下会导致子代理工具一个都不注册。当前保持该行为不变
    /// (工具注册过滤确实没有会话身份可用, 见 RebuildSubagentTools)。</para>
    public IReadOnlyList<AgentRosterEntry> CurrentRosterEntries =>
        Sessions.Active?.Engine.RosterEntries ?? [];

    /// <summary>
    /// 按会话 ID 取该会话引擎的 Roster 快照; 会话运行时未加载或从未被 GUI 推送过时返回空表。
    ///
    /// <para>供子代理工具按"本次调用归属的会话"解析会话级开关(压缩 / Plan 授权)使用,
    /// 以避免 <see cref="CurrentRosterEntries"/> 读活动会话造成的跨会话串味。</para>
    ///
    /// <para>刻意返回空表而非 null: 空表与 null 在下游语义一致(都表示"没有会话级授权/配置"),
    /// 而 <c>RosterBuilder</c> 的"null = 无限制"语义不应泄漏到工具侧, 否则会退化成无限制。</para>
    ///
    /// <para><b>空 / 未知 sessionId 是安全的, 不会抛异常</b>: null、空串一律直接返回空表;
    /// 未知 ID 由 <c>Sessions.TryGet</c> 返回 null 收成空表(注册表未持有运行时, 例如会话已释放)。
    /// 注意本方法把「运行时不在线」与「该会话确实无人授权」压成同一个空表,
    /// 因此 Plan 授权这类<b>需要区分二者</b>的场景请用带 sessionId 的
    /// <see cref="IsAgentRegisteredInPlanMode(CliAgentDefinition, string?)"/>, 它会显式回退活动会话。</para>
    /// </summary>
    public IReadOnlyList<AgentRosterEntry> GetRosterEntriesFor(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return [];
        return Sessions.TryGet(sessionId)?.Engine.RosterEntries ?? [];
    }

    /// <summary>构造工具层对全局状态的显式依赖, 替代原先在 <c>AgentToolFactory</c> 内部
    /// 反向读静态单例 <c>CommanderRuntime.Instance</c> 的做法。
    ///
    /// <para><b>为什么要 scope</b>: 工具原先通过静态单例读人格 / roster / Plan 授权 /
    /// 活动会话, 这些依赖在「工具被拆到独立 Worker 进程」后会全部缺失。缺依赖时代码走的是
    /// <c>?? false</c> / 空表 / 空串这类<b>静默降级</b> —— 不报错, 只是行为悄悄变错,
    /// 是最难排查的一类问题。改成显式注入后, 缺依赖在构造期就看得见。</para>
    ///
    /// <para><b>⚠ CommanderPersonaText 是快照而非委托</b>: 改人格(切 Plan/Build 模式、换人格)
    /// 后必须重建 scope 并重建子代理工具, 否则子代理仍带旧人格。故
    /// <see cref="RefreshCommanderPersonaText"/> 与 <see cref="SetPersonaText"/> 末尾都补了
    /// <see cref="RebuildSubagentTools"/>。</para>
    /// </summary>
    private AgentExecutionScope BuildToolScope() => new()
    {
        WorkspaceRoot = WorkspaceRoot,
        CommanderPersonaText = CurrentPersonaText,
        // sessionId 为空/未知时回退活动会话 —— 与工具侧原先 RosterOf 的回退完全一致,
        // 保持「行为逐字不变」。注意 null 与空表在这里都表示「无授权/无配置」,
        // 而 RosterBuilder 的「null = 无限制」语义不会泄漏进工具侧。
        RosterResolver = sessionId => string.IsNullOrEmpty(sessionId)
            ? CurrentRosterEntries
            : GetRosterEntriesFor(sessionId),
        PlanAuthorizer = (sessionId, agent) => IsAgentAllowedInPlanMode(agent, sessionId),
        ActiveSessionIdResolver = () => Sessions.ActiveSessionId,
    };

    public static CommanderRuntime Boot(string? workspaceRoot = null, string? personaId = null)
    {
        AppPaths.EnsureDirectoriesExist();

        PersonaService.WriteSampleFiles();
        AgentTemplateService.EnsureSamplesExist();
        RosterBuilder.WriteDefaultTemplate();
        AgentConfigService.EnsureDefaultExists();
        ProviderSettingsService.EnsureDefaultExists();
        McpConfigService.EnsureDefaultExists();
        AgentConfigService.Refresh();

        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(workspaceRoot) ? "." : workspaceRoot);

        var personas = PersonaService.LoadAll();
        var personasList = personas.ToList();
        var templates = AgentTemplateService.LoadAll();
        var templatesList = templates.ToList();
        var agents = AgentConfigService.LoadAll();
        var agentsList = agents.ToList();

        var llm = new LlmService();
        var checkpoints = new GitCheckpointStore();
        var gitService = new GitService(checkpoints);
        // 记录被淘汰时同步回收对应 tag, 否则 tag 会一直残留, 被 doctor 的遗留产物扫描
        // 反复报成"可清理"(回滚只用 CommitSha, 不用 TagName, 所以删 tag 不影响回滚能力,
        // 反而让失去记录引用的旧 commit 可被 git gc 回收)
        checkpoints.TagDeleter = (repoRoot, tag) => gitService.DeleteCheckpointTag(repoRoot, tag);
        var assignments = new AssignmentManager();
        var mcp = new McpService();

        // 工作区执行协调器 + 会话事件 Hub: 同 worktree 同分支多会话并发, 跨分支互斥
        //
        // ⚠ 解析器提成局部变量与 Worker 池**共用同一个实例**: 它带 2s TTL 缓存(每次未命中要起
        // 两个 git 进程), 而 Worker 每次拉起进程都要解析一次工作树根(AcquireAsync 的频率是
        // "每回合一次")。各建一个等于把缓存命中率对半砍 —— 协调器刚解析过的目录, 池那份缓存还是冷的。
        var workspaceResolver = new GitWorkspaceResolver(root);
        var coordinator = new WorkspaceExecutionCoordinator(workspaceResolver);
        var hub = new EngineEventHub();
        CommanderRuntime? self = null;
        var sessions = new SessionRuntimeRegistry(hub, coordinator,
            (sessionId, sessionTitle, host) => self is null
                ? throw new InvalidOperationException("会话引擎工厂在运行时装配前被调用")
                : self.CreateEngine(sessionId, sessionTitle, host, hub));

        // 子 Agent 终态时记录调用统计(Completed 视为成功)
        assignments.AssignmentChanged += a =>
        {
            if (a.Status is SubagentStatus.Completed or SubagentStatus.Failed
                or SubagentStatus.Cancelled or SubagentStatus.TimedOut)
            {
                UsageStatsService.RecordAgentCall(a.AgentId, a.AgentName,
                    a.Status == SubagentStatus.Completed);
            }
        };

        var registry = new ToolRegistry();

        var persona = personasList.FirstOrDefault(p =>
            string.Equals(p.Id, personaId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Name, personaId, StringComparison.OrdinalIgnoreCase))
            ?? personasList.FirstOrDefault();
        // 指挥官人格提示词按当前模式(默认 Build)解析: 通用正文 + 模式专属段
        var personaText = string.IsNullOrWhiteSpace(persona?.ResolveForMode(false))
            ? null
            : persona!.ResolveForMode(false);

        self = Instance = new CommanderRuntime
        {
            WorkspaceRoot = root,
            Llm = llm,
            GitService = gitService,
            Checkpoints = checkpoints,
            Assignments = assignments,
            Registry = registry,
            Sessions = sessions,
            Coordinator = coordinator,
            Mcp = mcp,
            Personas = personasList,
            Templates = templatesList,
            Agents = agentsList,
            CurrentPersonaText = personaText
        };
        Instance._commanderPersona = persona;

        // 工具注册必须在 Instance 赋值之后: AgentExecutionScope 要读 CurrentPersonaText。
        // 挪动是安全的 —— CreateEngine 只读已赋值的 Registry 属性, 而引擎由 GetOrCreate 懒创建,
        // Boot 期间不存在引擎。
        foreach (var tool in AgentToolFactory.CreateCoreTools(gitService, Instance.BuildToolScope()))
        {
            registry.Register(tool);
        }

        foreach (var tool in AgentToolFactory.CreateSubagentTools(
                     agentsList, personasList, templatesList, assignments, llm, Instance.BuildToolScope()))
        {
            registry.Register(tool);
        }

        // Worker 进程池: 每个 (工作目录 × 会话) 一个独立进程, 拉不起来时降级为「主进程内联执行」。
        //
        // ⚠ 必须放在 Instance 赋值之后**且**在两轮工具注册之后: 降级用的内联传输工厂要读
        // Registry(按工具名找执行入口、列举工具集)与 BuildToolScope()(人格 / roster / Plan 授权),
        // 这两样只有装配完成之后才是真值 —— 挪到对象初始化器里会拿到一个空注册表,
        // 症状是"降级后所有工具都报未知工具"。
        //
        // ⚠ liveSessionProvider 必须接: 它是对账(每 60s)的比对基准, 传 null 会把对账整个关掉,
        // 于是 L3("目录完全没有会话 → 关掉全部 Worker")就只能依赖每一次 Release 都被正确调用,
        // 而那两条路径(GUI 删会话 / 主进程退出)目前都还没接 —— 漏一次就是进程永久残留, 且无任何现象。
        Instance.Workers = new WorkerPool(
            workspaceResolver,
            // ⚠ 形参刻意不叫 personaText: Boot 上方已有一个同名局部变量(personaText),
            // 而同名遮蔽在嵌套作用域里是编译错误(CS0136)。叫 commanderPersonaText
            // 既避开它, 又与 ToolContext / WorkerHelloRequest 上的字段同名, 读起来更准。
            inlineFactory: (sessionId, workDir, workspaceRoot, commanderPersonaText, rosterEntries, planMode) =>
                // ⚠ 必须经 Instance. 显式走实例: 下面两个方法是实例方法(它们要读 Registry
                // 与 BuildToolScope(), 二者都是实例成员), 而本工厂委托是在**静态** Boot 里
                // 构造的 —— 静态上下文里没有 this 可用。lambda 体在 AcquireAsync 时才执行,
                // 那时 Instance 早已赋值, 不存在"空引用启动期窗口"。
                new InlineTransport(
                    executor: (request, token) => Instance.ExecuteWorkerToolInlineAsync(
                        request, token, workspaceRoot, commanderPersonaText, rosterEntries, planMode),
                    lister: (request, _) => Instance.ListWorkerToolsInlineAsync(
                        request, workspaceRoot, commanderPersonaText, rosterEntries, planMode)),
            liveSessionProvider: () => WorkerPool.LiveSessionIdsFrom(sessions.AllSessions));

        // 分派状态经 Hub 广播一次(替代旧的每引擎订阅, 避免多会话重复触发)
        assignments.AssignmentChanged += a => hub.Publish(new EngineAssignmentChanged(a));

        // 后台连接 MCP 服务器并注册桥接工具(不阻塞启动)
        var runtime = Instance;
        _ = Task.Run(async () => await runtime.RefreshMcpToolsAsync().ConfigureAwait(false));

        Log.Info("Boot",
            $"装配完成: 工作区={Path.GetFullPath(root)}, Agent={agentsList.Count}, 专家={personasList.Count}, " +
            $"模板={templatesList.Count}, 工具={registry.All.Count}, 人格字符数={personaText?.Length ?? 0}");

        return Instance;
    }

    /// <summary>创建会话引擎(注册表工厂回调): 新会话继承当前 Plan 模式与全局人格设置;
    /// Roster 由本会话自己的 roster.json 合成, 不跨会话继承。</summary>
    ///
    /// <para><b>为什么要在引擎构造时就带上 roster</b>: 原实现不传, 引擎的 RosterEntries 为 null,
    /// <c>RosterBuilder</c> 把 null 当"无限制"从而向 AI 广告全部 run_&lt;id&gt;。
    /// GUI 的 AgentPanel 稍后才 PushRoster, 在此之前的第一个回合(或后台会话)就会
    /// 把用户在 AgentPanel 里关掉的子代理照旧广告出去, AI 持续尝试调用未注册的工具。</para>
    ///
    /// <para><b>不跨会话继承的决定与理由</b>: roster 是会话级持久状态(存在
    /// sessions/{id}/roster.json, 由用户在该会话的 AgentPanel 开关写盘)。若新会话继承
    /// 上一个会话的 roster, 会出现"磁盘上属于本会话的 roster 与引擎快照不一致"的瞬态:
    /// 工具注册过滤用 A 的条目、提示词用 B 的条目, 且一旦 PushRoster 落地就被覆盖回去。</para>
    private AgentEngine CreateEngine(string sessionId, string sessionTitle,
        ISessionEngineHost host, EngineEventHub hub)
        => new AgentEngine(
            llm: Llm,
            registry: Registry,
            git: GitService,
            assignments: Assignments,
            personas: Personas,
            templates: Templates,
            agents: Agents,
            workspaceRoot: WorkspaceRoot,
            personaText: CurrentPersonaText,
            // ProviderId / Model 刻意留 null —— null 在本项目里的语义是「跟随当前全局路由」
            // (LlmService.GetProvider(null) / ResolveModel(null, null) 每次调用现读
            // LlmSettings.ActiveProviderId / ActiveModel), 而不是"路由信息缺失"。
            //
            // 为什么不在构造期就填上当时的 provider: 那会把路由**冻结**在这一个会话引擎上。
            // 用户在聊天页切 provider/model 只写全局 providers.toml, 已打开的会话引擎不会重建,
            // 于是"切换下一条消息生效"直接失效(用户切了模型, 会话仍在用旧 provider)。
            // 需要把本回合的 provider 显式带进 ToolContext(如子代理输出压缩)时, 在**每回合开始前**
            // 调 <see cref="ApplyLlmRouting"/>: 它是 fail-safe 的 —— 不调用就退回"跟随全局",
            // 而构造期固化是 fail-unsafe 的(漏刷新会静默钉死旧 provider)。
            options: new EngineOptions { IsPlanMode = _isPlanMode },
            rosterEntries: BuildInitialRosterEntries(sessionId),
            sessionId: sessionId,
            sessionTitle: sessionTitle,
            host: host,
            hub: hub);

    /// <summary>
    /// 合成该会话的初始 Roster 快照: 全局 Agent 列表 + 该会话 roster.json 逐项覆盖,
    /// 语义与 AgentPanel 的 PushRoster 对齐(默认全部启用, 会话条目覆盖
    /// Enabled / 描述 / 专家 / 压缩 / Plan 授权)。
    ///
    /// <para><b>必须"合成完整表"而不能直接用 roster.json 的条目</b>: AgentPanel 的开关只在用户
    /// 实际改动某一项时才写 roster.json, 因此文件里通常只有被改动过的少数几个 Agent。
    /// 直接把这份残表当快照, RosterBuilder 会只列出这几个而把其余可用 Agent 全部从提示词里抹掉。</para>
    ///
    /// <para>roster.json 从不存在时返回 null(= "无限制", 不臆造): 全新会话没有任何会话级记录,
    /// 由 GUI 首次 PushRoster 收敛到用户的实际开关状态。</para>
    /// </summary>
    private IReadOnlyList<AgentRosterEntry>? BuildInitialRosterEntries(string sessionId)
    {
        var overrides = RosterConfigService.LoadEntriesOrNull(sessionId);
        if (overrides is null)
        {
            return null;
        }

        // 用 Agents(而非重新 LoadAll): 它正是本引擎解析子代理所用的那份定义列表
        var result = new List<AgentRosterEntry>(Agents.Count);
        foreach (var agent in Agents)
        {
            var ov = overrides.FirstOrDefault(e =>
                string.Equals(e.AgentId, agent.Id, StringComparison.OrdinalIgnoreCase));

            result.Add(new AgentRosterEntry
            {
                AgentId = agent.Id,
                // Display 为空表示沿用 Agent 自带名(DisplayText 会回退)
                Display = string.IsNullOrEmpty(ov?.Display) ? agent.Display : ov!.Display,
                // 描述只取会话覆盖值: 为空时 RosterBuilder 会补上 agent.Description, 避免同一句出现两次
                Description = ov?.Description ?? string.Empty,
                // 与 AgentPanel 一致: 会话条目的专家为空视为"沿用推荐专家"
                PersonaId = string.IsNullOrWhiteSpace(ov?.PersonaId) ? agent.RecommendedPersonaId : ov!.PersonaId,
                Enabled = ov?.Enabled ?? true,
                CompactEnabled = ov?.CompactEnabled ?? false,
                UseInPlanMode = ov?.UseInPlanMode ?? false
            });
        }

        return result;
    }

    /// <summary>重建 MCP 桥接工具: 断开旧连接, 连接全部启用服务器并注册 mcp_* 工具。
    /// 返回状态消息列表(含连接失败说明), UI 可展示。</summary>
    public async Task<List<string>> RefreshMcpToolsAsync(CancellationToken ct = default)
    {
        var messages = new List<string>();

        await Mcp.DisposeAsync().ConfigureAwait(false);
        Registry.UnregisterWhere(t => t is McpProxyTool);

        messages.AddRange(await Mcp.ConnectAllAsync(ct).ConfigureAwait(false));

        foreach (var (_, serverId, descriptor) in Mcp.EnumerateTools())
        {
            Registry.Register(new McpProxyTool(Mcp, serverId, descriptor));
        }

        foreach (var m in messages.Where(m => !m.Contains("连接失败")))
        {
            Log.Info("MCP", m);
        }

        foreach (var m in messages.Where(m => m.Contains("连接失败")))
        {
            Log.Warn("MCP", m);
        }

        messages.Add($"[MCP] 已连接 {Mcp.ConnectedCount} 个服务器, 注册 {Registry.All.Count(t => t is McpProxyTool)} 个工具");
        return messages;
    }

    /// <summary>直接指定指挥官人格提示词(覆盖按模式解析的结果); 传 null 清除。</summary>
    public void SetPersonaText(string? text)
    {
        _commanderPersona = null; // 避免后续模式切换时用 persona 解析结果覆盖显式设定
        CurrentPersonaText = text;
        ApplyPersonaTextToSessions(); // 全局设置同步到所有会话
        RebuildSubagentTools();      // scope 持有的是人格快照, 必须重注册子代理工具(同 RefreshCommanderPersonaText)
    }

    /// <summary>指定指挥官人格并按当前模式解析生效提示词(通用正文 + Plan/Build 专属段)。</summary>
    public void SetCommanderPersona(Persona? persona)
    {
        _commanderPersona = persona;
        RefreshCommanderPersonaText();
    }

    private bool _subagentToolsVisible = true;

    private bool _isPlanMode;

    /// <summary>
    /// <see cref="RebuildSubagentTools"/> 最近一次**实际注册**的子代理工具个数。
    /// </summary>
    /// <remarks>
    /// 为什么不是直接用 <c>_subagentToolsVisible</c> 下发: 协议里表达「一个子代理工具都不注册」
    /// 的唯一字段是 <c>SubagentVisible</c>, 而"右侧栏可见但 Plan 模式零授权"在本进程里同样落到
    /// "零个子代理工具"(见 <see cref="RebuildSubagentTools"/> 里那段【待产品决策】)。
    /// 两个语义共用一个布尔量就会让 Worker 与主进程的工具表分叉。
    /// <para>⚠ 独占写入点是 <see cref="RebuildSubagentTools"/>, 三条同步调用点都在它之后,
    /// 所以读到的永远是新鲜值。</para>
    /// </remarks>
    private int _registeredSubagentToolCount;

    /// <summary>
    /// Plan 模式下 <see cref="RebuildSubagentTools"/> 真正注册进去的 Agent Id 快照(白名单)。
    /// 非 Plan 模式恒为空表 —— 在协议里空表 = 不限制, 与"此时不过滤"一致。
    /// </summary>
    private IReadOnlyList<string> _registeredPlanAgentIds = [];

    /// <summary>
    /// fire-and-forget 同步的<b>序号闸</b>: 每次调度 +1。
    /// 同步是异步的, 用户连点两次右侧栏(或来回切 Plan 模式)会让两次 <c>tools/sync</c> 交叉,
    /// 而"后发先至"会让 Worker 停在**旧**策略上(工具表分叉且不会自愈, 因为没有下一次 sync 来纠正)。
    /// 序号让过期的那次直接放弃 —— 最新一次必然在跑, 且它的快照更新。
    /// </summary>
    private long _workerToolsSyncGeneration;

    /// <summary>最近一次 fire-and-forget 同步的任务句柄(供 <see cref="PendingWorkerToolsSync"/> 观察)。</summary>
    private Task? _workerToolsSyncPending;

    /// <summary>当前是否处于 Plan 模式(由聊天页切换, 联动子代理工具注册与 Roster 注入过滤)。</summary>
    public bool IsPlanMode => _isPlanMode;

    /// <summary>切换 Plan 模式: 重建子代理工具注册(Plan 模式下仅保留开启"在 Plan 模式中使用"
    /// 且配置了 plan_args 的 Agent), 下一回合 AI 工具列表即不再包含未授权子代理。</summary>
    public void SetPlanMode(bool planMode)
    {
        if (_isPlanMode == planMode) return;
        _isPlanMode = planMode;

        // Plan 模式是全局开关: 子代理工具注册是全局的(ToolRegistry 单例), 若只把 IsPlanMode
        // 写进活动会话引擎, 后台会话的 Options.IsPlanMode 会停在上一次取值。切回 Build 后
        // 后台会话仍按 Plan 过滤 Roster(少列子代理)且执行兜底拒绝合法调用, 工具注册与
        // 提示词互相打架。与 ApplyPersonaTextToSessions 同一做法: 同步到全部已存在引擎。
        ApplyPlanModeToSessions();

        // 指挥官人格按新模式重新解析提示词(通用正文 + 对应模式专属段)
        // 注: RefreshCommanderPersonaText 末尾已重建子代理工具, 这里不再重复调用
        RefreshCommanderPersonaText();
        Log.Info("Engine", $"Plan 模式切换为 {planMode}, 当前工具数={Registry.All.Count}");

        // ⚠ 必须在 RefreshCommanderPersonaText 之后: 它末尾会重建子代理工具,
        // 而下发给 Worker 的白名单取的是"这次重建真正注册了哪些"(见 CurrentWorkerToolPolicy)。
        // 放在之前会把上一次的授权名单发出去, 且没有任何人再纠正它。
        ScheduleWorkerToolsSync($"Plan 模式切换为 {planMode}");
    }

    /// <summary>Plan 模式是全局设置: 同步到所有已创建的会话引擎(下一回合生效)。</summary>
    private void ApplyPlanModeToSessions()
    {
        foreach (var rt in Sessions.All)
        {
            rt.Engine.Options.IsPlanMode = _isPlanMode;
        }
    }

    /// <summary>
    /// 把**当前全局 LLM 路由**物化进指定会话引擎的 <c>EngineOptions.ProviderId</c>;
    /// 供引擎把它带进 <c>ToolContext.ProviderId</c>(子代理输出压缩等"必须与主回合同模型"的子流程)。
    ///
    /// <para><b>调用时机: 每回合开始前</b>(与 GUI 往 <c>engine.Options</c> 写 Thinking / WorkDir /
    /// Plan 模式同一处)。这样"本回合用哪个 provider"是回合级快照: 回合中途切 provider 不影响正在跑的
    /// 回合, 下一回合自动跟上; 忘记调用也只是退回"跟随全局"(<c>ProviderId</c> 保持 null),
    /// 不会像在 <see cref="CreateEngine"/> 构造期固化那样把整个会话钉死在旧 provider 上。</para>
    ///
    /// <para><b>只写 ProviderId, 刻意不写 Model</b>: 模型留 null 让引擎每回合经
    /// <c>LlmService.ResolveModel</c> 现算 —— 那条路径会做 <c>EnabledModels</c> 合法性校验
    /// (当前模型被禁用时回落到列表内首个), 而 <c>ResolveModel(已填的 model)</c> 是直接短路返回、
    /// 不再校验的。先填后校验反而更容易配出"下发了已停用模型"的请求。</para>
    ///
    /// <para>未配置 / 无活动 Provider 时写入 null: 引擎会在回合开始时给出"未配置 Provider"提示,
    /// 与 <c>GetClient(null)</c> 的既有失败路径一致。</para>
    /// </summary>
    public void ApplyLlmRouting(AgentEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.Options.ProviderId = Llm.GetProvider()?.Id;
    }

    /// <summary>
    /// Plan 模式授权的唯一权威判据(G5: 原先存在两份语义不同的实现)。
    ///
    /// <para>授权需要同时满足两个条件:
    /// (1) Agent 侧支持 Plan 模式 —— 配置了 plan_args, 或开启"无 plan 参数也可在 Plan 模式使用";
    /// (2) 会话侧授权 —— 该 Agent 在本会话 Roster 里开启了 UseInPlanMode。
    /// 两者与 <c>RosterBuilder</c> 的 Plan 过滤、以及工具注册过滤保持同一判据。</para>
    ///
    /// <para><paramref name="rosterEntries"/> 为 null 或空表都表示"无人授权"
    /// (前者是没下发过, 后者是用户清空)。要否追加 plan_args 由调用方再与
    /// "主对话确实处于 Plan 模式"相与(AgentToolFactory.ShouldRunInPlanMode)。</para>
    /// </summary>
    public static bool IsAgentPlanModeAuthorized(CliAgentDefinition agent,
        IReadOnlyList<AgentRosterEntry>? rosterEntries)
    {
        // 条件 (1): Agent 侧支持 Plan 模式
        if (agent.PlanArgs is not { Count: > 0 } && !agent.AllowPlanModeWithoutArgs)
        {
            return false;
        }

        // 条件 (2): 会话侧授权
        return rosterEntries is not null && rosterEntries.Any(e =>
            string.Equals(e.AgentId, agent.Id, StringComparison.OrdinalIgnoreCase) && e.UseInPlanMode);
    }

    /// <summary>Plan 模式下是否**注册/放行**该子代理(按<b>发起本回合的会话</b>的 Roster 判定;
    /// 工具侧应优先用本重载)。<paramref name="sessionId"/> 取自 <c>ToolContext.SessionId</c>
    /// (引擎按 <c>AgentEngine.SessionId</c> 注入, 即"正在跑工具的那个会话")。</summary>
    ///
    /// <para><b>为什么必须按会话判</b>: 会话级 Plan 授权存在各会话的 roster.json 里,
    /// 而注册表是全局单例。若按活动会话判定, 后台会话 B 的子代理会在用户切到会话 A 后
    /// 拿 A 的授权结论 —— 既可能把 B 里已授权的 Agent 误拒, 也可能把 A 的授权放给 B 里未授权的
    /// Agent(Plan 模式「只规划不执行」的底线被绕过)。</para>
    ///
    /// <para><paramref name="sessionId"/> 为 null / 空, 或该会话的运行时已不在注册表里
    /// (从未加载 / 已释放)时, 回退 <see cref="CurrentRosterEntries"/>(保持改动前的行为,
    /// 不让"取不到会话"变成新的静默拒绝源)。</para>
    public bool IsAgentRegisteredInPlanMode(CliAgentDefinition agent, string? sessionId)
        => IsAgentPlanModeAuthorized(agent, RosterEntriesForPlanAuth(sessionId));

    /// <summary><see cref="IsAgentRegisteredInPlanMode(CliAgentDefinition, string?)"/> 的同义别名。</summary>
    public bool IsAgentAllowedInPlanMode(CliAgentDefinition agent, string? sessionId)
        => IsAgentRegisteredInPlanMode(agent, sessionId);

    /// <summary>Plan 模式下是否**注册/放行**该子代理(按<b>活动会话</b>的 Roster 判定;
    /// 未选定会话时无人授权 → 一律 false, 见 <see cref="CurrentRosterEntries"/> 的说明)。</summary>
    ///
    /// <para><b>旧语义(读活动会话), 只在拿不到会话身份时使用</b>: 保留是为了不改变既有调用点的行为
    /// (工具注册过滤 RebuildSubagentTools 确实没有会话身份)。凡是能拿到 <c>ToolContext.SessionId</c>
    /// 的调用点(执行兜底 / plan_args 追加判定)都应改用带 sessionId 的重载, 否则会跨会话串味。</para>
    public bool IsAgentRegisteredInPlanMode(CliAgentDefinition agent)
        => IsAgentRegisteredInPlanMode(agent, null);

    /// <summary><see cref="IsAgentRegisteredInPlanMode(CliAgentDefinition)"/> 的同义别名
    /// (旧语义: 按活动会话判定)。能拿到会话身份时请改用带 sessionId 的重载。</summary>
    public bool IsAgentAllowedInPlanMode(CliAgentDefinition agent)
        => IsAgentRegisteredInPlanMode(agent);

    /// <summary>Plan 授权专用取表: 优先「本回合归属会话」的 Roster, 取不到时回退活动会话。</summary>
    ///
    /// <para>与 <see cref="GetRosterEntriesFor"/> 的区别只有一处: 这里<b>需要</b>区分
    /// 「运行时在线但该会话无人授权」(空表 → 拒绝, 正确) 与「运行时不在注册表」(无法定位会话 →
    /// 回退活动会话, 保持旧行为), 因为两者被 GetRosterEntriesFor 压成了同一个空表。</para>
    ///
    /// <para>回退而非直接判 false 的理由: sessionId 为空/未知的来源是旧引擎、手工构造的
    /// ToolContext、自检代码, 它们过去拿到的是活动会话结论; 让它们突然变成"全部拒绝"会变成
    /// 「不报错的错误行为」(工具静默不可用), 比跨会话串味更难定位。</para>
    private IReadOnlyList<AgentRosterEntry> RosterEntriesForPlanAuth(string? sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId) && Sessions.TryGet(sessionId) is { } rt)
        {
            // null = 该引擎从未收到过 roster 快照(BuildInitialRosterEntries 返回 null 时就是这个状态),
            // 对授权判定等价于"无人授权"; 这里收成空表, 不让 RosterBuilder 的 "null = 无限制"
            // 语义泄漏进来(否则会把未授权 Agent 判成授权)。
            return rt.Engine.RosterEntries ?? [];
        }

        return CurrentRosterEntries;
    }

    /// <summary>
    /// 按右侧栏可见性与 Plan 模式重建子代理工具注册。
    /// </summary>
    /// <remarks>
    /// <para><b>⚠ 它同时是「要下发给 Worker 的工具集策略」的唯一产地</b>:
    /// 末尾会把「本次实际注册了什么」写进 <see cref="_registeredSubagentToolCount"/> 与
    /// <see cref="_registeredPlanAgentIds"/>, 供 <see cref="SyncWorkerToolsAsync"/> 照抄。
    /// 不这么做就得让同步侧把过滤规则再实现一遍 —— 而两份过滤规则必然漂移, 症状是
    /// 「Worker 里还能调一个主进程已经注销的子代理」(Plan 模式授权形同虚设)。</para>
    /// </remarks>
    private void RebuildSubagentTools()
    {
        Registry.UnregisterWhere(t =>
            t is AgentExecutionTool or AssignTaskTool or SubagentGroupTool);

        // 先归零: 下面每一条提前 return 的分支都必须留下"当前一个子代理工具都没注册"的结论,
        // 否则同步侧会拿着上一次的注册结果去下发策略。
        _registeredSubagentToolCount = 0;
        _registeredPlanAgentIds = [];

        if (!_subagentToolsVisible) return;

        // 从配置服务重新加载, 保证增删后的 Agent/专家/模板即时生效
        var agents = AgentConfigService.LoadAll();
        var personas = PersonaService.LoadAll();
        var templates = AgentTemplateService.LoadAll();

        if (_isPlanMode)
        {
            // 注册过滤是**全局**动作(ToolRegistry 单例, 且此处没有任何会话上下文),
            // 只能按活动会话的 Roster 判 —— 这是无 sessionId 重载仅剩的合法用途。
            // 写成显式 lambda 而非方法组: 重载增加后方法组不再有"显然选 1 参版"的读法,
            // 这里必须让读者一眼看出是活动会话语义。
            agents = agents.Where(a => IsAgentRegisteredInPlanMode(a)).ToList();
            if (agents.Count == 0)
            {
                // 【待产品决策】Plan 模式 + 零授权 ⇒ 子代理工具(含 assign_task / run_subagents)全部不注册,
                // AI 在 Plan 模式下完全无法分派任何子代理。当前**保持此行为不变**:
                // 它的好处是 Plan 模式严格"只规划不执行", 代价是用户即使授权为 0 也拿不到
                // "让子代理帮忙出方案"的入口。另一种可选语义是仍注册 assign_task / run_subagents
                // (让 AI 能编排), 但在执行兜底处拒绝未授权 Agent —— 那会把"Plan 模式"从
                // "模式"变成"部分禁用", 语义更模糊, 因此暂不改。
                Log.Info("Engine", "Plan 模式: 无授权子代理, 子代理工具全部移除");
                return;
            }
        }

        foreach (var tool in AgentToolFactory.CreateSubagentTools(
                     agents, personas, templates, Assignments, Llm, BuildToolScope()))
        {
            Registry.Register(tool);
            _registeredSubagentToolCount++;
        }

        // Plan 模式下把**本次真正注册进去的那批** Agent Id 留档: 它就是 Worker 的授权白名单,
        // 于是「主进程注册了什么」与「Worker 被要求注册什么」来自同一次计算。
        // Build 模式下留空 —— 空表在协议里是「不限制」, 与此时不过滤的事实一致。
        if (_isPlanMode)
        {
            _registeredPlanAgentIds = agents.Select(a => a.Id).ToList();
        }
    }

    /// <summary>右侧栏可见性联动: 关闭时从注册表移除子代理工具(AI 下一回合不再可见), 打开时重新注册。
    /// Roster 注入由 GUI 在打开后调用 SetRosterEntries 恢复。</summary>
    ///
    /// <para>关闭时必须往活动会话引擎写<b>空表</b>(而非置 null): RosterBuilder 的 null 语义是
    /// "无限制"、空表语义是"用户清空", 写 null 会让 prompt 反过来广告全部 run_&lt;id&gt;,
    /// 而此时工具已注销 —— AI 只会反复调用不存在的工具。重新打开时 GUI 的 PushRoster 写回真实条目。</para>
    ///
    /// <para>仅作用于活动会话: 后台会话的 Roster 若一并清空, 重新打开右侧栏时只有活动会话会被
    /// PushRoster 恢复, 其余会话的条目会永久丢失, 故保持会话级隔离。</para>
    public void SetSubagentToolsVisible(bool visible)
    {
        if (_subagentToolsVisible == visible) return;
        _subagentToolsVisible = visible;

        if (!visible)
        {
            SetRosterEntries([]);
        }

        RebuildSubagentTools();
        Log.Info("Engine", $"子代理工具{(visible ? "已注册" : "已移除")}, 当前工具数={Registry.All.Count}");
        ScheduleWorkerToolsSync("右侧栏可见性切换");
    }

    // ── Worker 工具集同步(B7)与内联降级执行 ───────────────────────────────────

    /// <summary>
    /// 把当前的工具集策略同步给全部已登记的 Worker(右侧栏可见性 / Plan 授权)。
    /// 同步等确认: 下一回合 AI 看到的工具列表必须与本进程 Registry 一致。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么是「按池广播」而不是按会话</b>: 右侧栏与 Plan 模式都是<b>全局</b>开关
    /// (工具注册是全局的 <see cref="ToolRegistry"/> 单例), 而每个 (目录 × 会话) 各有一个 Worker 进程,
    /// 它们各自持有一份自己的注册表 —— 只同步发起切换的那一个, 其余 Worker 会继续按旧策略
    /// 持有(甚至继续可调)已注销的工具。</para>
    /// <para><b>为什么必须等确认</b>: <c>tools/sync</c> 是全量替换语义, 返回的
    /// <see cref="WorkerToolsResponse"/> 是该配置下的<b>权威</b>工具清单。父进程在 sync 确认之后
    /// 才去注册 <c>WorkerProxyTool</c>, 才能保证「本回合 AI 看到的工具 = Worker 真能执行的工具」。
    /// 反过来(先注册后 sync)会出现一个窗口: LLM 调了一个还没同步过去的工具名,
    /// 症状是"工具明明在列表里, 一调就报未知工具"。</para>
    /// <para><b>⚠ 这里不碰注册表</b>: 同步只改 Worker 侧; 主进程这一侧要不要用返回的清单
    /// 替换自己的工具列表, 属于「谁来 <c>AcquireAsync</c> 并注册 <c>WorkerProxyTool</c>」那条接线
    /// (策略出口见 <see cref="BuildWorkerToolsSyncRequest"/>), 不该在本方法里做 ——
    /// 在这里注册会与 <see cref="Registry"/> 里既有的固定工具/子代理工具打架。</para>
    /// </remarks>
    public async Task SyncWorkerToolsAsync()
    {
        var policy = CurrentWorkerToolPolicy();

        // 先取快照再遍历: 组字典与槽位在遍历期间会因 L3/L4/对账而增删,
        // 边遍历边发请求会打到已经被摘下的句柄上(写帧失败, 表现为一条无意义的 EPIPE 告警)。
        var clients = Workers.EnumerateClients();
        if (clients.Count == 0)
        {
            // 绝大多数时候就是这一支: Worker 是懒启动的(L1), 还没开过回合就没有任何句柄。
            Log.Debug("Worker", "工具集同步: 池内暂无已登记的 Worker, 本次无事可做");
            return;
        }

        var synced = 0;
        foreach (var client in clients)
        {
            // 断开的句柄直接跳过: 它已经进了 L5 的待重拉路径, 下一个回合的 Acquire 会换上一个
            // 全新 Worker(状态经 hello 重新下发), 向一个已死的管道写帧只会拿到 EPIPE。
            if (!client.IsConnected) continue;

            try
            {
                // ⚠ 传 CancellationToken.None: 这是一次「让状态跟上」的后台动作, 没有对应的用户
                // 取消语义。挂一个别的令牌只会把"应用正在退出"变成一次同步失败日志。
                var response = await Workers.SyncToolsAsync(client,
                        policy.CoreTools,
                        policy.SubagentVisible,
                        policy.PlanMode,
                        policy.AllowedAgentIds,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                synced++;
                Log.Debug("Worker",
                    $"Worker {client.WorkerKey} 工具集已同步: " +
                    $"{response?.Tools?.Count ?? 0} 个工具(subagentVisible={policy.SubagentVisible}, " +
                    $"planMode={policy.PlanMode}, 授权={policy.AllowedAgentIds.Count})");
            }
            catch (OperationCanceledException)
            {
                // 铁律: 取消原样上抛(虽然本方法传的令牌不可取消, 但纪律不能因为"反正不会发生"而不写)。
                throw;
            }
            catch (Exception ex)
            {
                // 一个 Worker 同步失败**不得**带走其余的: 漏同步的后果只是"那一个会话的工具表滞后",
                // 而让异常冒出去会把「用户点了一下右侧栏」变成一次可见的失败。
                Log.Warn("Worker", ex, $"向 Worker {client.WorkerKey} 同步工具集失败, 该会话的工具表可能滞后");
            }
        }

        Log.Info("Worker", $"工具集同步完成: {synced}/{clients.Count} 个 Worker 已跟上" +
            $"(子代理可见={policy.SubagentVisible}, Plan模式={policy.PlanMode}, 授权={policy.AllowedAgentIds.Count})");
    }

    /// <summary>
    /// 当前应下发给 Worker 的工具集策略快照(与 <see cref="RebuildSubagentTools"/> 同源)。
    /// </summary>
    /// <remarks>
    /// <b>⚠ 三条配对规则, 改一处必须同时想另一处</b>:
    /// <list type="bullet">
    /// <item><b>CoreTools 恒为 true</b>: 主进程从不下发 false —— 固定工具在本进程是常驻的,
    /// 而 Worker 侧收到 false 会把文件/git/ask_user 整体注销, 目前没有任何产品语义对应它。</item>
    /// <item><b>SubagentVisible 用"实际注册数"而不是右侧栏开关</b>: 「Plan 模式零授权」在本进程里
    /// 表现为"零个子代理工具", 协议里唯一能表达这件事的字段就是 <c>SubagentVisible</c>。
    /// 若这里下发 true, Worker 会按自己的 roster 继续注册子代理工具 ——
    /// <b>Plan 模式「只规划不执行」的底线在子进程里被绕过</b>。</item>
    /// <item><b>AllowedAgentIds 空表 = 不限制</b>(协议语义), 所以「零授权」<b>不能</b>靠空白名单表达;
    /// 必须由上一条(SubagentVisible=false)承载。这两件事必须成对出现。</item>
    /// </list>
    /// </remarks>
    private WorkerToolPolicy CurrentWorkerToolPolicy() => new(
        CoreTools: true,
        SubagentVisible: _subagentToolsVisible && _registeredSubagentToolCount > 0,
        PlanMode: _isPlanMode,
        AllowedAgentIds: _registeredPlanAgentIds);

    /// <summary>
    /// 以当前策略组装一份 <c>tools/sync</c> 载荷, 供「刚 Acquire 完一个新 Worker」的接线方使用。
    /// </summary>
    /// <remarks>
    /// <b>为什么单独给一个出口</b>: <c>hello</c> 只带人格 / roster / Plan 模式,
    /// <b>不带</b>右侧栏可见性与 Plan 授权白名单 —— 后两者只有 <c>tools/sync</c> 这一条通道。
    /// 于是"Acquire 之后必须补一次 sync"是协议要求(R8), 而策略的算法只应该存在于一处:
    /// 谁接 Acquire 就调本方法, 不要自己再拼一遍 CoreTools/SubagentVisible/AllowedAgentIds。
    /// </remarks>
    public WorkerToolsSyncRequest BuildWorkerToolsSyncRequest()
    {
        var policy = CurrentWorkerToolPolicy();
        return new WorkerToolsSyncRequest
        {
            CoreTools = policy.CoreTools,
            SubagentVisible = policy.SubagentVisible,
            PlanMode = policy.PlanMode,
            // 绝不能传 null: DTO 声明为非空, 而 Worker 侧会直接 Contains/NRE(见该字段的 remarks)。
            AllowedAgentIds = policy.AllowedAgentIds
        };
    }

    /// <summary>
    /// 最近一次 <c>void</c> 入口调度出去的工具集同步任务(已完成时返回已完成任务)。
    /// </summary>
    /// <remarks>
    /// <b>它是给"回合开始前等确认"预留的缝</b>: 现在两个切换入口都是同步 void, 只能 fire-and-forget
    /// (见 <see cref="ScheduleWorkerToolsSync"/> 的理由)。等有人给回合开头接上确认钩子时,
    /// 在那里 await 本属性即可, 不用改这两个切换方法的签名。
    /// </remarks>
    public Task PendingWorkerToolsSync => Volatile.Read(ref _workerToolsSyncPending) ?? Task.CompletedTask;

    /// <summary>
    /// 在保持 <c>void</c> 签名的前提下触发一次工具集同步(fire-and-forget)。
    /// </summary>
    /// <param name="reason">触发原因, 只进日志。</param>
    /// <remarks>
    /// <para><b>为什么不同步等待(选择与理由)</b>: 两个调用点(<see cref="SetSubagentToolsVisible"/> /
    /// <see cref="SetPlanMode"/>)都在 GUI 的属性 setter 路径上, 由用户点击直接驱动。
    /// <c>tools/sync</c> 走管道是一去一回两帧, 每帧的兜底是
    /// <c>WorkerProtocol.ToolsListTimeout</c>(15s), N 个 Worker 串起来最坏是 N × 15s ——
    /// 在 UI 线程上同步等它等于让"点一下右侧栏"卡住界面十几秒, 而这两个方法的返回值(void)
    /// 早被 GUI 依赖, 改成 async 会破坏既有调用方。</para>
    /// <para><b>不等确认会不会出问题</b>: 工具集变更本来就是"<b>下一回合生效</b>"的语义
    /// (注册表与 Roster 注入都是如此, 见 <see cref="RebuildSubagentTools"/>), 同步只是让
    /// 子进程跟上同一份结论; 而 <c>tools/sync</c> 是全量替换 + 幂等, 漏掉一次会在下一次切换或
    /// 下一次 Acquire(重拉)时被覆盖。<b>真正的兜底是「Acquire 之后必发一次 sync」那条接线</b>
    /// (见 <see cref="BuildWorkerToolsSyncRequest"/>)。</para>
    /// <para><b>异常绝不外泄</b>: 这里没有任何调用方能 await 它, 未观察的异常会在别的线程上炸出
    /// 一条与本处无关的报错, 所以内部吃干净并记日志。</para>
    /// </remarks>
    private void ScheduleWorkerToolsSync(string reason)
    {
        if (Workers is null)
        {
            // Boot 期间理论上不可达(池在 Boot 的工具注册之后就赋值), 但这里不赌:
            // 漏一次同步的后果只是"Worker 工具表滞后", 而抛异常会把一次用户操作变成失败。
            // ⚠ 这里 return 之前**不能**动序号闸, 否则会把上一次还在跑的同步作废掉。
            Log.Debug("Worker", $"工具集同步({reason})跳过: Worker 池尚未装配");
            return;
        }

        var generation = Interlocked.Increment(ref _workerToolsSyncGeneration);

        // Volatile.Write 与 PendingWorkerToolsSync 里的 Volatile.Read 配对: 那个属性可能被
        // 别的线程(将来的回合开头确认钩子)读, 而普通写对引用来说只有"最终可见"的保证。
        Volatile.Write(ref _workerToolsSyncPending, Task.Run(async () =>
        {
            try
            {
                // 竞态闸: 过期的那次直接放弃(见 _workerToolsSyncGeneration 的说明)。
                // 检查放在真正取策略之前 —— 让"最新那次"读到的一定是最新状态。
                if (Volatile.Read(ref _workerToolsSyncGeneration) != generation)
                {
                    Log.Debug("Worker", $"工具集同步({reason})已过期, 交给更新的一次");
                    return;
                }

                await SyncWorkerToolsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("Worker", ex, $"工具集同步({reason})失败: Worker 的工具表可能滞后于主进程");
            }
        }, CancellationToken.None));
    }

    /// <summary>
    /// 内联(降级)执行路径: 在本进程的 <see cref="ToolRegistry"/> 里跑一次工具调用。
    /// </summary>
    /// <remarks>
    /// <para><b>与 Worker 侧 <c>WorkerSlimHost.ExecuteAsync</c> 刻意同构</b>: 降级路径必须与管道路径
    /// 行为一致, 否则"Worker 起不来时用户看不出差别"这条承诺无从验证 —— 而降级恰恰是<b>最需要
    /// 可信</b>的那条路径(它平时根本不会被执行到, 一旦执行往往正是出事的时候)。</para>
    /// <para><b>审批仍然发生在主进程</b>: 内联模式只是换了个执行位置, 调用它的是
    /// <c>WorkerProxyTool</c>, 而审批闸由 <c>AgentEngine</c> 按描述符里的
    /// <c>RequiresApproval</c> 在主进程求值 —— 换句话说, 内联执行永远发生在"已经批过"之后。</para>
    /// <para>⚠ <paramref name="workspaceRoot"/> 是<b>工作树根</b>(池解析的结果),
    /// 而 Worker 侧 <c>ToolContext.WorkspaceRoot</c> 用的是 <b>workDir</b>。
    /// 两者在 workDir 正好是工作树根时一致, 否则内联的文件工具沙箱会比管道模式宽一层
    /// (仍被限制在工作树内)。要对齐得改 <c>WorkerSlimHost.BuildToolContext</c> 或把 workDir
    /// 也传进来 —— 属于协议外的一次口径统一, 留作后续。</para>
    /// </remarks>
    private async Task<WorkerToolCallResponse> ExecuteWorkerToolInlineAsync(
        WorkerToolCallRequest request,
        CancellationToken ct,
        string workspaceRoot,
        string? commanderPersonaText,
        IReadOnlyList<AgentRosterEntry>? rosterEntries,
        bool planMode)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Registry.TryGet(request.Name, out var tool))
        {
            // 不抛: 抛了这条调用会被传输层当成"传输故障", 连带把整个降级会话判成断线。
            // 正确形态是「这一次调用失败」, 让 LLM 看得见原因并在下一轮改用别的工具。
            Log.Warn("Worker", $"内联降级路径: 未知工具 {request.Name}(当前注册表 {Registry.All.Count} 个)");
            return new WorkerToolCallResponse
            {
                IsError = true,
                Content = $"未知工具: {request.Name}(当前工具数 {Registry.All.Count})"
            };
        }

        JsonElement args;
        try
        {
            // ⚠ Clone() 不是可选的: JsonElement 只是 JsonDocument 上的一个视图, 而文档本身
            // 不被元素引用; 它被 GC 回收后底层缓冲区归还 ArrayPool, 工具再去读这个元素就可能抛
            // ObjectDisposedException 或读到别人的数据 —— 现场表现为"参数解析得好好的, 一执行就炸",
            // 与真正的原因毫无关联。
            args = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(request.Arguments) ? "{}" : request.Arguments).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new WorkerToolCallResponse
            {
                IsError = true,
                Content = $"工具参数不是合法 JSON: {ex.Message}"
            };
        }

        var ctx = new ToolContext
        {
            WorkspaceRoot = workspaceRoot,
            // 两个来源取或: 闭包捕获的是 Acquire 那一刻的模式, request 载的是本回合的;
            // 后者更近, 但父进程漏填时 IsPlanMode 会静默退回 false, 那等于让 Plan 模式失效。
            IsPlanMode = planMode || request.IsPlanMode,
            SessionId = request.SessionId,
            // ⚠ roster 原样透传, 绝不收成空表: null = 无限制, 空表 = 用户已清空,
            // 归一化会让「未下发」被误判成「用户清空」(或反过来), 症状是压缩开关恒 false /
            // 提示词仍在广告 run_<id>。
            RosterEntries = rosterEntries,
            CommanderPersonaText = commanderPersonaText,
            // 这两个字段必须转发: 子代理输出压缩要"与主回合同 Provider/模型",
            // 不转发的话用户切了非默认模型, 压缩会跑在另一个模型上。
            ProviderId = request.ProviderId,
            Model = request.Model,
            // ⚠ OnToolOutput 置 null: 内联执行的实时输出仍走<b>进程内</b>那条权威通道
            // (引擎侧 ctx.OnToolOutput → EngineEventHub), 本路径手里并没有引擎 ctx;
            // 且 InlineTransport 刻意不发 toolOutput 通知(见其类注释「事件恒不触发」),
            // 两边一致, 不会双投递。
            OnToolOutput = null,
            // ⚠ AskUser 同样置 null: 反问链路(弹窗、等作答)是 GUI 能力, 刻意留在主进程。
            // AskUserTool 对 null 有**已实现**的降级分支(返回「当前环境不支持向用户提问。」),
            // 不是异常也不是挂起。
            AskUser = null
        };

        ToolResult? result;
        try
        {
            result = await tool.ExecuteAsync(args, ctx, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ⚠ 必须严格排在 catch (Exception) 之前 —— 全仓铁律。
            // 被兜底分支吞掉的话: 用户点「停止」只会停住主循环, 而真正的子代理进程树继续跑到超时,
            // 日志里还只剩一条误导性的「工具执行异常」, 把真正的故障线索冲掉。
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("Worker", ex, $"内联降级路径: 工具 {request.Name} 执行失败");
            return new WorkerToolCallResponse
            {
                IsError = true,
                Content = $"工具执行异常: {ex.Message}"
            };
        }

        if (result is null)
        {
            // 契约上不该为 null(ITool 必须返回 ToolResult)。真发生了按"工具实现有 bug"处理,
            // 合成一条错误响应而不是让 NRE 逃出去 —— 逃出去会把一次"返回 null"变成"传输故障"。
            Log.Warn("Worker", $"内联降级路径: 工具 {request.Name} 返回了 null, 已合成错误响应");
            return new WorkerToolCallResponse
            {
                IsError = true,
                Content = $"工具返回了空结果({request.Name}): 这是工具实现的 bug"
            };
        }

        return BuildWorkerToolResponse(request, result);
    }

    /// <summary>
    /// 内联(降级)列举路径: 从本进程 <see cref="ToolRegistry"/> 出一份协议声明清单。
    /// </summary>
    /// <remarks>
    /// <para>其余三个参数(工作树根 / 人格 / roster / Plan 模式)目前<b>不参与</b>列举 ——
    /// 描述符只描述"有哪些工具、什么类别、要不要审批", 而这些事实都由注册表本身承载
    /// (注册表已经是 <see cref="RebuildSubagentTools"/> 过滤后的结果)。
    /// 保留它们是为了让两个委托共用同一份上下文形状: 将来列举真需要会话上下文时,
    /// 不必再回头改工厂委托的签名(而这类改动会在"降级路径"上被漏掉)。</para>
    /// <para>⚠ 分类规则(Kind / RequiresGitWrite / schema 降级 / 空名剔除)全部委托给
    /// <c>WorkerToolDescriptorFactory</c> —— 它是 Core 内唯一的落点, Worker 侧
    /// <c>tools/list</c> 也调同一份。两边各写一份的话, 漂移的方向永远是
    /// 「工具分类显示不出来」这种没有报错的地方。</para>
    /// </remarks>
    private Task<WorkerToolsResponse> ListWorkerToolsInlineAsync(
        WorkerToolsSyncRequest request,
        string workspaceRoot,
        string? commanderPersonaText,
        IReadOnlyList<AgentRosterEntry>? rosterEntries,
        bool planMode)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 纯内存操作, 没有 await 点: 返回已完成任务而不是写一个空的 async 方法。
        // 过滤按 request(而不是按主进程的私有开关)做, 这样"同一份载荷在两侧得到同一个答案"。
        var tools = Registry.All;
        var descriptors = WorkerToolDescriptorFactory.BuildFiltered(tools, request);

        Log.Debug("Worker", $"内联降级路径列举工具: {descriptors.Count}/{tools.Count} 个(载荷: " +
            $"coreTools={request.CoreTools}, subagentVisible={request.SubagentVisible}, planMode={request.PlanMode})");

        return Task.FromResult(new WorkerToolsResponse { Tools = descriptors });
    }

    /// <summary><see cref="ToolResult"/> → 协议响应。卡片详情这一层<b>必须软失败</b>。</summary>
    /// <remarks>
    /// 判别符与序列化都可能抛(新增派生类型忘了在 <c>ToolCardDetailCodec</c> 三张表登记 →
    /// <c>InvalidOperationException</c>)。但工具<b>已经成功执行完</b>了: 此刻因为一张装饰用的
    /// 卡片把整次调用报成失败, 是本末倒置 —— LLM 会重跑一个已经做完的工具(git_commit 尤其危险)。
    /// 所以降级成 Detail=null(父进程渲染通用文本卡)并记 Warn。
    /// </remarks>
    private static WorkerToolCallResponse BuildWorkerToolResponse(
        WorkerToolCallRequest request, ToolResult result)
    {
        string? detailType = null;
        string? detailJson = null;

        if (result.Detail is not null)
        {
            try
            {
                detailType = ToolCardDetailCodec.GetTypeName(result.Detail);
                detailJson = ToolCardDetailCodec.Serialize(result.Detail);
            }
            catch (Exception ex)
            {
                detailType = null;
                detailJson = null;
                Log.Warn("Worker", ex, $"内联降级路径: 工具 {request.Name} 的卡片详情编解码失败, 已降级为通用文本卡");
            }
        }

        return new WorkerToolCallResponse
        {
            Content = result.Content,
            IsError = result.IsError,
            // StepId 透传: 当前只有 git_create_checkpoint 会赋值, 子代理侧恒为 null
            // (子代理统一在当前分支就地工作, 回滚入口是"每条用户消息"的检查点)。
            StepId = result.StepId,
            DetailType = detailType,
            DetailJson = detailJson
        };
    }

    /// <summary>下发给 Worker 的工具集策略快照(见 <see cref="CurrentWorkerToolPolicy"/>)。</summary>
    private readonly record struct WorkerToolPolicy(
        bool CoreTools,
        bool SubagentVisible,
        bool PlanMode,
        IReadOnlyList<string> AllowedAgentIds);

    /// <summary>一键还原默认设置: 删除 providers/agents/mcp-servers/roster.prompt(含旧 JSON 兼容文件)后
    /// 按启动流程重新生成默认, 并让运行时即时生效(LLM client 缓存清空)。返回各步骤状态说明(失败带 ⚠)。
    /// 界面偏好(preferences.toml)由 GUI 层的 ThemeService.ResetToDefault 负责; 人格/模板示例与会话/统计数据不受影响。</summary>
    public List<string> ResetSettingsToDefault()
    {
        var messages = new List<string>();

        // LLM Provider: 删除后重新生成, 并替换 Settings 触发 _clients 缓存清空
        DeleteIfExists(AppPaths.ProvidersPath);
        DeleteIfExists(Path.ChangeExtension(AppPaths.ProvidersPath, ".json"));
        ProviderSettingsService.EnsureDefaultExists();
        Llm.Settings = ProviderSettingsService.Load();
        messages.Add(StepMessage("[Provider] 默认 LLM 配置", File.Exists(AppPaths.ProvidersPath)));

        // Agent
        DeleteIfExists(AppPaths.AgentsPath);
        DeleteIfExists(Path.ChangeExtension(AppPaths.AgentsPath, ".json"));
        AgentConfigService.EnsureDefaultExists();
        AgentConfigService.Refresh();
        messages.Add(StepMessage("[Agent] 默认 Agent 配置", File.Exists(AppPaths.AgentsPath)));

        // MCP 服务器
        DeleteIfExists(AppPaths.McpServersPath);
        McpConfigService.EnsureDefaultExists();
        McpConfigService.Refresh();
        messages.Add(StepMessage("[MCP] 默认 MCP 配置", File.Exists(AppPaths.McpServersPath)));

        // Roster 注入模板
        DeleteIfExists(AppPaths.RosterTemplatePath);
        RosterBuilder.WriteDefaultTemplate();
        messages.Add(StepMessage("[Roster] 默认 Roster 模板", File.Exists(AppPaths.RosterTemplatePath)));

        Log.Info("Config", $"已还原默认设置: {string.Join("; ", messages)}");
        return messages;
    }

    /// <summary>按生成结果组装步骤消息(生成失败标记 ⚠ 便于用户/日志定位)。</summary>
    private static string StepMessage(string label, bool generated)
        => generated ? $"{label} 已还原" : $"⚠ {label} 生成失败(见日志)";

    private static void DeleteIfExists(string path)
    {
        try
        {
            // 用 AtomicFile.Delete 而非 File.Delete: 必须连带清掉 .bak/.tmp。
            // 否则"还原默认设置"删掉 providers.toml 后, 旧 providers.toml.bak 仍留在磁盘上
            // 保存着旧 API Key; 且重建后的默认文件一旦解析异常, TryReadText 会回退到那个
            // 陈旧备份, 把用户刚清掉的 Provider/Agent 配置复活。
            AtomicFile.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn("Config", ex, $"删除配置文件失败: {path}");
        }
    }

    /// <summary>把会话 Roster 快照写入<b>活动会话</b>引擎(下一回合生效)。</summary>
    ///
    /// <para>参数语义与 <c>RosterBuilder.Build</c> 一致: <c>null</c> = 无限制(列出全部子代理),
    /// 非 null(含空表) = 以该表为准。空表表示"用户清空全部", 工具通常也已被注销。</para>
    ///
    /// <para>仅写入当前活动会话: 其它会话的运行中回合不受影响, 下一回合各按自己的快照。</para>
    public void SetRosterEntries(IReadOnlyList<AgentRosterEntry>? entries)
    {
        Sessions.ActiveEngine.SetRosterEntries(entries);

        // Plan 模式下 Roster 变化(如切换"在 Plan 模式中使用")需同步重建工具注册
        if (_isPlanMode && _subagentToolsVisible)
        {
            RebuildSubagentTools();

            // 与 SetPlanMode 同一条纪律: 重建改的就是「Plan 授权白名单」本身,
            // 而白名单要靠 tools/sync 才能到达各子进程。漏这一步的症状是"用户明明授权了,
            // Plan 模式下 Worker 里的 run_<id> 却还是老的那批"。
            ScheduleWorkerToolsSync("Roster 更新(Plan 授权可能变化)");
        }
    }
}
