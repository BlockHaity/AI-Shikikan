# AI-Shikikan Worker 进程架构（决策文档）

> 状态：**已定稿；B0 已在并行分支/工作树中开工**
> 定稿日期：2026-10-02
> 修订记录：**2026-10-03 —— 4.3 的 L6「目录级长空闲超时」语义反转并改为默认启用**（`DirectoryIdleTimeout` 由 `TimeSpan.Zero` 改为 `TimeSpan.FromMinutes(1)`，
> 从"只推迟 L3、且仅作用于已归零的组"改为**独立的槽位级空闲回收**，判据为合取式，见 4.3.1；4.4 / 4.7 / 8.2 与附录 A 同步）。
> 初版 4.3 里"L6 与 L3 互斥、有会话引用则永不因空闲关闭"的结论**已作废**，不要再引用。
> 行号锚点：`git fb24185` + 定稿时的工作树（含**未提交**的 B0 前置解耦改动）
> 适用范围：`src/AIShikikan.Core`、`src/AIShikikan.Gui`、新增 `src/AIShikikan.Worker`、构建与打包链路
> 一句话结论：**把「非 MCP 的工具执行」与「子代理进程执行」搬进一个独立的 `AIShikikan.Worker` 进程；主进程保留 LLM 主循环、审批闸、事件总线、UI 与全部 MCP；两者之间用 stdio 匿名管道 + NDJSON 行协议通信；找不到 Worker 产物时静默降级回单进程内联执行，但降级必须可见。**

> ⚠️ **关于行号的阅读约定（重要）**
>
> 定稿时 `AgentToolFactory.cs` / `ToolFramework.cs` / `CommanderRuntime.cs` / `AppJsonContext.cs` /
> `ToolCardDetail.cs` / `GitCheckpointStore.cs` / `Log.cs` 正在被 B0 改造（未提交），行号会继续漂移。
> **这 7 个文件请按符号名定位**（本文档每个引用都同时给了符号名）；其余文件未被改动，行号可直接使用。

---

## 0. 如何阅读本文档

本文档是**决策归档**，不是实现说明。它假定读者**没有参与过本次讨论**，因此：

- 所有关键结论都附「为什么这么选」与「放弃了什么方案」；
- 所有涉及代码的事实都带 `文件路径:行号`，行号以定稿时的仓库状态为准（`VERSION` = `1.0.0-vibe`，`AIShikikan.slnx` 只含 Core + Gui 两个项目）；
- 与既有文档（`AGENTS.md`）不一致的地方，本文档以**代码为准**，并在 [附录 A](#附录-a核对代码时发现与既有文档不一致之处) 集中列出差异。

### 0.1 代码坐标速查

| 概念 | 位置（行号 → 符号名） |
|---|---|
| 工具接口与上下文 | `Services/Tools/ToolFramework.cs`（`ToolResult`、`ToolContext.Detail`、`ITool`、`ToolRegistry`、`ToolRegistry.ToSpecs`） |
| 工具集装配 | `Services/Runtime/AgentToolFactory.cs`（`CreateCoreTools`、`CreateSubagentTools`） |
| 子代理执行器 | `Services/Runtime/AgentToolFactory.cs`（`AgentExecutor`，B0 后已是 `sealed class`） |
| **工具执行期依赖容器（B0 新增）** | `Services/Runtime/AgentExecutionScope.cs`（`AgentExecutionScope`） |
| 引擎工具调用唯一入口 | `Services/Engine/AgentEngine.cs:868`（`result = await tool.ExecuteAsync(...)`） |
| 审批闸 | `AgentEngine.cs:806-830`；超时常量 `AgentEngine.cs:117`（`ApprovalTimeout`） |
| 引擎事件出口 | `AgentEngine.cs:247`（`Raise`） |
| 事件总线投递规则 | `Services/Session/EngineEventHub.cs:75`（`ShouldDeliver`） |
| MCP 远端工具桥接范式 | `Services/Mcp/McpProxyTool.cs`（`McpProxyTool.ExecuteAsync`） |
| MCP 行协议传输范式 | `Services/Mcp/McpStdioClient.cs`（`_writeLock` / `WriteLineAsync` / `ReadLoopAsync`） |
| 子代理 CLI 进程执行 | `Services/Agents/CliAgentDefinition.cs:73`（`CliAgentRunner`） |
| 分派记录（落盘 + 事件） | `Services/Engine/AssignmentManager.cs`（`Create` / `RunSyncAsync` / `Save`） |
| 检查点落盘 | `Services/Git/GitCheckpointStore.cs`（构造函数 `LoadAll` / `Save`） |
| 原子写 | `Serialization/AtomicFile.cs`（`WriteAllTextCore` 的 `tempPath` / `TryBackupExisting`） |
| AOT 源生成 JSON 上下文 | `Serialization/AppJsonContext.cs`（`AppJsonContext` 的 `[JsonSourceGenerationOptions]`） |
| 工具卡片多态模型 | `Models/ToolCardDetail.cs`（`ToolCardDetail` 的 `[JsonPolymorphic]` / `[JsonDerivedType]`） |
| 工作区键规范化 | `Services/Session/WorkspaceResolver.cs:115`（`GitWorkspaceResolver.Normalize`） |
| 会话运行时注册表 | `Services/Session/SessionRuntimeRegistry.cs`（`AllSessions` / `RemoveSession` / `Dispose`） |
| 启动外观（**Worker 禁止调用**） | `Services/CommanderRuntime.cs`（`Boot`） |
| 日志（进程标签可用） | `Logging/Log.cs:61`（`SetProcessLabel`）/ `:117`（`Log.Warn`）/ `:183`（日志文件打开） |
| `doctor` | `src/AIShikikan.Gui/Program.cs:171`（`RunDoctor`） |
| GUI 手动分派（第四条子代理执行路径） | `src/AIShikikan.Gui/ViewModels/AppShell.cs:59`（`Dispatch`） |

---

## 1. 背景与目标

### 1.1 现状问题

当前所有工具与子代理执行都发生在 GUI 主进程内，带来四类具体问题。

**(a) 子代理 / `grep` 卡死会拖垮整个 GUI。**

`run_<agent>` / `assign_task` / `run_subagents` 是阻塞式工具，`CliAgentRunner` 直接 `await process.WaitForExitAsync(...)`
（`CliAgentDefinition.cs:211`）。子进程卡死时（网络挂起、等凭据 helper、等锁），引擎回合不结束；
用户唯一的手段是「停止」，而停止依赖取消令牌一路传到 `Kill(entireProcessTree: true)`（`CliAgentDefinition.cs:217`）。
一旦这条链上任一环不成立，用户只能杀整个应用，正在写的会话、用量、检查点一并丢。

`grep` 同理：`GrepTool` 全量遍历工作区（`GrepTool.cs:99-103`），只有单文件正则 2 秒超时（`GrepTool.cs:62`），
没有整体上限；一次全仓 grep 在大仓库上可以让 UI 长时间无响应。

**(b) UI 线程同步跑 git 进程（技术债 #3）。**

`GitService` 的公开 API 成对提供同步/异步两版，同步版一律是 `RunAsync(...).GetAwaiter().GetResult()`
（`GitService.cs:14`、`:31`、`:785`）。GUI 侧目前全部调用同步版，即在 UI 线程上阻塞到 git 退出。
`GitService` 类注释已经写明「新代码（尤其 GUI 的 UI 线程）必须用 `*Async`」（`GitService.cs:15`），但现状相反。

一次「发送消息 → 回合结束」的完整路径上，UI 线程同步执行的 git 进程**共 9 个**（逐项核对见下表）：

| 阶段 | 调用点 | 同步 git 调用 | 进程数 |
|---|---|---|---|
| 点发送 | `ChatPageViewModel.cs:1520` | `ResolveContext` → `symbolic-ref --short HEAD` + `rev-parse --verify HEAD` | 2 |
| 点发送 | `ChatPageViewModel.cs:1475` | `GetHeadSha` → `rev-parse HEAD` | 1 |
| 点发送 | `ChatPageViewModel.cs:1494` | `MarkCheckpoint` → `cat-file -t` + `tag -l` + `tag` | 3 |
| 回合结束 | `ChatPageViewModel.cs:580` → `:275`/`:286` | `RefreshWorkspaceContext` → `ResolveContext`(2) + `IsClean`(1) | 3 |
| **合计** | | | **9** |

补充两点，避免误读这个数字：

- `ResolveContext` 是 **2** 个进程而不是 3 个 —— `FindRepositoryRoot` 是纯文件系统向上遍历（`GitService.cs:706-719`），不起 git 进程。
- 同一路径上还有**引擎线程**上的 4 个：`RosterBuilder.BuildGitSection` 每回合调一次
  （`RosterBuilder.cs:186`/`:195`/`:198`，共 4 个只读进程）。它不卡 UI，但同样拖慢回合，且与 UI 线程的 git 进程并发争抢仓库锁。

此外 `RefreshWorkspaceContext()` 还会在切会话（`ChatPageViewModel.cs:384`）、进入聊天页（`:415`）、
工作目录变更（`:242`）、回滚（`:1139`）、Fork（`:1224`）时各再跑 3 个。

**(c) Core 的 AOT 兼容性从未被验证（技术债 #2）。**

`Directory.Build.props` 用 `SuppressTrimAnalysisWarnings` / `SuppressAotAnalysisWarnings` 把裁剪/AOT 分析器全静音；
`AIShikikan.Core.csproj` 又显式 `EnableAotAnalyzer=false`。结果是 Core 里 9 处 `RequiresDynamicCode`
（`JsonArray.Add<T>` / `JsonSerializer.SerializeToElement<T>`）从未被真正编译检查过，
唯一的兜底是 `./build.sh aot` 的真实发布，而那一次也只编译 GUI 走到的代码路径。

把工具与子代理执行搬进一个**独立的 AOT 可执行项目**，意味着 Core 会第一次被一个
`PublishAot=true` 的项目真实链接，裁剪器会把这些路径全部走一遍。

**(d) 无法做资源隔离。**

子代理会拉起外部 CLI 进程（`run_subagents` 并发上限 4，即 `AgentToolFactory.cs` 的 `SubagentGroupTool.MaxConcurrentSubagents`），
它们和 GUI 共用一个地址空间，无法单独限制内存 / CPU / 优先级，也无法在不影响界面响应的情况下被强杀。

### 1.2 目标

1. **崩溃隔离**：子代理进程树卡死、Worker 自身崩溃，最坏后果退化为「本回合工具返回错误」，GUI 不受影响。
2. **消灭 UI 线程同步 git 调用**：git 调用整体移出主进程；主进程剩余的 git 调用（回滚 / Fork / 面板刷新 / Roster 注入）走异步化改造。
3. **真实的 Native AOT 验证**：Worker 以 `PublishAot=true` 发布，Core 的未验证代码路径第一次被真实编译。
4. **为按会话资源隔离留口子**：Worker 粒度是「目录 × 会话」，天然可以后续叠加每进程限额。

### 1.3 非目标的一句话提醒

这次改造**不追求**把系统做成微服务。它是「把最容易卡死、最好验证、边界最清晰的一段副作用代码挪到子进程」，
其余一切（LLM 主循环、事件总线、审批、UI、MCP）保持单进程。详见 [第 11 节](#11-非目标)。

---

## 2. 边界划分

### 2.1 搬去 Worker

| 类别 | 具体项 | 代码位置 |
|---|---|---|
| 文件工具（4） | `read_file` / `glob` / `grep` / `list_directory` | `Services/Tools/Builtin/{ReadFile,Glob,Grep,ListDirectory}Tool.cs` |
| git 工具（5） | `git_status` / `git_add` / `git_commit` / `git_create_checkpoint` / `git_diff` | `AgentToolFactory.cs` 的 `GitStatusTool` / `GitAddTool` / `GitCommitTool` / `GitCreateCheckpointTool` / `GitDiffTool` |
| 子代理工具（3） | `run_<agent>` / `assign_task` / `run_subagents` | `AgentToolFactory.cs` 的 `AgentExecutionTool` / `AssignTaskTool` / `SubagentGroupTool` |
| 执行器 | `AgentExecutor`（提示词构建 + 执行 + 压缩调用） | `AgentToolFactory.cs`（`AgentExecutor` 整个类） |
| 执行侧 | `AssignmentManager`（`Create` / `RunSyncAsync` / 落盘 / 终态广播） | `AssignmentManager.cs:114/140` |
| 子进程执行 | `CliAgentRunner.RunAsync` | `CliAgentDefinition.cs:84` |
| git 写能力 | `GitService` + `GitCheckpointStore`（Worker 侧实例） | `Git/` |

这 4 个文件工具只消费 `ToolContext.WorkspaceRoot` 一个字段（`ReadFileTool.cs:52`、`GlobTool.cs:66`、
`GrepTool.cs:78`/`:98`、`ListDirectoryTool.cs:42`）；5 个 git 工具同样只消费 `WorkspaceRoot`
（每个工具里各一处 `ResolveContext(ctx.WorkspaceRoot)`）。**这意味着把它们搬走几乎不损失任何上下文语义。**

### 2.2 留在主进程

| 保留项 | 代码位置 | 理由 |
|---|---|---|
| MCP 全部：`McpService` / `McpProxyTool` / stdio·http·sse 客户端 | `Services/Mcp/` | 见 2.3 |
| `ask_user` 工具 | `Builtin/AskUserTool.cs` | 本质是 UI 交互，见 2.4 |
| LLM 客户端与引擎主循环 | `Services/Llm/`、`AgentEngine.RunTurnCoreAsync` | 引擎持有对话历史（普通 `List`，非线程安全，`AgentEngine.cs:133`）与压缩状态，跨进程搬移收益为负 |
| 工具审批闸 | `AgentEngine.cs:806-830` | 需要 `TaskCompletionSource<bool>` 与 GUI 弹窗在同一进程内闭环 |
| 事件总线 | `EngineEventHub` | 投递规则依赖「当前活动会话」这一 GUI 概念 |
| UI | `src/AIShikikan.Gui/` | — |

### 2.3 为什么 MCP 全部留在主进程

这是本次边界划分里最重要的一个决定。理由不是「MCP 更容易搬」或更难，而是下面这条可验证的事实：

> **MCP 层是全仓唯一不消费 `ToolContext` 任何字段的层。**

`McpProxyTool.ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)`
（`McpProxyTool.cs:46`）签名收 `ctx`，但函数体内对 `ctx` 的引用数为 **0**
（逐行核对 `:46-72`）。它只用 `args`，其余全部经 `_service.CallBridgeToolAsync(...)`（`:59`）走 MCP 自己的传输栈。

这条事实的意义是：**MCP 留在主进程不引入任何「ToolContext 需要跨进程同步」的字段**。
反过来，如果把 MCP 也搬进 Worker，`ToolContext` 的 6 个字段（`ToolFramework.cs:27-59`）就得全部进协议，
其中 `OnToolOutput`（委托）与 `AskUser`（返回 `Task<string?>`）是**进程内回调**，跨进程只能退化成通知 + 请求/响应往返，
复杂度与出错面直接翻倍。为了搬 MCP 而付出这个代价，收益（崩溃隔离）却远小于搬文件/git/子代理工具。

补充一条次要理由：MCP 服务器连接是有状态的、需要 `initialize` 握手与长连接维持
（`McpClientBase.cs:212`、`McpStdioClient.cs:113` 的常驻读循环），而 Worker 是**可被随时重建**的
（见第 4 节崩溃语义）。把长连接状态放进一个随时会被杀掉重拉的进程里没有收益。

**放弃的方案**：把 MCP 也搬走，理由是「MCP 工具是最容易挂死的外部进程」。放弃原因是上面的 `ctx` 字段问题
+ 长连接状态问题；如果将来出现「MCP 服务器挂死拖垮 GUI」的真实案例，正确做法是给 MCP 加超时与自动重连
（技术债 #8 已经在列），而不是把它搬进另一个进程。

### 2.4 为什么 `ask_user` 留在主进程

`ask_user` 的实现完全建立在 `ToolContext.AskUser` 这个回调上（`AskUserTool.cs:38/53`），
而该回调由引擎注入、经 `EngineQuestionRequested` 事件触达 GUI（`AgentEngine.cs:860-865`）。
留在主进程意味着：

- `EngineEventHub.ShouldDeliver` 的四条规则**一行都不用改**（`EngineEventHub.cs:75-87`），
  特别是规则 2「`EngineQuestionRequested` 强制全投」，否则引擎会挂到 5 分钟超时（`:117`）；
- `AskUserTool` 已有的安全降级（`ctx.AskUser is null` → 返回 `ToolResult.Error("当前环境不支持向用户提问。")`，
  `AskUserTool.cs:38-41`）在 Worker 侧天然可用 —— Worker 侧的 `ToolContext.AskUser` 就是 null，
  所以未来若把 `ask_user` 也搬过去，**不需要改 `AskUserTool` 一行**，它会立刻给出可读错误而不是挂起。

**放弃的方案**：把 `ask_user` 一起搬走，用协议里的 `request/askUser` 请求/响应往返实现。
协议里**保留**该方法（见 5.3）纯粹是为了将来搬它时不必改协议；当前方案不启用。

### 2.5 跨进程面为什么能压到这么小

远端工具桥接的现成范式就是 `McpProxyTool`：一个实现 `ITool` 的壳，把 `args` 转成远端调用，
把结果转回 `ToolResult`，`Name` / `Description` / `Parameters` / `RequiresApproval` 全部本地给出。
Worker 侧**照抄这个模式**做成 `WorkerProxyTool`，于是：

- 主进程里注册的不是本地工具对象，而是「描述 + 转发」；
- LLM 看到的工具声明（`ToolRegistry.ToSpecs`，`ToolFramework.cs:149`）格式完全不变；
- **`AgentEngine` 一行都不用改** —— 它只在 `AgentEngine.cs:868` 调 `tool.ExecuteAsync(args, context, ct)`，
  拿到的还是 `ToolResult`。审批闸（`:806`）读的是 `tool.RequiresApproval`，事件出口（`:855`）用的是 `ctx.OnToolOutput`，
  两者都留在主进程，语义不变。

---

## 3. 进程与管道架构

### 3.1 拓扑

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ 主进程 (AIShikikan.Gui)                                                        │
│                                                                              │
│   LLM 客户端 ◄──► AgentEngine 主循环                                          │
│                       │                                                      │
│                       ├─ ToolRegistry ─┬─ ask_user ──────────► GUI 输入框     │
│                       │                 │                        (问/答)        │
│                       │                 └─ mcp_* ──► McpService ──► 外部 MCP 进程│
│                       │                                                      │
│                       └─ WorkerProxyTool × N  ◄──┐                           │
│                            (Name/Description/     │ NDJSON 行协议              │
│                             RequiresApproval      │ (双向, 同一条管道)         │
│                                   │               │                           │
│                          审批闸(Engine.cs:806)    │                           │
│                                   │               │                           │
│                          事件出口(Engine.cs:247)  │                           │
└───────────────────────────────────┼───────────────┼───────────────────────────┘
                                    │ stdio 匿名管道  │ (System.IO.Pipes)
                                    ▼               ▲
                          ┌─────────────────────────┴─────────────────────────┐
                          │ Worker 进程 (AIShikikan.Worker)                   │
                          │                                                   │
                          │  WorkerServer  ◄── 反向通道(读循环)               │
                          │       │                                           │
                          │       ├─ write lock ──► 帧写串行化               │
                          │       ▼                                           │
                          │  SlimHost (手写 new, 不调 Boot)                   │
                          │    ├─ GitService + GitCheckpointStore   ★写       │
                          │    ├─ AssignmentManager                 ★写       │
                          │    ├─ AgentConfigService / PersonaService         │
                          │    │    / AgentTemplateService / LlmService  (只读) │
                          │    └─ 工具集(文件 4 + git 5 + 子代理 3)            │
                          │            └─ AgentExecutor ─► CliAgentRunner     │
                          │                             └─ 子代理 CLI 进程树   │
                          └───────────────────────────────────────────────────┘

★ = 该进程是对应数据文件的唯一写者（见第 9 节）
```

### 3.2 为什么选 stdio 匿名管道

传输层选择：**父进程创建 `AnonymousPipeServerStream`，把客户端句柄通过命令行参数传给子进程，
子进程用 `AnonymousPipeClientStream` 接回**；两个方向各一条管道（或一条双向管道 + 各自的读写半边）。

| 候选 | 判断 |
|---|---|
| **stdio 匿名管道（选中）** | 零命名管理、零端口管理；子进程退出后父侧读端立即 EOF（不需要轮询 `HasExited`）；句柄随进程消亡自动回收；与仓库内**已验证**的 `McpStdioClient` 完全同构（行协议 + `SemaphoreSlim` 写锁 + 常驻读循环 + 断开时 `FailAllPending`，`McpStdioClient.cs:15/98-110/113-157`） |
| 命名管道（`NamedPipeServerStream`） | 需要 `pipeName` 全局唯一，多实例并发时要么加后缀要么撞名；跨平台命名规则有差异；权限模型在 Linux 上要额外处理。零收益 —— 我们本来就有父子进程关系 |
| 本地 socket（`TcpListener` + `127.0.0.1`） | 需要选端口、防端口冲突、防误连（任何本机进程都能连上），且要自己实现连接鉴权 |
| stdin/stdout 重定向（`ProcessStartInfo.RedirectStandardInput`） | 与匿名管道在 OS 层是同一类对象，但 API 面是 `StreamReader`/`StreamWriter`；我们需要在读循环里做「按行 → 解析 → 派发」且要能显式控制写侧串行化与两端关闭时机，`PipeStream` 的 `ReadAsync`/`WriteAsync`/`FlushAsync` 语义更直接。与 `McpStdioClient` 的差异只有传输 API，协议结构完全一致 |

### 3.3 传输层抽象：`IToolTransport`

管道不是唯一实现，必须抽象，否则无法降级、无法分步验证：

```csharp
// 位于 Core: Services/Worker/
public interface IToolTransport : IAsyncDisposable
{
    string Kind { get; }                 // "inline" | "pipe"，用于 UI/日志展示
    Task<IReadOnlyList<RemoteToolSpec>> ListToolsAsync(CancellationToken ct);
    Task<ToolCallOutcome> CallAsync(ToolCallRequest req, CancellationToken ct);
    Task SendAsync(WorkerFrame frame, CancellationToken ct);   // tools/sync / ping / shutdown / cancel
    Func<WorkerFrame, Task>? OnFrame { get; set; }             // 反向通道(通知/请求)
}
```

| 实现 | 行为 | 用途 |
|---|---|---|
| `InlineTransport` | 直接持有本地 `ITool` 字典，`CallAsync` 就是 `await tool.ExecuteAsync(args, ctx, ct)` | **降级路径**（找不到 Worker 产物时）；也是 B1/B2 批次的验证载体 |
| `PipeTransport` | 真实子进程 + 匿名管道 + NDJSON 行协议 | 正常路径 |

**为什么必须有两个实现**：

1. **降级**：见第 6 节，第 5 级候选全未命中时行为必须与今天完全一致，否则「发布漏了 Worker」会直接变成功能不可用。
2. **分步验证**：B1（协议 + 传输）阶段可以只做 `InlineTransport`，把协议帧的编解码、`ToolResult.Detail` 的多态往返、
   写锁、心跳、崩溃收尾全部在**不引入进程**的前提下测完；B3 才引入真实管道。B2 的代理层同理 ——
   `WorkerProxyTool` 对着 `InlineTransport` 跑通后，换 `PipeTransport` 只改一行工厂代码。

---

## 4. Worker 标识与生命周期

### 4.1 WorkerKey

```
WorkerKey = SHA256(Normalize(worktreeRoot) + "\n" + sessionId)[..16]
```

- `Normalize` 复用 `GitWorkspaceResolver.Normalize`（`WorkspaceResolver.cs:115`，`public static`）：
  全路径 + 去尾分隔符（根目录与 Windows 盘符除外）。
- 比较器策略复用 `GitWorkspaceResolver` 的平台判定（`WorkspaceResolver.cs:33-36`）：
  Windows / macOS 用 `OrdinalIgnoreCase`，Linux 用 `Ordinal`。
- 用 `\n` 而不是直接拼接，避免 `("/a", "bc")` 与 `("/ab", "c")` 撞 key。

### 4.2 粒度：每个（目录 × 会话）一个 Worker 进程

**决定：一个 Worker 进程服务一个 (worktreeRoot, sessionId) 组合。**

为什么不是「一个 Worker 管所有会话」或「每个会话一个 Worker（不分目录）」：

- 「一个大 Worker 管所有会话」→ 目录引用计数（4.3 的核心规则）失去意义；一个会话的子代理卡死会连带影响同进程内其他会话的工具调用（因为工具执行是同一个进程内的同步代码，grep 卡住就是整个 Worker 卡住）。
- 「每个会话一个 Worker（不分目录）」→ 同一目录下用户切来切去开会话会反复拉进程，而目录级 git 写操作（`git_add`/`git_commit`）的仓库锁（`GitService.GetRepoLock`，`GitService.cs:86`）本来就是按仓库根串行的 —— 分目录能让「同一目录的 git 写」天然落在同一个进程里，与既有锁语义对齐。

### 4.3 生命周期规则

| # | 规则 |
|---|---|
| L1 | **懒启动**：会话首回合首次需要工具时才 spawn。不需要工具的回合（纯文本问答）不产生 Worker。 |
| L2 | 目录内**会话引用计数 ≥ 1** → 该目录的 Worker 保持存活。 |
| L3 | **该目录完全没有会话时 → 关闭该目录下全部 Worker。** |
| L4 | 会话 WorkDir 变更 → 旧目录计数 -1、加入新目录（旧目录归零则按 L3 关闭）。 |
| L5 | Worker 崩溃 / 心跳僵死 → **仅重拉那一个 `(目录, 会话)`**，不动其他。 |
| L6 | **空闲回收（槽位级）**：某个 (目录, 会话) 的 Worker **连续 1 分钟没有任何任务**就关掉它 —— `WorkerPoolOptions.DirectoryIdleTimeout` **默认 `TimeSpan.FromMinutes(1)`、已启用**（置 `TimeSpan.Zero` 即完全关闭）。**只摘句柄、保留槽位**：会话仍登记在目录组里，下次 `AcquireAsync` 走懒重建。 |

### 4.3.1 关于 L6（2026-10-03 修订：语义已反转）

> ⚠️ **初版结论作废。** 早期版本把 L6 写成「备用、默认 0（关闭）」，理由是「它与 L3 语义重叠且方向相反，
> 两者并存会产生『目录刚被判定要关、又有会话切进来』的竞态，因此启用时必须与 L3 互斥（有会话引用则永不因空闲关闭）」。
> **该判断是错的，规则已按下面的口径重写。**

| 维度 | 初版（作废） | 现状（`DirectoryIdleTimeout = 1 分钟`） |
|---|---|---|
| 作用对象 | 只作用于**引用计数已归零**的目录组 | **任何**有句柄的槽位，包括会话还活着、只是没在干活的那种 |
| 与 L3 的关系 | 与 L3 **互斥**（"有会话引用则永不因空闲关闭"） | 与 L3 **叠加**：L3 管"没人要的目录"（会话数归零 → 整组退役，**不等超时**）；L6 管"有人要但一直闲着的 Worker"（**只摘句柄、保留槽位**）。两者都在，先命中哪个关哪个 |
| 判据 | 组级 `LastActivityTicks` | 槽位级**合取式**：`WorkerClient.ActiveCallCount == 0` **且** `now - WorkerClient.LastActivityTicks >= 阈值`（外加跳过 `InFlight != null` 与 `Slot.TurnActive`） |
| 回收粒度 | 整组 | 单个槽位的句柄；槽位本身（L3 的计数、退避字段、`Degraded` 粘滞标记）全部保留 |
| 触发节拍 | 60s 对账 | `WorkerPool.EffectiveReconcileInterval` = `clamp(阈值 / 4, 2s, ReconcileInterval)`，默认 **15s** |

**为什么反转（性能）**：每个 Worker 是一份独立的 Core 运行时（JIT 后的代码页 + 常驻堆，AOT 下可执行映像约 12MB）。
十个开着不动的会话就是十份常驻内存，而空闲的 Worker 既不产出价值，又持着工作目录的 git 上下文。下次调用本来就是懒重建
（约 100~300ms），**重建成本远低于长期占着的内存**。

**初版担心的竞态并不存在**：竞态来自"整组关闭"这个动作，而 L6 只摘句柄、保留槽位 —— 会话不会因此从引用计数表里消失，
`SessionCount` 仍 ≥ 1，L3 的判定不受影响；而 L3 触发时本来就会把整组的槽位连同计数一起清掉（`BeginRetire` → `_slots.Clear()`），
两条路径的处置对象根本不同。

**空闲判据为什么必须是合取式（`WorkerDirectoryGroup.DetachIdleClients`）**：

1. `WorkerClient.ActiveCallCount == 0`（等价 `IsBusy == false`）—— **正确性底线**。子代理工具合法跑 30 分钟
   （`CliAgentDefinition.TimeoutMinutes` 默认 30），只看"距上次活动多久"会在它跑到 1 分钟时被当成空闲杀掉：
   子代理进程随即变孤儿，它持有的仓库 git 写锁要等到超时才释放，而在飞的那次 `tools/call` 会以工具错误收场
   （父进程认为这一回合结束了，**真正在跑的却是另一个进程里的子代理**）。
   ⚠️ 这条判据**必须由 `WorkerClient` 报**（`CallToolAsync` 的 `NotifyCallStarted` / `NotifyCallEnded`，减计数放在 `finally`），
   不能由 `WorkerPool` 数：池只在 `AcquireAsync` / `Release` 这些记账路径上被调用，**看不到一次调用的整个区间**。
2. `now - WorkerClient.LastActivityTicks >= 阈值` —— 这一条才对应用户说的"1 分钟无任务"。活动时间由 `WorkerClient` 在
   **调用开始/结束**、**实时输出到达**（`OnTransportToolOutput`）、**握手与工具集同步**时刷新。
   两条缺一不可：子代理可能连续十几分钟不吐一行输出（内部思考 / 跑长命令），那段时间既没有调用开始也没有输出，
   **只有 `ActiveCallCount` 能证明它还活着**。

**`WorkerPool.MarkTurnActive(sessionId, workDir, active)` 是纯性能优化，不是正确性要求**：回合的 LLM 流式阶段恒定
"没有在飞调用"，不通知的话这个回合的**下一个**工具调用要额外付一次重建（约 100~300ms）。1 分钟阈值大于绝大多数单回合耗时，
正常不会命中；长上下文 + 慢模型的长回合会命中。三条注意事项：

- **调用方必须 `try/finally` 成对**。漏掉 `active: false` → 该会话的 Worker **永远**不参与回收，
  症状是"有的会话的 Worker 一直关不掉"，而它是按会话发生的、极难定位。
- **只作用于已存在的槽位**：会话还没 `Acquire` 过（未挂进目录组）时调用是 no-op，标记不会留到后来。
  因此接线位置应在**回合开始处**（与 `SessionRuntime` 已有的 `TryBeginTurn` / `EndTurn` 同一对生命周期），
  而不是"每回合第一次工具调用之后"。
- 该方法目前**零生产调用方**。

**节拍为什么必须自适应（`EffectiveReconcileInterval`）**：空闲回收寄生在 reconcile 循环上（`StartReconcileLoop` 在
`liveSessionProvider == null` 或 `ReconcileInterval <= 0` 时直接不启动；`ReconcileNowAsync` 拿不到比对基准时静默跳过整轮）。
若仍按 60s 对账，"空闲 1 分钟就关"实际会变成 **1~2 分钟**（取决于 tick 落在超时点哪一侧）。取阈值的 1/4 → 抖动上界是阈值的 1/4
（默认最迟 75s），对"省内存"这个目标完全够用。夹在 `[2s, ReconcileInterval]` 的理由：下界 2s 是因为阈值被配得极小时每次 tick
都要遍历全部目录组，太密只是白烧 CPU；上界取 `ReconcileInterval` 保证**空闲回收未启用时行为与初版完全一致**。

> ⚠️ **这条规则尚未在生产路径生效**：聊天页发消息的路径上工具仍走主进程内的 `ToolRegistry`，全仓没有任何
> `new WorkerProxyTool(...)` 的生产调用方（见 `AGENTS.md` 技术债 #20），所以生产里池内压根没有句柄可回收。
> 判据本身**已被 `WorkerSelfCheck` 的 S8–S12 覆盖**（`ScenarioCount = 12`）：S8 空闲被摘句柄而槽位/目录组保留、
> S9 有在飞调用绝不回收（正确性底线）、S10 回合进行中不回收且置 `false` 后恢复、S11 活动度刷新（`Touch()`）、
> S12 阈值置 `TimeSpan.Zero` 时不回收。但那 5 组走的是**专用探针池**（内联传输 + `ReconcileInterval = TimeSpan.Zero`
> 的手动对账 + 200ms 阈值），**生产默认的「真管道 + 15s 自适应节拍 + 1 分钟阈值」这一组合没有任何自动验证**。

### 4.4 前提条件（必须接线，否则规则不成立）

> ⚠️ **这条是整个生命周期机制的承重墙。**

L3 依赖「目录内每个会话最终都会 -1」。但当前代码里：

- **`SessionRuntimeRegistry.RemoveSession`（`SessionRuntimeRegistry.cs:360`）零生产调用方**
  —— 全仓检索只有它自己与文档注释引用它。GUI 删除会话目前没有走这条路。
- **`SessionRuntimeRegistry.Dispose`（`SessionRuntimeRegistry.cs:459`）同样零生产调用方**
  —— `CommanderRuntime` 自身没有 `Dispose`，主进程退出时靠进程消亡回收，没人通知注册表。

所以实施 Worker 时**必须同时接线**：

1. GUI 删除会话 → 调 `Sessions.RemoveSession(id)`；
2. 主进程退出（Avalonia lifetime 结束 / `Program.cs` 的 `finally`，`Program.cs:43-50`）→ 调 `Sessions.Dispose()`；
3. **定时与 `Sessions.All` 做一次 reconcile 兜底**：把「引用计数表里存在、但 `Sessions.All` 里已不存在的 (目录, 会话)」一律 -1 并关闭对应 Worker。
   这条兜底不是可选项 —— 只要有任何一条路径漏掉 -1（异常吞掉、未来新增的删除入口、Worker 启动失败后计数未回滚），
   目录就永远不会归零，Worker 进程会一直残留，且没有任何现象提示。

reconcile 的比对基准是 `SessionRuntimeRegistry.AllSessions`（`:305`，`All` 是其旧别名 `:312`）。
注意它的语义：**只含「已按需创建」的运行时**，历史会话列表里从未打开过的会话不在其中 ——
但那种会话本来就没有 Worker，所以不会造成误判。

> **一处轮询、三段处置**（`WorkerPool.ReconcileNowAsync`，按此顺序）：
> ① `WorkerDirectoryGroup.Reconcile(live)` —— **会话级**回收，摘掉整个槽位（会话已不存在）；
> ② `WorkerDirectoryGroup.DetachIdleClients(timeout, now)` —— **槽位级**空闲回收，只摘句柄、保留槽位（4.3.1）；
> ③ `SessionCount == 0` 的目录整组退役 —— L3 的兜底。「没有任何会话要这个目录」是比「空闲」更强的信号，**不等空闲超时**。
>
> ⚠️ 轮询**节拍是 `EffectiveReconcileInterval` 而不是 `ReconcileInterval`**：启用空闲回收时取「阈值 / 4」并夹在
> `[2s, ReconcileInterval]`，默认 15s。拿不到比对基准时**静默跳过整轮**（误回收比不回收糟得多）。
> ⚠️ `ReconcileNowAsync` 的调用方只有两处：**后台对账循环**，以及 `WorkerSelfCheck` 的 S8–S12（它们把
> `ReconcileInterval` 设为 `TimeSpan.Zero` 关掉后台循环，改为自己显式调用，好让"是谁触发的这次回收"没有竞态）。

### 4.5 崩溃语义

**不得让引擎永久等待。**

Worker 进程退出（正常 `worker/shutdown`、崩溃、被强杀、父进程管道断开）时，主进程必须：

1. 读循环感知 EOF / `IOException`（照抄 `McpStdioClient.ReadLoopAsync` 的 `finally { FailAllPending(...) }`，`:153-156`）；
2. 把该 Worker 上**所有在飞的 `tools/call`** 完成为 `ToolResult.Error(...)`（软失败文案，例如「Worker 进程已退出，工具调用未完成」）；
3. 本回合**继续**：引擎会把这些错误当工具结果塞回对话（`AgentEngine.cs:735-741`），LLM 自行决定重试或换路径；
4. 下一回合需要工具时按 L5 重拉。

`tools/call` 走软失败而非抛异常，是因为抛异常会被引擎的 `catch (Exception)` 兜成
`"工具执行异常: ..."`（`AgentEngine.cs:884-888`），丢失「Worker 崩了」这个可诊断信息；
而软失败可以让 LLM 看到失败并在下一轮重试，同时主进程在日志里留下 Worker 的退出码与 stderr。

### 4.6 取消语义

```
引擎 ct 取消 → 主进程发 notify/cancel(callId)
             → Worker 侧查 callId → CancellationTokenSource.Cancel()
             → CliAgentRunner 的 linked token 取消
             → process.Kill(entireProcessTree: true)   (CliAgentDefinition.cs:217)
             → 抛 OperationCanceledException → 在飞 call 以 OCE 语义终结
```

> ⚠️ **必须保住既有铁律**：`catch (OperationCanceledException) { throw; }` 必须在 `catch (Exception)` 之前。
> 这个模式在仓库里已经出现 4 次，每处都有明确理由注释：
> `AgentExecutor.ExecuteAsync`、`SubagentGroupTool.RunOneAsync`、`AssignmentManager.RunSyncAsync`（`AssignmentManager.cs:169`）、`McpProxyTool.ExecuteAsync`（`McpProxyTool.cs:63`）。
> 理由统一是：被 `catch (Exception)` 吞掉会让「停止」按钮只停住主循环，
> 子代理进程仍在后台跑（`AgentExecutor.ExecuteAsync` 里 `catch (OperationCanceledException)` 上方的注释），用户以为停了实际没停。
>
> Worker 侧新增的每一处 `catch` 都必须遵守同一条，否则「停止」会退化成「只停主循环、不停子进程」。

### 4.7 超时约定

| 位置 | 值 | 说明 |
|---|---|---|
| `worker/tools/call` | **默认无超时** | 子代理合法运行 30 分钟（`CliAgentDefinition.TimeoutMinutes` 默认 30，`:38`；上界 `MaxTimeoutMinutes = 24*60 = 1440`，`:79`）。在传输层加一个比子代理超时更短的上限，会把「合法的长任务」变成「传输层超时」，比慢更糟 |
| `worker/ping` | 5 秒 | 只探活性，不带业务语义 |
| 心跳判僵死 | 60 秒无任何帧 | Worker 每 5 秒发 `notify/heartbeat`；60 秒无帧即视为僵死（12 倍余量，足以吸收 GC / 磁盘 IO 抖动） |
| 引擎审批 | 5 分钟（不变） | `AgentEngine.ApprovalTimeout`，`:117` |
| **空闲回收阈值**（`DirectoryIdleTimeout`） | **1 分钟**（`TimeSpan.Zero` = 关闭） | 判据是**合取式**：`ActiveCallCount == 0` **且** `now - LastActivityTicks >= 阈值`，另跳过 `InFlight != null` 与 `Slot.TurnActive`。见 4.3.1 |
| **空闲回收节拍**（`EffectiveReconcileInterval`） | `clamp(阈值 / 4, 2s, ReconcileInterval)` → 默认 **15s** | ⚠️ 阈值**不是**"多久之后一定关"的承诺：实际关闭时刻 = 首次满足判据的 tick，上界是「阈值 + 一个节拍」（默认最迟 75s） |

> ⚠️ **空闲回收与「`worker/tools/call` 无超时」是同一件事的两面**：正因为传输层刻意不给工具执行设上限
> （子代理合法跑 30 分钟），空闲回收**绝不能**只看时间 —— 必须带上 `ActiveCallCount == 0` 这条正确性底线。
> 反过来，心跳（`worker/ping`，5s/60s）**不能**拿来当"Worker 是否卡住"的判据：心跳由独立定时器发，
> 一个正在跑长任务的 Worker 心跳照常。三个信号的职责必须分开：**心跳 = 连接是否僵死**、
> **`ActiveCallCount` = 是否有在飞调用**、`LastActivityTicks` = 距上次真实活动多久。

---

## 5. 协议（NDJSON）

### 5.1 形状

- **一行一帧**，每帧一个完整 JSON 对象，行分隔（`\n`），UTF-8 无 BOM。
- 复用 **JSON-RPC 2.0 的外形状**：`{"jsonrpc":"2.0","id":<n>,"method":"...","params":{...}}`，
  响应 `{"jsonrpc":"2.0","id":<n>,"result":{...}}` 或 `{"jsonrpc":"2.0","id":<n>,"error":{...}}`。
  形状与派发逻辑照抄 `McpClientBase`（`Pending` 字典 + `NormalizeRpcId` + `DispatchMessage`，
  `McpClientBase.cs:25/109/134`），**method 用自有命名空间**（`worker/*` 与 `notify/*`），
  不声称兼容 MCP —— 我们不实现 MCP 的任何语义。
- `id` 缺省 = 通知（不等待响应），与 JSON-RPC 一致。

### 5.2 序列化约束（Native AOT）

**序列化栈零新增。** 具体做法：

| 用途 | 手段 |
|---|---|
| 帧的外层 | `JsonObject` / `JsonNode.Parse` —— STJ 内置，全 AOT 安全（`McpClientBase.cs:8-10` 已确立这条约定） |
| 工具调用 `arguments` | 原样透传 `JsonElement` 的 raw text，不反序列化成 DTO |
| 需要强类型 DTO 时 | 往 `AppJsonContext`（`Serialization/AppJsonContext.cs`）加 `[JsonSerializable]`。**只有新增类型才需要补**，已注册类型的 public 属性会被源生成器自动带上（`AppJsonContext.cs:22-24` 的注释已说明） |
| `ToolResult.Detail` | 见 5.4 |

### 5.3 方法表

**父 → 子**

| method | params | 语义 |
|---|---|---|
| `worker/hello` | `protocolVersion`, `sessionId`, `workdir`, `workspaceRoot`, `personaText`, `rosterEntries`, `planMode`, `parentPid` | 握手。Worker 用它建立本会话的「人格 / roster / Plan 模式 / 工作目录」上下文，替代主进程里那些读 `CommanderRuntime.Instance` 的调用点。`parentPid` 供 Worker 检测父进程已死（Linux 下可 `kill(pid,0)` 探活） |
| `worker/tools/list` | — | 返回 Worker 侧全部工具的 `name` / `description` / `parameters` / `requiresApproval` |
| `worker/tools/sync` | `coreTools`, `subagentVisible`, `planMode`, `allowedAgentIds` | 动态同步工具集。对应主进程侧的两条既有路径：右侧栏开关注册/注销子代理工具（`CommanderRuntime.SetSubagentToolsVisible`，`:434`）与 Plan 模式重建（`SetPlanMode` → `RebuildSubagentTools`，`:327`/`:390`） |
| `worker/tools/call` | `callId`, `name`, `arguments`, `isPlanMode`, `sessionId`, `providerId`, `model` | 执行一个工具。`isPlanMode` / `providerId` / `model` 三项对应 `ToolContext` 的同名字段（`ToolFramework.cs:30/39/47`），必须显式传 —— Worker 侧拿不到主进程的引擎选项快照 |
| `worker/ping` | — | 活性探测（配 5 秒超时） |
| `worker/shutdown` | — | 主进程主动关闭（父进程退出、目录归零） |

**子 → 父**

| method | params | 主进程动作 |
|---|---|---|
| `notify/toolOutput` | `callId`, `line` | 转发成 `EngineToolOutput` 事件（等价于 `ToolContext.OnToolOutput` 的语义，`AgentEngine.cs:855`）。仅子代理工具有输出 |
| `notify/assignment` | `assignment`（`Assignment` DTO） | ① 写 `assignments/{id}.json`（等价 `AssignmentManager.Save`，`AssignmentManager.cs:235-242`）② `UsageStatsService.RecordAgentCall(...)`（等价 `CommanderRuntime.Boot` 里的订阅，`:135-143`）③ 广播 `EngineAssignmentChanged`（等价 `:184`）以刷新 Agent 面板 |
| `notify/heartbeat` | `ts` | 更新最后活动时间；60 秒无帧判僵死 |
| `request/askUser` | `callId`, `question` | **当前方案不启用。** `ask_user` 留主进程（2.4）。保留此方法是「将来若搬 `ask_user`，协议不必改」 |

**父 → 子（通知）**：`notify/cancel`，params `callId`（见 4.6）。

### 5.4 三个必须处理的传输细节

**(a) 帧写必须串行化。**

`System.IO.Pipes` 的流**非线程安全**：并发 `WriteAsync` 会交错字节，把两帧拼成一行。
而 `run_subagents` 下多个子代理会并发刷 `notify/toolOutput`（`SubagentGroupTool` 用 `Task.WhenAll` 并发执行，
`AgentToolFactory.cs` 的 `SubagentGroupTool.ExecuteAsync` 内），主进程侧转发这些帧时正是并发写。

做法照抄 `McpStdioClient`：一个实例级 `SemaphoreSlim _writeLock = new(1, 1)`，
`WriteLineAsync` 全程持锁、锁外不碰流（`McpStdioClient.cs:15/98-110`）。

读侧同理：读循环单消费者（`SingleReader` 语义，`McpStdioClient.cs:113-157`），
`run_subagents` 的并发输出在 Worker 侧经各自的 `IProgress<string>.Report` 进入队列，**由 Worker 的读循环单点写出**。

**(b) 单行 8 KB 上限（沿用既有约定）。**

`CliAgentRunner.Truncate` 对每一行输出做 8 KB 截断（`CliAgentDefinition.cs:319`：`s.Length <= 8 * 1024 ? s : s[..8192] + "..."`）。
协议的帧行沿用同一上限，超长行在写入前截断并追加标记。理由：单行无限长会让 `StreamReader.ReadLineAsync`
在没有换行的输入上无限缓冲，一个失控的子代理输出就能把 Worker 内存吃光。**注意 8 KB 是「单行」上限而非「整帧」上限** ——
`read_file` 的结果可达数百 KB，它是被 JSON 转义后落在**一行**里的，因此帧本身远大于 8 KB。
这两条规则不冲突：8 KB 约束的是「子代理进程吐出的原始文本行」，帧级别的上限另设（建议 8 MB，对齐 `CliAgentRunner.MaxOutputChars = 512 KB` 与 `git_diff` 的 26000 字符截断，（`GitDiffTool.MaxDiffChars`），留足余量）。

**(c) `ToolResult.Detail` 的多态往返。**

`ToolResult.Detail` 的类型是抽象基类 `ToolCardDetail`（`ToolFramework.cs:18`），
主进程无法从协议帧里知道具体是哪个派生类。

**复用仓库里已有的判别机制，不自造。** `ToolCardDetail` 已经声明了 STJ 多态判别符
`[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]`（`ToolCardDetail.cs:6`），
并登记了 5 个派生类型（`:7-11`：`fileRead` / `dirList` / `glob` / `grep` / `subagents`）；
`CheckpointDetail` 自己也重复声明了一次判别符并登记自己（`CheckpointDetail.cs:6-7` → `"checkpoint"`）。
`AppJsonContext` 已注册 `ToolCardDetail` 与全部派生类型（`AppJsonContext.cs:42-51`）。

所以协议帧里直接用 `AppJsonContext.Default.ToolCardDetail` 序列化 / 反序列化 `Detail` 即可：
判别串就是现成的 `$type`，**不需要新增任何 `[JsonSerializable]`**。
需要注意的是 `AppJsonContext` 的全局策略是 `CamelCase` + `WriteIndented = true`
（`AppJsonContext.cs:25-28`）—— 帧体积上 `WriteIndented` 是负优化，但为了不引入第二套选项上下文，
**接受这一点**，在帧编码处显式注释说明「帧里的 Detail 带缩进是已知且可接受的代价」。
新增卡片类型时只需在 `ToolCardDetail` 上加一行 `[JsonDerivedType]`，协议零改动。

---

## 6. Worker 自定位（`WorkerLocator`）

主进程必须在启动后（首次需要工具时即可，也可以提前在 `doctor` 里）定位 Worker 可执行文件。
**5 级候选，按顺序尝试，命中即止**：

| # | 候选 | 覆盖场景 |
|---|---|---|
| 1 | 环境变量 `AISHIKIKAN_WORKER_PATH` 指向的绝对路径 | `debug.sh` 注入；用户自行覆盖（源码构建、非标准布局） |
| 2 | `<AppContext.BaseDirectory>/AIShikikan.Worker[.exe]` | 与 GUI 平铺在同一目录的布局（开发时的 `dotnet run` 若两项目输出到同一目录也命中这里） |
| 3 | `<AppContext.BaseDirectory>/worker/AIShikikan.Worker[.exe]` | **正式发布布局**（zip / tar.gz 解压即用）；Linux 系统包为 `/usr/lib/ai-shikikan/worker/`。三个变体统一走这一级（见 7.3） |
| 4 | 从 `AppContext.BaseDirectory` 向上逐级查找 `AIShikikan.slnx`，命中后取 `src/AIShikikan.Worker/bin/<Configuration>/net10.0/AIShikikan.Worker[.exe]` | 源码仓库里的 `dotnet run`（`debug.sh` 启动 GUI 时，BaseDirectory 是 `src/AIShikikan.Gui/bin/Debug/net10.0/`，与 Worker 产物目录不同） |
| 5 | 以上全未命中 → **降级 `InlineTransport`，行为等同今天** | Worker 未构建 / 未随包分发 / 用户删了文件 |

`<Configuration>` 取 `Release` 除非进程是 Debug 构建（与 `dotnet run` 的默认行为对齐；
`debug.sh` 用 `CONFIGURATION=Debug`，`debug.sh:32`）。

### 6.1 降级必须可见

第 5 级降级的**唯一危险不是功能失效，而是无声的性能与隔离回归**：工具仍能跑、界面完全正常，
但子代理又回到主进程、`grep` 又能卡 UI、`git` 又在 UI 线程排队 —— 用户没有任何线索。

因此降级时**必须三处同时可见**：

1. **状态栏指示**：`MainWindow.axaml` 的 `DrawerPage.DrawerFooter`（`MainWindow.axaml:183-200`，目前只有主题切换按钮）加一个
   降级态标记；或侧栏「状态」页 `StatusPanelView`（`Views/StatusPanelView.axaml`，ViewModel `StatusPanelViewModel.cs`）显示
   「工具执行：进程内（未启用 Worker）」。两处都可接受，**至少要有一处常驻可见**。
2. **`doctor` 报告**：`Program.cs:171` 的 `RunDoctor` 增加一项检查，走一遍 `WorkerLocator.Resolve()`，
   命中则报 `Worker: <路径> (<变体>)`，未命中则报 `⚠ Worker 未找到, 工具将退回进程内执行`。
   放在失败早退之前（与既有 `ScanLegacyArtifacts` 提示同一位置策略，`Program.cs:213-250`）。
3. **`Log.Warn`**：`Log.Warn("Worker", ...)`（`Log.cs:117`），含「已尝试的 4 级候选路径」，
   便于用户报障时从日志直接看到排查线索。

---

## 7. 构建与发布布局

### 7.1 项目引用图

```
        AIShikikan.Core  (类库, 零 Avalonia 依赖, IsAotCompatible=true)
              ▲                              ▲
              │ ProjectReference             │ ProjectReference
    AIShikikan.Gui (Exe, PublishAot=true)   AIShikikan.Worker (Exe, PublishAot=true)
```

**硬约束**：

- `AIShikikan.Core` **不得**引用 `AIShikikan.Worker`（会形成 Core→Worker→Core 的环，且 Core 会被拖进子进程的依赖面）；
- `AIShikikan.Worker` **不得**引用 `AIShikikan.Gui`（Worker 无 GUI，也不需要 Avalonia）；
- `AIShikikan.Worker` **不得**引用任何 Avalonia 包（同上）；
- 因此 `McpService` / `ColorExtractionService` 一类的 GUI 相关物天然留在主进程，不存在「Worker 需要 Avalonia 类型」的诱惑。

新增项目后 `AIShikikan.slnx` 增加一行：

```xml
<Project Path="src/AIShikikan.Worker/AIShikikan.Worker.csproj" />
```

`Directory.Build.props` 无需改动 —— 它已经写明「让『加第三个项目』时零成本继承」（该文件注释）。

### 7.2 Worker 出三种变体，与 GUI 一一配对

```
artifacts/aot/           ← GUI (Native AOT)
artifacts/aot/worker/    ← Worker (Native AOT)
artifacts/selfcontained/ ← GUI (自带运行时, 单文件)
artifacts/selfcontained/worker/ ← Worker (同上参数)
artifacts/dotnet/        ← GUI (框架依赖)
artifacts/dotnet/worker/ ← Worker (框架依赖)
```

**为什么必须三种，而不是「只出一个 AOT 版 Worker」**：

- 用户选 `dotnet` 变体的动机就是**省磁盘**（不打包 150MB 的运行时）。给他塞一个 15 MB 的 AOT Worker，
  等于用他明确拒绝的方式解决他的问题；
- 反过来也不能假设装了 .NET 运行时的用户存在 —— 这正是 `aot` 变体存在的理由。
  单一 Worker 变体必然在「用户所选变体」与「Worker 所需运行时」之间错配。

因此：`build_variant` 里 GUI 与 Worker 的 `dotnet publish` 参数**必须逐项对齐**（同 `-c` / `-r` / `-p:Version` /
同 `PublishAot` / 同 `PublishSingleFile` / 同 `PublishTrimmed` / 同 `IncludeNativeLibrariesForSelfExtract`），
只是输出到 `$outdir/worker`。

### 7.3 归档与系统包里 Worker 放在 `worker/` 子目录

**决定：Worker 产物一律位于变体目录下的 `worker/` 子目录**，即用户看到的布局是
`AIShikikan.Gui` 与 `worker/AIShikikan.Worker` 平级。

理由（按证据强度排序）：

1. **`dotnet`（框架依赖、多文件）布局下，同目录必然同名冲突。** 该布局是「apphost + `AIShikikan.Gui.dll` + ~69 个依赖 DLL +
   `.deps.json` + `.runtimeconfig.json` + `en/` 卫星目录」（`Packagers/linux/package.sh:211-223` 的注释与 `:229` 的整目录拷贝）。
   GUI 与 Worker 各自带一整套 `*.dll`，其中 `AIShikikan.Core.dll`、`Azure.AI.OpenAI.dll`、`OpenAI.dll`、`Tomlyn.dll`、
   `YamlDotNet.dll` **完全同名**。`cp -a` 同目录写两次同名文件就是「后写覆盖」，正确性从此依赖拷贝顺序；
   一次配置或版本漂移就会让其中一个进程静默加载到对方的 DLL —— 且不报错。
2. **aot 布局下，安装脚本把变体目录里的 `*.so` 一律装进 `/usr/lib/ai-shikikan/`**（`package.sh:236-240`）。
   GUI 侧带 `libSkiaSharp.so` / `libHarfBuzzSharp.so`。Worker 今天**不引用 Avalonia，因此并不会产出这两个文件**
   —— 这一条是防御性的、而非已发生的冲突：一旦 Worker 将来引入任何跨平台共享的原生栈，
   同名 `.so` 就会被后装的覆盖，而 `dlopen` 的探测顺序（应用目录 → 系统库路径）决定谁生效。
   放子目录后各自的探测根目录不同，互不干扰。
3. **归档无需改结构**：`build.sh` 的 `tar czf ... -C "$variant" .`（`build.sh:94`）/ `zip -qr ... "$variant/"`（`:89`）
   以及 CI 的 `tar czf ... -C stage/aot .`（`release.yml:171-173`）/ `Compress-Archive -Path stage/aot/*`（`:186-188`）
   都会把 `worker/` 原样带进去。

**推论：`WorkerLocator` 的候选 3 同时服务全部三种变体，用户与代码都不需要感知变体。**
`package.sh` 的三条安装分支其实已经能把 `<variant>/worker/` 整目录带过去
（`dotnet` 分支是整目录 `cp -a`，`package.sh:229`；`aot`/`selfcontained` 分支的 `for d in "$tree_src"/*/;`
子目录拷贝，`package.sh:244-248`）。**所以 `package.sh` 的主要改动不是拷贝逻辑，而是把这件事显式化 + 加前置校验。**

### 7.4 需改动的文件清单

| 文件 | 改动 |
|---|---|
| `AIShikikan.slnx` | 新增 `src/AIShikikan.Worker/AIShikikan.Worker.csproj` 一行 |
| `src/AIShikikan.Worker/AIShikikan.Worker.csproj` | **新建**。`OutputType=Exe`、`PublishAot=true`、`StripSymbols=true`、`AssemblyName=AIShikikan.Worker`；`ProjectReference` 只指 Core；**不得**有 `PackageReference` 到 Avalonia 系 |
| `build.sh` | `build_variant` 里每个变体追加一次 Worker publish（输出 `$outdir/worker`）；`pack_variant` 已有整目录打包逻辑（`:89`/`:94`），无需改；`clean` 已 `rm -rf artifacts`（`:127`），自动覆盖 |
| `build.ps1` | 同上（Windows 侧等价改动） |
| `debug.sh` | 构建 Worker 并注入 `AISHIKIKAN_WORKER_PATH`（保证 `dotnet run` 场景稳定命中候选 1，而不是靠候选 4 猜路径）；`debug.ps1` 同步 |
| `Packagers/linux/package.sh` | ① `install_app_tree` 增加**第 4 个参数**（当前是 3 个：目标根 / 产物目录 / 变体名，`package.sh:225-226`，函数头注释 `:224`）显式接收并校验 Worker 子目录；② 前置依赖探测（`:146-181` 的 `missing` 列表）对**每个变体**校验 `<stage>/<variant>/worker/AIShikikan.Worker` 存在，缺失即 `die`。**宁可构建失败，也不要装出没有 Worker 的包** |
| `.github/workflows/release.yml` | 三个 Publish 步骤（`:126-150`）各加一次 Worker publish；构建产物打包（`:163-188`）已含 `worker/`，无需改 |
| `.github/workflows/debug.yml` | 同 release.yml |
| `AGENTS.md` | 项目结构表新增 Worker 行；「已知技术债」更新 #2/#3 的状态；「常用命令」补 Worker 验证命令 |
| `Packagers/README.md` | 说明 `worker/` 子目录布局与前置校验语义 |

### 7.5 新增铁律：Worker 侧不得使用 GUI 的 `Strings` 资源

- `AIShikikan.Worker` **不引用 `Resources/Strings*.resx`**，不 `ProjectReference` Gui。
  卫星资源程序集 `en/AIShikikan.Gui.resources.dll` 的名字由程序集名推出（`AGENTS.md` 已记录该约束），
  Worker 一旦引入就等于隐式依赖 GUI 程序集名。
- Worker 侧**面向用户可见的错误文案硬编码中文**。
  这**不是回退**：现状所有工具的错误文案本来就硬编码中文
  （如 `"当前目录不是 git 仓库。"`（`GitAddTool`）、`"缺少参数: task"`（`AgentExecutor.ExecuteAsync`）、
  `"git diff 失败: ..."`（`GitDiffTool`）、`"路径不存在: {path}"`（`GrepTool.cs:89`）），
  这些文案会经 `ToolResult.Content` 原样进入 LLM 上下文，**从来就没有 i18n**。
  要让后来者知道的是这条**边界**：Worker 里的字符串不是「忘了加翻译」，而是「按现状不该加」。
- 唯一例外是**协议层与日志层**：`Log.*` 的 category 名、`WorkerLocator` 的候选描述等面向开发者的文本用中文即可，
  不进 `Strings`。

---

## 8. 批次划分与前置解耦

### 8.0 B0：前置解耦（7 项，**不引入进程，行为零变化**）

B0 是整个改造的地基。它的验收标准非常严格：**B0 合并后，`AgentEngine` / `ToolRegistry` / GUI 行为必须与今天完全一致**，
任何「顺便改点行为」都会让后续批次的回归定位失效。

> **进度提示**：截至定稿时，**B0-1 已在工作树中开工但未提交** ——
> 新增了 `Services/Runtime/AgentExecutionScope.cs`，`AgentExecutor` 已由 `public static class` 改为 `public sealed class`，
> 函数体对 `CommanderRuntime.Instance` 的 7 处引用已全部移除（现在文件里剩下的 5 处 `CommanderRuntime.Instance`
> 全在**注释**中，是迁移说明）。下表的「现状耦合点」列描述的是**改造前**的状态，仍作为推论依据保留。

| # | 解耦项 | 现状耦合点（改造前） | 目标 |
|---|---|---|---|
| B0-1 | `AgentExecutor` 去静态 | `AgentToolFactory.cs` 的 `AgentExecutor` 曾是 `public static class`，函数体直接读 `CommanderRuntime.Instance` | 改为实例类；执行期对全局状态的依赖经构造注入。**定稿时已落地为 `AgentExecutionScope`**（`Services/Runtime/AgentExecutionScope.cs`）—— 一个显式依赖容器，持有 `CommanderPersonaText` / `RosterResolver` / `PlanAuthorizer` / `WorkspaceRoot` / `ActiveSessionIdResolver` 五个委托或值，全部为 null 时行为必须与「单例不存在」逐字一致。静态工具方法 `ResolvePersonaText` / `BuildFinalPrompt` / `ResolveWorkingDir` 保留为纯函数（它们本就不读单例） |
| B0-2 | 4 个文件工具去掉 `ctx` 之外的无隐式依赖 | 已无隐式依赖，但签名 `(args, ctx, ct)` 绑死主进程 | 保持签名不变；新增「从显式参数构造 `ToolContext`」的自检辅助 |
| B0-3 | 5 个 git 工具与 `GitService` 解耦 | `GitCreateCheckpointTool` 曾读 `CommanderRuntime.Instance?.Sessions.ActiveSessionId` | 改为从 `ctx.SessionId` 取（引擎已注入，`AgentEngine.cs:851`），并经 `AgentExecutionScope.ActiveSessionIdResolver` 兜底旧引擎路径；`ctx.SessionId` 为空时回落为空串，语义与原 `?? string.Empty` 一致 |
| B0-4 | `AssignmentManager` 去单例 | `RunCliAsync`（`AssignmentManager.cs:210`）读 `CommanderRuntime.Instance?.WorkspaceRoot` 作为工作目录兜底 | 工作目录兜底改为构造参数（`AgentExecutionScope.WorkspaceRoot`）；`AppShell.Dispatch`（`AppShell.cs:59-93`）这条**第四条子代理执行路径**同步改造 |
| B0-5 | `CommanderRuntime` 拆分「Boot 的写副作用」与「运行时装配」 | `Boot`（`CommanderRuntime.cs`）把 6 类配置写出、`GitCheckpointStore`/`AssignmentManager` 全盘扫描、后台 spawn MCP 混在一起 | 拆出「只读装配」入口（供 Worker 用，见第 10 节风险 R1）与「带写副作用的启动」入口（供主进程用）。**拆分后两者行为都不变** |
| B0-6 | `RosterBuilder.BuildGitSection` 的 git 调用外提 | `RosterBuilder.cs:186`/`:195`/`:198` 直接调 `GitService` 同步 API（每回合 4 个进程） | 改为接受一个已算好的 git 摘要（由调用方经 `*Async` 版提供），`RosterBuilder` 不再持有 `GitService` |
| B0-7 | 主进程剩余 git 调用 async 化 | `ChatPageViewModel.cs:275/286/1065/1122/1182/1475/1494/1520`、`GitPanelViewModel.cs:140/155/239/265`、`StatusPanelViewModel.cs:138` 全用同步 API | 全部改 `*Async`。**这是技术债 #3 在主进程侧的收尾**，B5 之后 git 调用全在 Worker，主进程只剩这一批 |

**B0 验收锚点**：
- `dotnet build` 零警告；
- `./debug.sh doctor` 输出与 B0 之前逐行一致（`Program.cs:171` 的检查项集合不变）；
- 手工回归：发消息 → 自动检查点卡片出现 → 点回滚 → 卡片变灰；切会话 → 侧栏状态页分支/脏标记正确；设置页改 `timeout_minutes` → 子代理行为不变。

### 8.1 两个「必然推论」（决定了批次顺序不可调换）

> **推论 1：勾选「写类 git + 检查点写权收口」⇒ 主进程剩余 git 调用必须一并路由。**

`git_create_checkpoint`（`GitCreateCheckpointTool`）搬进 Worker 后，`checkpoints/` 的写者就变成了 Worker。
但主进程**仍在写同一个目录**：自动检查点（`ChatPageViewModel.cs:1494`）、GitPanel 手动打标记
（`GitPanelViewModel.cs:265`）、以及 `GitCheckpointStore` 淘汰时的删除（`GitCheckpointStore.cs` 的 `Delete` 系列）。
**如果只搬工具、不搬主进程的检查点写入，`checkpoints/` 就是双写者** —— 而这个目录是 Reset/Revert/Fork 的唯一依据
（`GitService.cs:259-262` 的注释明确写了「检查点 tag 是唯一回滚依据，不可重建覆盖」）。

因此 B5 的范围必须是「主进程剩余 git 调用**一并路由**到 Worker」，覆盖清单：

| 主进程侧调用点 | 涉及场景 |
|---|---|
| `ChatPageViewModel.cs:1520` | 发消息前的 `ResolveContext` |
| `ChatPageViewModel.cs:1475/1494` | 自动检查点 |
| `ChatPageViewModel.cs:275/286` | `RefreshWorkspaceContext`（`ResolveContext` + `IsClean`） |
| `ChatPageViewModel.cs:1065` | `ForkUserCheckpointAsync` |
| `ChatPageViewModel.cs:1122` | `RollbackAsync`（`ResetHardToCheckpoint` / `RevertToCheckpoint`） |
| `ChatPageViewModel.cs:1182` | `ExecuteForkAsync`（`GitService.Fork`） |
| `GitPanelViewModel.cs:140/155/239/265` | Git 面板刷新 / 手动检查点 |
| `StatusPanelViewModel.cs:138` | 侧栏状态页 |
| `RosterBuilder.cs:186/195/198` | 每回合 git 段（由 B0-6 已外提为「传入摘要」，B5 里摘要由 Worker 提供） |

**这正是技术债 #3 被彻底消灭的地方**：上表 12 处全部改成「经 Worker 拿结果 / 交给 Worker 执行」后，
UI 线程上不再有任何 git 进程 —— 不是改成 async（async 仍会在 UI 线程上占线程池调度），
而是**根本不在主进程执行 git**。

> **推论 2：勾选「子代理执行」⇒ `AgentExecutor` 解耦是硬前置。**

**改造前**，`AgentExecutor` 是 `public static class`，其执行路径上有 **7 处**读 `CommanderRuntime.Instance`
（Plan 执行兜底、`ShouldRunInPlanMode`、`RosterOf`、三处人格文本、**`GitCreateCheckpointTool` 的检查点会话 ID**），
`AssignmentManager.RunCliAsync` 另有 1 处工作目录兜底。

若不先做 B0-1/B0-3/B0-4 就把它们搬进 Worker，Worker 里的 `CommanderRuntime.Instance` 是 **null**
（Worker 绝不调 `Boot`，见风险 R1），而这 7 处**全部是 `?.` / `is { }` 形式的空安全访问** ——
**不会抛异常、不会有任何日志**，只会静默退化：

| 退化项（改造前的位置） | 静默后果 |
|---|---|
| Plan 执行兜底（`AgentExecutor.ExecuteAsync`） | 未授权子代理在 Plan 模式下**不被拒绝**（执行层兜底失效；注册层过滤仍在，所以影响面小于表面） |
| `ShouldRunInPlanMode` | 恒 `false` → **Plan 授权的子代理不会附加 `plan_args`**（`CliAgentDefinition.cs:143-149`） |
| `RosterOf` → `IsCompactEnabled` | 恒空表 → 压缩开关恒 `false` → **子代理输出压缩静默失效**（而 `ProviderId`/`Model` 照常从 `ctx` 传，所以日志上看不出任何异常） |
| 三处 `CommanderRuntime.Instance?.CurrentPersonaText` | 恒 `null` → **子代理丢失指挥官人格注入** |
| 检查点会话 ID（`GitCreateCheckpointTool`） | 恒空串 → 检查点记录 `SessionId` 为空 |
| `AssignmentManager.RunCliAsync` 工作目录兜底 | 子代理可能落到进程 CWD（工作区外）去跑 |

这六类（Plan 授权 / 压缩开关 / 人格 / 工作目录 / 检查点归属）**全部是"不报错、只是行为错"**，
是本项目里最危险的一类 bug：用户看到的是「Plan 模式下子代理没有按计划模式跑」「压缩好像没生效」，
而日志里干干净净。因此 B0-1 是 B6 的**硬前置**，不允许合并 B6 而未做 B0-1。

`AgentExecutionScope`（`Services/Runtime/AgentExecutionScope.cs`）正是为消掉这六项而引入的，
其类注释已把「全部委托为 null 时的降级约定」逐条写死 —— **那份注释是本节的法律依据，实现时不得简化。**

### 8.2 批次与验收锚点

| 批次 | 内容 | 验收锚点 |
|---|---|---|
| **B0** | 8.0 的 7 项前置解耦 | `dotnet build` 零警告；`doctor` 输出逐行一致；6 条手工回归全过 |
| **B1** | 协议帧类型 + `IToolTransport` + `InlineTransport`（**无进程**） | 用 `InlineTransport` 跑通全部 13 个工具；`ToolResult.Detail` 六种卡片多态往返（含 `CheckpointDetail`）编解码一致；写锁在 4 并发下不交错；`worker/shutdown` 后无残留 await |
| **B2** | `WorkerProxyTool` + 工具集装配改造（注册 `WorkerProxyTool` 取代本地工具对象） | 换成代理后**所有工具行为与 B1 一致**（工具名/描述/参数 schema/审批标记逐项 diff 一致）；`git_create_checkpoint` 的 `Detail` 仍是 `CheckpointDetail` 且卡片渲染正常 |
| **B3** | 新建 `AIShikikan.Worker` 项目 + `PipeTransport` + `WorkerServer` + `SlimHost` + `WorkerLocator` | `dotnet build` 通过；`debug.sh` 能起 Worker；`worker/ping` 往返；**故意 kill Worker** 后引擎收到 `ToolResult.Error` 而非挂起 |
| **B4** | 4 个只读文件工具外置 | 在 5 GB 目录上 grep，**GUI 保持可交互**（可用 `xdotool`/截图或直接观察窗口响应）；`read_file`/`glob`/`list_directory`/`grep` 结果与 B2 逐字节一致 |
| **B5** | 5 个 git 工具外置 + 检查点写权收口（8.1 推论 1 的 12 处一并路由）+ B0-7 的 async 化收尾 | **UI 线程上 0 个 git 进程**（用 `ps`/日志确认发送消息时主进程不起 `git`）；Reset / Revert / Fork 三条路径功能与行为不变；`checkpoints/` 单写者（两个进程同时运行时目录里不出现交错覆盖） |
| **B6** | 3 个子代理工具 + `AgentExecutor` + `AssignmentManager` + `CliAgentRunner` 外置 | ① `run_subagents` 4 并发正常；② 点「停止」后**子代理进程树真的消失**（`ps` 确认）；③ Plan 模式下**授权子代理确实带上了 `plan_args`**（对比 B6 前后启动命令行）；④ **压缩开关生效**（开/关两次对比输出长度）；⑤ 子代理确实拿到了指挥官人格 |
| **B7** | 工具集动态同步（`worker/tools/sync`）+ 生命周期（WorkerKey / 引用计数 / 4.4 接线 + 自适应 reconcile（L3 兜底 + L6 空闲回收）/ 心跳 / 崩溃重拉） | ① 关右侧栏 → 子代理工具消失、Worker 侧同步注销；② Plan 模式切换 → 授权过滤在 **Worker 侧**生效；③ 删除会话 → 对应 Worker 退出（`ps` 确认）；④ 手工 `kill -9` Worker → 下一回合自动重拉；⑤ 目录内最后一个会话被删 → 该目录全部 Worker 退出；⑥ **空闲 >1 分钟（阈值配小以缩短观察时间）后 `ps` 里该 Worker 消失，但会话仍在、下一次工具调用能重新拉起（槽位保留而非会话被摘）**；⑦ **一个跑了 5 分钟以上的子代理不被空闲回收杀掉**（验证判据里真的有 `ActiveCallCount == 0` 这一条，而不是只看时间） |
| **B8** | 构建 / 打包 / CI / 文档（7.4 清单全部落地） | `./build.sh all` 产出 3 个 GUI 变体 + 3 个 `worker/` 子目录；`VERSION=$(cat VERSION) VARIANTS=dotnet ./Packagers/linux/package.sh` 装出的包内 `/usr/lib/ai-shikikan/worker/AIShikikan.Worker` 存在且可执行；**故意删掉 Worker 产物后重跑 `package.sh` 必须失败**；`./debug.sh doctor` 报出 Worker 路径 |

---

## 9. 多进程共享数据的单写者原则

### 9.1 为什么不能靠锁

`AtomicFile` 有进程内路径锁（`AtomicFile.cs:40-70`，按规范化路径取 `SemaphoreSlim`），
但它的类注释**已经自陈**了这一点：

> 「该锁只覆盖进程内, 不解决多进程并发(本项目是单实例 GUI, 无此需求)。」
> —— `AtomicFile.cs:30`

引入 Worker 之后，「单实例 GUI」这个前提**不再成立**（一个 GUI 进程 + N 个 Worker 进程），
所以跨进程写冲突必须靠**约定（单写者）**解决，不能指望锁。做法：逐文件指定唯一写者，
另一侧**只读**。

### 9.2 逐文件归属

| 文件 / 目录 | 写者 | 另一侧行为 | 现状写点 |
|---|---|---|---|
| `checkpoints/**` | **Worker** | 主进程只读（`GitCheckpointStore.Get` / `GetAll`） | `GitCheckpointStore.Save`（`:53-94`，`AtomicFile.WriteAllText`）与淘汰/删除（`AtomicFile.Delete`） |
| `assignments/**` | **Worker** | 主进程只读（Agent 面板展示；`AppShell.ProcessList` 同步） | `AssignmentManager.Save`（`:235-242`，`AtomicFile.TryWriteAllText`） |
| `usage.json` | **主进程** | Worker **不发**任何用量记录 | `UsageStatsService.Flush`（`:270-287`，整文件覆盖） |
| `sessions/{id}.json` | **主进程** | Worker 不读写会话 | `ChatService` 的 `AtomicFile.WriteAllText` |
| `sessions/{id}/roster.json` | **主进程** | Worker 只读（由 `worker/hello` 的 `rosterEntries` 传递，通常连读都不必） | `RosterConfigService`（`AtomicFile.TryWriteAllText`） |
| `agents.toml` / `providers.toml` / `mcp-servers.toml` | 主进程 | Worker **只读** | `AgentConfigService` / `ProviderConfig` / `McpServerConfig` |
| `personas/**` / `templates/**` | 主进程 | Worker **只读** | `PersonaService` / `AgentTemplateService` |
| `roster.prompt` | 主进程 | Worker **只读**（`RosterBuilder`） | `RosterBuilder.WriteDefaultTemplate` |
| `preferences.toml` | 主进程 | Worker 不读不写 | `ThemeService` |
| `models.toml`（缓存） | 主进程 | Worker 不读不写 | `ModelProfileService` |

> ⚠️ **术语澄清**：早先讨论里把「只读配置」和「Worker 写」并列在同一格里，容易被读成 Worker 会写
> `agents.toml` / `providers.toml` / `mcp-servers.toml` / `personas` / `templates`。**以代码为准：这些文件 Worker 一律只读。**
> Worker 侧唯一的两个写者是 `checkpoints/` 与 `assignments/`，原因见下。

`checkpoints/` 与 `assignments/` 归 Worker 写，是因为它们的**产生者已经整体搬过去了**：
检查点由 `git_create_checkpoint`（Worker）与自动检查点（B5 后也路由到 Worker）产生，
分派记录由 `AssignmentManager`（Worker）产生。让写者与产生者同进程，
才能保证「事件 → 落盘 → 面板刷新」的顺序不跨进程。

### 9.3 两个具体的跨进程写冲突机制

即使有单写者约定，**只读侧的缓存**仍可能造成读到的数据陈旧；下面两条是必须写进实现注释的具体机制。

#### (a) `AtomicFile` 共享 `.tmp` 文件名的最坏交错

`AtomicFile` 的临时文件名是**由目标路径派生的确定值**：`var tempPath = path + ".tmp";`（`AtomicFile.cs:117`）。
两个进程写同一个目标文件时，它们会**写同一个 `.tmp`**。最坏交错（进程 P1 / P2 同时写 F）：

```
P1: 打开 F.tmp (FileMode.Create → 截断) → 写入 A → Flush(true)
P2: 打开 F.tmp (FileMode.Create → 截断) → 写入 B → Flush(true)     ← P1 的 A 被截断
P1: TryBackupExisting(F) → F.bak = F(旧)
P1: File.Move(F.tmp, F, overwrite) → F = B                        ← P1 搬走的是 P2 的内容
P2: TryBackupExisting(F) → File.Exists(F) 为真 → File.Move(F, F.bak) → F.bak = B
P2: File.Move(F.tmp, F, overwrite) → F.tmp 已不存在 → 抛 FileNotFoundException
P2: catch → TryDelete(F.tmp) → 重新 throw → 调用方 TryWriteAllText 记 Log.Error
```

结果：

1. **主文件内容是完整的**（A 或 B 都是完整刷盘过的写入，不是半截）—— 这一点上不会数据损坏；
2. **但会记一条误导性的 `Log.Error("原子写入失败…")`**（`AtomicFile.cs:154`），而磁盘上的文件其实完好；
   用户/维护者会据此去追一个不存在的问题；
3. **`.bak` 的「上一代内容」语义被破坏**：`TryBackupExisting` 先 `File.Delete(backupPath)` 再 `File.Move(path, backupPath)`
   （`AtomicFile.cs:257-262`），因此 `.bak` 可能与主文件**同代甚至同内容** ——
   读取端的 `.bak` 回退（`TryReadText`，`:182-191`）就再也拿不到更早的一代数据，**回退能力静默失效**。

补充一条比「数量」更值得注意的点：`.bak` 是**确定性同名兄弟文件**，因此它的**数量恒为 1**，
不会「数量不定」；真正坏掉的是它的**代际语义**。

**结论**：`checkpoints/` 与 `assignments/` 必须单写者。多写者场景下的 `AtomicFile` 不是「可能丢数据」，
而是「丢数据 + 假告警 + 备份失效」三重问题，且都无告警可循。

#### (b) `usage.json` 是「内存全量快照 + 整文件覆盖 + 1.5s 防抖」

`UsageStatsService` 的写模型是：

- 内存里持有**完整** `UsageData`（`:132` `_data`，懒加载自文件 `:168-177`）；
- 每次 `RecordLlmUsage` / `RecordAgentCall` / `MarkSessionDeleted` 只改内存并 `MarkDirty()`（`:231-238`）；
- 1.5 秒后（`SaveDebounceMs = 1500`，`:157`）由定时器触发 `Flush()`，**序列化整个内存快照并整文件覆盖**
  （`:270-287`，`SerializeSnapshot()` → `AtomicFile.TryWriteAllText(AppPaths.UsageStatsPath, json)`）。

两个进程各自持有一份**完整且互不知情**的 `_data` 快照时，后写的那次覆盖会把先写进程的增量**整段吞掉**：
Worker 记了 3 次子代理调用 → 1.5s 后写盘；主进程在这 1.5s 内记了 5 次 LLM 调用（它的快照里没有那 3 次子代理调用）
→ 同样 1.5s 后写盘 → **3 次子代理调用永久消失**。这是纯粹的「读-改-写丢失更新」，无异常、无日志。

**结论**：`usage.json` **必须**只有主进程一个写者。Worker 侧的子代理调用统计通过
`notify/assignment` 通知主进程，由主进程调 `UsageStatsService.RecordAgentCall`（`:336`）落盘
（语义与今天 `CommanderRuntime.Boot:135-143` 的订阅完全一致）。

---

## 10. 风险清单

| # | 风险 | 说明与对策 |
|---|---|---|
| **R1** | **Worker 绝不能调 `CommanderRuntime.Boot()`** | `Boot`（`CommanderRuntime.cs:94-195`）做了四件在子进程里全是灾难的事：① **写 6 类配置** —— `PersonaService.WriteSampleFiles`（`:98`）、`AgentTemplateService.EnsureSamplesExist`（`:99`）、`RosterBuilder.WriteDefaultTemplate`（`:100`）、`AgentConfigService.EnsureDefaultExists`（`:101`）、`ProviderSettingsService.EnsureDefaultExists`（`:102`）、`McpConfigService.EnsureDefaultExists`（`:103`），再叠加 `AppPaths.EnsureDirectoriesExist`（`:96`）。Worker 与主进程同时启动会互相覆盖用户的 `providers.toml`；② **构造时全盘扫描** —— `new GitCheckpointStore()`（`:116`）在构造函数里 `LoadAll()` 扫 `checkpoints/`（`GitCheckpointStore.cs:32-37`），`new AssignmentManager()`（`:122`）扫 `assignments/*.json`（`AssignmentManager.cs:59-87`）；N 个 Worker 各扫一遍，纯浪费；③ **后台 spawn MCP** —— `Task.Run(RefreshMcpToolsAsync)`（`:188`）会在每个 Worker 里再拉起 N 个外部 MCP 服务器进程；④ 注册全局静态单例 `Instance`（`:165`），与主进程语义冲突。**对策**：Worker 侧使用一个手写的 `SlimHost`（只 `new` 工具执行真正需要的 6 个服务：`GitService`、`GitCheckpointStore`、`AssignmentManager`、`AgentConfigService`、`PersonaService`、`AgentTemplateService`、`LlmService`），**一个静态单例都不设**。B0-5 就是为了把这个边界在代码层面固定下来 |
| **R2** | **Worker 的 AOT 会真实编译 Core 那些从未验证的路径** | `AIShikikan.Core.csproj` 显式 `EnableAotAnalyzer=false`（因为类库无 `PublishAot`，SDK 规则 `EnableAotAnalyzer = (PublishAot \|\| IsAotCompatible)` 会翻转并翻出 9 处既有 `RequiresDynamicCode`）。`PublishAot=true` 的 Worker 项目会**真的**把这些路径链接一遍，首次发布**可能翻出裁剪/AOT 告警**。这是**预期内的、不是回归** —— 它正是本次改造的收益之一（技术债 #2 的一半：把"没人验证"变成"每次发布都验证"）。**不要为了让构建安静而加新的 `Suppress*`**；真出问题时按告警逐条修 Core，收益永久。修完后可考虑把 Core 的 `EnableAotAnalyzer=false` 去掉（属技术债 #2 的另一半，不在本次范围） |
| **R3** | **API Key 被两个进程读** | `providers.toml` 存着明文 API Key（`ProviderConfig` 的写点在 `AtomicFile.TryWriteAllText(AppPaths.ProvidersPath, …, "providers.toml(含 API Key)")`），环境变量兜底见 `Program.cs:186`。Worker 需要 `providers.toml` 的唯一理由是子代理输出压缩（`SubagentCompactService.CompactIfNeededAsync` 走 `llm.GetProvider(providerId)`，`SubagentCompactService.cs:41`）。这意味着**同一台机器上多一份明文 Key 的读取者**。缓解：Worker 只 `Load()` 一次并缓存在内存，不落盘、不打日志（`ProviderConfig` 的写点 context 字符串已提示含 Key，日志里不得出现序列化后的内容）。**接受这一风险**：用户本来就信任自己的 GUI 进程，而把压缩挪回主进程会破坏 9.2 的单写者与「子代理执行整体在 Worker」的边界 |
| **R4** | **Worker 侧错误文案无法 i18n** | 见 7.5。这是**边界而非缺陷**：现状工具错误文案本来就硬编码中文。写进代码注释，避免后来者"顺手修正" |
| **R5** | **`dotnet run` 下 BaseDirectory 与 Worker 产物目录不一致** | `debug.sh` 走 `dotnet run`（`debug.sh:130`），GUI 的 BaseDirectory 是 `src/AIShikikan.Gui/bin/Debug/net10.0/`，而 Worker 的产物在 `src/AIShikikan.Worker/bin/Debug/net10.0/`。候选 2 / 3 都不命中，只能靠候选 4（找 `AIShikikan.slnx` 再拼路径）或候选 1（`debug.sh` 注入 `AISHIKIKAN_WORKER_PATH`）。**对策**：两者都做 —— `debug.sh` 注入环境变量（确定性最高），候选 4 作为手工 `dotnet run`（不经脚本）的兜底。`debug.ps1` 同步 |
| **R6** | **仓库无测试（技术债 #1），只能靠 `doctor` 自检扩展** | 无任何测试项目、两个 workflow 均手动触发。分进程之后手测矩阵呈组合爆炸。**对策**：把 `doctor`（`Program.cs:171`）扩成唯一的自动化自检入口 —— 增加「Worker 定位」「Worker 握手」「NDJSON 帧往返」「`ToolResult.Detail` 六种卡片多态往返」「心跳判僵死」五项纯内存/无副作用检查，风格对齐既有的 `WorkspaceExecutionCoordinator.SelfCheck()`（`:440`，10 组场景，`Program.cs:200-205`）。**不要**为此引入测试框架：那会让本次改造的评审面从「进程边界」膨胀到「测试基础设施」 |
| **R7** | **两个进程写同一个日志文件** | `Log` 的消费循环用 `FileMode.Append` + `FileShare.Read` 打开按日滚动的单一文件 `AppPaths.LogDir/ai-shikikan-{yyyyMMdd}.log`（`Log.cs:183-186`），而两个进程的 `AppPaths.LogDir` 是**同一个目录**。Linux/macOS 上 `O_APPEND` 的单行追加写基本原子，串行日志可用；但 **Windows 上两个进程都以写方式打开同一文件时，后一个会因 `FileShare` 冲突抛 `IOException`**，而 `ConsumeLoopAsync` 的异常处理是**静默吞掉**（`Log.cs:195-198`）→ **该进程的日志系统永久失效、且零痕迹**。**对策**：(1) Worker 启动时调 `Log.SetProcessLabel("Worker")`（`Log.cs:61`，已存在的机制，每行都会带标签）；(2) Worker 的日志**改写到独立文件**（如在 `Log.Initialize` 之前把 `AppPaths.LogDir` 指向 `LogDir/worker/` 子目录，或给日志文件名加进程后缀）；(3) 把 `ConsumeLoopAsync` 的静默 catch 改为至少一条 `Log.Debug`（无 logger 时降级为 `Console.Error`）—— 这一条属顺手修，与 R1 同源（都是"静默失败"类问题） |
| **R8** | **`ToolRegistry` 是全局单例，工具集重建跨进程有延迟** | `ToolRegistry` 单例 + `CommanderRuntime.RebuildSubagentTools`（`:390`）在**右侧栏开关注**与**Plan 模式切换**时整体重建。但「整体重建」对主进程是立即生效（下一回合），对 Worker 是「发了 `worker/tools/sync` 之后才生效」。若主进程已经用新工具集发起了 LLM 请求而 Worker 尚未收到 sync，LLM 会调用一个 Worker 侧不存在的工具名 → Worker 返回「未知工具」错误。对策：B7 中 `worker/tools/sync` **在主进程侧同步等待确认**（`tools/sync` 返回 `result.ok` 后主进程才继续），并在 `WorkerProxyTool.CallAsync` 遇到「未知工具」时**重发一次 `tools/sync` 后重试一次**（幂等，代价一次 RTT），避免竞态变成用户可见的失败 |
| **R9** | **`SubagentGroupTool` 的并发闸跨进程后语义变化** | 并发上限 `MaxConcurrentSubagents = 4`（`AgentToolFactory.cs` 的 `SubagentGroupTool`）由同类的 `SemaphoreSlim` 实现。搬进 Worker 后，这个闸是**单个 Worker 进程内**的闸。因为一个 Worker 只服务一个会话（4.2），语义与今天**完全一致**（今天也是单会话引擎内 4 并发）。**但**：如果将来放宽 Worker 粒度（例如一个 Worker 服务多会话），这个闸就会变成跨会话的全局闸，行为静默改变。必须在 `MaxConcurrentSubagents` 的注释里补一句「本闸的作用域 = 单个 Worker 进程 = 单个会话」 |
| **R10** | **子代理在工作区内并发改文件，跨进程不改变任何东西** | 现状已有约束：`SubagentGroupTool.Description` 明确要求文件范围互不重叠，并把 `files` 写进子任务提示词（`SubagentGroupTool.RunOneAsync`）。程序**无法强制**外部 CLI 不碰某个文件（该方法注释已注明）。搬进程不改变这一点，不要误以为进程隔离带来了文件隔离 |
| **R11** | **`WorkspaceExecutionCoordinator` 的未接线 API（技术债 #14）在跨进程后更难验证** | `TryReserve` / `WaitGitWriteAsync` / `TryEnterGitWrite` / `TryBeginAssignment` 全部无生产调用方（`WorkspaceExecutionCoordinator.cs:89/95/126/187/237`）。跨进程后这些「进程内锁」对 Worker 侧无效。**对策**：B5 收口检查点写权时**顺手把 `TryEnterGitWrite` 接上**（主进程侧的回滚/Fork 确认期申请写门，Worker 侧的 git 写工具也申请同一把门，但两者是不同进程 —— 所以这把门必须由主进程仲裁，即「Worker 的 git 写工具调用经主进程审批闸，主进程在此处申请/释放写门」）。这条不属本次必做，但 B5 的架构位置天然适合接，且接了就能顺带消掉技术债 #14 的一半 |

---

## 11. 非目标

明确**不做**的事，以及不做之后留下的后果：

| 非目标 | 理由 / 后果 |
|---|---|
| **MCP 不搬** | 见 2.3。MCP 服务器挂死的问题留给超时与自动重连（技术债 #8） |
| **LLM 主循环不搬** | 引擎持有对话历史（`List`，非线程安全，`AgentEngine.cs:133`）、压缩状态（`_lastInputTokens`、`_conversation`）、per-turn CTS。跨进程搬它要序列化整个对话历史，收益（LLM 调用本来就慢、卡不死 UI）远小于代价 |
| **事件总线不搬** | `EngineEventHub.ShouldDeliver`（`EngineEventHub.cs:75`）依赖「当前活动会话」，而这是 GUI 概念（`SessionRuntimeRegistry` 注入的 `ActiveSessionId`，`SessionRuntimeRegistry.cs:273`）。Worker 侧只通过 `notify/*` 上报，由主进程转成事件 —— 投递规则一行不改 |
| **不引入 DI 容器** | 沿用现状的手写 `new`（`CommanderRuntime.Boot` 就是一串 `new`）。引入容器会与 `IsAotCompatible` / 源生成序列化的约束打架，且本次改造的评审面已经够大 |
| **不做跨平台 / 跨架构构建** | `build.sh` 头部注释已明确「仅构建当前平台，交叉编译由 CI 各 runner 分别完成」。Worker 遵循同一约束：本地 `./build.sh aot` 只出本平台产物，跨平台由 `.github/workflows/*.yml` 的矩阵负责 |
| **不做跨机器 / 远程 Worker** | 匿名管道隐含父子关系。要远程化需要换传输层并引入鉴权，那是完全另一个项目 |
| **不重构 `ChatPageViewModel`（约 1992 行）与 `ChatPageView.axaml`（879 行）** | 技术债 #11 独立立项。本次只在 `ChatPageViewModel` 上改 git 调用点（B0-7 / B5），不顺手重构 |
| **不修技术债 #12（重复代码：`Truncate` 5 份等）** | 与本次改造正交。B0 若顺手统一 `Truncate` 可以，但不得以此为名扩大 diff |

---

## 附录 A：核对代码时发现与既有文档不一致之处

以下差异在定稿前已逐条核对源码。**本节的存在本身就是给未来 agent 的提醒：不要照抄 `AGENTS.md` 的数字，要以代码为准。**

| # | 既有说法 | 代码实际 | 影响 |
|---|---|---|---|
| A1 | `AGENTS.md` 技术债 #3：发消息前「在 UI 线程拉起约 **6** 个 git 进程」 | **9 个**。明细见 1.1 的表：`ResolveContext`=2（`GitService.cs:64/66`）、`GetHeadSha`=1（`:667`）、`MarkCheckpoint`=3（`:275/282/290`）、`RefreshWorkspaceContext`=3（`:275`+`:286` 的 `IsClean`）。另有引擎线程每回合 4 个（`RosterBuilder.cs:186/195/198`） | 数量被低估 50%，问题比记录的更严重。不影响本方案结论 |
| A2 | `ChatPageViewModel.cs:1471` 与 `:1518` 的注释：合计「约 8 个进程：ResolveContext 3 + 检查点 3 + 上下文刷新 2」 | 分解口径两处都错：`ResolveContext` 是 **2**（`FindRepositoryRoot` 是纯文件系统遍历，`GitService.cs:706-719`，不起进程）；检查点是 **4**（漏算 `GetHeadSha`）；上下文刷新是 **3**（漏算 `IsClean`）。合计 9 | 这两条注释应随 B0-7/B5 一并更正 |
| A3 | 「复用 `WorkspaceResolver.Normalize`」 | 类名是 `GitWorkspaceResolver`，`Normalize` 是它的 `public static` 方法（`WorkspaceResolver.cs:17` 类、`:115` 方法）。`IWorkspaceResolver`（`:7`）接口上**没有** `Normalize` | 引用时须写全名，否则实现方会找不到 |
| A4 | 「`AgentExecutor` 是 static class 且 **6** 处读单例」 | **改造前** `AgentToolFactory.cs` 内有 **7** 处 `CommanderRuntime.Instance`（Plan 执行兜底、`ShouldRunInPlanMode`、`RosterOf`、三处人格文本、**`GitCreateCheckpointTool` 取 `Sessions.ActiveSessionId`**），另 `AssignmentManager.cs:210` **1** 处 → 执行侧合计 **8** 处。最后那一处（检查点会话 ID）在 git 工具里，很容易被漏掉。**定稿时 B0-1 已落地**：`AgentExecutor` 已是 `public sealed class`，依赖收进 `AgentExecutionScope`，文件内 5 处 `CommanderRuntime.Instance` 字样**全部在注释里**，代码中为 0 | 推论 2 的依据改用「改造前」的历史状态表述；`AgentExecutionScope` 的降级约定（其类注释）成为新的法律依据。`AssignmentManager.RunCliAsync` 的 1 处**仍待 B0-4 处理** |
| A5 | 「Worker **写**：… 只读配置（`agents.toml` / `providers.toml` / `mcp-servers.toml` / `personas` / `templates`）」 | 自相矛盾。按代码，这些文件**全部由主进程写**（`AgentConfigService` / `ProviderConfig` / `McpServerConfig` / `PersonaService` / `AgentTemplateService`），**Worker 一律只读**。Worker 的写者只有 `checkpoints/` 与 `assignments/` 两个 | 已按代码口径写入 9.2 并加了澄清注 |
| A6 | `AGENTS.md` 技术债 #17：「3 处读取路径仍裸读（会话列表主加载、会话标题、模板读取）」 | 前两处**已修**：`ChatService.ReadMetadata` 用 `AtomicFile.TryReadText(file, out var json, HasSessionId)`（`ChatService.cs:449`）；`SessionRuntimeRegistry.ReadTitle` 用 `AtomicFile.TryReadText(path, out var json, …)`（`SessionRuntimeRegistry.cs:430`，该方法注释 `:417-419` 已自陈"技术债 #17"并声明已加固）。**只剩 `AgentTemplateService` 的模板读取仍是裸读**（`AgentTemplateService.cs:62/235/237` 的 `File.ReadAllText`）。实际是 **1 处**，不是 3 处 | `AGENTS.md` 技术债 #17 的描述已过时，应更新为「1 处：`AgentTemplateService` 模板读取」。本方案不依赖此条 |
| A7 | 「避免 AOT 产出的 `*.so` 与 GUI 的 `libSkiaSharp`/`libHarfBuzzSharp` 同名冲突」 | **该冲突今天不存在**：Worker 只引用 Core，Core 不引用 Avalonia（`AIShikikan.Core.csproj` 无 Avalonia 依赖），因此 Worker 不产出 SkiaSharp/HarfBuzzSharp。真正会冲突的是 **`dotnet` 多文件布局下的同名托管 DLL**（`AIShikikan.Core.dll` 等，`package.sh:211-229`）；`.so` 同名是**将来**引入共享原生栈时的防御性理由 | 已按证据强度重排理由（7.3），DLL 冲突列为第一条，`.so` 降为防御性第二条 |
| A8 | `package.sh` 的 `install_app_tree` 需要改拷贝逻辑才能带上 `worker/` | **三条安装分支已经能带上**：`dotnet` 走整目录 `cp -a "$tree_src/."`（`:229`）；`aot`/`selfcontained` 走 `for d in "$tree_src"/*/` 子目录拷贝（`:244-248`）。真正缺的是**显式参数化 + 前置校验** | 改动量比预期小，但前置校验（缺失即 `die`）不能省 |
| A9 | （文档未提及） | **两个进程写同一个日志文件**。`Log` 用 `FileMode.Append` + `FileShare.Read` 打开按日滚动的单一文件（`Log.cs:183-186`），两个进程的 `LogDir` 相同。Windows 上后开的进程会因 `FileShare` 冲突抛 `IOException`，而消费循环的 catch 是**静默**的（`:195-198`）→ Worker 日志永久静默丢失 | 新增为风险 R7，对策含「Worker 日志写独立文件」与「把静默 catch 改为至少一条痕迹」 |
| A10 | （文档未提及） | `AppShell.Dispatch`（`AppShell.cs:59-93`）是**第四条子代理执行路径**，与 `run_<agent>` 并行存在，且**不走审批、不申请工作区执行权、不做输出压缩**（其注释 `:54-58` 已自陈）。它同样调 `AgentExecutor.ResolvePersonaText` / `BuildFinalPrompt` 与 `AssignmentManager.RunSyncAsync` | B0-4 与 B6 必须把它一并纳入，否则 GUI 手动分派会与 Worker 侧行为不一致 |
| A11 | 本文档初版 4.3 的 L6：「备用『目录级长空闲超时』**默认 0（关闭）**，启用时必须与 L3 互斥（有会话引用则永不因空闲关闭）」 | **已作废**。`WorkerPoolOptions.DirectoryIdleTimeout` 现为 `TimeSpan.FromMinutes(1)`（`WorkerPool.cs` 该属性），且是**独立的槽位级规则**（`WorkerDirectoryGroup.DetachIdleClients`）：判据为 `ActiveCallCount == 0` **且** `now - LastActivityTicks >= 阈值`，只摘句柄保留槽位，与 L3 叠加而非互斥 | 已按 4.3.1 全量重写；「有会话引用则永不因空闲关闭」这句话已从**现行规则**中删除（仅在 4.3.1 的作废对照与本行作为历史引述保留）。⚠️ **代码内还有两处未跟上的注释**：`WorkerPool` 类 remarks 的 L6 条目仍写「保留参数位, 默认 0(关闭)」，`WorkerDirectoryGroup.LastActivityTicks` 的 remarks 仍写「该超时默认关闭」 |
| A12 | 「空闲回收没有任何验证入口」/ `WorkerSelfCheck` 只覆盖 7 组场景 | **已扩展为 12 组**（`WorkerSelfCheck.ScenarioCount = 12`），S8–S12 专测空闲回收：S8 空闲摘句柄但**保留槽位与目录组**、S9 有在飞调用绝不回收、S10 `MarkTurnActive` 期间不回收且置回 `false` 后恢复、S11 `Touch()` 刷新 `LastActivityTicks`、S12 `DirectoryIdleTimeout = TimeSpan.Zero` 时不回收。为让"是谁触发的这次回收"没有竞态，这 5 组用 `CreateIdleProbePool`：内联传输（不 spawn 进程）+ `ReconcileInterval = TimeSpan.Zero`（关掉后台循环）+ `liveSessionProvider` 返回**含本会话**的集合（否则 `ReconcileNowAsync` 会静默跳过整轮、场景全绿而空转）+ 200ms 阈值 | 空闲回收的**判据**已被自动验证；仍未验证的是**生产配置**（真管道 + 15s 自适应节拍 + 1 分钟阈值）与**生产路径**（无句柄可回收，见 `AGENTS.md` 技术债 #20/#21）。B7 验收锚点已补 ⑥⑦ 两条 |

---

*本文档随代码演进更新。任何与代码不符的描述，以代码为准并回写本文档的对应小节与附录 A。*