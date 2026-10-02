# AGENTS.md

面向 AI 编码助手 / Agent 的项目指南。开始工作前请阅读本文件。

## 项目概览

AI-Shikikan 是一个用 **.NET 10 / C#** 开发的「Agent 指挥官」：把 Claude Code、OpenCode、Codex CLI、Gemini CLI、DeepSeek Harness 等终端 Agent 集合起来，由 AI 统一调度完成复杂任务。

用户请求 → AI 指挥官 → 分析任务 → 调用子 Agent → 汇总结果 → 返回用户。

## 项目结构

两个项目，源码全在 `src/` 下，仓库根只留构建脚本与元数据：

```
/                              - 仓库根（无源码）
  AIShikikan.slnx              - 唯一解决方案文件（根目录裸 dotnet build 即用它）
  Directory.Build.props        - 两项目共用的 MSBuild 属性（TFM / AOT 开关 / 从 VERSION 注入版本）
  VERSION / LICENSE / CHANGELOG.md / AGENTS.md / README.md
  build.sh build.ps1           - 发布构建（publish + 打包归档）
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
```

两个 csproj 的分工：

| 项目 | 类型 | 关键内容 |
|---|---|---|
| `src/AIShikikan.Core` | 类库 | LLM/MCP SDK、Tomlyn、YamlDotNet；**不引用任何 Avalonia 包**；`DefaultConfig/*.toml` 以 LogicalName `AIShikikan.Core.DefaultConfig.*` 内嵌 |
| `src/AIShikikan.Gui` | Exe（`PublishAot=true`） | Avalonia 全家桶、CommunityToolkit.Mvvm、ScottPlot、MaterialColorUtilities；`ProjectReference` 指向 Core；`AvaloniaResource Assets\**`；`Resources/Strings*.resx`（卫星资源 `en/AIShikikan.Gui.resources.dll` 依赖程序集名，勿改） |

> ⚠️ **不要改程序集名/项目名**。`Strings.cs` 的 `ResourceManager` 名为 `AIShikikan.Gui.Resources.Strings`（由 `RootNamespace` + 目录推出），卫星资源程序集名又由程序集名推出；任一改动都会让英文界面静默回退为中文。

根目录文件：

| 文件 | 说明 |
|------|------|
| `AIShikikan.slnx` | 解决方案（唯一），`dotnet build` / `dotnet clean` 直接用 |
| `VERSION` | 单一版本源（软件与 CI 共用，勿在代码中硬编码） |
| `Directory.Build.props` | 两项目共用属性：TFM / Nullable / ImplicitUsings / `IsAotCompatible` / 裁剪与 AOT 静音开关 / 从 VERSION 注入版本 |
| `build.sh` / `build.ps1` | 发布构建脚本（**仅当前平台**：命令 `all`（默认，三变体）/ `aot` / `selfcontained` / `dotnet` / `clean`，环境变量 `CONFIGURATION` / `VERSION`；无 `ARCH`、无 `AOT_MODE`） |
| `debug.sh` / `debug.ps1` | 本地调试：`dotnet run --project src/AIShikikan.Gui/AIShikikan.Gui.csproj`，余下参数原样透传给程序；`--no-build`（或 `NO_BUILD=1`）跳过编译 |
| `.github/workflows/release.yml` | 手动触发的 Release 发布流程 |
| `.github/workflows/debug.yml` | 手动触发的构建产物辅助 workflow |
| `Packagers/` | Linux 系统包打包：`linux/package.sh`（deb/rpm/pacman 构建脚本）+ deb(control) / rpm(spec) / pacman(PKGBUILD) / .desktop 模板，两个 workflow 均调用 `package.sh` |
| `set-version.sh` | 一键修改版本号（同步 VERSION / PKGBUILD / `src/AIShikikan.Gui/app.manifest` / AGENTS.md / 脚本与 CI 回退值） |

> ✅ 根目录只有 `.slnx` 一个解决方案文件，裸 `dotnet build` 可直接用（重组前根目录同时有 csproj 与 slnx，会报 MSB1011）。
>
> ⚠️ 根 `Directory.Build.props` 里 `SuppressTrimAnalysisWarnings` / `SuppressAotAnalysisWarnings` 是**属性、不是属性组**，两个项目都会继承，不要在单个 csproj 里「顺手删掉」。
>
> ⚠️ `AIShikikan.Core.csproj` 显式写了 `<EnableAotAnalyzer>false</EnableAotAnalyzer>`。SDK 规则是 `EnableAotAnalyzer = (PublishAot || IsAotCompatible)`，Core 作为类库没有 `PublishAot`，规则会翻转并翻出 9 处既有的 `RequiresDynamicCode` 用法。**这不是「关掉检查就没事」**——AOT 兼容性目前只由 `./build.sh aot` 的真实发布兜底（见已知技术债 #2）。

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
Runtime/     - AgentToolFactory: 工具集组装 + AgentExecutor + 全部 ITool 实现
               (含 5 个 git_* 工具; 注意子代理工具不直接操作 git, 该参数被显式丢弃)
Git/         - Git 服务 (GitService: HEAD commit + tag 检查点、Reset/Revert/Fork,
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

## 关键机制

### 引擎与回合

- **引擎循环**：`AgentEngine` 将人格 + Roster 提示词发给 LLM，执行工具调用直至回合结束，事件流 `AgentEngineEvent` 驱动 UI。工具循环上限 `EngineOptions.MaxTurns`（默认 10），每轮发送最近 `MaxHistoryMessages`（默认 40）条。
- **同会话回合串行**：`SessionRuntime` 持有单消费者 `Channel` 队列，`EnqueueTurnAsync` 入队后串行执行。**不要绕过它直接调 `Engine.RunTurnAsync`** —— 那会并发写 `_conversation`（普通 `List`）。GUI 在有排队时禁用发送按钮。
  > `PendingTurnCount` 只计「还在排队等待」的，不含正在执行的那个（UI 用 500ms 轮询它来决定是否显示排队态）。
  > ⚠️ `IsQueued` / `QueuedTurnCount` 目前**没有 XAML 绑定**——禁用发送已生效，但「排队中 (N)」的可视化未接（`SessionPanelViewModel` 侧待接）。
- **自动压缩**：上下文占用达 `AutoCompactThreshold`（默认 0.85 × 窗口）时，`CompactConversationAsync` 把较早历史交给 LLM 摘要，保留最近 6 条。

### 子 Agent

- **三个工具**：`run_<agent>`（单个）、`assign_task`、`run_subagents`。均无 `mode` 参数。三者都是**阻塞式**工具（`await` 到子 Agent 终态才返回，不 fire-and-forget）；其中 `run_subagents` 内部用 `Task.WhenAll` **并发**执行多个子代理。
  > ⚠️ 工具描述里写的「`assign_task` 分派返回 assignmentId」与实现不符——它同样阻塞到终态并直接返回结果，assignmentId 不在返回值里。
- **审批策略一致**：三个工具都需要用户批准（`run_<agent>` 透传 agent 的 `require_approval`，`assign_task` 与 `run_subagents` 恒为 true）；MCP 工具恒需批准。
- **统一在当前分支工作**：子代理**不执行 `git switch -c`**，不建 worktree。产出由每条用户消息的检查点兜底。
- **plan_args**：Agent 可配置 `plan_args`（Plan 模式附加 CLI 参数）；主对话处于 Plan 模式且该子代理的会话条目开启 `UseInPlanMode` 时，启动进程会追加 plan_args。Plan 模式下 `CommanderRuntime.SetPlanMode` 三层过滤未授权子代理：仅注册授权 Agent 的 `run_<agent>`（无授权者连 `assign_task`/`run_subagents` 一并移除）、Roster 注入仅列出授权条目、`AgentExecutor` 执行兜底拒绝。
- **Compact Subagent**：会话级子代理配置（AgentPanel 每条目独立开关，存于 roster.json `CompactEnabled`）。开启后该子代理输出超过 `MinLengthToCompact`（2000 字符）时先经 LLM 压缩为纪要，超过 `TargetLength`（4000 字符）则硬截断。压缩失败或被取消时原样返回。
  > ⚠️ `CompactIfNeededAsync` 已支持传入 `providerId` / `model`，但调用点尚未转发，压缩**目前仍走全局默认 provider/model**。
- **取消与超时区分**：`CliAgentRunResult.Cancelled` 与 `TimedOut` 分开。子代理执行路径的 `catch(Exception)` 之前必须先 `catch(OperationCanceledException) { throw; }`，否则停止按钮只会停住主循环而子进程仍在跑。

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

### 超时约定

所有等待外部响应的位置都必须有超时兜底，否则 GUI 无订阅者时会永久挂起：

| 位置 | 值 | 说明 |
|---|---|---|
| `AgentEngine.ApprovalTimeout` | 5 分钟 | 工具审批与 `ask_user` 等待，超时按拒绝处理 |
| `McpClientBase.DefaultCallTimeout` | 300 秒 | `tools/call`（真正的工具执行） |
| `McpClientBase.DefaultSetupTimeout` | 30 秒 | `initialize` / `tools/list` |
| `GitService` 默认 / 网络 | 15 / 60 秒 | 只读命令 / pull、push |

### Git 检查点系统

每条用户消息自动创建检查点（记录 HEAD commit + `ai-shikikan/checkpoint/<id>` tag），卡片支持 **Reset**（`reset --hard`）/ **Revert**（反向提交）/ **Fork**（从检查点派生分支）。这是**唯一的回滚入口**——工具卡上没有回滚按钮。非 Git 仓库降级为警告，聊天仍可用。

> 早期版本曾用 `ac/<stepId>` 分支给子代理做检查点，该机制已废弃（`GitStepService` / `LegacyGitMigrationService` 已删除）。
> `doctor` 会扫描并提示清理遗留的 `ac/*` 分支、`steps/*.json` 与检查点 tag（**只提示不自动删**，检查点 tag 是当前回滚依据）。

### 工作区并发隔离

`WorkspaceExecutionCoordinator` 以「工作树根 + 当前分支」为键管理执行权：同一 worktree 同一分支可多会话并发，跨分支互斥，不同 worktree 互不影响。目前**只有 `TryBeginTurn` / `EndTurn` 接了生产调用方**（`SessionRuntime` ← `AgentEngine`），且只覆盖「跨分支互斥」这一条规则。

> ⚠️ **已知缺口**：`TryReserve` / `WaitGitWriteAsync` / `TryEnterGitWrite` / `TryBeginAssignment` **均无生产调用方**（仅 `SelfCheck` 覆盖）。回滚 / Fork 确认期**没有** worktree 级保护——目前只靠 `Fork_SessionOccupied` 这条**会话级**守卫（当前会话在跑就禁用 Fork）。若要接通 reservation，见 `docs/plans/`。

### 会话停止的语义陷阱

`SessionRuntime.Stop()` → `AgentEngine.CancelSession()` 取消 `_sessionCts`。该 CTS **只创建一次且取消后不再重置**，因此 **`Stop()` 之后该会话引擎无法再执行任何回合**（后续回合会立即被取消）。恢复路径只有删除并重建会话运行时。

聊天页的「停止」按钮走的是另一条路径（每会话独立的 `_turnCts`），不影响后续回合——两者语义不同，不要混用。

### 事件流

`EngineEventHub` 是全局单例，所有引擎共享。投递规则：未标注归属的事件全量投递；`EngineApprovalRequested` / `EngineQuestionRequested` / `EngineAssignmentChanged` 强制投递（否则引擎会永久等待）；其余仅投递到当前活动会话。每个订阅者独立 try/catch，单个订阅者异常不影响其他。

**GUI 侧铁律**：`AgentEngineEvent` 可能在引擎线程触发，且 `run_subagents` 下多个子代理的进度回调会**并发**进入。`ChatPageViewModel` 用 `ConcurrentQueue` + `Interlocked` 节流把事件转交给 UI 线程，**不要**在引擎线程直接碰 UI 集合。

### 其他

- **MCP 服务器**：配置于 `mcp-servers.toml`，支持 stdio（command/args/env）、`http`（Streamable HTTP）与 `sse`（HTTP+Server-Sent Events）三种传输。设置页可增删/开关/重连，调用 `Runtime.RefreshMcpToolsAsync()`。协议版本 "2025-06-18"。
- **工具结果出口**：所有 Agent 执行工具统一走 `AgentExecutor.ExecuteAsync(..., llm, ct)`，压缩在该出口生效。
- **聊天页**：支持手动停止按钮 + 双击 ESC（600ms 内两次）终止生成、「继续输出」续写。
- **首页**：用量统计含 ScottPlot 折线图（固定坐标轴 + 标尺 + 折点悬浮详情）与活跃热力图。趋势图与热力图**共用全局时间范围**（近 7 / 14 / 30 天 / 全部，默认近 14 天），热力图周列数按范围动态计算；选「全部」且无数据时回退近 26 周。配色全部跟随主题资源。
- **设置页**：卡片使用自绘 `Views/WaterfallPanel.cs` 自适应瀑布流布局。
- **Linux 输入法**：Program.cs 的 `FixupLinuxImeEnvironment()` 启动时清洗 IME 环境变量弯引号、缺失时探测 fcitx/ibus 进程补写 `AVALONIA_IM_MODULE`，并显式启用 X11 IME。

## 常用命令

```bash
# 构建（仅当前平台，输出到 artifacts/）
./build.sh                       # = ./build.sh all：aot + selfcontained + dotnet 三变体
./build.sh aot                   # 只构建 Native AOT
./build.sh selfcontained         # 只构建自带 .NET 运行时
./build.sh dotnet                # 只构建框架依赖
./build.sh clean                 # 清理 artifacts/
CONFIGURATION=Debug ./build.sh selfcontained   # 覆盖构建配置

# 编译检查（改完代码至少跑一次；根目录只有 slnx，裸命令直接可用）
dotnet build                                        # 构建两个项目
dotnet build src/AIShikikan.Gui/AIShikikan.Gui.csproj   # 只构建 GUI（Core 作为 ProjectReference 带上）
dotnet build src/AIShikikan.Core/AIShikikan.Core.csproj # 只构建 Core

# 本地调试（本地开发最常用；走 dotnet run，不 publish、不产出 AOT 二进制）
./debug.sh                # 增量编译并启动 GUI
./debug.sh --version      # 查看版本（-h/--help/help 之外的参数原样传给程序）
./debug.sh doctor         # 环境诊断（含并发规则自检 + 废弃产物提示）
./debug.sh --no-build doctor   # 跳过编译，只跑上次的产物（反复 attach 调试器时用）
NO_BUILD=1 ./debug.sh --version  # --no-build 的环境变量写法

# AOT / 自包含 / 单文件产物只能走发布链路验证
./build.sh aot

# Linux 系统包（deb / rpm / pacman）
VERSION=$(cat VERSION) VARIANTS=dotnet ./Packagers/linux/package.sh   # 需先 ./build.sh dotnet

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

# 发布产物（./build.sh <变体> 之后，产物名恒为 AIShikikan.Gui）
./artifacts/dotnet/AIShikikan.Gui           # 框架依赖
./artifacts/selfcontained/AIShikikan.Gui   # 自带运行时（单文件）
./artifacts/aot/AIShikikan.Gui             # Native AOT
```

## 技术栈与关键约束

- .NET 10 (`net10.0`)，`PublishAot=true`（仅 Gui）：全链路 Native AOT 兼容。
- **两项目分层**：`AIShikikan.Core` 不得引用任何 Avalonia 包（Core 里出现 `using Avalonia` 即为分层破坏）；Gui 单向引用 Core，Core **不得**反向引用 Gui（`ColorExtractionService` 因此被移到 GUI 层）。
- UI：Avalonia 12.1.3 + CCSWE.Avalonia.Material (Material3)、Markdown.Avalonia、Material.Icons、ScottPlot.Avalonia（趋势图）。版本以 `src/AIShikikan.Gui/AIShikikan.Gui.csproj` 为准。
- MVVM：CommunityToolkit.Mvvm；编译绑定默认开启（`AvaloniaUseCompiledBindingsByDefault=true`，XAML 需 `x:DataType`）。XAML 里的 `clr-namespace` / `using:` 可直接引用 Core 的命名空间（跨程序集解析已验证）。
- 序列化约束（AOT 必需）：JSON **仅**用源生成上下文 `AppJsonContext.cs`（位于 `src/AIShikikan.Core/Serialization/`）；TOML 用 Tomlyn（经 `TomlBridge`）；YAML frontmatter 用 YamlDotNet。禁止反射式序列化。
- LLM SDK：OpenAI / Anthropic 官方 SDK（仅客户端内部 HTTP 细节使用，主数据结构自定义于 `ChatTypes.cs`），仅 Core 引用。
- 动态主题取色：MaterialColorUtilities（GUI 层，配合 `ColorExtractionService` / `DynamicThemeService`）。

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

当前覆盖：14 个服务的写入端、11 个服务的读取端（带 `.bak` 回退）。

> ⚠️ 仍有裸读路径未走 `.bak` 回退：`ChatService.ReadMetadata`（会话列表主加载路径）、
> `SessionRuntimeRegistry.ReadTitle`、`AgentTemplateService` 的全部读取。新增带备份的服务时记得一并加固。

> ⚠️ 用 `AtomicFile.Delete` 而非 `File.Delete`，否则删主文件时会留下 `.bak`；`CommanderRuntime.DeleteIfExists`（「还原默认设置」）已因此踩过坑——会复活旧 API Key。

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

| 文件 | 说明 |
|------|------|
| `providers.toml` | LLM Provider（API Key、模型、端点、思考等级限制） |
| `agents.toml` | Agent 定义 + 推荐专家 |
| `mcp-servers.toml` | MCP 服务器定义（stdio/http/sse 传输；默认不内置任何服务器） |
| `preferences.toml` | 界面偏好（明暗/语言/字体/背景） |
| `personas/` | 人格/专家 Markdown(YAML frontmatter) |
| `templates/` | 任务模板 |
| `models.toml` | 模型上下文窗口与价格（可选；缺失时回退 Provider `/v1/models`） |
| `roster.prompt` | Roster 注入模板 |
| `sessions/` | 会话记录（含每会话 `roster.json`） |
| `usage.json` | 用量统计 |
| `checkpoints/` | Git 检查点记录（按仓库哈希分目录） |
| `assignments/` | 子代理分派记录 |

内嵌默认配置在 `src/AIShikikan.Core/DefaultConfig/`（以 `AIShikikan.Core.DefaultConfig.<文件名>` 为 LogicalName 嵌入 **Core** 程序集，`DefaultConfig.Load` 按 `typeof(DefaultConfig).Assembly` 取，首启动时写出）。
面向用户的示例文件在 `templates/`。**添加新配置字段时需同步更新两处**。

## 编码约定

- 语言：**C#**，target `net10.0`，`Nullable` + `ImplicitUsings` 开启。
- 新增服务放入 `src/AIShikikan.Core/Services/<领域>/`（与 Avalonia 无关的业务逻辑）或 `src/AIShikikan.Gui/Services/`（界面相关），并通过 `CommanderRuntime.Boot` 装配；新工具按类别在 `AgentToolFactory.CreateCoreTools`（固定工具）或 `CreateSubagentTools`（随右侧栏可见性注册的子代理工具）注册。
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
fix(gui): 描述
refactor(git)!: 描述
build: 构建脚本/CI 相关
ci: GitHub Actions 相关
```

## 发布

- 版本：更新根目录 `VERSION` 文件。
- 发布流程：`.github/workflows/release.yml`（手动触发），按 `v<版本>` 标签去重构建并发布 GitHub Release（含 AOT / dotnet / selfcontained 三种包）。发布前确认 `VERSION` 已更新且对应 tag 不存在。
- 本地构建辅助：`.github/workflows/debug.yml`（手动触发；产出上传为 workflow artifacts，含 Linux deb/rpm/pacman 打包，但不发布 GitHub Release）。

## 注意事项

- 不要直接提交 `artifacts/`、`bin/`、`obj/`（已在 `.gitignore` 中）。
- 不要提交用户配置（providers.toml 等含 API Key）。
- 构建脚本只构建**当前平台**：跨平台/跨架构构建已移除，交叉编译由 CI 各 runner 分别完成（见 `build.sh` 头部注释与 `.github/workflows/*.yml`），不要再往脚本里加平台矩阵。
- `AOT_MODE` 仅 `debug.sh` / `debug.ps1` 支持（`auto|always|off`，默认 `off`）；`build.sh` / `build.ps1` 按命令分别发 aot / selfcontained / dotnet，没有 AOT 回退逻辑。
- 图表交互类需求注意保持「固定坐标系」语义（禁用平移缩放），悬浮层用 Canvas 叠加避免影响布局测量。

## 已知技术债

改动前请了解这些坑，能顺手修就修：

| # | 问题 | 位置 |
|---|---|---|
| 1 | **无测试、无 CI 门禁**：仓库无任何测试项目，两个 workflow 均需手动触发，push/PR 不做验证 | 全仓 |
| 2 | **AOT 兼容性无人验证**：`SuppressTrimAnalysisWarnings` + `SuppressAotAnalysisWarnings` 把裁剪/AOT 分析器全静音；`AIShikikan.Core.csproj` 另需显式 `EnableAotAnalyzer=false`（拆包后类库无 `PublishAot`，SDK 规则会翻转并翻出 9 处既有 `RequiresDynamicCode` 用法） | `Directory.Build.props` / `src/AIShikikan.Core/AIShikikan.Core.csproj` |
| 3 | **UI 线程同步跑 git 进程**：发消息前会在 UI 线程拉起约 6 个 git 进程（`GitService.Run` 用 `GetAwaiter().GetResult()`），最坏可冻结数十秒 | `GitService.cs` |
| 4 | **流式文本无时间节流**：每个 token 触发一次全量 Markdown 重解析，长回复呈 O(n²) | `ChatPageViewModel.cs` |
| 5 | **图片附件全流程在 UI 线程**：解码 + PNG 编码 + 二次解码，单张可达 1 秒（最多连贴 4 张） | `ImageAttachmentService.cs` |
| 6 | **背景图取色全量像素搬运**：4K 图约 100MB 分配 + 830 万次循环，切换背景即触发 | `MainWindow.axaml.cs` |
| 7 | **`Bitmap` 从不 Dispose**：图片分段与附件缩略图泄漏原生内存 | `ChatItemViewModel.cs` / `ImageAttachmentService.cs` |
| 8 | **MCP 客户端无自动重连**：进程崩溃后 UI 仍显示已连接，只能手动重连 | `Mcp/` |
| 9 | **LLM 层无 429/5xx 重试退避**，无首 token 超时 | `src/AIShikikan.Core/Services/Llm/` |
| 10 | **每 agent 一个 `run_<id>` 工具无上限**：agent 多了会挤占上下文并降低工具选择准确率 | `AgentToolFactory.cs` |
| 11 | **超大文件**：`ChatPageViewModel.cs` 约 1750 行、`ChatPageView.axaml` 875 行 | `ViewModels/` `Views/` |
| 12 | **重复代码**：`Truncate` 5 份（同名函数一个取头一个取尾）、`EnsureUniqueId` 2 份、JSON 参数提取 4 份、路径规范化 4 套 | `src/AIShikikan.Core/` `src/AIShikikan.Gui/ViewModels/` |
| 13 | ~~csproj 冗余~~（**已解决**）：失效的 `<Folder Include="Models\" />` 与和 `Assets\**` 重复的 `logo.jpg` 条目在拆包重建 csproj 时已删除 | `src/AIShikikan.Gui/AIShikikan.Gui.csproj` |
| 14 | **`TryReserve` 等协调器 API 无生产调用方**，回滚/Fork 确认期无 worktree 级保护 | `WorkspaceExecutionCoordinator.cs` |
| 15 | **子代理输出未接检查点**：`Assignment.StepId` 已无人赋值，工具卡头部的「检查点:」会是空白 | `AgentToolFactory.cs` |
| 16 | **压缩未转发当前回合 provider/model**：`ToolContext` 已注入但 `AgentExecutor` 未传给 `CompactIfNeededAsync` | `AgentToolFactory.cs` |
| 17 | **原子写未全覆盖**：3 处读取路径仍裸读（会话列表主加载、会话标题、模板读取） | 见上节 |
| 18 | **i18n 自检未接线**：`Strings.FindMissingEnglishKeys()` 已写好但无调用方；4 个 `ToolCard_Rollback*` 键已成孤儿 | `Resources/Strings.cs` |

更完整的分析与批次划分见 `docs/plans/`。
