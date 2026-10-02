using AIShikikan.Core.Models;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Tools;

namespace AIShikikan.Core.Services.Worker;

/*
 * Worker 协议的消息 DTO: 全部 public sealed class + 可读写属性(源生成序列化最省事),
 * 属性名 camelCase(由 AppJsonContext 的 JsonSourceGenerationOptions 决定)。
 * 方法名/版本/超时/跨进程身份键见 WorkerProtocol.cs。
 *
 * ⚠ 本文件用块注释而不是文档注释(/// <summary>)承载文件级说明: 文件作用域 namespace 之后的
 * 第一个文档注释块会被解析成"紧邻类型"的文档, 挂到 WorkerHelloRequest 头上,
 * 与它自己的 <summary> 叠成两段 —— 这类错位不报错, 只是文档悄悄挂错地方。
 * (同理: 块注释里绝不能出现字面的注释结束标记, 那会提前闭合整段说明。)
 *
 * ── 三条不可违反的编帧纪律(违反任何一条都是静默错误, 不会编译失败) ──
 *
 * 1. 一行一帧, 帧内不得有裸换行。
 *    本协议里至少三个字段的值必然是多行文本:
 *      - WorkerToolDescriptor.ParametersJson —— 工具 schema 来自源码里的多行原始字符串字面量;
 *      - WorkerToolCallRequest.Arguments      —— JsonElement.GetRawText() 原样回吐;
 *      - WorkerToolCallResponse.DetailJson   —— AppJsonContext 开了 WriteIndented。
 *    正确做法是作为 JSON 字符串字段嵌套 —— STJ 会把裸换行转义成 \n, 外层帧因此仍是一行。
 *    绝不能把多行 JSON 直接拼进帧里(会被读行端切成两半, 表现为"偶发 JsonException",
 *    且因为只在参数恰好多行时才发生, 极难复现)。
 *
 * 2. 只走源生成上下文。
 *    必须 JsonSerializer.Serialize(x, AppJsonContext.Default.Xxx),
 *    禁止 Serialize(obj) / SerializeToElement(obj) 这类不带 JsonTypeInfo 的调用 ——
 *    AOT 下会抛 InvalidOperationException, 而仓库 AOT 兼容性目前没有 CI 门禁
 *    (见 AGENTS.md 技术债 #1/#2), 只在真实发布时才暴露。
 *
 * 3. 枚举当数字。
 *    AppJsonContext 未开 UseStringEnumConverter, 跨进程出现的枚举
 *    (当前只有 Assignment.Status)以数字落盘。两侧必须同版本,
 *    由 WorkerProtocol.ProtocolVersion 在握手时校验。
 *
 * ⚠ 本文件不定义 JSON-RPC 的信封(jsonrpc/id/method/params/result/error):
 * 那些字段是通用的, 与 McpClientBase 手写的 stdio JSON-RPC 同构, 由传输层负责拼装/拆解;
 * 本文件只定义 params/result 的载荷。传输实现不在本文件职责内。
 */

/// <summary>握手请求(父 → 子, <c>worker/hello</c>)。父进程启动 Worker 后<b>必须</b>先发这一条。</summary>
/// <remarks>
/// 放在 <c>hello</c> 而不是首个 <c>tools/call</c> 里, 是为了让 Worker 侧<b>启动即完成全部状态装载</b>
/// (人格、roster、目录), 之后每个请求都是纯函数式的"给参数、拿结果", 不再有隐式的第二阶段初始化 ——
/// 隐式初始化会让并发 <c>tools/call</c> 与初始化产生竞态, 且很难在日志里看出因果。
/// </remarks>
public sealed class WorkerHelloRequest
{
    /// <summary>协议版本(必须等于 <see cref="WorkerProtocol.ProtocolVersion"/>)。不符则父进程终止 Worker。</summary>
    public int ProtocolVersion { get; set; }

    /// <summary>宿主会话 Id。</summary>
    /// <remarks>
    /// <b>⚠️ 必须与父进程算 <see cref="WorkerProtocol.MakeWorkerKey"/> 时传入的 sessionId 逐字相同</b>
    /// —— 它是 Worker 路由与缓存键的一半, 不一致会让父进程认不出自己派生的实例。
    /// Worker 侧用它定位 <c>sessions/{id}/roster.json</c>。
    /// </remarks>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>本会话的工作目录(用户/引擎选定的那一层, 可能是 worktree 的子目录)。</summary>
    /// <remarks>
    /// 工具的相对路径解析、git 命令的 <c>-C</c> 都以它为准。它<b>必须与父进程算 Worker key 时用的目录
    /// 指向同一目录</b>; 不要求逐字相同 —— <see cref="WorkerProtocol.MakeWorkerKey"/> 内部会先
    /// <c>Normalize</c>, 两端各自带不带尾斜杠都能算出同一个 key。
    /// 这与 <c>GitCheckpointStore.GetRepoHash</c> 要求"逐字相同"是<b>不同</b>的契约, 别混用。
    /// </remarks>
    public string WorkDir { get; set; } = string.Empty;

    /// <summary>已解析的工作树根(<c>IWorkspaceResolver.ResolveWorkTreeRoot</c> 的结果)。</summary>
    /// <remarks>
    /// 与 <see cref="WorkDir"/> 分开传: 执行权隔离与检查点目录都按<b>工作树根</b>建键, 而文件工具
    /// 按 <b>WorkDir</b> 解析相对路径。非 git 目录下两者相同(退化为该目录自身)。
    /// </remarks>
    public string WorkspaceRoot { get; set; } = string.Empty;

    /// <summary>指挥官人格全文快照(注入子代理 prompt 用)。</summary>
    /// <remarks>
    /// 语义与 <c>ToolContext.CommanderPersonaText</c> 完全一致: null/空串 = <b>本回合无指挥官人格</b>,
    /// 此时子代理<b>不</b>附加人格前缀, 而<b>不是</b>回退去读全局
    /// <c>CommanderRuntime.CurrentPersonaText</c> —— 回退会把"切人格串味到别的会话"这个缺陷原样保留。
    /// 判定用 <c>string.IsNullOrWhiteSpace</c>, 别只判 null。
    /// </remarks>
    public string? CommanderPersonaText { get; set; }

    /// <summary>会话级子代理 roster 快照(Enabled / CompactEnabled / UseInPlanMode)。</summary>
    /// <remarks>
    /// <para><b>⚠️ null 与空表语义截然不同, 判据只能判 null 与否, 绝不能用 <c>Count &gt; 0</c></b>:</para>
    /// <list type="bullet">
    /// <item><c>null</c> = <b>无限制</b>(roster.json 从未下发, 即全新会话; 回退为"列出全部已定义子代理");</item>
    /// <item>非 null(含<b>空表</b>) = <b>以该表为准</b>, 空表即「用户已清空全部子代理」。</item>
    /// </list>
    /// <para>历史上用 <c>Count &gt; 0</c> 一并表达这两种含义, 结果
    /// <c>SetSubagentToolsVisible(false)</c> 传的空表落进「列出全部 Agent」分支:
    /// <b>工具已注销而提示词仍在广告 <c>run_&lt;id&gt;</c></b>, AI 持续调用不存在的工具。
    /// 与 <c>RosterConfigService.LoadEntriesOrNull</c> 的三态语义同构, 默认值必须留 null。</para>
    /// <para><b>⚠️ 是引用快照, 元素可变</b>: <c>AgentRosterEntry</c> 实现了
    /// <c>INotifyPropertyChanged</c>(直接绑在右侧栏 UI 上), <c>IReadOnlyList</c> 只挡"替换元素",
    /// 挡不住"改元素字段" —— 构造 <c>hello</c> 期间用户改开关, 序列化出来的值就跟着变。
    /// 需要严格时点一致请自行深拷贝。</para>
    /// <para><b>⚠️ 跨进程后即失联</b>: 序列化后 Worker 拿到的是<b>独立副本</b>,
    /// 会话级配置的后续变更(Plan 授权/压缩开关)<b>不会</b>自动传播到已启动的 Worker
    /// —— 当前协议没有承载 roster 热更新的通道(见 <c>WorkerToolsSyncRequest</c> 的说明)。
    /// 采纳新开关后需要一次重启 Worker 才生效, 这点必须在产品行为上如实反映。</para>
    /// </remarks>
    public IReadOnlyList<AgentRosterEntry>? RosterEntries { get; set; }

    /// <summary>主对话是否处于 Plan 模式(决定子代理是否追加 <c>plan_args</c>, 以及授权过滤是否生效)。</summary>
    public bool IsPlanMode { get; set; }

    /// <summary>父进程 Id。</summary>
    /// <remarks>
    /// Worker 用它做<b>孤儿自检</b>: 父进程异常退出(被 kill / 崩溃)时管道会一起断开, 但断开与
    /// "父进程只是暂时没发消息"难以区分, 用 pid 探测可以更快、更确定地自我了断,
    /// 避免留下孤儿进程占着工作目录的 git 写锁。Unix 下可直接发信号 0 探测存活。
    /// </remarks>
    public int ParentPid { get; set; }
}

/// <summary>握手响应(子 → 父)。<see cref="Ok"/> 为 false 时父进程<b>必须</b>终止 Worker 并把 <see cref="Error"/> 记入日志。</summary>
public sealed class WorkerHelloResponse
{
    /// <summary>是否握手成功。</summary>
    public bool Ok { get; set; }

    /// <summary>Worker 自身进程 Id(日志与 pid 兜底用)。</summary>
    public int WorkerPid { get; set; }

    /// <summary>Worker 侧读到的协议版本; 回显便于双方定位"版本不一致"到底是哪边错。</summary>
    public int ProtocolVersion { get; set; }

    /// <summary>失败原因(成功时为 null)。</summary>
    public string? Error { get; set; }
}

/// <summary>单个工具的声明(子 → 父)。工具<b>实现</b>留在 Worker 侧, 父进程只拿声明去喂 LLM。</summary>
public sealed class WorkerToolDescriptor
{
    /// <summary>工具名, 如 <c>read_file</c> / <c>git_commit</c> / <c>run_&lt;agentId&gt;</c>(LLM 实际调用用的名字)。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>给 LLM 看的工具说明(与 <c>ITool.Description</c> 逐字一致)。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>参数的 JSON Schema, 取自 <c>ITool.Parameters</c>(<c>JsonElement</c>)的 <c>GetRawText()</c>。</summary>
    /// <remarks>
    /// <para><b>为什么用字符串而不是 <c>JsonElement</c></b>: <c>JsonElement</c> 背后是
    /// <c>JsonDocument</c>, 跨进程需要先重新解析, 而且 <c>JsonElement</c> 本身在源生成序列化里要多一层
    /// <c>[JsonSerializable]</c> 兜底; 字符串是零歧义的跨进程形态。取原始文本还顺带保住了 schema 的
    /// 原始排版(源码里是多行原始字符串字面量)。</para>
    /// <para><b>⚠️ 该值必然含裸换行, 必须作为 JSON 字符串字段嵌套</b>(见本文件顶部的编帧纪律第 1 条)。</para>
    /// <para><b>⚠️ schema 只是给 LLM 看的, 不参与参数校验</b>: 真正被解析的是
    /// <c>WorkerToolCallRequest.Arguments</c>, 且解析结果交给各工具自行处理
    /// (与今天 <c>ToolRegistry</c> 的行为一致 —— <c>ITool.ExecuteAsync</c> 收的是裸
    /// <c>JsonElement</c>, 一贯没有 schema 强校验)。所以别在父进程侧"顺手加一层校验",
    /// 那会引入 Worker 侧不存在的行为差异。</para>
    /// </remarks>
    public string ParametersJson { get; set; } = string.Empty;

    /// <summary>是否需要用户批准(与 <c>ITool.RequiresApproval</c> 一致)。父进程据此弹审批卡片。</summary>
    public bool RequiresApproval { get; set; }

    /// <summary>工具类别(自由字符串, 父进程用于分类展示/路由)。</summary>
    /// <remarks>
    /// 约定取值: <c>"file"</c> / <c>"git"</c> / <c>"subagent"</c>。
    /// <b>刻意不做成枚举</b>: 本协议不引入新的跨进程序列化枚举(枚举在 <c>AppJsonContext</c> 下是数字,
    /// 加成员就要抬 <see cref="WorkerProtocol.ProtocolVersion"/>), 而且它是纯展示用途 ——
    /// 父进程<b>必须容忍未知取值</b>(遇到没见过的 Kind 只降级为通用分类, 不要拒帧/断连)。
    /// </remarks>
    public string Kind { get; set; } = string.Empty;

    /// <summary>该工具是否会<b>写</b> git(提交/暂存/建检查点), 用于父进程侧串行化 git 写。</summary>
    /// <remarks>
    /// 与 <c>WorkspaceExecutionCoordinator.WaitGitWriteAsync</c> 的 git 写门配套。
    /// ⚠️ 该协调器 API 目前<b>无生产调用方</b>(AGENTS.md 技术债 #14), 所以本字段在接线前是纯提示:
    /// 漏标/错标<b>不会</b>造成比现状更差的结果(今天本来就没门)。接线时它必须与
    /// <c>TryEnterGitWrite</c> 的实际保护范围一致, 否则会出现"工具以为自己被串行化了"的假安全感。
    /// </remarks>
    public bool RequiresGitWrite { get; set; }
}

/// <summary>工具清单响应(子 → 父, <c>worker/tools/list</c>)。</summary>
public sealed class WorkerToolsResponse
{
    /// <summary>当前 Worker 已注册的全部工具。</summary>
    /// <remarks>
    /// 语义是<b>全量快照</b>而非增量: 父进程每次都应整份替换自己的 LLM 工具列表,
    /// 不做 diff 合并 —— 增量合并在"子代理工具动态增删"时必然漂移。
    /// </remarks>
    public IReadOnlyList<WorkerToolDescriptor> Tools { get; set; } = [];
}

/// <summary>工具集重配置请求(父 → 子, <c>worker/tools/sync</c>)。<b>全量替换</b>语义, 不是补丁。</summary>
/// <remarks>
/// 承载"右侧栏子代理工具可见性"与"Plan 模式授权过滤"这类<b>运行时开关</b>。
/// 为什么必须是全量替换: 这两处既有实现(<c>CommanderRuntime.SetSubagentToolsVisible</c> /
/// <c>SetPlanMode</c>)本来就是"注销一批 + 重建一批", 增量协议要额外维护差量状态机,
/// 而一旦某次差量丢失, 工具集就永久错位且无自愈路径。
/// </remarks>
public sealed class WorkerToolsSyncRequest
{
    /// <summary>是否注册固定工具(<c>read_file</c>/<c>glob</c>/<c>grep</c>/<c>list_directory</c> + 全部 <c>git_*</c> + <c>ask_user</c>)。</summary>
    /// <remarks>注意 <c>ask_user</c> 归在固定工具里: <see cref="WorkerAskUserRequest"/> 目前<b>未启用</b>,
    /// 父进程侧执行它不需要 Worker 往返。</remarks>
    public bool CoreTools { get; set; }

    /// <summary>子代理工具是否可见(对应右侧栏开关; false 时注销 <c>run_&lt;agent&gt;</c>/<c>assign_task</c>/<c>run_subagents</c>
    /// 并清空 roster 注入)。</summary>
    public bool SubagentVisible { get; set; }

    /// <summary>主对话当前是否处于 Plan 模式。true 时子代理工具要按 <see cref="AllowedAgentIds"/> 过滤。</summary>
    public bool PlanMode { get; set; }

    /// <summary>Plan 模式下的<b>授权 Agent 白名单</b>。空表 = <b>不限制</b>(不按 Agent 过滤), 而非"无人授权"。</summary>
    /// <remarks>
    /// <para><b>空值语义: 空 = 不限制(null 与空表等同)</b>。理由:</para>
    /// <list type="bullet">
    /// <item><b>失败方向要选"宽松"而非"全禁"</b>: 本字段是<b>过滤器</b>而非 roster 本体。
    /// 若把空表解释成"无人授权", 那么"父进程还没把 roster 算好就发了 sync"这一正常时序就会
    /// 让指挥官<b>彻底丧失委派能力</b>, 而且现场只表现为"AI 突然不再调用子代理"—— 无异常、无日志。</item>
    /// <item><b>与 <c>WorkerHelloRequest.RosterEntries</c> 的 null/空表三分语义不同源</b>:
    /// roster 的区分有实打实的落盘依据(<c>roster.json</c> 存不存在), 且历史上正是合并两者才出了
    /// "工具已注销、提示词仍广告"的 bug; <c>AllowedAgentIds</c> 背后<b>没有</b>这种磁盘状态,
    /// 它纯粹是父进程每回合算出来的一个派生集合, 没有"文件不存在"那种第三态需要表达。
    /// 强行造一个 null 态只会增加两侧心智负担, 却不换来任何信息。</item>
    /// <item><b>"Plan 模式下无人授权"另有专用表达</b>: 零授权时代码路径本就把
    /// <c>assign_task</c>/<c>run_subagents</c> 一并<b>注销</b>, 由
    /// <see cref="SubagentVisible"/>/工具注册状态表达即可, 不需要靠空白名单表达。</item>
    /// </list>
    /// <para>⚠️ <b>但 Plan 模式下有一处"合法"的 <c>Count &gt; 0</c> 判据, 别与上面的坑混淆</b>:
    /// <see cref="PlanMode"/> 为 true 时本字段是真白名单, 空表 = 零授权, 此时
    /// <c>assign_task</c>/<c>run_subagents</c> <b>不得</b>注册(照搬
    /// <c>CommanderRuntime.SetPlanMode</c> 的三层过滤: 工具集 / roster 注入 / 执行兜底)。
    /// 这处用 <c>Count &gt; 0</c> 是业务规则,与"用 <c>Count &gt; 0</c> 代替 null 判据"那个
    /// <b>语义 bug</b> 是两回事 —— 判别标准是: 有没有第三态需要表达。</para>
    /// <para>判定请写 <c>AllowedAgentIds is null || AllowedAgentIds.Count == 0</c>(统一当不限制),
    /// 不要只判 null, 也不要依赖调用方总是发非 null。</para>
    /// </remarks>
    public IReadOnlyList<string> AllowedAgentIds { get; set; } = [];
}

/// <summary>工具执行请求(父 → 子, <c>worker/tools/call</c>)。</summary>
public sealed class WorkerToolCallRequest
{
    /// <summary>本次调用的关联 Id。由<b>父进程</b>生成, 在该 Worker 生命周期内唯一且不透明。</summary>
    /// <remarks>
    /// 关联三处东西: 响应(<c>WorkerToolCallResponse.CallId</c>)、流式输出
    /// (<c>WorkerToolOutputNotification.CallId</c>)、取消(<c>WorkerCancelNotification.CallId</c>)。
    /// <b>必须由父进程生成</b> —— Worker 侧只能看到顺序, 无法保证并发调用的 Id 不与父进程的簿记对得上;
    /// 用单调递增序号即可, 无需随机。
    /// </remarks>
    public string CallId { get; set; } = string.Empty;

    /// <summary>要执行的工具名(对应 <see cref="WorkerToolDescriptor.Name"/>)。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>参数 JSON(<c>JsonElement.GetRawText()</c> 的原始文本)。空串视为 <c>{}</c>。</summary>
    /// <remarks>
    /// <b>⚠️ 必然可能含裸换行, 必须作为 JSON 字符串字段嵌套</b>(见本文件顶部编帧纪律第 1 条)。
    /// Worker 侧 <c>JsonDocument.Parse</c> 后交给 <c>ITool.ExecuteAsync</c>。
    /// </remarks>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>本回合的 Plan 模式标志(覆盖 <c>hello</c> 时的初值, 因为模式可能在会话中途切换)。</summary>
    public bool IsPlanMode { get; set; }

    /// <summary>发起本调用的会话 Id。用于按会话读 roster 配置, 为空时由 Worker 侧回退。</summary>
    /// <remarks>
    /// <b>绝不能读"当前活动会话"</b>: 后台会话同时跑时两者不是同一个, 会导致会话级配置串味
    /// (Plan 授权 / 输出压缩开关判错)。
    /// </remarks>
    public string? SessionId { get; set; }

    /// <summary>本回合生效的 Provider Id。null = 跟随全局活动 Provider(与 <c>ToolContext.ProviderId</c> 同语义)。</summary>
    public string? ProviderId { get; set; }

    /// <summary>本回合实际生效的模型名。用于子代理输出压缩, 避免与主对话跑偏 Provider/模型。</summary>
    /// <remarks>
    /// 这两个字段是<b>为修技术债 #16 而存在的通道</b>(<c>CompactIfNeededAsync</c> 已支持接收
    /// provider/model, 但调用点尚未转发, 压缩仍走全局默认)。Worker 侧接线时<b>必须</b>转发它们,
    /// 否则搬进程这件事白搬: 压缩跑偏 Provider 的问题会原样跨进程延续。
    /// </remarks>
    public string? Model { get; set; }
}

/// <summary>工具执行响应(子 → 父, <c>worker/tools/call</c> 的 result)。</summary>
/// <remarks>
/// <b>注意这是"执行完成"的回执, 不是"开始执行"的回执</b>: 子代理工具合法耗时可达数十分钟,
/// 父进程不得在等待它期间因"没有立刻收到响应"而误判超时(见 <see cref="WorkerProtocol.ToolCallTimeout"/>)。
/// </remarks>
public sealed class WorkerToolCallResponse
{
    /// <summary>给 LLM 看的文本结果(等价 <c>ToolResult.Content</c>)。空串合法。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>是否执行失败(等价 <c>ToolResult.IsError</c>)。为 true 时 <see cref="Content"/> 是错误描述。</summary>
    public bool IsError { get; set; }

    /// <summary>关联检查点步骤 Id(等价 <c>ToolResult.StepId</c>)。当前只有 <c>git_create_checkpoint</c> 会赋值。</summary>
    /// <remarks>
    /// 子代理工具恒为 null: 子代理统一在当前分支就地工作, 回滚入口是"每条用户消息"的检查点。
    /// (另: <c>Assignment.StepId</c> 已被标记 <c>[Obsolete]</c>, 赋值会有 CS0618 告警。)
    /// </remarks>
    public string? StepId { get; set; }

    /// <summary>卡片详情的类型判别符(如 <c>"fileRead"</c>/<c>"dirList"</c>/<c>"glob"</c>/<c>"grep"</c>/<c>"subagents"</c>/<c>"checkpoint"</c>), 无详情时为 null。</summary>
    /// <remarks>
    /// <para><b>为什么不直接传 <c>ToolResult.Detail</c></b>: 它是
    /// <see cref="ToolCardDetail"/>(<c>[JsonPolymorphic]</c> 多态基类)。
    /// 多态序列化依赖<b>声明类型</b>上的 <c>[JsonDerivedType]</c> 清单, 而本协议要按
    /// <b>具体类型</b>把载荷切出来再塞进字符串字段 —— 走多态路径会额外引入判别符前缀与
    /// <c>UnknownDerivedTypeHandling</c> 的静默降级陷阱(清单漏登记时字段被悄悄丢掉、读回时抛
    /// <c>JsonException</c> 拖垮整个会话加载)。判别符 + 独立 JSON 串是绕开这一切的最短路径。</para>
    /// <para><b>取值必须与 <see cref="ToolCardDetail"/> 上的 <c>[JsonDerivedType]</c> 清单一致</b>
    /// —— 那里是唯一的真源, 不要在本文件复制一份常量表(两处必然漂移; 且已落盘的历史会话里
    /// <c>ToolSegment.Detail.$type</c> 就是这些字符串, 改名会让历史会话读不出来)。</para>
    /// <para><b>不变量</b>: <see cref="DetailType"/> 为 null ⇔ <see cref="DetailJson"/> 为 null/空 ⇔
    /// 原 <c>ToolResult.Detail</c> 为 null。父进程看到 null 就按"无卡片详情"处理。</para>
    /// </remarks>
    public string? DetailType { get; set; }

    /// <summary>按<b>具体类型</b>序列化出的卡片详情 JSON(等价 <c>ToolResult.Detail</c>)。无详情时为 null。</summary>
    /// <remarks>
    /// <para>Worker 侧写法(必须是<b>具体类型</b>的 <c>JsonTypeInfo</c>, 不能用基类的):
    /// <c>JsonSerializer.Serialize(detail, AppJsonContext.Default.FileReadDetail)</c>。
    /// 这正是 <c>AppJsonContext</c> 里逐个登记 <c>FileReadDetail</c>/<c>GlobDetail</c> 等 12 个派生类型的原因
    /// —— 只登记基类拿不到具体类型的元数据, 会抛 "no metadata for type"。</para>
    /// <para><b>⚠️ 本字段必然是多行文本</b>(<c>AppJsonContext</c> 开了 <c>WriteIndented</c>),
    /// <b>必须作为 JSON 字符串字段嵌套</b>, 由 STJ 转义换行(见编帧纪律第 1 条)。也正因如此,
    /// 它的体积会比紧凑 JSON 大 2~3 倍 —— 大文件详情尤其明显, 属可接受的代价。</para>
    /// <para><b>⚠️ 父进程侧解码必须"先独立解析、失败即降级"</b>: 解析成功后再赋给
    /// <c>ToolSegment.Detail</c>; 解析失败<b>只</b>降级为通用文本卡片 + 一条日志,
    /// 绝不能让坏数据流进 <c>ChatSession</c> —— <c>ToolCardDetail</c> 是 abstract,
    /// 一帧坏 JSON 会让整份会话反序列化失败, 进而 <c>EnsureLoaded</c> 回退 .bak、再失败就
    /// <c>IsCorrupted</c> 锁死写回。即"<b>一次工具调用失败 = 用户整个会话历史报废</b>"。</para>
    /// </remarks>
    public string? DetailJson { get; set; }
}

/// <summary>流式输出通知(子 → 父, <c>notify/toolOutput</c>)。子代理/长工具的实时进度行。</summary>
public sealed class WorkerToolOutputNotification
{
    /// <summary>关联的在途调用(对应 <see cref="WorkerToolCallRequest.CallId"/>)。</summary>
    /// <remarks>
    /// <b>父进程必须按 CallId 缓冲后再喂给 UI</b>, 且<b>不得假设"收到最终响应后就不会再收到该 CallId 的通知"</b> ——
    /// 通知由工具执行路径上的回调发出, 与最终响应之间存在交错的可能。
    /// </remarks>
    public string CallId { get; set; } = string.Empty;

    /// <summary>一行输出文本。⚠️ 可能含换行符, <b>必须作为 JSON 字符串字段嵌套</b>。</summary>
    /// <remarks>
    /// 长度按 <see cref="WorkerProtocol.MaxLineChars"/> 封顶(8KB), 与
    /// <c>CliAgentRunner</c> 上报子进程 stdout 单行时的处理一致。
    /// </remarks>
    public string Line { get; set; } = string.Empty;
}

/// <summary>心跳通知(子 → 父, <c>notify/heartbeat</c>)。</summary>
/// <remarks>
/// 用途是<b>让父进程知道 Worker 还活着</b>(心跳超时 <see cref="WorkerProtocol.HeartbeatTimeout"/> = 60s,
/// 远大于 5s 的发送间隔)。⚠️ 它<b>不能</b>用来判断"Worker 卡在某个工具上" ——
/// 心跳由独立定时器发, 与工具执行无关; 判断单个调用是否卡住靠取消通道。
/// </remarks>
public sealed class WorkerHeartbeatNotification
{
    /// <summary><c>Environment.TickCount64</c> 快照(毫秒)。仅用于测 RTT 与排查时序。</summary>
    /// <remarks>
    /// <b>不是墙上时钟, 也不是时间戳</b>: 单调、自系统启动计时, 跨机器/跨重启<b>无意义</b>,
    /// 严禁落盘或参与任何持久化/排序。
    /// </remarks>
    public long Ticks { get; set; }
}

/// <summary>取消通知(<b>父 → 子</b>, <c>notify/cancel</c>)。</summary>
/// <remarks>
/// ⚠️ 名字是 <c>notify/</c> 前缀(子 → 父 的命名空间)但方向是父 → 子 —— 见
/// <see cref="WorkerProtocol.NotifyCancel"/> 的行内标注。<b>必须</b>保留这处不一致,
/// 改名会牵动所有已按现字符串写好的对端代码。
/// </remarks>
public sealed class WorkerCancelNotification
{
    /// <summary>要取消的调用(<c>CallId</c>)。</summary>
    public string CallId { get; set; } = string.Empty;
}

/// <summary>子代理分派状态通知(子 → 父, <c>notify/assignment</c>)。</summary>
/// <remarks>
/// 让父进程侧据此驱动 <c>AssignmentManager</c> 与右侧栏 UI。
/// ⚠️ <b>这是跨进程序列化通道</b>: 该通知每到一个生命周期变化就发一次, 而
/// <c>Assignment.Status</c> 是 <c>SubagentStatus</c> 枚举 —— 在 <c>AppJsonContext</c> 下
/// <b>以数字落盘</b>。所以它与整个协议共享同一条硬约束:<b>两侧必须同版本</b>
/// (<see cref="WorkerProtocol.ProtocolVersion"/>), 且<b>任何改动 <c>SubagentStatus</c> 成员的动作
/// 都必须抬版本</b>, 否则旧父进程会把 <c>Running</c>(1) 读成别的状态, 表现为分派卡住不消失。
/// </remarks>
public sealed class WorkerAssignmentNotification
{
    /// <summary>分派记录快照(整对象替换, 不做字段级合并)。</summary>
    /// <remarks>
    /// 复用引擎既有的 <see cref="Assignment"/>(已登记在 <c>AppJsonContext</c>), 不另立一套传输模型 ——
    /// 两套模型必然会各自漂移, 而分派记录还要继续持久化到 <c>assignments/*.json</c>。
    /// 父进程侧同样按"整份替换"处理: 以 <c>AssignmentId</c> 为键 upsert。
    /// </remarks>
    public Assignment Assignment { get; set; } = new();
}

/// <summary>向用户反问的请求(子 → 父, <c>request/askUser</c>)。<b>当前方案未启用</b>, 仅保留协议位。</summary>
/// <remarks>
/// <see cref="AIShikikan.Core.Services.Tools.AskUserTool"/> 刻意<b>留在主进程</b>: 它要弹
/// GUI 审批卡片并等用户作答, 那条链路本身就在父进程侧, 绕 Worker 出去只会多一跳。
/// 保留 DTO 与方法名是为了将来(如子代理想反问指挥官)能启用而不必改协议骨架。
///
/// <para><b>⚠️ 若要启用, 必须同时解决超时</b>: 本请求是在一个
/// <see cref="WorkerToolCallRequest"/> <b>在途</b>期间发出的, 而
/// <see cref="WorkerProtocol.ToolCallTimeout"/> 是<b>刻意无限</b>的 ——
/// 用户不答就永远挂着。所以启用方<b>必须</b>自行套
/// <c>AgentEngine.ApprovalTimeout</c>(5 分钟)级别的超时并回一个
/// <see cref="WorkerAskUserResponse"/>(<c>Answered = false</c>), 照搬 <c>ask_user</c> 现有的超时兜底语义。</para>
/// </remarks>
public sealed class WorkerAskUserRequest
{
    /// <summary>关联的在途调用(<c>CallId</c>)。</summary>
    public string CallId { get; set; } = string.Empty;

    /// <summary>要问用户的问题文本。</summary>
    public string Question { get; set; } = string.Empty;
}

/// <summary>反问的答复(父 → 子, <c>request/askUser</c> 的 result)。</summary>
public sealed class WorkerAskUserResponse
{
    /// <summary>用户是否作答。</summary>
    /// <remarks>false = 用户取消或等待超时; <b>此时 <see cref="Answer"/> 必为 null</b>,
    /// 语义与 <c>ToolContext.AskUser</c> 返回 null 一致(工具据此放弃或回退)。</remarks>
    public bool Answered { get; set; }

    /// <summary>用户的回答原文。</summary>
    public string? Answer { get; set; }
}

/// <summary>通用成功/失败回执(<c>worker/ping</c> / <c>worker/shutdown</c> / <c>worker/tools/sync</c> 的 result)。</summary>
/// <remarks>
/// 这三个方法没有各自的响应 DTO, 统一用它: 成功只需一个 <c>Ok</c>,
/// 失败只需一个 <c>Error</c> 字符串。刻意<b>不</b>为 sync 定义带工具清单的响应 ——
/// sync 只改 Worker 内部状态, 父进程改自己的 LLM 工具列表应通过<b>随后一次
/// <c>worker/tools/list</c></b> 拉全量快照(见 <see cref="WorkerToolsResponse"/>),
/// 避免"同一份工具清单有两个真源"在两侧分叉。
/// </remarks>
public sealed class WorkerOkResponse
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; set; }

    /// <summary>失败原因(成功时为 null)。</summary>
    public string? Error { get; set; }
}