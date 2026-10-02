using AIShikikan.Core.Services.Tools;

namespace AIShikikan.Core.Services.Worker;

/*
 * 文件级说明(用块注释而非 /// <summary>: 文件作用域 namespace 之后的第一个文档注释块
 * 会被解析成"紧邻类型"的文档并与类型自己的 <summary> 叠成两段, 见 WorkerMessages.cs 顶部同类说明)。
 *
 * IWorkerToolHost 是 Worker 进程里"协议层"与"工具层"之间唯一的接缝:
 *   WorkerServer(协议层: 认识 JSON-RPC 帧 / 管道 / 取消 / 心跳)
 *       └─依赖─> IWorkerToolHost <─实现─ 工具宿主(认识 ITool / ToolRegistry / AgentExecutionScope)
 * 方向是单向的: 宿主不需要知道 WorkerServer 的存在, 只按接口被调用。
 *
 * ⚠️ 这么切分的三条理由:
 *   1. 可测: WorkerServer 的读循环 / 握手 / 心跳 / 取消全都不需要真实管道 ——
 *      两条 MemoryStream 就能测完整协议; 宿主也能脱离协议单独测"工具集组装"。
 *   2. 不引入 Avalonia、不形成依赖倒挂: Core 里只出现 ITool 与协议 DTO, 不出现任何 GUI 类型。
 *   3. 唯一真源: 工具清单的权威是宿主的 ToolRegistry(它才是今天 AgentToolFactory 那套工具的持有者)。
 *      让 WorkerServer 自己 new 一份工具集等于复制一份注册逻辑, 两边迟早漂移。
 *
 * ⚠️ 本文件里的契约不是"建议", 是 Worker 协议能跑起来的前提:
 *   违反 ExecuteAsync 的异常边界 → 父进程只看到一句"执行失败", 丢掉工具自己的文案与卡片数据;
 *   违反输出顺序 → 工具卡片少结尾几行且无任何报错(详见 ExecuteAsync 的 remarks)。
 */

/// <summary>Worker 侧的工具宿主: 把协议帧翻译成本进程的 ITool 调用。
/// 刻意与传输层解耦 —— 这样 WorkerServer 可以在没有真实管道的环境里被测试。</summary>
/// <remarks>
/// <para><b>⚠️ 握手是硬前置, 宿主在 <see cref="BindHello"/> 被调用前处于「未绑定」状态</b>。
/// <c>WorkerServer</c> 保证任何 <c>worker/tools/call</c> 之前一定先成功握手(版本不符会当场终止进程),
/// 所以实现方可以放心把 <see cref="BindHello"/> 里拿到的东西当成「此后一直有效」的常量使用 ——
/// 但<b>不能</b>反过来依赖这个顺序去省掉防御: 一旦将来放开某个方法的前置条件
/// (例如允许 <c>tools/list</c> 免握手), 「委托全为 null」的宿主会**静默降级**而不是报错:
/// <c>AgentExecutionScope</c> 的委托全 null ⇒ Plan 授权恒 false、roster 恒空表,
/// 现场只表现为「AI 突然不再委派子代理」, 无异常、无日志。</para>
///
/// <para><b>最容易写错的一条是输出顺序</b>(见 <see cref="ExecuteAsync"/> 的 remarks):
/// Worker 侧的工具走 <c>ToolContext.OnToolOutput</c>, 而现有实现(AgentToolFactory 对子代理工具用
/// <c>new Progress&lt;string&gt;(ctx.OnToolOutput)</c>)是 <b>fire-and-forget</b> 的 ——
/// <c>Report()</c> 只入队, 真正投递发生在工具返回<b>之后</b>的线程池上。
/// 所以宿主<b>不能</b>把 <c>OnToolOutput</c> 直连管道写: 那样输出行与最终响应会竞争,
/// 父进程侧 WorkerProxyTool 的 <c>using var outputSub</c> 会在收到响应时退订, 末几行被丢弃。
/// 正确接法是把 <c>OnToolOutput</c> 接到 <see cref="WorkerServer.EmitToolOutput"/>:
/// 它只做入队, 排空以及「响应排在全部输出之后」的保证都在 <c>WorkerServer</c> 里。</para>
///
/// <para><b>异常边界</b>: <see cref="ExecuteAsync"/> 只允许 <see cref="OperationCanceledException"/>
/// 上抛(那是取消语义, 不是失败), 其余一切必须收成 <c>IsError = true</c> 的响应 ——
/// 协议层分不清「工具失败了」与「宿主写错了」, 一律回 <c>-32603</c> 会让父进程只看到一句执行失败,
/// 丢掉 <c>ToolResult</c> 自己的文案与卡片数据。</para>
/// </remarks>
public interface IWorkerToolHost
{
    /// <summary>当前工具集快照(权威清单, 回给 tools/list 与 tools/sync)。</summary>
    /// <remarks>
    /// <para><b>必须是纯内存读, 而且要极快</b>: 它跑在协议读循环线程上(<c>worker/tools/list</c> 同步处理),
    /// 一旦它阻塞, 读循环就停摆 —— <c>notify/cancel</c> 进不来, 「用户点停止」会退化成
    /// 「主循环停了、Worker 里的子代理进程还在跑」。今天它是 <c>ToolRegistry.All</c> 的一次快照拷贝,
    /// 天然满足这一条。</para>
    /// <para>返回<b>快照</b>而非活视图: WorkerServer 会在同一线程上把它逐个转成
    /// <c>WorkerToolDescriptor</c>, 期间主循环可能正在重建工具集(右侧栏开关 / Plan 模式切换),
    /// 返回引用集合会拿到「转了一半」的混合清单。</para>
    /// <para>返回 null 视为「没有工具」, 不抛异常: 宿主刚起来、配置还没读全时不该让整条连接崩掉。</para>
    /// </remarks>
    IReadOnlyList<ITool> SnapshotTools();

    /// <summary>执行一次工具调用。<b>异常必须在这里收成 IsError 响应</b>,
    /// 只有 OperationCanceledException 允许上抛(它由 WorkerServer 转成 cancel 终态)。</summary>
    /// <remarks>
    /// <para><b>实现方要做的三件事</b>:</para>
    /// <list type="number">
    /// <item>按 <see cref="WorkerToolCallRequest.Name"/> 从 <see cref="SnapshotTools"/> 里取工具,
    /// 取不到就回 <c>IsError = true</c> 的响应(文案写明「未知工具 X」), <b>不要抛</b> ——
    /// 抛了会丢掉「名字拼错了 / 版本不同步」这条对排障最有用的事实, 父进程只看到一句执行失败;</item>
    /// <item>用 <see cref="WorkerToolCallRequest.Arguments"/>(原始 JSON 文本)自行
    /// <c>JsonDocument.Parse</c> 后交给 <c>ITool.ExecuteAsync</c>。⚠️ <b>刻意不做 schema 校验</b>:
    /// 与今天 <c>ToolRegistry</c> 的行为一致(收的是裸 <c>JsonElement</c>, 各工具自己处理),
    /// 在父侧或子侧「顺手加一层校验」都会引入另一侧不存在的行为差异;</item>
    /// <item>构造 <c>ToolContext</c>: <c>WorkspaceRoot</c> 取 <c>hello</c> 绑定的
    /// <b>WorkspaceRoot</b>(不是 WorkDir —— 路径沙箱的根是工作树根, 见 ToolPathSanitizer),
    /// <c>IsPlanMode</c>/<c>ProviderId</c>/<c>Model</c>/<c>SessionId</c> 取<b>本次调用</b>的值
    /// (它们每回合可变, 不能用 <c>hello</c> 时的快照), <c>RosterEntries</c> 与
    /// <c>CommanderPersonaText</c> 取 <c>hello</c> 的快照。</item>
    /// </list>
    /// <para><b>⚠️ 工具执行时长没有上限</b>(<see cref="WorkerProtocol.ToolCallTimeout"/> 刻意无限,
    /// 子代理合法跑 30 分钟)。宿主不要自己加一个更短的闸: 那只会把「合法的长任务」报成失败,
    /// 而父进程随后看到的还只是一个没有信息量的错误。取消是唯一的停止手段。</para>
    /// <para><b>⚠️ 输出顺序(最容易写错的一条)</b>: 必须把 <c>ToolContext.OnToolOutput</c> 接到
    /// <see cref="WorkerServer.EmitToolOutput"/>, <b>不要</b>直连管道写。
    /// 协议层的隐含契约是「该调用的<b>全部</b> <c>notify/toolOutput</c> 排在它的
    /// <c>tools/call</c> 响应<b>之前</b>」, 而父进程侧 WorkerProxyTool 用
    /// <c>using var outputSub = SubscribeOutput(callId, ...)</c> 订阅, <b>收到响应即退订</b>;
    /// 输出行若与响应竞争, 尾部若干行会落在退订之后被丢弃 —— 症状是「工具卡片少了结尾几行」,
    /// 且没有任何报错。</para>
    /// <para><b>⚠️ 但也别把输出「攒到调用结束再一次性发」</b>: 那是另一个方向的错 ——
    /// 实时输出的全部价值就在于工具卡在跑的过程中就在滚字, 攒到结束等于这个功能白做。</para>
    /// </remarks>
    Task<WorkerToolCallResponse> ExecuteAsync(WorkerToolCallRequest request, CancellationToken ct);

    /// <summary>握手时把会话上下文交给宿主(供它构造 AgentExecutionScope 等)。
    /// 必须在任何工具执行之前调用, 且只调用一次。</summary>
    /// <remarks>
    /// <para><b>一次性且不可撤销</b>: WorkerServer 只在 <c>worker/hello</c> 成功时调用一次,
    /// 第二次 <c>hello</c> 会被拒绝。原因见 <see cref="WorkerHelloRequest.RosterEntries"/> 的说明:
    /// 快照一旦序列化过, 会话级配置的后续变更<b>不会</b>自动传播到已启动的 Worker ——
    /// 当前协议没有承载 roster 热更新的通道(运行态开关走 <c>worker/tools/sync</c>, roster 本身不在其中)。</para>
    /// <para><b>应该在这里完成全部启动期装载</b>(读 <c>sessions/{id}/roster.json</c>、算好路径沙箱的根、
    /// 建好 <c>AgentExecutionScope</c> 的委托)。之后每个请求就只剩「给参数、拿结果」这一件事,
    /// 没有隐式的第二阶段初始化 —— 隐式初始化会和并发 <c>tools/call</c> 形成竞态,
    /// 而且日志里看不出因果顺序。</para>
    /// <para><b>抛异常 = 握手失败</b>: WorkerServer 会回 <c>Ok = false</c> 并终止进程。
    /// 宿主若遇到「可以降级继续」的状况, 应自己在方法内降级并只记日志, 而不是把启动失败往上抛 ——
    /// 降级的判据是「工具还能不能用」, 而不是「一切是否完美」。</para>
    /// </remarks>
    void BindHello(WorkerHelloRequest request);
}

/// <summary><see cref="IWorkerToolHost"/> 的<b>可选</b>扩展能力: 接受 <c>worker/tools/sync</c> 的全量工具集重配置。</summary>
/// <remarks>
/// <para><b>为什么是可选能力而不是 <see cref="IWorkerToolHost"/> 的成员</b>:
/// <c>worker/tools/sync</c> 承载的是「右侧栏子代理工具可见性」与「Plan 模式授权过滤」这两个<b>运行态开关</b>
/// (对应主进程侧 <c>CommanderRuntime.SetSubagentToolsVisible</c> 与 <c>SetPlanMode</c>)。
/// 只有真正持有 <c>ToolRegistry</c> 的宿主才有能力把它落地, 而只想「列举清单」的宿主不需要;
/// 做成必需成员会迫使所有实现方都写一个空方法。</para>
/// <para><b>宿主未实现时的行为</b>: WorkerServer 回 <c>Ok = true</c>(帧级契约成立: 收到了、解析了、没出错)
/// 并留一条 Warn 日志。<b>刻意不</b>回 <c>Ok = false</c> —— 那会被父进程当成「Worker 不可用」而禁用工具集,
/// 症状是「AI 突然少了所有能力」, 比「同步没生效」严重得多; 后者至少还有一条日志可查。</para>
/// <para>⚠️ <b>已知缺口</b>: 未实现时, 已启动 Worker 的工具集<b>不会</b>跟随右侧栏开关与 Plan 模式变化
/// (同一缺口在 <see cref="WorkerHelloRequest.RosterEntries"/> 的说明里也提到过:
/// 采纳新开关需要重启 Worker 才生效)。</para>
/// </remarks>
public interface IWorkerToolHostSync
{
    /// <summary>按 <paramref name="request"/> 全量替换 Worker 侧的工具集(不是补丁)。</summary>
    /// <remarks>
    /// 语义必须是<b>全量替换</b>: 那两条既有实现本来就是「注销一批 + 重建一批」,
    /// 增量协议要额外维护一个差量状态机, 而一旦某次差量丢失, 工具集就永久错位且无自愈路径。
    /// 父进程随后会用一次 <c>worker/tools/list</c> 拉全量快照刷新自己的 LLM 工具列表,
    /// 所以本方法<b>不需要</b>把新清单回给调用方。
    /// </remarks>
    void ApplyToolsSync(WorkerToolsSyncRequest request);
}