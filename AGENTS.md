# AGENTS.md

面向 AI 编码助手 / Agent 的项目指南。开始工作前请阅读本文件。

## 项目概览

AI-Shikikan 是一个用 **.NET 10 / C#** 开发的「Agent 指挥官」：把 Claude Code、OpenCode、Codex CLI、Gemini CLI、DeepSeek Harness 等终端 Agent 集合起来，由 AI 统一调度完成复杂任务。

用户请求 → AI 指挥官 → 分析任务 → 调用子 Agent → 汇总结果 → 返回用户。

## 项目结构

三个项目，源码全在 `src/` 下，仓库根只留构建脚本与元数据：

```
/                              - 仓库根（无源码）
  AIShikikan.slnx              - 唯一解决方案文件（根目录裸 dotnet build 即用它）
  Directory.Build.props        - 三个项目共用的 MSBuild 属性（TFM / AOT 开关 / 从 VERSION 注入版本）
  VERSION / LICENSE / CHANGELOG.md / AGENTS.md / README.md
  build.sh build.ps1           - 发布构建（publish + 打包归档，GUI 与 Worker 同时产出）
  debug.sh debug.ps1           - 本地调试（dotnet run，不 publish）
  set-version.sh               - 一键改版本号
  Packagers/                   - Linux 系统包打包（deb / rpm / pacman），CI 引用
  templates/                   - 面向用户的 example 文件
  static/                      - README 插图
  docs/plans/                  - 方案与决策归档
  src/
    AIShikikan.Core/           - 核心逻辑（类库，零 Avalonia 依赖）
      AppPaths.cs / AppInfo.cs - 路径解析与应用信息
      Logging/ Models/ Serialization/
      Services/                - 全部业务服务（见下节）
      DefaultConfig/           - 内嵌默认配置（agents.toml 等，随 Core 程序集打包）
    AIShikikan.Gui/            - Avalonia 可执行项目（程序集名 AIShikikan.Gui）
      Program.cs / App.axaml(.cs) / ViewLocator.cs / app.manifest
      ViewModels/ Views/       - Avalonia MVVM
      Services/                - GUI 层服务 (DynamicThemeService, ImageAttachmentService, ColorExtractionService)
      Resources/               - 双语字符串 + Markdown 主题
      Assets/                  - 字体、logo
    AIShikikan.Worker/         - 独立 Worker 进程（headless Exe，无 Avalonia）
      Program.cs               - 命令行入口 + 日志初始化 + 退出收尾
      WorkerSlimHost.cs        - Worker 侧组合根（手写 new，**绝不允许**调 CommanderRuntime.Boot）
```

三个 csproj 的分工：

| 项目 | 类型 | 关键内容 |
|---|---|---|
| `src/AIShikikan.Core` | 类库 | LLM/MCP SDK、Tomlyn、YamlDotNet；**不引用任何 Avalonia 包**；`DefaultConfig/*.toml` 以 LogicalName `AIShikikan.Core.DefaultConfig.*` 内嵌 |
| `src/AIShikikan.Gui` | Exe（`PublishAot=true`） | Avalonia 全家桶、CommunityToolkit.Mvvm、ScottPlot、MaterialColorUtilities；`ProjectReference` 指向 Core；`AvaloniaResource Assets\**`；`Resources/Strings*.resx`（卫星资源 `en/AIShikikan.Gui.resources.dll` 依赖程序集名，勿改） |
| `src/AIShikikan.Worker` | Exe（`PublishAot=true`，**无任何 Avalonia 引用**） | 承载非 MCP 的工具与子代理执行；只 `ProjectReference` Core；**不引用 resx**（错误文案硬编码中文） |

**引用图（硬约束）**：`Core ← Gui`、`Core ← Worker`。**Core 不得引用 Worker**（会成 `Core → Worker → Core` 的环），**Worker 不得引用 Gui**（会把整个 Avalonia 依赖面拖进子进程，还会隐式依赖 GUI 程序集名——卫星资源程序集名由它推出）。

> 为什么 Worker 的组合根（`WorkerSlimHost`）刻意放在 **Worker 项目**里而不是 Core：装配代码里那些「绝不能 new 什么」的决定属于**进程边界知识**。放进 Core 意味着 Core 反过来知道 Worker 的存在（架构文档 §7.1 明确禁止）。放 Worker 项目里，Core 只留 `IWorkerToolHost` 一个纯契约。

> ⚠️ **不要改程序集名/项目名**。`Strings.cs` 的 `ResourceManager` 名为 `AIShikikan.Gui.Resources.Strings`（由 `RootNamespace` + 目录推出），卫星资源程序集名又由程序集名推出；任一改动都会让英文界面静默回退为中文。

根目录文件：

| 文件 | 说明 |
|------|------|
| `AIShikikan.slnx` | 解决方案（唯一，含 3 个项目），`dotnet build` / `dotnet clean` 直接用 |
| `VERSION` | 单一版本源（软件与 CI 共用，勿在代码中硬编码） |
| `Directory.Build.props` | 三项目共用属性：TFM / Nullable / ImplicitUsings / `IsAotCompatible` / 裁剪与 AOT 静音开关 / 从 VERSION 注入版本 |
| `build.sh` / `build.ps1` | 发布构建脚本（**仅当前平台**：命令 `all`（默认，三变体）/ `aot` / `selfcontained` / `dotnet` / `clean`，环境变量 `CONFIGURATION` / `VERSION`；无 `ARCH`、无 `AOT_MODE`）。**每个变体同时产出 GUI 与 Worker** |
| `debug.sh` / `debug.ps1` | 本地调试：`dotnet run --project src/AIShikikan.Gui/AIShikikan.Gui.csproj`，余下参数原样透传给程序；`--no-build`（或 `NO_BUILD=1`）跳过编译。启动前先编译 Worker（框架依赖）并 `export AISHIKIKAN_WORKER_PATH` |
| `.github/workflows/release.yml` | 手动触发的 Release 发布流程（三种变体的归档里都含 `worker/` 子目录） |
| `.github/workflows/debug.yml` | 手动触发的构建产物辅助 workflow |
| `Packagers/` | Linux 系统包打包：`linux/package.sh`（deb/rpm/pacman 构建脚本）+ deb(control) / rpm(spec) / pacman(PKGBUILD) / .desktop 模板，两个 workflow 均调用 `package.sh`。Worker 装到 `<prefix>/lib/<pkg>/worker/` |
| `set-version.sh` | 一键修改版本号（同步 VERSION / PKGBUILD / `src/AIShikikan.Gui/app.manifest` / AGENTS.md / 脚本与 CI 回退值） |

> ✅ 根目录只有 `.slnx` 一个解决方案文件，裸 `dotnet build` 可直接用（重组前根目录同时有 csproj 与 slnx，会报 MSB1011）。
>
> ⚠️ 根 `Directory.Build.props` 里 `SuppressTrimAnalysisWarnings` / `SuppressAotAnalysisWarnings` 是**属性、不是属性组**，三个项目都会继承，不要在单个 csproj 里「顺手删掉」。
>
> ⚠️ `AIShikikan.Core.csproj` 显式写了 `<EnableAotAnalyzer>false</EnableAotAnalyzer>`。SDK 规则是 `EnableAotAnalyzer = (PublishAot || IsAotCompatible)`，Core 作为类库没有 `PublishAot`，规则会翻转并翻出 9 处既有的 `RequiresDynamicCode` 用法。**这不是「关掉检查就没事」**——而且 `AIShikikan.Worker.csproj` 里的 `PublishAot=true` **不会**翻转它：`EnableAotAnalyzer` 只在**当前正在编译的那个项目**里生效，Core 作为被引用的独立程序集早已编译完毕。所以 Worker 侧看不到 Core 那 9 处告警属预期，**不代表「已修复」**。AOT 兼容性的真实判据只有 `./build.sh aot` 的发布（ILLink + ILC 真的会编译到 Core 的 IL），见已知技术债 #2。

### Worker 架构（速览）

完整的方案、决策理由与取舍在 **`docs/plans/worker-architecture.md`**（约 940 行；L6 空闲回收的口径见其 4.3.1）。本节只给导航：

| 概念 | 一句话 |
|---|---|
| 进程边界 | 非 MCP 的工具（4 个文件工具 + 5 个 git 工具 + 3 个子代理工具）与子代理执行搬到 `AIShikikan.Worker` |
| 传输 | stdio 匿名管道 + **NDJSON（一行一帧）** 的 JSON-RPC 2.0 子集，与 `McpClientBase` 手写的 stdio JSON-RPC 同构 |
| 抽象 | `IToolTransport`（一次工具调用怎么执行）之上有两个实现：`PipeTransport`（真子进程）与 `InlineTransport`（进程内降级） |
| 生命周期 | 实例粒度 = **（工作目录 × 会话）**；懒启动 / 引用计数 / 崩溃重拉 / 指数退避 / **空闲回收**，全部由 `WorkerPool` 编排（空闲回收的**判据由 `WorkerClient` 观测、槽位处置由 `WorkerDirectoryGroup` 执行**，详见下方「Worker 生命周期规则」） |
| 可见性 | 降级**绝不静默**：状态栏常驻标记 + `doctor` 的「Worker 定位」/「Worker 端到端」两项 + `Log.Warn("Worker", …)` 三处同时透出 |

`src/AIShikikan.Core/Services/Worker/` 逐文件职责：

| 文件 | 职责 |
|---|---|
| `WorkerProtocol.cs` | 协议**契约常量**：方法名、`ProtocolVersion`、超时预算、`MaxLineChars`、`MakeWorkerKey(workDir, sessionId)` 跨进程身份键 |
| `WorkerMessages.cs` | 14 个协议 DTO（hello / tools list / sync / call / 输出通知 / 心跳 / 取消 / 分派 / askUser / ok） |
| `WorkerLocationResult.cs` | 定位结果类型 `WorkerLocationResult` + `WorkerLocationSource` 枚举（带 `Detail` / `AttemptedPaths` 供三处透出） |
| `WorkerLocator.cs` | **5 级候选**定位 Worker 可执行文件（环境变量 → 同目录 → `worker/` 子目录 → 上溯 `.slnx` 拼 bin → 未命中）。纯读、不抛异常 |
| `IToolTransport.cs` | 传输抽象接口 + 共享辅助 `ToolTransportContract`（外部取消优先等三条约定的单一落点） |
| `InlineTransport.cs` | **降级路径**：主进程内联执行，零 IPC，`IsConnected` 恒 true，刻意不发 `toolOutput` 通知 |
| `PipeTransport.cs` | **真实管道**：spawn 子进程、握手、RPC 配对、心跳僵死检测、优雅关闭 + `Kill(entireProcessTree)` 兜底 |
| `WorkerFrameCodec.cs` | NDJSON 帧读写内核：`WorkerFrameWriter` / `WorkerFrameReader` / `WorkerRpcCore`（请求-响应配对）/ `WorkerRpcException` |
| `IWorkerToolHost.cs` | Worker 侧纯契约：`IWorkerToolHost`（工具快照 + 执行 + `BindHello`）与 `IWorkerToolHostSync`（`ApplyToolsSync`） |
| `WorkerServer.cs` | Worker 侧服务端：接管 stdin/stdout、跑读循环、处理 `hello`/`tools/list`/`tools/sync`/`tools/call`、串行写出 `notify/*` |
| `WorkerToolDescriptorFactory.cs` | **Core 内唯一的**工具分类落点（`Kind` / `RequiresGitWrite` / 空名剔除 / schema 降级）。Worker 侧与内联降级侧共用同一份，两边各写一份必然漂移 |
| `WorkerClient.cs` | 主进程侧的连接句柄：持有 transport、按 `callId` 路由实时输出、故障可见性（`Faulted` / `LastFault`）**+ 活动度观测**（`ActiveCallCount` / `IsBusy` / `LastActivityTicks` / `Touch()`，空闲回收的**唯一依据**；`CallToolAsync` 在 `finally` 里减计数） |
| `WorkerProxyTool.cs` | `WorkerClient` → `ITool` 的进程外包装（形态照抄 `McpProxyTool`），**尚未有生产调用方**，见「已完成 vs 尚未接线」 |
| `ToolCardDetailCodec.cs` | `ToolCardDetail` 多态卡片详情的判别符编解码（三张表：判别符 ↔ 具体类型 ↔ 序列化器）+ `SelfCheck()` |
| `WorkerPool.cs` | 进程池与生命周期编排：`WorkerPoolOptions`（退避 / **空闲阈值 `DirectoryIdleTimeout`（默认 1 分钟）** / 对账 / `PipeEnabled`）+ `WorkerPool`（分组、Acquire、退避、崩溃重拉、健康聚合、**`MarkTurnActive`**、**`ReconcileNowAsync` 的两段式回收**、**`EffectiveReconcileInterval` 自适应节拍**） |
| `WorkerDirectoryGroup.cs` | 一个工作目录下的全部会话槽位：会话引用计数、`(会话 → WorkerClient)` 映射、`Reconcile`（**会话级**回收：摘整个槽位）、`DetachIdleClients`（**槽位级**空闲回收：摘句柄、**保留槽位**）、`MarkTurnActive` / `Slot.TurnActive` |
| `WorkerHealth.cs` | 健康快照类型：`WorkerMode` 枚举 + `WorkerHealthEntry` + `WorkerHealth`，同时喂 doctor 与状态栏 |
| `WorkerSelfCheck.cs` | **12 组端到端场景**的自检（前 7 组：协议编解码 / 工具执行 / 取消传播 / 身份键一致性 / 卡片编解码 / 降级可见性 / 管道真起进程；**S8–S12 为空闲回收**：空闲被摘句柄而槽位保留 / 有在飞调用绝不回收 / 回合进行中不回收且置 false 后恢复 / 活动度刷新 / 阈值置 Zero 时不回收）。⚠️ S8–S12 用的是**专用探针池**（内联传输 + 手动 `ReconcileNowAsync` + 200ms 阈值），不是生产配置 |

### Core/Services 内部结构

以下均位于 `src/AIShikikan.Core/Services/`（最后几个 `.cs` 直接在该目录下）：

```
Engine/      - Agent 调度引擎 (AgentEngine, AssignmentManager, RosterBuilder,
               RosterConfigService, SubagentCompactService)
Agents/      - Agent 定义与配置加载 (CliAgentDefinition, CliAgentRunner, AgentConfigService)
Llm/         - LLM 抽象: IChatCompletionsClient + OpenAI/Anthropic 双实现,
               LlmService(Provider路由), ProviderConfig, ModelListService,
               ChatTypes, ThinkingLevel(思考深度), LlmMessages
Personas/    - 人格/专家管理 (YAML frontmatter + Markdown)
Templates/   - 任务模板
Tools/       - 工具框架 (ITool/ToolResult/ToolContext/ToolRegistry/ToolPathSanitizer) + Builtin 文件工具
               (ReadFile/Glob/Grep/ListDirectory/AskUser)
Runtime/     - AgentExecutionScope(工具对进程全局状态的显式依赖注入, 替代反向读单例)
               + AgentToolFactory: 工具集组装 + AgentExecutor + 子代理工具(AgentExecutionTool/
               AssignTaskTool/SubagentGroupTool) + 5 个 git_* 工具
               (注意子代理工具不直接操作 git —— 原先那个只被存成字段从不读取的 git 构造参数已删除,
                留着只会让人误以为子代理工具有 git 副作用)
Worker/      - 独立 Worker 进程协议与传输层 (见上节「Worker 架构」表格, 18 个文件)
Git/         - Git 服务 (GitService: HEAD commit + tag 检查点、Reset/Revert/Fork、
               ScanLegacyArtifacts), GitCheckpointStore(检查点持久化), GitTypes
Mcp/         - MCP 客户端: McpConfigService(TOML 配置), McpStdioClient(手写 stdio
               JSON-RPC 2.0, 零依赖 AOT 兼容), McpHttpClient(Streamable HTTP / SSE),
               McpClientBase(共享 JSON-RPC 内核), McpService(连接管理+路由),
               McpProxyTool(ITool 桥接, 工具名 mcp_<serverId>_<toolName>)
Session/     - 会话运行时: SessionRuntimeRegistry(会话引擎注册表 + 回合排队队列),
               WorkspaceExecutionCoordinator(工作区执行权), WorkspaceResolver(目录 → worktree+分支),
               EngineEventHub(事件总线)
Usage/       - 用量统计持久化 (UsageStatsService) + 模型档案 (ModelProfileService, 可选 models.toml)
Serialization/ - AppJsonContext(AOT 源生成上下文) + AtomicFile(原子写)
ChatService.cs - 会话与消息持久化 (ChatSession/ChatMessage 读写)
ChatTitleService.cs - 首条消息后自动生成会话标题
DefaultConfig.cs - 内嵌默认配置的写出（providers/agents/mcp-servers.toml）
ThemeService.cs - preferences.toml 读写（明暗/语言/字体/背景）
I18nService.cs - 语言切换
```

> ⚠️ **背景图主色提取已移出 Core**：`ColorExtractionService.cs` 现在在
> `src/AIShikikan.Gui/Services/`（命名空间 `AIShikikan.Gui.Services`）。它经
> `MaterialColorUtilities` 依赖 Avalonia 的 `Color` 类型，放在 Core 会让 Core
> 传递引入 Avalonia 引用、破坏分层。只有 `DynamicThemeService` 与
> `MainWindow.axaml.cs` 用它，两者本就在 GUI 层，改动时别再加回 Core。

> 模型定义分散在 `src/AIShikikan.Core/Models/`（如 `GitCheckpointRecord.cs`）；`GitTypes.cs` 只放 Git 命令结果类型，
> `LegacyArtifactScanResult` 则定义在 `GitService.cs` 末尾。

启动外观：`CommanderRuntime.Boot()`（`src/AIShikikan.Core/Services/CommanderRuntime.cs`）初始化配置目录、示例文件、全部服务与工具集，并注册到静态 `Instance`。GUI 外壳单例 `AppShell`（`src/AIShikikan.Gui/ViewModels/AppShell.cs`）持有 Runtime。

⚠️ **`Boot` 里的装配顺序不是随意的**：`Instance` 赋值 → 注册固定工具 → 注册子代理工具 → **最后**才 `Instance.Workers = new WorkerPool(...)`。因为降级用的内联传输工厂要读 `Registry`（按工具名找执行入口）与 `BuildToolScope()`（人格 / roster / Plan 授权），塞不进对象初始化器（那里 Registry 还是空的）。症状是"降级后所有工具都报未知工具"。同理 `GitWorkspaceResolver` 提成局部变量与 Worker 池**共用同一个实例** —— 它带 2s TTL 缓存（每次未命中要起两个 git 进程），各建一个等于把命中率对半砍。

> ⚠️ **`CommanderRuntime.Boot()` 只属于主进程。** Worker 侧的组合根是 `src/AIShikikan.Worker/WorkerSlimHost.cs`，见「工具分层与 Worker 边界」。

## 关键机制

### 引擎与回合

- **引擎循环**：`AgentEngine` 将人格 + Roster 提示词发给 LLM，执行工具调用直至回合结束，事件流 `AgentEngineEvent` 驱动 UI。工具循环上限 `EngineOptions.MaxTurns`（默认 10），每轮发送最近 `MaxHistoryMessages`（默认 40）条。
- **同会话回合串行**：`SessionRuntime` 持有单消费者 `Channel` 队列，`EnqueueTurnAsync` 入队后串行执行。**不要绕过它直接调 `Engine.RunTurnAsync`** —— 那会并发写 `_conversation`（普通 `List`）。GUI 在有排队时禁用发送按钮。
  > `PendingTurnCount` 只计「还在排队等待」的，不含正在执行的那个（UI 用 500ms 轮询它来决定是否显示排队态）。
  > ✅ 排队态可视化已接：`IsQueued` 绑定在 `ChatPageView.axaml` 的输入框右侧（`TextBlock IsVisible="{Binding IsQueued}"`），文案走 `QueuedBadgeText`。⚠️ 该文案目前是 `ChatPageViewModel` 里的**硬编码常量** `"排队中 ({0})"`，尚未收进 resx（代码里有 `TODO(i18n)` 标注了建议键名 `Session_QueuedBadge`）——英文界面下这一处会露出中文。
  > ⚠️ 另一个**产品决策待定**项（代码里标为 F14）：输入框在发送中被禁用，用户既无法修改也无法**预输入下一条**；而排队态下反而能输入、发送键却被 `CanSendMessage`（含 `!IsQueued`）禁用，无法预先排队。两种语义（"专注当前轮" vs "允许连发"）都有道理，需要产品拍板，不要擅自改。
- **自动压缩**：上下文占用达 `AutoCompactThreshold`（默认 0.85 × 窗口）时，`CompactConversationAsync(ct, providerId, model)` 把较早历史交给 LLM 摘要，保留最近 `KeepRecent = 6` 条。
  > `KeepRecent` 是方法内的局部常量，切点还要过 `FindToolSafeCutoff` —— 保留段不得以 tool 结果 / user 开头，也不得切断 `assistant(tool_calls)` 与其结果。摘要为纯文本，图片不进摘要但标注数量，避免后续上下文误判"用户从未发图"。

### 子 Agent

- **三个工具**：`run_<agent>`（单个）、`assign_task`、`run_subagents`。均无 `mode` 参数。三者都是**阻塞式**工具（`await` 到子 Agent 终态才返回，不 fire-and-forget）；其中 `run_subagents` 内部用 `Task.WhenAll` **并发**执行多个子代理。
  > ⚠️ 工具描述里写的「`assign_task` 分派返回 assignmentId」与实现不符——它同样阻塞到终态并直接返回结果，assignmentId 不在返回值里。
- **审批策略一致**：三个工具都需要用户批准（`run_<agent>` 透传 agent 的 `require_approval`，`assign_task` 与 `run_subagents` 恒为 true）；MCP 工具恒需批准。
- **统一在当前分支工作**：子代理**不执行 `git switch -c`**，不建 worktree。产出由每条用户消息的检查点兜底。
- **plan_args**：Agent 可配置 `plan_args`（Plan 模式附加 CLI 参数）；主对话处于 Plan 模式且该子代理的会话条目开启 `UseInPlanMode` 时，启动进程会追加 plan_args。Plan 模式下 `CommanderRuntime.SetPlanMode` 三层过滤未授权子代理：仅注册授权 Agent 的 `run_<agent>`（无授权者连 `assign_task`/`run_subagents` 一并移除）、Roster 注入仅列出授权条目、`AgentExecutor` 执行兜底拒绝。
- **Compact Subagent**：会话级子代理配置（AgentPanel 每条目独立开关，存于 roster.json `CompactEnabled`）。开启后该子代理输出超过 `MinLengthToCompact`（2000 字符）时先经 LLM 压缩为纪要，超过 `TargetLength`（4000 字符）则硬截断。压缩失败或被取消时原样返回。
  > ✅ provider/model 已转发：`AgentExecutor` 把 `ctx.ProviderId` / `ctx.Model` 传进 `CompactIfNeededAsync`。这两个字段由 `CommanderRuntime.ApplyLlmRouting(engine)` 在**每回合开始前**写进 `EngineOptions`（fail-safe：不调用就退回"跟随全局"，而构造期固化是 fail-unsafe 的），再由 `AgentEngine` 注入 `ToolContext`。Worker 协议里也各有对应字段（`WorkerToolCallRequest.ProviderId` / `.Model`），两侧行为一致。
- **取消与超时区分**：`CliAgentRunResult.Cancelled` 与 `TimedOut` 分开。子代理执行路径的 `catch(Exception)` 之前必须先 `catch(OperationCanceledException) { throw; }` —— 见下方「Worker 新增铁律」第 1 条，在 Worker 语境下写反后果更严重。

### 工具集

固定工具由 `AgentToolFactory.CreateCoreTools` 注册：

| 工具 | 用途 | 需批准 |
|---|---|---|
| `read_file` / `glob` / `grep` / `list_directory` | 文件读取与检索 | 否 |
| `git_status` | 查分支、脏状态、文件列表 | 否 |
| `git_add` | 暂存文件或全部改动 | 是 |
| `git_commit` | 提交 | 是 |
| `git_create_checkpoint` | 把当前 HEAD 标记为检查点 | 是 |
| `git_diff` | 查两个 sha 之间的差异 | 否 |
| `ask_user` | 反问用户 | 否 |

子代理工具由 `CreateSubagentTools` 注册，**随聊天页右侧栏可见性动态增删**（`CommanderRuntime.SetSubagentToolsVisible`）。右侧栏关闭时移除 `run_<agent>` / `assign_task` / `run_subagents` 并清空 Roster 注入（下一回合生效），重新打开时重建。

MCP 工具在 `CommanderRuntime.Boot` 后台连接，桥接为 `mcp_<serverId>_<toolName>` 注册。

### 工具分层与 Worker 边界

**留在主进程**（`AIShikikan.Gui`）：

| 工具 / 组件 | 为什么留主 |
|---|---|
| `ask_user` | 本质是 UI 交互。审批与提问的事件机制（`EngineApprovalRequested` / `EngineQuestionRequested`）全在主进程，留在这里则**零改动**；绕 Worker 出去只会多一跳，还必须自己套超时（协议里的 `request/askUser` 位保留但**未启用**） |
| **全部 MCP**（`McpService` / `McpProxyTool` / stdio·http·sse 客户端） | 见下 |

**搬到 Worker**（`AIShikikan.Worker`）：

| 内容 | 说明 |
|---|---|
| 4 个文件工具 | `read_file` / `glob` / `grep` / `list_directory`（耗时扫描会卡 UI 线程，隔离的收益最直接） |
| 5 个 git 工具 | `git_status` / `git_add` / `git_commit` / `git_create_checkpoint` / `git_diff` |
| 3 个子代理工具 | `run_<agent>` / `assign_task` / `run_subagents` |
| `AgentExecutor`、`AssignmentManager` **执行侧** | 子代理进程树（`CliAgentRunner`）整体进 Worker |

> **为什么 MCP 全留主进程**：`McpProxyTool` 是全仓唯一**不消费 `ToolContext` 任何字段**的层（它的 `ExecuteAsync` 收下 `ctx` 后从不读）。这意味着它已经是现成的"进程外工具 → `ITool`"范式，把它留在主进程就能把**跨进程面压到最小**（MCP 侧的会话状态、`AskUser`、实时输出、审批都在同一边）。`WorkerProxyTool` 照抄它做成 `ITool` 的进程外包装，于是 **`AgentEngine` 与 `ToolRegistry` 一行都不用改** —— 工具循环、审批闸、结果落库、卡片渲染全部复用现成链路。
>
> ⚠️ **`WorkerProxyTool` 与 `McpProxyTool` 必须是两个独立类型，不可抽公共基类**：`CommanderRuntime.RefreshMcpToolsAsync` 用 `Registry.UnregisterWhere(t => t is McpProxyTool)` 注销 MCP 工具。若 Worker 工具挂在同一类型上、或挂在它的**基类**上，这条谓词会在每次"重连 MCP"时把 Worker 工具**连坐删掉** —— 表现为"动一下 MCP 设置，Worker 工具集体消失"，没有任何报错。两个代理工具的名字前缀（`mcp_` / `worker_`）天然不撞名，但**类型隔离是唯一真正的护栏**。
>
> **审批零改动的原因**：`RequiresApproval` 是**代理对象在主进程里的属性**，由 `AgentEngine` 在执行前求值。所以无论工具在哪个进程跑，审批卡片、Plan 模式过滤、`ask_user` 都留在主进程。同理 `InlineTransport`（降级）路径下，内联执行永远发生在"已经批过"之后。

> ⚠️ **Worker 绝不允许调用 `CommanderRuntime.Boot()`**。Boot 做三件对 Worker 是灾难的事：① 写出 6 类配置文件（Worker 是被派生出来执行工具的短命子进程，不是应用入口；让它去写用户配置，等于把"谁有权改配置"扩散到一个随时可能被杀、且主进程可能正并行读写同一文件的进程 —— `providers.toml` 里还有 API Key，覆盖 = 密钥丢失）；② 构造期扫 `checkpoints/` 与 `assignments/` 全量记录；③ **后台 `Task.Run(RefreshMcpToolsAsync)` 真的 spawn MCP 子进程**（Worker 若也连一遍，就变成"一次 git 工具调用顺带起了三个 MCP 进程"，且它们不随 Worker 的 kill 走 → 孤儿堆积）。
> `WorkerSlimHost` 是瘦装配组合根，刻意**不** new：`McpService` / `ChatService` / `UsageStatsService` 的任何写入路径 / `SessionRuntimeRegistry` / `AgentEngine` / `WorkspaceExecutionCoordinator` / `EngineEventHub`。

> ✅ **已完成 vs 尚未接线（最容易误读的一处）**：`WorkerPool` 已在 `CommanderRuntime.Boot` 装配、`doctor` 已能真起进程跨进程自检、`ChatPageViewModel` / `AppShell` / `App.axaml.cs` 已接上 Release 与 Shutdown —— **但聊天页发消息的路径上，工具仍走主进程内的 `ToolRegistry`**。全仓**没有任何 `new WorkerProxyTool(...)` 的生产调用方**，`WorkerPool.AcquireAsync` 的唯一调用方是 `WorkerSelfCheck`。把工具注册从本地 `Registry` 切到 Worker 代理是**下一阶段**的事（出口已备好：`CommanderRuntime.BuildWorkerToolsSyncRequest()` 供"刚 Acquire 完"的一方补发一次 sync，`PendingWorkerToolsSync` 给回合开头的确认钩子预留）。看到 `WorkerPool` 已装配就以为"工具已经全在子进程跑"是错的。**同一条理由也适用于空闲回收**：`DirectoryIdleTimeout` 默认 1 分钟、判据与节拍都已就位，但生产里没有任何东西会往池里放 Worker 句柄，因此**空闲回收今天在真实会话里不会发生任何事**。

### Worker 生命周期规则

实例粒度 = **（工作目录 × 会话）**，`WorkerProtocol.MakeWorkerKey(workDir, sessionId)` 是身份键（SHA-256 前 8 字节 → 16 位十六进制）。

| 规则 | 行为 | 为什么 |
|---|---|---|
| **懒启动** | 首回合首次需要工具才 spawn，**不预热** | 预热意味着打开应用就 fork N 个进程；而绝大多数会话根本不会用到 git 工具 |
| **目录级存活** | 目录内会话引用计数 ≥ 1 → 存活；**= 0 则关闭该目录下全部 Worker** | Worker 占着工作目录的 git 写锁，无人用却留着是纯负担 |
| **换目录** | 会话 WorkDir 变更 → `Release` 旧目录（-1），下一次 `AcquireAsync` 自动挂进新目录 | 不释放的话旧目录引用计数永远归不了零（该会话还活着，定时对账也回收不到） |
| **崩溃/僵死重拉** | 订阅 `WorkerClient.Faulted`，**只重拉那一个** (目录, 会话)，其余一概不动 | 全局重启会把无关会话正在跑的工具一起打断 |
| **空闲回收（槽位级，默认 1 分钟）** | `WorkerPoolOptions.DirectoryIdleTimeout` **默认 `TimeSpan.FromMinutes(1)`、已启用**：某个 (目录, 会话) 的 Worker **连续 1 分钟既没有在飞工具调用、又没有任何新活动**就关掉它 —— **只摘句柄、保留槽位**，该会话仍留在目录组里，下次 `AcquireAsync` 走懒重建。置 `TimeSpan.Zero` 即完全关闭，退回"只有 L3 回收" | 每个 Worker 是一份独立的 Core 运行时（JIT 后的代码页 + 常驻堆，AOT 下可执行映像约 12MB），**空闲的既不产出价值又占内存、还持着工作目录的 git 上下文**；而下次调用本来就是懒重建（约 100~300ms），重建成本远低于长期占着的内存。⚠️ **与「目录级存活」叠加，不是替代**：L3 管"没人要的目录"（会话数归零 → 整组退役，**不等超时**），本条管"有人要但一直闲着的 Worker"。改造前它只是把 L3 推迟到超时点、且只作用于已归零的组，因此**从来不会**关闭"会话还活着但空闲"的 Worker |
| **同步** | 按目录分组 + **每组一把短锁**，绝不用一把全局锁 | 全局锁会把"A 目录的 Worker 握手慢（最坏 30s）"传导成"B 目录的新会话也起不来" |

**空闲回收（L6）的判据与接线**（实现落在 `WorkerDirectoryGroup.DetachIdleClients` + `WorkerPool.ReconcileNowAsync`）：

- **判据是合取式，两条缺一不可**：
  1. `WorkerClient.ActiveCallCount == 0`（等价于 `IsBusy == false`，**没有在飞工具调用**）—— 这是**正确性底线**。子代理工具合法跑 30 分钟（`CliAgentDefinition.TimeoutMinutes` 默认 30），只看"距上次活动多久"会在它跑到 1 分钟时被当成空闲杀掉：子代理进程随即变孤儿，且它持有的仓库 git 写锁要等到超时才释放。⚠️ 因此"有没有在飞调用"**必须由真正发起调用的那一层报**（`WorkerClient.CallToolAsync` 的 `NotifyCallStarted` / `NotifyCallEnded`）—— 池只在 `AcquireAsync` / `Release` 这些记账路径上被调用，它**看不到**一次调用从开始到结束的整个区间。
  2. `now - WorkerClient.LastActivityTicks >= DirectoryIdleTimeout`。活动时间由 `WorkerClient` 在**调用开始/结束**（减计数必须放在 `finally`：取消 / 传输故障 / 正常返回三条路径都要走到，漏一条会让 `ActiveCallCount` 永久大于 0 → 该 Worker **永远**不被回收，而症状只是"空闲关闭看起来完全没生效"）、**实时输出到达**（`OnTransportToolOutput`）、**握手与工具集同步**（`HandshakeAsync` / `SyncToolsAsync`）时刷新。
  > ⚠️ 两条都要的理由：子代理可能连续十几分钟不吐一行输出（内部思考 / 跑长命令），那段时间既没有调用开始也没有输出，**只有 `ActiveCallCount` 能证明它还活着**。
- **额外跳过两种槽位**：`InFlight != null`（正在拉起，句柄还没交付 —— 此时 `slot.Client` 可能仍是上一个、且已判空闲的句柄，不跳过就会把"即将交付的新 Worker"收掉）；`Slot.TurnActive`（回合进行中，见下）。
- **`WorkerPool.MarkTurnActive(sessionId, workDir, active)` 是纯性能优化，不是正确性要求**：回合的 LLM 流式阶段恒定"没有在飞调用"，不通知的话这个回合的**下一个**工具调用要额外付一次重建（约 100~300ms）。1 分钟阈值大于绝大多数单回合耗时，正常不会命中；长上下文 + 慢模型的长回合会命中。
  > ⚠️ **调用方必须 `try/finally` 成对**。漏掉 `active: false` → 该会话的 Worker **永远**不参与回收，症状是"有的会话的 Worker 一直关不掉"，而它是按会话发生的、极难定位。重复置同一个值是幂等的。
  > ⚠️ 它**只作用于已存在的槽位**：会话还没 `Acquire` 过（尚未挂进目录组）时调用是 no-op，标记不会留到后来。因此接线位置应在**回合开始处**（与 `SessionRuntime` 已有的 `TryBeginTurn` / `EndTurn` 同一对生命周期），而不是"每回合第一次工具调用之后"。⚠️ 该方法目前**零生产调用方**。
- **回收节拍自适应**（`WorkerPool.EffectiveReconcileInterval`）：取**空闲阈值的 1/4**，夹在 `[2s, ReconcileInterval]` → 默认配置（阈值 1 分钟 + 对账 60s）下**每 15s 一轮**。不这么做的话"空闲 1 分钟就关"实际会变成 1~2 分钟（tick 落在超时点哪一侧）；下界 2s 是因为阈值被配得极小时每次 tick 都要遍历全部目录组，太密只是白烧 CPU；上界取 `ReconcileInterval` 保证**空闲回收未启用时行为与改造前完全一致**（仍是 60s 的会话对账节拍）。
- ⚠️ **空闲回收寄生在对账循环上**：`StartReconcileLoop` 在 `liveSessionProvider == null` 或 `ReconcileInterval <= TimeSpan.Zero` 时直接不启动，`ReconcileNowAsync` 在拿不到比对基准时**静默跳过整轮**（误回收比不回收糟得多）。`CommanderRuntime.Boot` 注入了 `liveSessionProvider` 且用默认 options，所以主进程里它默认开着。
- 回收动作复用 `CloseClientsAwaitedAsync`（并发、`ShutdownAsync` 限时 → `DisposeAsync` 限时），日志是**一条汇总 `Log.Info`**（记数量与阈值，不逐个记）—— 用户报"我的 Worker 怎么老重启"时这一行是唯一线索。
- ⚠️ **空闲回收尚未在生产路径生效**（与上方「已完成 vs 尚未接线」同源）：聊天页发消息的路径上工具仍走主进程内的 `ToolRegistry`，全仓没有任何 `new WorkerProxyTool(...)` 的生产调用方 → **生产里池内压根没有 Worker 句柄可回收**。判据与节拍本身**已被 `WorkerSelfCheck` 的 S8–S12 覆盖**（专用探针池：内联传输 + 手动 `ReconcileNowAsync` + 200ms 阈值 + `ReconcileInterval = TimeSpan.Zero` 关掉后台循环，让每一轮回收都由场景显式触发）—— 但那验的是**探针配置**下的判据，生产默认的「真管道 + 15s 自适应节拍 + 1 分钟阈值」这一组合**没有任何自动验证**，见技术债 #21。

**退避**：指数 `2s → 4s → … → 60s` 封顶 + **±20% 抖动**，连续 **3** 次失败后**永久降级**为进程内执行（对该 `(目录, 会话)` 粘滞，直到它被 Release）。
- 不退避时"起不来 → 立刻重拉 → 又起不来"是 CPU 与日志双重打爆的循环，且失败根因通常在用户下一次干预前不会自己变好。
- 60s 封顶：再往上没有实际收益 —— 用户不会在 2 分钟后重试同一个会话，而 120s+ 会让"被 transient 地搞挂"失去自愈机会。
- ±20% 抖动：同一目录的 N 个会话同时崩溃时，无抖动的指数退避会让它们**在同一毫秒**集体重拉，退避就从"削峰"变成"制造尖峰"。
- 退避期间 `AcquireAsync` **立刻**返回进程内句柄，**不阻塞这一回合用工具** —— 否则用户看到的是"点了发送没反应"。
- **terminal 故障**（立即钉死、不退避重试）：定位未命中（候选 1 环境变量配错是"配错即报错"，继续往下探会把"脚本路径写错"伪装成"没找到"）、协议版本不符（`PipeTransport` 在版本错位时抛异常，池嗅探"协议版本不符"字样升级为 terminal）、`WorkerPoolOptions.PipeEnabled = false`。

> ⚠️ **生命周期的前提接线**：`WorkerPool.Release` 只有在"每次减一都被正确调用"时才成立。两个原本**零生产调用方**的入口现已接上：① `SessionRuntimeRegistry.RemoveSession` ← `AppShell.ReleaseSessionResources`（GUI 的删除入口，聊天页与会话面板都走这一个，**先 `Release` 再 `RemoveSession`，顺序不能反** —— 后者会把运行时摘掉并 Dispose，那时 `SessionRuntime.WorkDir` 就再也取不到了）；② `SessionRuntimeRegistry.Dispose` ← `App.axaml.cs` 的 `OnDesktopExit`（先 `Workers.ShutdownAllAsync` 同步等，预算 = `ShutdownTimeout × 2 + 1`，**宁可超时也不抛**）。
> 「换目录」另有一条独立路径：`ChatPageViewModel.ReleaseWorkerOnWorkDirChange` 直接调 `Workers.Release(sessionId, previous)`，不经过 `AppShell`。判据是「会话上已持久化的目录仍等于被替换掉的那个值」，所以**切会话时不会误减**。
> 第三层兜底是池内**与实际会话集合定时对账**（`WorkerPoolOptions.ReconcileInterval`，基准集合由 `Boot` 注入的 `liveSessionProvider` 提供；传 null 会把整个对账关掉）。⚠️ **实际节拍是 `EffectiveReconcileInterval` 而不是 `ReconcileInterval`**：启用空闲回收时取"空闲阈值的 1/4"并夹在 `[2s, ReconcileInterval]`，默认即 **15s**；空闲回收关闭时才回到 60s。同一轮对账里做三件事：摘掉"会话已不存在"的整个槽位（`Reconcile`）→ 摘掉"空闲"的句柄但保留槽位（`DetachIdleClients`）→ 计数归零的目录整组退役（L3 兜底）。
> ⚠️ 已知交互：本会话**正在跑回合**时改目录，旧目录的 Worker 会被立刻关闭，此刻正在旧目录执行的那次工具调用会以一条明确的工具错误收场（而不是静默挂起）。这是「立即释放」的代价。

### Worker 新增铁律

1. **`catch (OperationCanceledException) { throw; }` 必须在 `catch (Exception)` 之前**（全仓通用，Worker 语境下写反后果**更严重**）。`OperationCanceledException` 是 `Exception` 的子类，被兜底分支吞掉时，在 Worker 语境下是：父进程以为调用已结束、不再转发取消，而 **Worker 侧真正的子代理进程树继续跑到超时** —— 父侧取消的只是"父进程的等待"，真正让子进程树死掉的是 Worker 侧的 `notify/cancel` → `Cancel()` → `CliAgentRunner` 的 `Kill(entireProcessTree: true)`。少这一半，「停止」按钮就只停住主循环，子进程照跑，日志里还只剩一条误导性的"工具执行异常: …"。
2. **Worker 侧不得使用 GUI 的 `Strings` 资源**（`AIShikikan.Worker` 不引用 resx），面向用户 / LLM 可见的错误文案一律**硬编码中文**。这不是"忘了加翻译"：现状所有工具的错误文案本来就硬编码中文（`GitAddTool` / `GrepTool` / `AgentExecutor`），它们经 `ToolResult.Content` 原样进 LLM 上下文，从来没有 i18n；而引用 resx 会隐式依赖 GUI 程序集名（卫星资源程序集名由它推出），正是必须守住的分层边界。⚠️ 同理**不要**照搬 GUI 的 `FixupLinuxImeEnvironment()`（Worker 没有窗口，且那段逻辑只在环境变量全缺失时遍历 `/proc` 探测 fcitx/ibus）。
3. **JSON 只能走源生成上下文**。本次新增的 14 个协议 DTO 已逐个登记进 `AppJsonContext`（`WorkerHelloRequest` … `WorkerOkResponse`）。编帧用 `JsonObject` / `JsonNode`，**禁止** `JsonSerializer.Serialize(obj)` / `SerializeToElement(obj)` 这类不带 `JsonTypeInfo` 的反射式重载 —— AOT 下会抛，而仓库 AOT 兼容性没有 CI 门禁（技术债 #1/#2），只在真实发布时才暴露。
4. **`ToolCardDetail` 的派生类型只能登记在基类的 `[JsonDerivedType]` 上**。⚠️ **历史事故**：`CheckpointDetail` 曾把 `[JsonPolymorphic]` + 自我 `[JsonDerivedType]` 挂在自己身上，而基类清单漏了它 → 卡片序列化成 `"detail": {}`（13 个字段**静默全丢**，因为 `UnknownDerivedTypeHandling` 默认 `FallBackToBaseType`）→ 反向读回时抽象基类实例化失败 → **整份会话判损坏、此后永久拒绝写回**。派生类型清单**不继承**，自登记只对"把该类型当声明类型直接序列化"生效。新增派生类型只改基类清单（当前 6 种：`fileRead` / `dirList` / `glob` / `grep` / `subagents` / `checkpoint`；判别符字符串是**已落盘数据的一部分**，改名会让历史会话读不出来）。
5. **多行的 `ParametersJson` / `Arguments` / `DetailJson` 必须是 JSON 字符串字段**，不能把多行 JSON 拼进帧 —— 否则裸换行会把 NDJSON 劈成两行，变成"只在载荷恰好换行时才复现"的间歇性 `JsonException`。STJ 会把裸换行转义成 `\n`，外层帧因此仍是一行。（`DetailJson` 体积会比紧凑 JSON 大 2~3 倍，`AppJsonContext` 开了 `WriteIndented`，属可接受的代价。）
6. **`MaxLineChars`（8KB）只适用于流式输出行，绝不是协议帧上限**。它沿用的是 `CliAgentRunner` 裁剪子进程 stdout **每一行**的上限，对应协议里 `WorkerToolOutputNotification.Line`。请求/响应帧（内含整份文件内容与 `DetailJson`）可以远超 8KB —— 套到 `read_file` 结果上会把内容截成残缺且**没有任何错误提示**（表现为"文件读到一半就没了"）。
7. **`IsConnected` 的契约是「握手成功且此后没有断线」，`Start()` 不含握手**。所以 `PipeTransport.Start(...)` 返回后、握手完成前它为 **false**，这是**正确**的，不要去"修"。这条契约正是 `WorkerPool` 把句柄交给代理工具前的最后一道状态检查（先注册再握手会出现"工具在列表里，一调就报未知工具"）。
8. **`Log.Flush()` 之后所有日志被静默丢弃**（`_initialized` 置回 false，`Write` 直接 return，不报错、不提示），所以 Flush 必须是**退出路径的最后一步**，之后不要再打任何日志 —— 包括全局异常钩子里的 Error 与尚在收尾的后台任务日志。`AIShikikan.Worker/Program.cs` 的 `Shutdown(int, string)` 把这个顺序固化成一个入口，正是为了不再出现"退出信息恰好丢在 Flush 之后"。

> ⚠️ **协议版本契约**：`AppJsonContext` 刻意不开 `UseStringEnumConverter`，跨进程枚举一律以**数字**落盘（当前唯一的是 `Assignment.Status` 的 `SubagentStatus`）。数字枚举在两侧版本不一致时**静默错位**（不抛异常，只是解析成另一个状态）→ 表现为"分派卡住不消失"这类极难定位的现象。因此握手必须校验 `WorkerProtocol.ProtocolVersion`，不一致直接终止 Worker。DTO 的**字段增删是兼容的**（STJ 忽略未知字段、缺失取默认），但**枚举成员的重排/删除/改名不兼容**，改动任何跨进程出现的枚举都必须抬版本。

### 超时约定

所有等待外部响应的位置都必须有超时兜底，否则 GUI 无订阅者时会永久挂起：

| 位置 | 值 | 说明 |
|---|---|---|
| `AgentEngine.ApprovalTimeout` | 5 分钟 | 工具审批与 `ask_user` 等待，超时按拒绝处理 |
| `McpClientBase.DefaultCallTimeout` | 300 秒 | `tools/call`（真正的工具执行） |
| `McpClientBase.DefaultSetupTimeout` | 30 秒 | `initialize` / `tools/list` |
| `GitService` 默认 / 图谱 / 网络 | 15 / 30 / 60 秒 | 只读命令 / `git log --graph`（大仓库上 15s 偏紧，超时会**静默退化成空图谱**，只留一条 Warn）/ pull、push |
| `WorkerProtocol.HandshakeTimeout` | 30 秒 | `worker/hello` 握手 |
| `WorkerProtocol.ToolsListTimeout` | 15 秒 | `worker/tools/list`（`tools/sync` 的兜底同量级） |
| `WorkerProtocol.HeartbeatInterval` / `HeartbeatTimeout` | 5 秒 / 60 秒 | 心跳发送间隔 / 僵死判定。⚠️ 心跳由**独立定时器**发，**不能**用来判断"Worker 卡在某个工具上" |
| `WorkerProtocol.ShutdownTimeout` / `CancelTimeout` | 5 秒 / 5 秒 | 优雅关闭 / 取消通知送达 |
| **Worker 工具执行** | **无超时**（`ToolCallTimeout = Timeout.InfiniteTimeSpan`） | 见下 |
| `WorkerPoolOptions.DirectoryIdleTimeout` | **1 分钟**（`TimeSpan.Zero` = 关闭） | 空闲回收阈值。⚠️ 它**不是**"多久之后一定杀掉"的承诺：实际关闭时刻 = 首次满足判据的 tick，因此上界是"阈值 + 一个节拍"（默认 15s → 最迟 75s）。判据与接线见「Worker 生命周期规则」 |

> ⚠️ **Worker 侧工具执行刻意不加时间上限**：子代理**合法**跑 30 分钟（`CliAgentDefinition.TimeoutMinutes` 默认 30、上界 `MaxTimeoutMinutes` = 1440），而三个子代理工具都是**阻塞式**的。沿用 MCP 的 300s 会把正常跑满上下文的长任务**误杀**成超时失败 —— 而且是**间歇性**的（只在任务偏长时炸），极难与真实失败区分。长任务的闸门应该由**它自己**（`timeout_minutes`）管，传输层只负责"出事了不要让我永久等待"：靠**取消通道 + 崩溃完结 + 心跳僵死**三者兜底，而不是"到点就杀"。

### Git 检查点系统

每条用户消息自动创建检查点（记录 HEAD commit + `ai-shikikan/checkpoint/<id>` tag），卡片支持 **Reset**（`reset --hard`）/ **Revert**（反向提交）/ **Fork**（从检查点派生分支）。这是**唯一的回滚入口**——工具卡上没有回滚按钮。非 Git 仓库降级为警告，聊天仍可用。

> 早期版本曾用 `ac/<stepId>` 分支给子代理做检查点，该机制已废弃（`GitStepService` / `LegacyGitMigrationService` 已删除）。
> `doctor` 会扫描并提示清理遗留的 `ac/*` 分支、`steps/*.json` 与检查点 tag（**只提示不自动删**，检查点 tag 是当前回滚依据）。

### 工作区并发隔离

`WorkspaceExecutionCoordinator` 以「工作树根 + 当前分支」为键管理执行权：同一 worktree 同一分支可多会话并发，跨分支互斥，不同 worktree 互不影响。`TryBeginTurn` / `EndTurn` **已接生产调用方**（`SessionRuntime` ← `AgentEngine`），覆盖「跨分支互斥」这一条规则。

> ⚠️ **已知缺口（git 写闸仍未接线）**：`TryReserve` / `WaitGitWriteAsync` / `TryEnterGitWrite` / `TryBeginAssignment` **均无生产调用方**（仅 `SelfCheck` 覆盖）。Worker 侧已经在 `WorkerToolDescriptor.RequiresGitWrite` 里**如实上报**了哪些工具会写 git（`git_add` / `git_commit` / `git_create_checkpoint`），但那只是**提示**——`WaitGitWriteAsync` 没人调，闸门就是关着的。接线时它必须与 `TryEnterGitWrite` 的实际保护范围一致，否则会出现"工具以为自己被串行化了"的**假安全感**。
> 回滚 / Fork 确认期**没有** worktree 级保护——目前只靠 `Fork_SessionOccupied` 这条**会话级**守卫（当前会话在跑就禁用 Fork）。若要接通 reservation，见 `docs/plans/`。

### 会话停止的语义陷阱

`SessionRuntime.Stop()` → `AgentEngine.CancelSession()` 取消 `_sessionCts`。该 CTS **只创建一次且取消后不再重置**，因此 **`Stop()` 之后该会话引擎无法再执行任何回合**（后续回合会立即被取消）。恢复路径只有删除并重建会话运行时。

聊天页的「停止」按钮走的是另一条路径（每会话独立的 `_turnCts`），不影响后续回合——两者语义不同，不要混用。

### 事件流

`EngineEventHub` 是全局单例，所有引擎共享。投递规则：未标注归属的事件全量投递；`EngineApprovalRequested` / `EngineQuestionRequested` / `EngineAssignmentChanged` 强制投递（否则引擎会永久等待）；其余仅投递到当前活动会话。每个订阅者独立 try/catch，单个订阅者异常不影响其他。

**GUI 侧铁律**：`AgentEngineEvent` 可能在引擎线程触发，且 `run_subagents` 下多个子代理的进度回调会**并发**进入。`ChatPageViewModel` 用 `ConcurrentQueue` + `Interlocked` 节流把事件转交给 UI 线程，**不要**在引擎线程直接碰 UI 集合。

### 其他

- **MCP 服务器**：配置于 `mcp-servers.toml`，支持 stdio（command/args/env）、`http`（Streamable HTTP）与 `sse`（HTTP+Server-Sent Events）三种传输。设置页可增删/开关/重连，调用 `Runtime.RefreshMcpToolsAsync()`。协议版本 "2025-06-18"。⚠️ `RefreshMcpToolsAsync` 里那条 `Registry.UnregisterWhere(t => t is McpProxyTool)` 是 Worker 工具的护栏所在，见「工具分层与 Worker 边界」。
- **Worker 状态栏**：`StatusPanelViewModel` 用 500ms 轮询（`DispatcherPriority.Background`，与聊天页的排队计数轮询同款）读 `WorkerPool.GetHealth()`，显示 `Status_WorkerPipe` / `Status_WorkerInline`。⚠️ **降级没有任何事件可订阅**（池是在别的线程上把某个槽位钉死成内联的，不经 `EngineEventHub` 也不经 `AppShell.DataChanged`），只能轮询；轮询处理器内不允许抛异常，否则会沿 `DispatcherTimer` 冒到 UI 循环卡死整个界面。文案按 `WorkerMode` 枚举自行映射到 resx（不用 `WorkerHealth.ModeText`，那是 Core 里的硬编码中文）。⚠️ **空闲回收后的槽位在 `SnapshotHealth` 里呈现为 `NotConnected`**（句柄已摘、尚未重建；`Degraded` 为假且 `SpawnAttempts == 0`，所以不会被误报成 `Inline`）—— 这与"会话还没开过第一回合"的懒启动状态在数据上**不可区分**。模式本身只说明"此刻有没有存活实例"，判断依据得看该会话是否用过工具；且 `NotConnected` 被状态栏判为**非降级**（`IsWorkerDegraded` 只认 `Inline` / `NotFound`），所以空闲回收不会让状态栏翻成"降级"标记。
- **工具结果出口**：所有 Agent 执行工具统一走 `AgentExecutor.ExecuteAsync(..., llm, ct)`，压缩在该出口生效。
- **聊天页**：支持手动停止按钮 + 双击 ESC（600ms 内两次）终止生成、「继续输出」续写。
- **首页**：用量统计含 ScottPlot 折线图（固定坐标轴 + 标尺 + 折点悬浮详情）与活跃热力图。趋势图与热力图**共用全局时间范围**（近 7 / 14 / 30 天 / 全部，默认近 14 天），热力图周列数按范围动态计算；选「全部」且无数据时回退近 26 周。配色全部跟随主题资源。
- **设置页**：卡片使用自绘 `Views/WaterfallPanel.cs` 自适应瀑布流布局。
- **日志**：`Log` 是**两个进程共写同一份**（文件名按日滚动，全部 append），所以每行都带 `[pid:<pid> <标签>]` 段（`Environment.ProcessId` + `Log.ProcessLabel`）。Worker 在 `Log.Initialize()` **之前**先 `Log.SetProcessLabel("worker")` —— 顺序不能反，否则最早那几行会顶着默认标签 `"gui"` 落盘，交错后无法归属。排 Worker 的问题时先按这个标签过滤。
- **`doctor` 的 10 项检查**：配置目录 / Provider API Key / Agent 定义 / 专家+模板 / 工具装载 / Git 仓库 / 并发协调规则（`WorkspaceExecutionCoordinator.SelfCheck`）/ **工具卡片编解码**（`ToolCardDetailCodec.SelfCheck`）/ **Worker 定位**（`WorkerLocator.Locate`）/ **Worker 端到端**（`WorkerSelfCheck.RunDetailed`，12 组场景，**真的会 spawn Worker 进程**；定位未命中时跳过管道那一段并记一条说明，跳过不算失败但必须让用户看见"这次没验到跨进程那条路"。⚠️ 空闲回收那 5 组走**专用探针池**（内联传输 + 手动对账 + 200ms 阈值），不 spawn 进程）。只有「Git 仓库」失败降为 ⚠️，其余失败都计入退出码。
- **Linux 输入法**：Program.cs 的 `FixupLinuxImeEnvironment()` 启动时清洗 IME 环境变量弯引号、缺失时探测 fcitx/ibus 进程补写 `AVALONIA_IM_MODULE`，并显式启用 X11 IME。⚠️ **仅 GUI**：`AIShikikan.Worker/Program.cs` 刻意不调用它。

## 常用命令

```bash
# 构建（仅当前平台，输出到 artifacts/；每个变体同时产出 GUI 与 Worker）
./build.sh                       # = ./build.sh all：aot + selfcontained + dotnet 三变体
./build.sh aot                   # 只构建 Native AOT
./build.sh selfcontained         # 只构建自带 .NET 运行时
./build.sh dotnet                # 只构建框架依赖
./build.sh clean                 # 清理 artifacts/（含 artifacts/worker/）
CONFIGURATION=Debug ./build.sh selfcontained   # 覆盖构建配置

# 编译检查（改完代码至少跑一次；根目录只有 slnx，裸命令直接可用）
dotnet build                                            # 构建三个项目
dotnet build src/AIShikikan.Gui/AIShikikan.Gui.csproj      # 只构建 GUI（Core 作为 ProjectReference 带上）
dotnet build src/AIShikikan.Worker/AIShikikan.Worker.csproj # 只构建 Worker
dotnet build src/AIShikikan.Core/AIShikikan.Core.csproj    # 只构建 Core

# 本地调试（本地开发最常用；走 dotnet run，不 publish、不产出 AOT 二进制）
./debug.sh                # 先编译 Worker(框架依赖)并 export AISHIKIKAN_WORKER_PATH，再启动 GUI
./debug.sh --version      # 查看版本（-h/--help/help 之外的参数原样传给程序）
./debug.sh doctor         # 环境诊断（10 项检查：并发规则 / 卡片编解码 / Worker 定位 / Worker 端到端…）
./debug.sh --no-build doctor   # 跳过编译，只跑上次的产物（反复 attach 调试器时用）
NO_BUILD=1 ./debug.sh --version  # --no-build 的环境变量写法

# AOT / 自包含 / 单文件产物只能走发布链路验证
./build.sh aot

# Linux 系统包（deb / rpm / pacman）
# ⚠️ STAGE_DIR 必须与 ./build.sh 的输出一致（build.sh 写 artifacts/，不是 stage/）。
#    漏掉它会在前置校验处明确报错，不会装出坏包，但不如一开始就对。
#    WORKER_STAGE_DIR 默认从 STAGE_DIR 派生，无需显式传。
VERSION=$(cat VERSION) VARIANTS=dotnet STAGE_DIR=artifacts \
  FORMATS=pacman ./Packagers/linux/package.sh   # 需先 ./build.sh dotnet

# 修改版本号（一键同步 VERSION / PKGBUILD / app.manifest / AGENTS.md 版本标注 / 脚本与 CI 回退值）
./set-version.sh 0.9.1-vibe   # 改版本（GitHub tag 会自动补 v 前缀）
./set-version.sh current      # 查看当前版本

# 版本来源：根目录 VERSION 文件（当前 1.0.0-vibe，用 ./set-version.sh 更新）
# 环境变量：build.sh = CONFIGURATION / VERSION；debug.sh = CONFIGURATION / NO_BUILD
# debug.sh 不再支持 VERSION（dotnet run 无 -p:Version 选项）与 AOT_MODE（不 publish）
# 架构由 uname 自动探测，无 ARCH 覆盖；跨平台/跨架构构建已移除（交叉编译由 CI 各 runner 分别完成）

# Windows（参数非环境变量，与 sh 版并不等价）
.\build.ps1 [all|aot|selfcontained|dotnet|clean|help] [-Configuration Release] [-Version x.y.z]
.\debug.ps1 [-NoBuild] [-Configuration Debug] [-Help] [app arguments...]
#   - .\build.ps1 无 -h/--help，只有位置参数 help
#   - .\debug.ps1 用 [CmdletBinding(PositionalBinding=$false)] 关掉了位置绑定：
#     -NoBuild / -Configuration 只能具名传，裸 token 一律透传给程序（.\debug.ps1 doctor 直接可用）
#   - PowerShell 绑定的是 -NoBuild，不是 --no-build（后者不是合法参数名）；$env:NO_BUILD=1 同样有效
#   - .\debug.ps1 --version：--version 不匹配任何参数名，作为裸 token 透传给程序 → 打印版本
```

## 运行方式

```bash
# 开发期一律走 debug 脚本（dotnet run，保留调用者的当前工作目录）
./debug.sh              # Avalonia 图形界面
./debug.sh --version    # 查看版本
./debug.sh doctor       # 环境诊断

# 发布产物（./build.sh <变体> 之后；GUI 与 Worker 逐变体一一配对）
./artifacts/dotnet/AIShikikan.Gui                  # 框架依赖 GUI
./artifacts/selfcontained/AIShikikan.Gui          # 自带运行时（单文件）GUI
./artifacts/aot/AIShikikan.Gui                    # Native AOT GUI
./artifacts/worker/dotnet/AIShikikan.Worker       # 与上面同名变体的 Worker 一一对应
./artifacts/worker/selfcontained/AIShikikan.Worker
./artifacts/worker/aot/AIShikikan.Worker
```

> ⚠️ **Worker 出三种变体并与 GUI 一一配对，不是「只出一个 AOT 版 Worker」**：用户选 `dotnet` 变体的动机就是省磁盘，塞给他一个 15MB 的 AOT Worker 等于用他明确拒绝的方式解决问题；反过来也不能假设"装了 .NET 运行时的用户存在"，那正是 `aot` 变体存在的理由。单一 Worker 变体必然在「用户所选变体」与「Worker 所需运行时」之间错配。缺失时 `build.sh` / 两个 workflow 都会 `warn`，绝不静默出包。

> ⚠️ **归档与 Linux 包里 Worker 放 `worker/` 子目录**（与 GUI 平级），**不是**平铺：框架依赖变体下 GUI 与 Worker 会带同名的 `AIShikikan.Core.dll` 等依赖（`dotnet publish` 不清空输出目录，陈旧内容会混进下一次归档，而归档文件名恒定不变），AOT 变体下还有 `libSkiaSharp` 等 `.so` 同名覆盖。发布布局对应 `WorkerLocator` 的**候选 3**。
> ⚠️ `WorkerLocator` 的候选 2/3/4 都以 `AppContext.BaseDirectory` 为起点（**不是** `Environment.ProcessPath` 的目录，更不是工作目录）。AOT / 单文件下它指向**可执行文件的真实目录**：Linux 包里 `/usr/bin/ai-shikikan` 只是软链，`/usr/lib/ai-shikikan/AIShikikan.Gui` 才是真实路径，Worker 就装在其 `worker/` 子目录。

> `./debug.sh` 会先以**框架依赖**编译 Worker 并 `export AISHIKIKAN_WORKER_PATH`（绝对路径，相对路径会被按 GUI 进程的 cwd 解析，而脚本刻意保留调用者的 cwd）。**Worker 编译失败不阻断 GUI 启动** —— 会降级为进程内执行；为一个辅助进程把 GUI 卡在启动入口才是更糟的失败模式。同理找不到产物时**不**导出一个不存在的路径：候选 1 是"显式指定"语义，配错即报错。

## 技术栈与关键约束

- .NET 10 (`net10.0`)，`PublishAot=true`（Gui 与 Worker 两个 Exe）：全链路 Native AOT 兼容。
- **三项目分层**：`AIShikikan.Core` 不得引用任何 Avalonia 包（Core 里出现 `using Avalonia` 即为分层破坏）；Gui 与 Worker 都单向引用 Core；Core **不得**反向引用 Gui（`ColorExtractionService` 因此被移到 GUI 层），也**不得**引用 Worker；Worker **不得**引用 Gui。
- UI：Avalonia 12.1.3 + CCSWE.Avalonia.Material (Material3)、Markdown.Avalonia、Material.Icons、ScottPlot.Avalonia（趋势图）。版本以 `src/AIShikikan.Gui/AIShikikan.Gui.csproj` 为准。
- MVVM：CommunityToolkit.Mvvm；编译绑定默认开启（`AvaloniaUseCompiledBindingsByDefault=true`，XAML 需 `x:DataType`）。XAML 里的 `clr-namespace` / `using:` 可直接引用 Core 的命名空间（跨程序集解析已验证）。
- 序列化约束（AOT 必需）：JSON **仅**用源生成上下文 `AppJsonContext.cs`（位于 `src/AIShikikan.Core/Serialization/`）；TOML 用 Tomlyn（经 `TomlBridge`）；YAML frontmatter 用 YamlDotNet。禁止反射式序列化。
- LLM SDK：OpenAI / Anthropic 官方 SDK（仅客户端内部 HTTP 细节使用，主数据结构自定义于 `ChatTypes.cs`），仅 Core 引用。
- 动态主题取色：MaterialColorUtilities（GUI 层，配合 `ColorExtractionService` / `DynamicThemeService`）。

### 多进程共享数据（单写者原则）

引入 Worker 之后仓库有**两个会落盘的进程**，而 `AtomicFile` 的**路径锁明确只覆盖进程内**（`AtomicFile.PathLocks` 是进程内的 `ConcurrentDictionary<string, SemaphoreSlim>`）。当前靠**约定**维持分工，没有任何强制：

| 数据 | 唯一写方 | 另一进程必须 |
|---|---|---|
| `checkpoints/` | **Worker**（`WorkerSlimHost` 里的 `GitCheckpointStore` + `GitService`） | 主进程只读 |
| `assignments/` | **Worker**（`AssignmentManager`） | 主进程只读 + 广播 UI |
| `usage.json` | **主进程**（`UsageStatsService` 是「内存全量快照 + 1.5s 防抖整文件覆盖」） | Worker 侧**绝不可**实例化 |
| `sessions/*.json` | **主进程**（`ChatService` 无锁全量覆盖） | Worker 侧**绝对不能**实例化 `ChatService` |
| 各配置 toml（`providers` / `agents` / `mcp-servers`） | **主进程**（`CommanderRuntime.Boot` 一步连写 8 处） | Worker 侧只读 |

> 两进程各写同一份会**互相吞掉增量**：两个进程都持有各自的内存全量快照，后写的一方用它那份旧快照整文件覆盖，把前一方刚落盘的增量抹掉。`GitCheckpointStore` 的每仓库 500 条淘汰计数在两进程间也会各算各的（各自只看到自己那侧写进去的记录数）。

### 原子写（AOT 无关，但关系到数据安全）

**所有用户数据落盘都必须走 `src/AIShikikan.Core/Serialization/AtomicFile.cs`**，禁止裸 `File.WriteAllText`。

| API | 用途 |
|---|---|
| `WriteAllText(path, content)` | 原子写，失败上抛 |
| `TryWriteAllText(path, content, context)` | 原子写，吞异常并记日志（写失败不应中断主流程时用） |
| `TryReadText(path, out content, validate)` | 读取，主文件损坏自动回退 `.bak`；`validate` 是语义校验回调 |
| `TryReadRaw(path, out content)` | 读原始文本，不回退 |
| `Delete(path)` | 删除并连带清理 `.tmp` / `.bak` |

原理：写 `.tmp` → `Flush(flushToDisk: true)` 强制刷盘 → `File.Move(overwrite: true)` 覆盖，覆盖前把上一份留作 `.bak`。裸 `File.WriteAllText` 是「截断后逐字节写入」，断电会留下半截文件；而加载端普遍 catch 后返回空对象，于是**下一次任意写操作就把空数据覆盖回原文件**——损坏被放大为永久数据丢失。

当前覆盖：**14 个服务的写入端、14 个服务的读取端**（带 `.bak` 回退）。

> ⚠️ 仍有裸读路径未走 `.bak` 回退：**只剩 `AgentTemplateService` 的模板读取**（`File.ReadAllText`，共 3 处调用点）。会话列表主加载（`ChatService.ReadMetadata`）与会话标题（`SessionRuntimeRegistry.ReadTitle`）已改走 `AtomicFile.TryReadText`，两者的 `validate` 回调分别用「能解析出 id」与「能反序列化出 `ChatSession`」作可用性判据。新增带备份的服务时记得一并加固。

> ⚠️ 用 `AtomicFile.Delete` 而非 `File.Delete`，否则删主文件时会留下 `.bak`；`CommanderRuntime.DeleteIfExists`（「还原默认设置」）已因此踩过坑——会复活旧 API Key。

> ⚠️ **多进程写同一路径时，路径锁救不了你**（见上节「多进程共享数据」）。它只让同进程同路径的写串行排队。

### 依赖版本治理

`CCSWE.Avalonia.Material`（`12.*`）与 `Material.Icons.Avalonia`（`3.*`）使用**浮动版本**，同一份源码在不同时间 restore 会得到不同依赖集，且仓库无 `packages.lock.json`。升级依赖时留意这一点。

`Tomlyn` 停在 `0.19.0`（上游已到 2.x），`Azure.AI.OpenAI 2.1.0` 与 `OpenAI 2.13.0` 双 SDK 并存——迁移前先确认 AOT 影响。

## 配置与数据路径

用户配置按平台解析（见 `src/AIShikikan.Core/AppPaths.cs`），Linux 走 XDG 规范：

| 目录 | Linux | macOS |
|------|-------|-------|
| Config | `~/.config/ai-shikikan/` | `~/Library/Application Support/AI-Shikikan/Config/` |
| Data | `~/.local/share/ai-shikikan/` | 同上 Data/ |

关键文件：

| 文件 | 说明 | 写方 |
|------|------|---|
| `providers.toml` | LLM Provider（API Key、模型、端点、思考等级限制） | 主进程 |
| `agents.toml` | Agent 定义 + 推荐专家 | 主进程 |
| `mcp-servers.toml` | MCP 服务器定义（stdio/http/sse 传输；默认不内置任何服务器） | 主进程 |
| `preferences.toml` | 界面偏好（明暗/语言/字体/背景） | 主进程 |
| `personas/` | 人格/专家 Markdown(YAML frontmatter) | 主进程 |
| `templates/` | 任务模板 | 主进程 |
| `models.toml` | 模型上下文窗口与价格（可选；缺失时回退 Provider `/v1/models`） | 主进程 |
| `roster.prompt` | Roster 注入模板 | 主进程 |
| `sessions/` | 会话记录（含每会话 `roster.json`） | 主进程 |
| `usage.json` | 用量统计 | 主进程 |
| `checkpoints/` | Git 检查点记录（按仓库哈希分目录） | **Worker** |
| `assignments/` | 子代理分派记录 | **Worker** |

> ⚠️ **两个进程同时启动写同一份配置**：两个**读**方法内部会写盘（缺失则写默认）——`AgentConfigService.LoadUserFile()` 与 `McpConfigService.LoadAll()`（在 `McpServerConfig.cs`）都是这种形状。而 `CommanderRuntime.Boot` 一步会连写 8 处（`WriteSampleFiles` / `EnsureSamplesExist` / `WriteDefaultTemplate` / `EnsureDefaultExists` × 3 等），Worker 侧构造时也会触发其中几处的**幂等**首次写出。
> 之所以还能接受，靠的是：① 主进程是先启动的那一个，所以 Worker 实际走的是"读已存在的文件"；② 两边写出的内容逐字相同（都来自内嵌 `DefaultConfig`）；③ 走的是 `AtomicFile`。**但"绝不能 new 什么"的判断不能靠这个运气**——`WorkerSlimHost` 刻意不 new `McpService` / `ChatService` / `UsageStatsService` 的原因见「工具分层与 Worker 边界」。

内嵌默认配置在 `src/AIShikikan.Core/DefaultConfig/`（以 `AIShikikan.Core.DefaultConfig.<文件名>` 为 LogicalName 嵌入 **Core** 程序集，`DefaultConfig.Load` 按 `typeof(DefaultConfig).Assembly` 取，首启动时写出）。
面向用户的示例文件在 `templates/`。**添加新配置字段时需同步更新两处**。

## 编码约定

- 语言：**C#**，target `net10.0`，`Nullable` + `ImplicitUsings` 开启。
- 新增服务放入 `src/AIShikikan.Core/Services/<领域>/`（与 Avalonia 无关的业务逻辑）或 `src/AIShikikan.Gui/Services/`（界面相关），并通过 `CommanderRuntime.Boot` 装配；新工具按类别在 `AgentToolFactory.CreateCoreTools`（固定工具）或 `CreateSubagentTools`（随右侧栏可见性注册的子代理工具）注册。
- 新增**跨进程**能力（Worker 协议字段 / 卡片派生类型 / 新工具分类）时三处必须同步：① 协议 DTO 与源生成上下文 `AppJsonContext`；② `WorkerToolDescriptorFactory`（分类落点，**唯一**）；③ `ToolCardDetail` 基类的 `[JsonDerivedType]` 清单（仅当新增卡片类型）。
- Core 必须 AOT 兼容且**不得引入 Avalonia 依赖**；序列化与原子写规则见上节。
- **代码注释使用中文**，且应记录「为什么这么做」而非复述代码。
- GUI 遵循 MVVM：ViewModels 继承 `ViewModelBase`，视图绑定 `Views/*.axaml`；用户可见文案一律走 `Resources/Strings.resx`（中文）+ `Strings.en.resx`（英文），经 `Strings.cs` 访问器引用。**新增文案必须两个 resx 同时加**——`Strings.Get` 在找不到时静默回退为 key 本身，英文界面会直接显示字面量。`Strings.FindMissingEnglishKeys()`（仅 Debug）供自检。
- 不得在代码中硬编码版本号，统一读取根目录 `VERSION`（见 `Directory.Build.props`）。
- 不得在代码中硬编码 API Key 等机密；Key 通过 `providers.toml` 或环境变量注入。
- 仓库暂无 `.editorconfig`，风格靠约定维持：文件作用域 `namespace`、4 空格缩进、Allman 大括号、私有字段 `_camelCase`、常量 `PascalCase`、广泛使用 `var`。

## 提交规范

遵循 Conventional Commits，scope 用项目/模块名，说明用中文。破坏性变更在 type 后加 `!` 并在正文说明迁移方式：

```
feat(core): 描述
feat(worker): 描述
fix(gui): 描述
refactor(git)!: 描述
build: 构建脚本/CI 相关
ci: GitHub Actions 相关
```

## 发布

- 版本：更新根目录 `VERSION` 文件。
- 发布流程：`.github/workflows/release.yml`（手动触发），按 `v<版本>` 标签去重构建并发布 GitHub Release（含 AOT / dotnet / selfcontained 三种包，**每种包里都含 `worker/` 子目录的对应变体 Worker**）。发布前确认 `VERSION` 已更新且对应 tag 不存在。
- ⚠️ **两个 workflow 在缺 Worker 产物时都只 `warn` 不 fail**（归档会缺 `worker/`，用户拿到的是"工具静默退回进程内执行"）。若改了产物布局，这条 warn 就是唯一的提示，别当成可以忽略的噪声。
- 本地构建辅助：`.github/workflows/debug.yml`（手动触发；产出上传为 workflow artifacts，含 Linux deb/rpm/pacman 打包，但不发布 GitHub Release）。

## 注意事项

- 不要直接提交 `artifacts/`、`bin/`、`obj/`（已在 `.gitignore` 中）。
- 不要提交用户配置（providers.toml 等含 API Key）。
- 构建脚本只构建**当前平台**：跨平台/跨架构构建已移除，交叉编译由 CI 各 runner 分别完成（见 `build.sh` 头部注释与 `.github/workflows/*.yml`），不要再往脚本里加平台矩阵。
- `AOT_MODE` 仅 `debug.sh` / `debug.ps1` 支持（`auto|always|off`，默认 `off`）；`build.sh` / `build.ps1` 按命令分别发 aot / selfcontained / dotnet，没有 AOT 回退逻辑。
- 图表交互类需求注意保持「固定坐标系」语义（禁用平移缩放），悬浮层用 Canvas 叠加避免影响布局测量。
- ⚠️ **不要在别人正在跑 `./build.sh` 时执行 `./build.sh clean`**（会动 `artifacts/`）。
- ⚠️ **Worker 找不到时会静默降级为同进程内联执行** —— 功能不丢，但**失去崩溃隔离**（子代理又回到主进程、`grep` 又能卡 UI、git 又在 UI 线程排队）。所以降级**必须可见**：状态栏常驻标记（`StatusPanelViewModel` 轮询 `WorkerPool.GetHealth()`，`NotFound` 与 `Inline` 合并成一档）+ `doctor` 的「Worker 定位」与「Worker 端到端」两项 + 每次降级一条 `Log.Warn("Worker", …)`。**不要把它当静默兜底。**
- ⚠️ **改卡片类型时按「三处同步」来做**（协议 DTO + `AppJsonContext` / `WorkerToolDescriptorFactory` / `ToolCardDetail` 基类清单）；只改其中一处的后果见「Worker 新增铁律」第 4 条 —— 那是最坏的一类症状（静默丢字段 → 整份会话报废）。
- ⚠️ **改协议字段时想清楚是否要抬 `WorkerProtocol.ProtocolVersion`**：新增可选字段兼容，改任何跨进程出现的枚举不兼容（详见「Worker 新增铁律」末尾的协议版本契约）。

## 已知技术债

改动前请了解这些坑，能顺手修就修：

| # | 问题 | 位置 |
|---|---|---|
| 1 | **无测试、无 CI 门禁**：仓库无任何测试项目，两个 workflow 均需手动触发，push/PR 不做验证。`WorkerSelfCheck` 的 12 组端到端场景目前唯一的执行入口是 `doctor` | 全仓 |
| 2 | **AOT 兼容性部分改善、门禁仍缺**：`AIShikikan.Worker` 的 `PublishAot=true` 让 `./build.sh aot` 第一次**真实编译** Core 里那批从未被验证的 `RequiresDynamicCode` 路径（git 服务、文件工具、`CliAgentRunner`、Tomlyn/YamlDotNet）—— 这是一个职责单一、体量小得多的 AOT 宿主，等于修掉了本条的一半。但：`SuppressTrimAnalysisWarnings` / `SuppressAotAnalysisWarnings` 仍把分析器全静音；`AIShikikan.Core.csproj` 仍需显式 `EnableAotAnalyzer=false`（且**不**会被 Worker 的 `PublishAot` 翻转）；一切仍靠人工跑 `./build.sh aot`，无 CI 门禁 | `Directory.Build.props` / `src/AIShikikan.Core/AIShikikan.Core.csproj` |
| 3 | **UI 线程同步跑 git 进程**：发消息前会在 UI 线程拉起约 6 个 git 进程（`GitService.Run` 用 `GetAwaiter().GetResult()`），最坏可冻结数十秒。Worker 隔离**尚未覆盖到聊天路径**，所以这条在今天仍然成立 | `GitService.cs` |
| 4 | **流式文本无时间节流**：每个 token 触发一次全量 Markdown 重解析，长回复呈 O(n²) | `ChatPageViewModel.cs` |
| 5 | **图片附件全流程在 UI 线程**：解码 + PNG 编码 + 二次解码，单张可达 1 秒（最多连贴 4 张） | `ImageAttachmentService.cs` |
| 6 | **背景图取色全量像素搬运**：4K 图约 100MB 分配 + 830 万次循环，切换背景即触发 | `MainWindow.axaml.cs` |
| 7 | **`Bitmap` 从不 Dispose**：图片分段与附件缩略图泄漏原生内存 | `ChatItemViewModel.cs` / `ImageAttachmentService.cs` |
| 8 | **MCP 客户端无自动重连**：进程崩溃后 UI 仍显示已连接，只能手动重连 | `Mcp/` |
| 9 | **LLM 层无 429/5xx 重试退避**，无首 token 超时 | `src/AIShikikan.Core/Services/Llm/` |
| 10 | **每 agent 一个 `run_<id>` 工具无上限**：agent 多了会挤占上下文并降低工具选择准确率 | `AgentToolFactory.cs` |
| 11 | **超大文件**：`ChatPageViewModel.cs` 约 2030 行、`ChatPageView.axaml` 约 880 行、`AgentToolFactory.cs` 约 1170 行、`CommanderRuntime.cs` 约 1150 行 | `ViewModels/` `Views/` `Services/` |
| 12 | **重复代码**：`Truncate` 5 份（同名函数一个取头一个取尾）、`EnsureUniqueId` 2 份、JSON 参数提取 4 份、路径规范化 4 套 | `src/AIShikikan.Core/` `src/AIShikikan.Gui/ViewModels/` |
| 13 | ~~csproj 冗余~~（**已解决**）：失效的 `<Folder Include="Models\" />` 与和 `Assets\**` 重复的 `logo.jpg` 条目在拆包重建 csproj 时已删除 | `src/AIShikikan.Gui/AIShikikan.Gui.csproj` |
| 14 | **git 写闸仍无生产调用方**：`TryReserve` / `WaitGitWriteAsync` / `TryEnterGitWrite` / `TryBeginAssignment` 均未被调（`TryBeginTurn`/`EndTurn` 早已接线）。Worker 已在 `WorkerToolDescriptor.RequiresGitWrite` 如实上报 `git_add`/`git_commit`/`git_create_checkpoint`，但那是**提示**而非闸门 —— 接线时必须与 `TryEnterGitWrite` 的实际保护范围一致，否则会出现"工具以为自己被串行化了"的假安全感。回滚/Fork 确认期仍无 worktree 级保护 | `WorkspaceExecutionCoordinator.cs` |
| 15 | **结论更正**：子代理工具卡的「检查点:」空白在**当前 UI 上已不存在** —— UI 根本不渲染 `ToolSegment.StepId`，而子代理侧恒为 null（统一在当前分支就地工作，回滚入口是"每条用户消息"的检查点）。残留的是 `Assignment.StepId` 与 `SubagentResultEntry.StepId` 两个**死字段**（均已 `[Obsolete]`，仅为兼容历史会话 JSON 而保留；赋值会有 CS0618 告警） | `Models/ToolCardDetail.cs` / `Engine/AssignmentManager.cs` |
| 16 | ~~压缩未转发 provider/model~~（**已过时**）：`ctx.ProviderId` / `ctx.Model` 的转发已修好 —— `AgentExecutor` 现在传给 `CompactIfNeededAsync`。真正的根因是 `EngineOptions.ProviderId` 曾**零生产调用方**，现已由 `CommanderRuntime.ApplyLlmRouting(engine)` 在**每回合开始前**写入（fail-safe 设计：不调用就退回"跟随全局"，而构造期固化是 fail-unsafe 的） | `CommanderRuntime.cs` / `ChatPageViewModel.cs` |
| 17 | ~~原子写未全覆盖~~（**大部分已过时**）：`ChatService.ReadMetadata`（会话列表主加载）与 `SessionRuntimeRegistry.ReadTitle`（会话标题）已改走 `AtomicFile.TryReadText`（带 `.bak` 回退）。**只剩 `AgentTemplateService` 的模板读取**一处裸读（`File.ReadAllText`）。⚠️ 且多进程下路径锁**只覆盖进程内**，见「多进程共享数据」 | `Services/Templates/AgentTemplateService.cs` |
| 18 | **i18n 自检未接线**：`Strings.FindMissingEnglishKeys()` 已写好但无调用方。（原先提到的 4 个 `ToolCard_Rollback*` 孤儿键**已删除**。）另有两处硬编码中文未收进 resx：排队徽标 `QueuedBadgeFormat`（代码里有 `TODO(i18n)`）、以及**全部 Worker 侧工具错误文案**（这是刻意的，见「Worker 新增铁律」第 2 条） | `Resources/Strings.cs` / `ChatPageViewModel.cs` |
| 19 | **多进程共享数据的单写者原则只靠约定、没有强制**：`AtomicFile` 的路径锁明确只覆盖进程内；`usage.json` 是「内存全量快照 + 1.5s 防抖整文件覆盖」，两进程各写会互相吞掉增量；`GitCheckpointStore` 的每仓库 500 条淘汰计数在两进程间各算各的。当前 Worker 侧的写方是 `checkpoints/` 与 `assignments/`，主进程侧是 `usage.json` / `sessions/*.json` / 各配置 toml —— 这个分工**必须保持**，且 `ChatService` 在 Worker 侧**绝对不能**实例化。将来若引入第三个进程、或把 Worker 的写集扩大，需要的是文件级锁（fcntl / `FileShare.None` 跨进程语义）或更明确的写方归属 | `Serialization/AtomicFile.cs` / `src/AIShikikan.Worker/WorkerSlimHost.cs` |
| 20 | **Worker 切换尚未接线（Worker 路线图上的下一步）**：`WorkerPool` 已装配、`doctor` 能跨进程自检、`Release`/`ShutdownAllAsync` 已接 GUI，但**聊天页发消息的路径上工具仍走主进程内的 `ToolRegistry`** —— 全仓没有任何 `new WorkerProxyTool(...)` 的生产调用方，`AcquireAsync` 的唯一调用方是 `WorkerSelfCheck`。出口已备好（`BuildWorkerToolsSyncRequest()` / `PendingWorkerToolsSync`），但"谁来 Acquire 并注册代理工具"这一层还没写 | `CommanderRuntime.cs` / `Services/Worker/WorkerProxyTool.cs` |
| 21 | **空闲回收只被"探针配置"验过，且在生产里无事可做**：`DirectoryIdleTimeout` 默认 1 分钟、`DetachIdleClients` / `MarkTurnActive` / `EffectiveReconcileInterval` 都已就位，`WorkerSelfCheck` 的 S8–S12 也覆盖了判据本身 —— 但 ① 生产侧**池里压根没有 Worker 句柄**（#20 未接线，`AcquireAsync` 零生产调用方），规则今天**不会对任何真实会话生效**；② S8–S12 用的是专用探针池（内联传输 + `ReconcileInterval = TimeSpan.Zero` 手动对账 + 200ms 阈值），**生产默认的「真管道 + 15s 自适应节拍 + 1 分钟阈值」组合没有任何自动验证**。另有两处**代码内注释仍写"默认关闭"**（`WorkerPool` 类 remarks 的 L6 条目、`WorkerDirectoryGroup.LastActivityTicks` 的 remarks），与 `DirectoryIdleTimeout = 1 分钟` 矛盾，会误导后来者"这功能没启用"。修法：接线 #20 时补一组跑真管道 + 生产阈值的端到端场景（或至少在 doctor 里显式报告"空闲回收按探针阈值验过"），并把这两处注释改掉 | `Services/Worker/WorkerPool.cs` / `WorkerDirectoryGroup.cs` / `WorkerSelfCheck.cs` |

更完整的分析与批次划分见 `docs/plans/`（Worker 方案见 `docs/plans/worker-architecture.md`）。
