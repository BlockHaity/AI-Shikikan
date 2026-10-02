using System.Text.Json;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Git;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Personas;
using AIShikikan.Core.Services.Runtime;
using AIShikikan.Core.Services.Templates;
using AIShikikan.Core.Services.Tools;
using AIShikikan.Core.Services.Tools.Builtin;
using AIShikikan.Core.Services.Worker;

namespace AIShikikan.Worker;

/*
 * Worker 进程的组合根(瘦装配): 手写 new 出工具层真正需要的服务, 并实现 IWorkerToolHost。
 * 形态上对应 docs/plans/worker-architecture.md §3.1 图里的 "SlimHost (手写 new, 不调 Boot)"。
 *
 * ═══════════════════════════════════════════════════════════════════════════
 * ⚠ 头号禁令: 本文件(以及本项目任何文件)绝对不允许调用 CommanderRuntime.Boot()
 * ═══════════════════════════════════════════════════════════════════════════
 * Boot 是**主进程**的启动外观, 它做的四件事里有三件对 Worker 是灾难:
 *
 *   1. 写出 6 类配置文件 —— WriteSampleFiles / EnsureSamplesExist /
 *      WriteDefaultTemplate / EnsureDefaultExists(agents) / (providers) / (mcp-servers)。
 *      Worker 是**被派生出来执行工具的短命子进程**, 不是应用的启动入口; 让它去写用户的
 *      配置文件, 等于把「谁有权改配置」这件事从主进程扩散到一个随时可能被杀、
 *      且主进程可能正并行读写同一文件的进程。真出并发写, 原子写的 rename 与主进程的写
 *      会互相覆盖(providers.toml 里还有 API Key, 覆盖 = 密钥丢失)。
 *   2. 构造期扫盘 —— GitCheckpointStore / AssignmentManager 会建目录并读全量既有记录,
 *      这不是问题本身, 但它同时意味着 Boot 把「扫描 + 建目录」塞进了每个 Worker 的启动路径。
 *   3. **后台 spawn MCP 子进程** —— Boot 末尾 `Task.Run(RefreshMcpToolsAsync)` 会真的
 *      Process.Start 外部 MCP 服务器。MCP 明确留在主进程(architecture §2.3); 若 Worker
 *      也连一遍, 就会变成「一个 git 工具调用顺带起了三个 MCP 进程」, 而且这些进程没有父子
 *      生命周期约束(Worker 被 kill 时它们未必跟着走) → 孤儿进程堆积。
 *
 * 因此本文件只 new 下面列出的那几个对象, 一个都不多。
 *
 * ── 刻意**没有** new 的东西(每一个都有理由) ──
 *   · McpService            —— MCP 留主进程; 见上面第 3 条。
 *   · ChatService           —— 会话消息持久化**完全无锁、全量覆盖** sessions/*.json。
 *                            主进程是唯一写方; Worker 若参与写, 一次覆盖就能抹掉用户整段历史。
 *   · UsageStatsService 的任何写入路径 —— 用量统计是防抖合并落盘的单写者快照,
 *                            Worker 记用量会用自己的旧快照覆盖主进程的统计(见 Program.cs 退出路径)。
 *   · SessionRuntimeRegistry / AgentEngine —— 会话循环留在主进程, 否则等于把「一个回合」
 *                            拆成两个引擎, 审批与事件流都要跨进程重做一遍。
 *   · WorkspaceExecutionCoordinator / EngineEventHub —— 会话级并发仲裁与事件广播在主进程,
 *                            跨进程仲裁是另一个设计议题(技术债 #14), 不在本次范围内。
 *
 * ── 刻意 new 的东西(以及各自的写入面) ──
 *   · AgentConfigService.LoadAll / PersonaService.LoadAll / AgentTemplateService.LoadAll
 *       纯读, 但各自在文件缺失时会做一次**幂等**的首次启动写出(Main 与 Worker 写出的
 *       内容逐字相同, 都来自内嵌的 DefaultConfig)。主进程是先启动的那一个, 所以实际路径
 *       是「读已存在的文件」; 极端情况下(用户手工删了配置就启动 Worker)才走幂等写出,
 *       风险可接受: 内容相同, 且写的是 AtomicFile。
 *   · GitCheckpointStore + GitService —— 本进程是 checkpoints/ 的唯一写方(architecture §9)。
 *   · AssignmentManager —— 本进程是 assignments/ 的唯一写方。
 *   · LlmService —— 仅供子代理输出压缩走 LLM。⚠ 其构造函数会调 ProviderSettingsService.Load(),
 *       文件缺失时**会写出 providers.toml**(含 API Key); 主进程已先写过, 所以实际是幂等读。
 *       这正是上面不 new McpService 那种"顺手写配置"的风险为什么要在注释里点名。
 *
 * ── 文案边界(新增铁律, architecture §7.5) ──
 *   本项目**不引用** Resources/Strings*.resx, 面向用户/LLM 可见的错误文案一律硬编码中文。
 *   这不是"忘了加翻译": 现状所有工具的错误文案本来就硬编码中文(见 GitAddTool /
 *   GrepTool / AgentExecutor), 它们经 ToolResult.Content 原样进 LLM 上下文, 从来没有 i18n。
 *   Worker 若引用 resx 就会隐式依赖 GUI 程序集名(卫星资源程序集名由它推出), 而这正是本项目
 *   必须守住的分层边界。
 */

/// <summary>
/// Worker 进程的启动身份(命令行给定, 进程生命周期内不变)。
/// <para>刻意与运行期可变状态(roster / 人格 / Plan 模式)分开: 后者由 <c>hello</c> 与
/// <c>tools/sync</c> 在握手后写入, 前者是"派我出来的那个实例"的固有属性。</para>
/// </summary>
internal sealed class WorkerHostOptions
{
    /// <summary>宿主会话 Id。参与 WorkerKey 计算, 必须与主进程算 key 时逐字相同。</summary>
    public required string SessionId { get; init; }

    /// <summary>命令行 <c>--workdir</c>。工具的相对路径解析与 git 上下文锚点都以它为准。</summary>
    public required string WorkDir { get; init; }

    /// <summary>工作树根。启动时只能拿 <see cref="WorkDir"/> 顶替, 握手后由 hello 的值接管。</summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>父进程 pid(0 = 未知)。仅诊断与孤儿自检用。</summary>
    public required int ParentPid { get; init; }

    /// <summary>主进程算好的 WorkerKey, 或本进程现算的结果(日志里两者能对上才算路由一致)。</summary>
    public required string WorkerKey { get; init; }
}

/// <summary>
/// Worker 侧工具宿主: 持有工具集快照与执行入口, 供 <c>WorkerServer</c> 调用。
///
/// <para><b>为什么组合根放在 Worker 项目而不是 Core</b>: 装配代码里那些"绝不能 new 什么"
/// 的决定属于进程边界知识, 放进 Core 意味着 Core 反过来知道 Worker 的存在(架构图 §7.1 明确
/// Core 不得引用 Worker)。放这里, Core 只留 <see cref="IWorkerToolHost"/> 这一个纯契约。</para>
/// </summary>
internal sealed class WorkerSlimHost : IWorkerToolHost, IWorkerToolHostSync
{
    private const string Category = "Worker";

    // ⚠ 这里刻意**不**定义 Kind 常量: WorkerToolDescriptor.Kind 由 WorkerServer.BuildDescriptors
    // 单独派生(见本文件末尾的说明)。本文件早期有一份同款常量与映射, 但它服务不到任何协议方法,
    // 留着只会诱导后人改错的那一份。

    private readonly WorkerHostOptions _options;

    private readonly GitService _git;
    private readonly LlmService _llm;
    private readonly AssignmentManager _assignments;
    private readonly IReadOnlyList<CliAgentDefinition> _allAgents;
    private readonly IReadOnlyList<Persona> _personas;
    private readonly IReadOnlyList<AgentTemplate> _templates;

    private readonly ToolRegistry _registry = new();

    // ---- 运行期可变状态: 由 ApplyHello / ApplyToolsSync 写入, 由工具读取 ----
    // 读侧不单独加锁: 这些字段只在 WorkerServer 的读循环(单线程派发)里被写,
    // 而工具执行发生在该循环派发出的任务上。刻意不加锁是"写只在握手/sync 阶段发生"这个前提的代价;
    // 若将来支持运行期并发 sync, 需要在这里补一把锁(或改成不可变快照整体替换)。
    private IReadOnlyList<AgentRosterEntry>? _rosterEntries;
    private string? _commanderPersonaText;
    private string _workspaceRoot;
    private bool _subagentToolsVisible = true;
    private bool _planMode;
    private IReadOnlyList<string> _allowedAgentIds = [];

    /// <summary>
    /// 流式输出出口: 把工具产出的每一行包成 <see cref="WorkerToolOutputNotification"/> 推回父进程。
    /// 由 <c>WorkerServer</c>(有写锁的那一侧)注入; 为 null 时只落 Debug 日志。
    /// </summary>
    /// <remarks>
    /// 为什么是委托而不是事件: 事件允许多订阅且订阅顺序不定, 而"一行输出对应一帧"的
    /// 写入必须串行且有序 —— 父进程按到达顺序把行喂给 UI, 乱序会让子代理日志错乱。
    /// 单个接收方天然满足这个要求。
    /// </remarks>
    public Action<WorkerToolOutputNotification>? OutputSink { get; set; }

    public WorkerSlimHost(WorkerHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _workspaceRoot = options.WorkspaceRoot;

        // ---- 配置只读三件套(内容见文件头注释: 纯读 + 首次启动幂等写) ----
        _allAgents = AgentConfigService.LoadAll();
        _personas = PersonaService.LoadAll();
        _templates = AgentTemplateService.LoadAll();

        // 子代理输出压缩要调 LLM; 为 null 时子代理工具会跳过压缩直接透传原始输出,
        // 那是可接受的降级, 但压缩开关一旦打开就静默失效, 所以这里坚持 new。
        _llm = new LlmService();

        // ---- git + 检查点 ----
        // ⚠ TagDeleter 的注入时序是有讲究的, 别看成一个可选回调:
        //   GitCheckpointStore 现在是**懒加载**(构造期不读盘), 所以注入 TagDeleter 必须早于
        //   "第一次可能触发淘汰的 Save"。这里 new 出来的 store 立刻注入、之后才会有任何 Save,
        //   顺序天然正确 —— 与 CommanderRuntime.Boot 里"new GitService → 赋 TagDeleter"的顺序一致。
        var checkpoints = new GitCheckpointStore();
        _git = new GitService(checkpoints);
        checkpoints.TagDeleter = (repoRoot, tag) => _git.DeleteCheckpointTag(repoRoot, tag);

        _assignments = new AssignmentManager();

        // ---- 工具注册 ----
        foreach (var tool in AgentToolFactory.CreateCoreTools(_git, BuildScope()))
        {
            _registry.Register(tool);
        }

        RebuildSubagentTools();
    }

    // ─────────────────────────── IWorkerToolHost ───────────────────────────

    /// <summary>
    /// 当前工具集快照(权威清单)。语义是**全量**而非增量: 父进程每次都应整份替换自己的
    /// LLM 工具列表, 不做 diff 合并 —— 增量在"子代理工具动态增删"时必然漂移。
    /// </summary>
    public IReadOnlyList<ITool> SnapshotTools() => _registry.All;

    /// <summary>
    /// 协议声明(<c>tools/list</c> 的载荷)由 <c>WorkerServer</c> 侧的
    /// <c>BuildDescriptors</c> 统一组装, 本类**刻意不再保留第二份映射</c>。
    /// </summary>
    /// <remarks>
    /// 曾经这里也有一份 <c>DescribeTools / Describe / ClassifyKind / IsGitWrite</c>, 但它是死代码
    /// (<c>WorkerServer</c> 服务 <c>tools/list</c> 时用的是自己那份)。两份分类规则必然漂移:
    /// 谁改了 git 工具名, 就有一处忘了改 <c>Kind</c> —— 而症状是「工具分类显示错误」这种
    /// 极难归因的小事。故只留一份, 且留在真正被使用的那一侧(Core, 便于随协议演进单测)。
    /// </remarks>

    /// <summary>
    /// 执行一次工具调用。失败一律**软失败**(返回 <c>IsError=true</c> 的响应)而不是抛异常,
    /// 只有取消才原样上抛。
    /// </summary>
    public async Task<WorkerToolCallResponse> ExecuteAsync(
        WorkerToolCallRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_registry.TryGet(request.Name, out var tool))
        {
            // 不抛: 抛了这条调用在父进程侧就变成"传输故障", 会连带把 Worker 判成断线。
            // 正确形态是「这一次调用失败」, 让 LLM 看得见原因并在下一轮重试或改用别的工具。
            Log.Warn(Category, $"未知工具: {request.Name}");
            return new WorkerToolCallResponse
            {
                IsError = true,
                Content = $"未知工具: {request.Name}(当前 Worker 工具数 {_registry.All.Count})"
            };
        }

        JsonElement args;
        try
        {
            // Clone() 不是可选的: RootElement 是"寄生"在 JsonDocument 上的视图, 而文档本身
            // 不被元素引用; 一旦它被 GC 回收, 底层缓冲区会归还 ArrayPool, 此时工具再去读这个
            // 元素就可能抛 ObjectDisposedException 或读到别人的数据 ——
            // 现场表现为"参数解析得好好的, 一执行就炸", 与真正的原因毫无关联。
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

        var ctx = BuildToolContext(request);

        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(args, ctx, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ⚠ 必须严格排在 catch (Exception) 之前 —— 这是全仓铁律。
            //   OperationCanceledException 是 Exception 的子类, 写反了会被兜底分支吞成
            //   「工具执行异常: ...」的 IsError 响应, 于是: 父进程以为调用已结束, 不再转发取消,
            //   而 Worker 里真正的子代理进程树继续跑 —— 「停止」按钮只停住了主循环,
            //   子进程完全无感地跑到超时, 且日志里只有一条误导性的错误文案。
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(Category, ex, $"工具 {request.Name} 执行失败");
            return new WorkerToolCallResponse
            {
                IsError = true,
                Content = $"工具执行异常: {ex.Message}"
            };
        }

        return BuildResponse(request, result);
    }

    // ─────────────────────────── 运行期状态入口 ───────────────────────────

    /// <summary>
    /// 握手后应用 <c>hello</c> 携带的状态(工作树根 / 指挥官人格 / 会话 roster / Plan 模式),
    /// 并重建子代理工具。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么必须重建工具而不是就地改字段</b>: <c>AgentExecutionScope</c> 的属性是
    /// <c>init</c>-only(跨进程契约的一部分), 子代理工具又在**构造期**就把 scope 存进了
    /// <see cref="AgentExecutor"/>。所以人格文本变了只能换一个新 scope + 重新注册, 与
    /// <c>CommanderRuntime.RefreshCommanderPersonaText</c> 末尾的重建是同一个道理。</para>
    /// <para>调用时机: <c>worker/hello</c> 处理完立刻调, 早于任何 <c>tools/call</c>。
    /// 协议保证 hello 先到, 所以并发面只在"sync 与 call 交错"这一处。</para>
    /// </remarks>
    void IWorkerToolHost.BindHello(WorkerHelloRequest hello) => ApplyHello(hello);

    /// <summary><see cref="IWorkerToolHost.BindHello"/> 的实现体(显式接口转发到
    /// <see cref="ApplyHello"/>, 后者是本类型的公开入口, 便于同进程单测直接调用)。</summary>
    public void ApplyHello(WorkerHelloRequest hello)
    {
        ArgumentNullException.ThrowIfNull(hello);

        if (hello.ProtocolVersion != WorkerProtocol.ProtocolVersion)
        {
            // 不在这里终止进程: 终止与否是 WorkerServer 的策略(它要回一帧 Ok=false 让父进程知道),
            // 这里只留下明确日志, 免得"版本不符"表现为后续一堆莫名其妙的字段错位。
            Log.Error(Category,
                $"协议版本不符: 收到 {hello.ProtocolVersion}, 本进程 {WorkerProtocol.ProtocolVersion}");
        }

        if (!string.IsNullOrEmpty(hello.SessionId) &&
            !string.Equals(hello.SessionId, _options.SessionId, StringComparison.Ordinal))
        {
            Log.Warn(Category,
                $"hello.SessionId(\"{hello.SessionId}\") 与 --session(\"{_options.SessionId}\") 不一致; " +
                "以命令行值为准(它是 WorkerKey 的一半), 但这通常意味着生命周期出了 bug");
        }

        if (!string.IsNullOrEmpty(hello.WorkDir) &&
            !PathsEqual(hello.WorkDir, _options.WorkDir))
        {
            // 同样只 Warn 不改: 工具的相对路径解析锚点一旦在运行中途改掉, 比不一致本身更难查。
            Log.Warn(Category,
                $"hello.WorkDir(\"{hello.WorkDir}\") 与 --workdir(\"{_options.WorkDir}\") 指向不同目录");
        }

        // 工作树根以 hello 为准: 它是父进程 IWorkspaceResolver 的解析结果, 执行权隔离与
        // 检查点目录都按它建键, 而启动时我们手里只有 --workdir。
        _workspaceRoot = string.IsNullOrWhiteSpace(hello.WorkspaceRoot)
            ? _options.WorkspaceRoot
            : hello.WorkspaceRoot;

        // 人格: 空白一律收成 null。协议明确 null/空串 = 「本回合无指挥官人格」, 此时子代理
        // 不应附加人格前缀, 而不是回退去读全局默认(那会把「切人格串味到别的会话」的缺陷原样保留)。
        _commanderPersonaText = string.IsNullOrWhiteSpace(hello.CommanderPersonaText)
            ? null
            : hello.CommanderPersonaText;

        // roster 的 null 与空表语义截然不同(null = 无限制, 空表 = 用户已清空),
        // 所以原样存, 绝不能顺手收成空表 —— 那个 bug 的现场是「工具已注销而提示词仍在广告 run_<id>」。
        _rosterEntries = hello.RosterEntries;

        _planMode = hello.IsPlanMode;

        RebuildSubagentTools();
        Log.Info(Category,
            $"握手状态已应用: 工作树根={_workspaceRoot}, roster={(hello.RosterEntries is null ? "无限制" : $"{hello.RosterEntries.Count} 项")}, " +
            $"人格字符数={_commanderPersonaText?.Length ?? 0}, Plan模式={_planMode}, 当前工具数={_registry.All.Count}");
    }

    /// <summary>
    /// 应用 <c>tools/sync</c>: 全量重配置工具集(固定工具开关 / 子代理可见性 / Plan 模式与授权白名单)。
    /// </summary>
    /// <remarks>
    /// 语义刻意是**全量替换**而不是补丁: 与 <c>CommanderRuntime.SetSubagentToolsVisible</c> /
    /// <c>SetPlanMode</c> 一样是"注销一批 + 重建一批"。增量协议要额外维护差量状态机,
    /// 而一旦某次差量丢失, 工具集就永久错位且没有自愈路径。
    /// </remarks>
    public void ApplyToolsSync(WorkerToolsSyncRequest sync)
    {
        ArgumentNullException.ThrowIfNull(sync);

        _planMode = sync.PlanMode;
        // 空表 = 不限制(与 AllowedAgentIds 的文档语义一致); Plan 模式下的"零授权"
        // 由"授权白名单非空但一个都没匹配上"表达, 不靠空白名单表达。
        // ⚠ 这里必须收 null: 属性声明成非空, 但 STJ 遇到帧里的 "allowedAgentIds": null
        //   会老老实实把 null 写进来, 而下面的 Contains/NRE 都假定非空。
        _allowedAgentIds = sync.AllowedAgentIds ?? [];

        if (!sync.CoreTools)
        {
            // 固定工具(文件/git/ask_user)整体注销。目前主进程不会下发 false,
            // 保留这条是为了协议位先落地: 万一将来有"最小权限模式", 不用再改协议骨架。
            Log.Info(Category, "tools/sync 要求关闭固定工具");
            _registry.UnregisterWhere(IsCoreTool);
        }

        _subagentToolsVisible = sync.SubagentVisible;

        RebuildSubagentTools();
        Log.Info(Category,
            $"工具集已按 sync 重配: 固定工具={sync.CoreTools}, 子代理可见={_subagentToolsVisible}, " +
            $"Plan模式={_planMode}, 授权={(_allowedAgentIds.Count == 0 ? "不限制" : string.Join("/", _allowedAgentIds))}, " +
            $"当前工具数={_registry.All.Count}");
    }

    /// <summary>
    /// 恢复固定工具(与 <see cref="ApplyToolsSync"/> 的 <c>CoreTools=false</c> 对称)。
    /// 之所以做成两个方向而不是"一个设置方法": 固定工具在进程生命周期里只增不减,
    /// 这里只需要在"曾经被关过"时能重建回来, 不必暴露一个可写入的开关。
    /// </summary>
    public void RestoreCoreTools()
    {
        if (_registry.All.Any(IsCoreTool))
        {
            return;
        }

        foreach (var tool in AgentToolFactory.CreateCoreTools(_git, BuildScope()))
        {
            _registry.Register(tool);
        }
    }

    // ─────────────────────────── 内部装配 ───────────────────────────

    /// <summary>组装工具层对"进程全局状态"的显式依赖(替代原先反向读 CommanderRuntime 单例)。</summary>
    /// <remarks>
    /// 形态照抄 <c>CommanderRuntime.BuildToolScope()</c>, 但值全部来自本进程的绑定与握手快照。
    /// 任一委托为 null 时工具侧会安全降级(见 <see cref="AgentExecutionScope"/> 类注释),
    /// 这里仍全部给出来: 降级路径应该是"出了意外才走", 而不是默认行为。
    /// </remarks>
    private AgentExecutionScope BuildScope() => new()
    {
        WorkspaceRoot = _workspaceRoot,
        CommanderPersonaText = _commanderPersonaText,
        // Worker 与会话是一对一绑定(WorkerKey = hash(workDir, sessionId)), 所以忽略入参
        // sessionId: 拿到的永远是本 Worker 自己的会话。返回 null 是合法值(语义 = 未收到
        // 限定, 工具侧按空表处理 → 压缩开关恒 false), 与主进程"没有会话级记录"时一致。
        RosterResolver = _ => _rosterEntries,
        PlanAuthorizer = (sessionId, agent) => IsPlanAuthorized(sessionId, agent),
        ActiveSessionIdResolver = () => _options.SessionId
    };

    /// <summary>
    /// 重建子代理工具(可见性 / Plan 授权过滤 / 作用域快照变化后调用)。
    /// </summary>
    private void RebuildSubagentTools()
    {
        _registry.UnregisterWhere(IsSubagentTool);

        if (!_subagentToolsVisible)
        {
            return;
        }

        var agents = _allAgents;
        if (_planMode)
        {
            agents = agents.Where(a => IsPlanAuthorized(_options.SessionId, a)).ToList();

            if (agents.Count == 0)
            {
                // 照搬 CommanderRuntime.SetPlanMode 的既有行为: Plan 模式 + 零授权 ⇒
                // 三个子代理工具(含 assign_task / run_subagents)全部不注册。
                // 好处是「只规划不执行」这条底线守得住, 代价是零授权时连"让子代理出方案"
                // 的入口都没有 —— 这是产品语义, 不是缺陷, 改它需要先改主进程。
                Log.Info(Category, "Plan 模式: 无授权子代理, 子代理工具全部移除");
                return;
            }
        }

        foreach (var tool in AgentToolFactory.CreateSubagentTools(
                     agents, _personas, _templates, _assignments, _llm, BuildScope()))
        {
            _registry.Register(tool);
        }
    }

    /// <summary>
    /// Plan 模式授权判定。
    /// </summary>
    /// <remarks>
    /// ⚠ 这里**调用**了 <c>CommanderRuntime.IsAgentPlanModeAuthorized</c> 这个 public static,
    /// 与头号禁令并不冲突 —— 禁令针对的是 <c>Boot()</c>: 它会写配置、扫盘、spawn MCP 子进程。
    /// 这一个方法是纯函数, 触及的静态状态只有 <c>Instance = null</c>(字段初始化), 无任何 IO。
    /// 复用它的理由是判据唯一: <c>AgentExecutionScope.PlanAuthorizer</c> 的文档明确要求
    /// 「判据应与工具注册过滤共用同一份规则」, 否则会出现"注册了但执行被拒"或
    /// "未注册却放行"的漂移 —— 而这两处(本方法内的注册过滤 + scope 里的执行兜底)
    /// 都在同一个进程里, 复制规则毫无收益。
    /// </remarks>
    private bool IsPlanAuthorized(string? sessionId, CliAgentDefinition agent)
    {
        // 刻意忽略 sessionId: Worker 与会话是一对一绑定(WorkerKey = hash(workDir, sessionId)),
        // 所以这个入参恒等于本进程绑定的那个会话; 拿它去"按会话查表"反而会引入查不到的分支。
        if (!CommanderRuntime.IsAgentPlanModeAuthorized(agent, _rosterEntries ?? []))
        {
            return false;
        }

        // 叠加父进程在 tools/sync 里下发的白名单; 空表 = 不限制(不按 Agent 过滤)。
        return _allowedAgentIds.Count == 0
            || _allowedAgentIds.Contains(agent.Id, StringComparer.OrdinalIgnoreCase);
    }

    private ToolContext BuildToolContext(WorkerToolCallRequest request) => new()
    {
        // WorkspaceRoot = 本 Worker 绑定的目录(不是握手下发的 WorkspaceRoot):
        // 它是文件工具相对路径解析与 git 上下文锚点的共同基准, 语义等同于主进程引擎侧
        // ToolContext.WorkspaceRoot = _workspaceRoot 的"本次工具调用的工作根"。
        // 用 workDir 而非工作树根是安全的: git 工具经 GitService.ResolveContext → FindRepositoryRoot
        // 会自己向上找到仓库根, 所以 workDir 指向 worktree 的子目录也没问题。
        // (工作树根另有一处用途: AgentExecutionScope.WorkspaceRoot 那个"末级兜底"字段。)
        WorkspaceRoot = _options.WorkDir,
        IsPlanMode = request.IsPlanMode,
        // 会话 id 优先取请求里的: 传输层契约要求父进程逐次下发, null/空才回退到绑定值。
        SessionId = string.IsNullOrEmpty(request.SessionId) ? _options.SessionId : request.SessionId,
        // 这两个字段必须转发: 子代理输出压缩要"与主回合同 Provider/模型", 不转发的话
        // 用户切了非默认模型, 压缩会跑在另一个模型上(技术债 #16 就是这个)。
        ProviderId = request.ProviderId,
        Model = request.Model,
        RosterEntries = _rosterEntries,
        CommanderPersonaText = _commanderPersonaText,
        // 流式输出转成协议通知推回父进程(子代理 stdout 逐行)。出口由 WorkerServer 注入。
        OnToolOutput = line => ReportOutput(request, line),
        // ⚠ AskUser 刻意留 null: 反问链路(弹窗、等作答)是 GUI 能力, 留在主进程执行
        //   (architecture §2.4)。AskUserTool 对 null 有**已实现**的降级分支, 返回
        //   「当前环境不支持向用户提问。」的错误结果 —— 不是异常、不是挂起。
        //   协议位 WorkerAskUserRequest 仍在, 等将来子代理想反问指挥官时再启用;
        //   启用方必须自己套 AgentEngine.ApprovalTimeout 级别的超时(见该 DTO 的说明)。
        AskUser = null
    };

    /// <summary>把工具产出的一行包成通知推回父进程; 未接出口时只落日志。</summary>
    private void ReportOutput(WorkerToolCallRequest request, string line)
    {
        var sink = OutputSink;
        if (sink is null)
        {
            Log.Debug(Category, $"[{request.Name}] {line}");
            return;
        }

        try
        {
            sink(new WorkerToolOutputNotification
            {
                CallId = request.CallId,
                Line = ClampLine(line)
            });
        }
        catch (OperationCanceledException)
        {
            // 管道写侧的取消仍按取消处理: 吞掉它等于让"父进程已放弃等待"这件事对 Worker 不可见。
            throw;
        }
        catch (Exception ex)
        {
            // 写通知失败(管道已断)不该让正在跑的工具失败: 父进程会从自己的读循环 EOF 感知断线
            // 并把在飞调用完成为错误, 那才是正确的收尾路径。此处只留一条 Warn。
            Log.Warn(Category, ex, "推送工具输出通知失败(管道可能已断开)");
        }
    }

    /// <summary>
    /// <see cref="ToolResult"/> → 协议响应。卡片详情这一层**必须软失败**。
    /// </summary>
    /// <remarks>
    /// 判别符 + 序列化都可能抛(新增派生类型忘了在 <c>ToolCardDetailCodec</c> 三张表登记 →
    /// <c>InvalidOperationException</c>)。但工具**已经成功执行完**了: 此刻因为一张装饰用的
    /// 卡片把整次调用报成失败, 是本末倒置 —— LLM 会重跑一个已经做完的工具(git_commit 尤其危险)。
    /// 所以降级成 Detail=null(父进程渲染通用文本卡)并记 Warn。
    /// </remarks>
    private static WorkerToolCallResponse BuildResponse(WorkerToolCallRequest request, ToolResult result)
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
                Log.Warn(Category, ex, $"工具 {request.Name} 的卡片详情编解码失败, 已降级为通用文本卡");
            }
        }

        return new WorkerToolCallResponse
        {
            Content = result.Content,
            IsError = result.IsError,
            // StepId 透传: 当前只有 git_create_checkpoint 会赋值, 而子代理侧恒为 null
            // (子代理统一在当前分支就地工作, 回滚入口是"每条用户消息"的检查点)。
            // 字段虽近乎死字段仍要传 —— 协议位在那儿, 将来恢复按步回滚时不必改协议。
            StepId = result.StepId,
            DetailType = detailType,
            DetailJson = detailJson
        };
    }

    // ─────────────────────────── 分类与小工具 ───────────────────────────

    /// <summary>
    /// 协议侧的 <c>Kind</c> / <c>RequiresGitWrite</c> 分类<b>刻意不在这里</b>:
    /// 那份映射的唯一落点是 <c>WorkerServer.BuildDescriptors</c>(Core 内, 服务 <c>tools/list</c> 的那一侧)。
    /// 曾经此处也有过一份同款实现, 但它是死代码 —— 两份分类规则必然漂移, 而漂移的症状
    /// 只是「工具分类显示错误」这类极难归因的小事。
    /// </summary>
    /// <remarks>
    /// 下面两个 <c>IsCoreTool</c> / <c>IsSubagentTool</c> 判定<b>不是</b>那份映射的副本:
    /// 它们判的是 <em>类型</em>(决定该注册进哪个工具集), 与按<em>名字</em>分类是两个不同维度的问题,
    /// 各自只有一处定义。
    /// </remarks>

    /// <summary>固定工具判定(与 <c>AgentToolFactory.CreateCoreTools</c> 的清单对应)。</summary>
    private static bool IsCoreTool(ITool tool)
        => tool is ReadFileTool or GlobTool or GrepTool or ListDirectoryTool
            or GitStatusTool or GitAddTool or GitCommitTool
            or GitCreateCheckpointTool or GitDiffTool or AskUserTool;

    /// <summary>子代理工具判定(与 <c>CommanderRuntime.RebuildSubagentTools</c> 同一份判据)。</summary>
    private static bool IsSubagentTool(ITool tool)
        => tool is AgentExecutionTool or AssignTaskTool or SubagentGroupTool;

    /// <summary>
    /// 单行长度封顶到 <see cref="WorkerProtocol.MaxLineChars"/>(8KB), 与
    /// <c>CliAgentRunner</c> 上报子进程 stdout 单行时的处理一致。
    /// </summary>
    /// <remarks>
    /// 作用域只限"流式输出行": 响应帧(整份文件内容 / DetailJson)可以远超这个上限,
    /// 对它们套用会把 <c>read_file</c> 的正常结果截成残缺内容, 且没有任何错误提示。
    /// </remarks>
    private static string ClampLine(string line)
        => line.Length <= WorkerProtocol.MaxLineChars
            ? line
            : string.Concat(line.AsSpan(0, WorkerProtocol.MaxLineChars), "…(已截断)");

    /// <summary>比较两个路径是否指向同一目录(忽略尾分隔符差异; 只做字符串级比较, 不碰文件系统)。</summary>
    private static bool PathsEqual(string a, string b)
        => string.Equals(TrimTrailingSeparator(a), TrimTrailingSeparator(b), StringComparison.Ordinal);

    private static string TrimTrailingSeparator(string path)
        => path.Length > 1 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
}