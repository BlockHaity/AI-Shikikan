# AGENTS.md

面向 AI 编码助手 / Agent 的项目指南。开始工作前请阅读本文件。

## 项目概览

AI-Shikikan 是一个用 **.NET 10 / C#** 开发的「Agent 指挥官」：把 Claude Code、OpenCode、Codex CLI、Gemini CLI、DeepSeek Harness 等终端 Agent 集合起来，由 AI 统一调度完成复杂任务。

用户请求 → AI 指挥官 → 分析任务 → 调用子 Agent → 汇总结果 → 返回用户。

## 项目结构

单一项目 `AIShikikan.Gui.csproj`，位于**仓库根目录**（无 src 嵌套）：

```
/                          - 仓库根 = 项目根
  Program.cs / App.axaml(.cs) / ViewLocator.cs
  Core/                    - 核心逻辑（AOT 兼容）
  ViewModels/ Views/       - Avalonia MVVM
  Services/                - GUI 层服务 (DynamicThemeService, ImageAttachmentService)
  Resources/               - 双语字符串 + Markdown 主题
  Assets/                  - 字体、logo
  templates/               - 配置/人格 example 文件
```

根目录文件：

| 文件 | 说明 |
|------|------|
| `VERSION` | 单一版本源（软件与 CI 共用，勿在代码中硬编码） |
| `Directory.Build.props` | 全局 MSBuild 属性（从 VERSION 注入版本） |
| `build.sh` / `build.ps1` | 发布构建脚本（**仅当前平台**：命令 `all`（默认，三变体）/ `aot` / `selfcontained` / `dotnet` / `clean`，环境变量 `CONFIGURATION` / `VERSION`；无 `ARCH`、无 `AOT_MODE`） |
| `debug.sh` / `debug.ps1` | Debug 构建并运行 GUI（本地开发最常用，余下参数原样传给程序；`debug.sh` 支持 `AOT_MODE`） |
| `.github/workflows/release.yml` | 手动触发的 Release 发布流程 |
| `.github/workflows/debug.yml` | 手动触发的构建产物辅助 workflow |
| `packagers/` | Linux 系统包打包源文件：deb(control) / rpm(spec) / pacman(PKGBUILD) 模板，release.yml 构建时引用 |
| `set-version.sh` | 一键修改版本号（同步 VERSION / PKGBUILD / manifest / 文档与脚本回退值） |

### Core/Services 内部结构

```
Engine/      - Agent 调度引擎 (AgentEngine, AssignmentManager, RosterBuilder,
               RosterConfigService, SubagentCompactService)
Agents/      - Agent 定义与配置加载 (CliAgentDefinition, AgentConfigService)
Llm/         - LLM 抽象: IChatCompletionsClient + OpenAI/Anthropic 双实现,
               LlmService(Provider路由), ProviderConfig, ModelListService,
               ChatTypes(含 ThinkingLevel 思考深度), LlmMessages
Personas/    - 人格/专家管理 (YAML frontmatter + Markdown)
Templates/   - 任务模板
Tools/       - 内置工具框架 (ITool/ToolResult/ToolPathSanitizer) + Builtin 文件工具
Runtime/     - AgentToolFactory: 工具集组装 (run_<agent>/assign_task/run_subagents/git_*; AI 不可指定人格/模板, 专家只由用户配置决定)
Git/         - Git 检查点服务 (GitService: HEAD commit + tag 检查点、Reset/Revert/Fork), GitCheckpointStore(检查点持久化), GitTypes
Mcp/         - MCP 客户端: McpConfigService(TOML 配置), McpStdioClient(手写 stdio
               JSON-RPC 2.0, 零依赖 AOT 兼容), McpService(连接管理+路由),
               McpProxyTool(ITool 桥接, 工具名 mcp_<serverId>_<toolName>)
Session/     - 会话运行时: SessionRuntimeRegistry(会话引擎注册表),
               WorkspaceExecutionCoordinator(同工作树同分支并发/跨分支互斥),
               GitWorkspaceResolver(目录 → worktree+当前分支), EngineEventHub(事件总线)
Usage/       - 用量统计持久化 (UsageStatsService) + 模型档案 (ModelProfileService, 可选 models.toml)
ChatService.cs - 会话与消息持久化 (ChatSession/ChatMessage 读写)
ColorExtractionService.cs - 背景图主色提取(动态主题取色)
```

启动外观：`CommanderRuntime.Boot()`（`Core/Services/CommanderRuntime.cs`）初始化配置目录、示例文件、全部服务与工具集，并注册到静态 `Instance`。GUI 外壳单例 `AppShell`（ViewModels/AppShell.cs）持有 Runtime。

## 关键机制

- **引擎循环**：`AgentEngine` 将人格 + Roster 提示词发给 LLM，执行工具调用直至回合结束，事件流 `AgentEngineEvent` 驱动 UI。
- **子 Agent 工具**：均同步执行。`run_<agent>`（单个）、`assign_task`（分派返回 assignmentId）、`run_subagents`（并发多任务，`Task.WhenAll` 后汇总）。无 `mode` 参数。Agent 可配置 `plan_args`（Plan 模式附加 CLI 参数）；主对话处于 Plan 模式且该子代理的会话条目开启 `UseInPlanMode` 时，启动进程会追加 plan_args。Plan 模式下 `CommanderRuntime.SetPlanMode` 三层过滤未授权子代理：仅注册授权 Agent 的 `run_<agent>`（无授权者连 `assign_task`/`run_subagents` 一并移除）、Roster 注入仅列出授权条目、`AgentExecutor` 执行兜底拒绝。
- **Compact Subagent**：会话级子代理配置（AgentPanel 每条目独立开关，存于 roster.json `CompactEnabled`），开启后该子代理超 2000 字符的输出先经 LLM 压缩为纪要再返回；无全局开关。
- **右侧栏与工具可见性**：聊天页右侧栏关闭时，`CommanderRuntime.SetSubagentToolsVisible(false)` 从 ToolRegistry 移除 `run_<agent>` / `assign_task` / `run_subagents` 并清空 Roster 注入（下一回合生效），重新打开时重建并恢复 Roster。
- **MCP 服务器**：配置于 `mcp-servers.toml`，支持 stdio（command/args/env）、`http`（Streamable HTTP）与 `sse`（HTTP+Server-Sent Events）三种传输（http/sse 用 url，`McpHttpClient`/`McpStdioClient` 共享 `McpClientBase` JSON-RPC 内核）。`CommanderRuntime.Boot` 后台连接启用的服务器并把其工具桥接为 `mcp_<serverId>_<toolName>` 注册进 ToolRegistry（MCP 工具默认需批准）。设置页可增删/开关/重连，调用 `Runtime.RefreshMcpToolsAsync()`。协议版本 "2025-06-18"。
- **工具结果出口**：所有 Agent 执行工具统一走 `AgentExecutor.ExecuteAsync(..., llm, ct)`，压缩在该出口生效。
- **聊天页**：支持手动停止按钮 + 双击 ESC 终止生成、「继续输出」续写。
- **首页**：用量统计含 ScottPlot 折线图（固定坐标轴 + 标尺 + 折点悬浮详情）与 26 周活跃热力图，配色全部跟随主题资源。
- **设置页**：卡片使用自绘 `Views/WaterfallPanel.cs` 自适应瀑布流布局。
- **Linux 输入法**：Program.cs 的 `FixupLinuxImeEnvironment()` 启动时清洗 IME 环境变量弯引号、缺失时探测 fcitx/ibus 进程补写 `AVALONIA_IM_MODULE`，并显式启用 X11 IME。
- **Git 检查点系统**：每条用户消息自动创建检查点（记录 HEAD commit + `ai-shikikan/checkpoint/<id>` tag），卡片支持 Reset（reset --hard）/ Revert（反向提交）/ Fork（从检查点派生分支）。非 Git 仓库降级为警告，聊天仍可用。子 Agent **不再自动 `git switch -c`**，直接在当前分支运行，产出由检查点系统兜底。
- **工作区并发隔离**：`WorkspaceExecutionCoordinator` 以「工作树根 + 当前分支」为键管理执行权——同一 worktree 同一分支可多会话并发，跨分支互斥，不同 worktree 互不影响；回滚 / Fork 确认期用 reservation 挡住该 worktree 的新回合。

## 常用命令

```bash
# 构建（仅当前平台，输出到 artifacts/）
./build.sh                       # = ./build.sh all：aot + selfcontained + dotnet 三变体
./build.sh aot                   # 只构建 Native AOT
./build.sh selfcontained         # 只构建自带 .NET 运行时
./build.sh dotnet                # 只构建框架依赖
./build.sh clean                 # 清理 artifacts/
CONFIGURATION=Debug ./build.sh selfcontained   # 覆盖构建配置

# Debug 构建并运行（最常用的本地开发方式）
./debug.sh                # 构建 Debug 版 GUI 并运行
./debug.sh --version      # 查看版本（-h/--help/help 之外的参数原样传给程序）
./debug.sh doctor         # 环境诊断

# 修改版本号（一键同步 VERSION / PKGBUILD / app.manifest / AGENTS.md 版本标注 / 脚本与 CI 回退值）
./set-version.sh 0.9.1-vibe   # 改版本（GitHub tag 会自动补 v 前缀）
./set-version.sh current      # 查看当前版本

# 版本来源：根目录 VERSION 文件（当前 0.9.0-vibe，用 ./set-version.sh 更新）
# 环境变量：build.sh = CONFIGURATION / VERSION；debug.sh = CONFIGURATION / VERSION / AOT_MODE(auto|always|off，默认 off)
# 架构由 uname 自动探测，无 ARCH 覆盖；跨平台/跨架构构建已移除（交叉编译由 CI 各 runner 分别完成）

# Windows（参数非环境变量，与 sh 版并不等价）
.\build.ps1 [all|aot|selfcontained|dotnet|clean|help] [-Configuration Release] [-Version x.y.z]
.\debug.ps1 [gui|help] [-Configuration Debug] [-Version x.y.z] [-AotMode auto|always|off] [-AppArgs <传给程序的参数>]
#   - .\build.ps1 无 -h/--help，只有位置参数 help
#   - .\debug.ps1 的 -App 只接受 gui|help：直接传 .\debug.ps1 doctor 会被 ValidateSet 拒绝
#   - .\debug.ps1 --version 会被 PowerShell 绑定成 -Version（改的是构建版本号，不会打印版本）
#     程序参数请显式走 -AppArgs，例如 .\debug.ps1 gui -AppArgs "--version"
```

## 运行方式

```bash
./AIShikikan.Gui           # Avalonia 图形界面
./AIShikikan.Gui --version # 查看版本
./AIShikikan.Gui doctor    # 环境诊断
```

## 技术栈与关键约束

- .NET 10 (`net10.0`)，`PublishAot=true`：全链路 Native AOT 兼容。
- UI：Avalonia 12.1.3 + CCSWE.Avalonia.Material (Material3)、Markdown.Avalonia、Material.Icons、ScottPlot.Avalonia（趋势图）。版本以 `AIShikikan.Gui.csproj` 为准。
- MVVM：CommunityToolkit.Mvvm；编译绑定默认开启（XAML 需 `x:DataType`）。
- 序列化约束（AOT 必需）：JSON 仅用源生成上下文 `Core/Serialization/AppJsonContext.cs`；TOML 用 Tomlyn（经 `TomlBridge`）；YAML frontmatter 用 YamlDotNet。禁止反射式序列化。
- LLM SDK：OpenAI / Anthropic 官方 SDK（仅客户端内部 HTTP 细节使用，主数据结构自定义于 ChatTypes.cs）。
- 动态主题取色：MaterialColorUtilities（配合 ColorExtractionService / DynamicThemeService）。

## 配置与数据路径

用户配置按平台解析（见 `Core/AppPaths.cs`），Linux 走 XDG 规范：

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
| `sessions/` | 会话记录（含每会话 roster.json） |
| `usage.json` | 用量统计 |

示例模板在 `templates/`（config 与 personas 子目录）。添加新配置字段时需同步更新对应 example 文件。

## 编码约定

- 语言：**C#**，target `net10.0`，`Nullable` + `ImplicitUsings` 开启。
- 新增服务放入 `Core/Services/<领域>/`，并通过 `CommanderRuntime.Boot` 装配；新工具按类别在 `AgentToolFactory.CreateCoreTools`（固定工具）或 `CreateSubagentTools`（随右侧栏可见性注册的子代理工具）注册。
- Core 必须 AOT 兼容；序列化规则见上节。
- 代码注释使用中文（与现有代码一致）。
- GUI 遵循 MVVM：ViewModels 继承 `ViewModelBase`，视图绑定 `Views/*.axaml`；用户可见文案一律走 `Resources/Strings.resx`（中文）+ `Strings.en.resx`（英文），经 `Strings.cs` 访问器引用。
- 不得在代码中硬编码版本号，统一读取根目录 `VERSION`（见 `Directory.Build.props`）。
- 不得在代码中硬编码 API Key 等机密；Key 通过 `providers.toml` 或环境变量注入。

## 提交规范

遵循 Conventional Commits，scope 用项目/模块名，说明用中文：

```
feat(core): 描述
fix(gui): 描述
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
- 图表交互类需求注意保持"固定坐标系"语义（禁用平移缩放），悬浮层用 Canvas 叠加避免影响布局测量。
