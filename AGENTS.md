# AGENTS.md

面向 AI 编码助手 / Agent 的项目指南。开始工作前请阅读本文件。

## 项目概览

AI-Shikikan 是一个用 **.NET 10 / C#** 开发的「Agent 指挥官」：把 Claude Code、OpenCode、Codex CLI、Gemini CLI、Reasonix 等终端 Agent 集合起来，由 AI 统一调度完成复杂任务。

用户请求 → AI 指挥官 → 分析任务 → 调用子 Agent → 汇总结果 → 返回用户。

## 项目结构

单一项目 `AIShikikan.Gui.csproj`，位于**仓库根目录**（无 src 嵌套）：

```
/                          - 仓库根 = 项目根
  Program.cs / App.axaml(.cs) / ViewLocator.cs
  Core/                    - 核心逻辑（AOT 兼容）
  ViewModels/ Views/       - Avalonia MVVM
  Services/                - GUI 层服务 (DynamicThemeService)
  Resources/               - 双语字符串 + Markdown 主题
  Assets/                  - 字体、logo
  templates/               - 配置/人格 example 文件
```

根目录文件：

| 文件 | 说明 |
|------|------|
| `VERSION` | 单一版本源（软件与 CI 共用，勿在代码中硬编码） |
| `Directory.Build.props` | 全局 MSBuild 属性（从 VERSION 注入版本） |
| `build.sh` / `build.ps1` | 发布构建脚本（`./build.sh linux` / `all`，支持 `ARCH` / `AOT_MODE`） |
| `debug.sh` / `debug.ps1` | Debug 构建并运行 GUI（本地开发最常用） |
| `.github/workflows/release.yml` | 手动触发的 Release 发布流程 |
| `.github/workflows/debug.yml` | 手动触发的构建产物辅助 workflow |

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
Git/         - Git 步骤管理 (自动分支/回滚/合并/提交)
Mcp/         - MCP 客户端: McpConfigService(TOML 配置), McpStdioClient(手写 stdio
               JSON-RPC 2.0, 零依赖 AOT 兼容), McpService(连接管理+路由),
               McpProxyTool(ITool 桥接, 工具名 mcp_<serverId>_<toolName>)
Usage/       - 用量统计持久化 (UsageStatsService) + 模型档案 (ModelProfileService)
```

启动外观：`CommanderRuntime.Boot()`（`Core/Services/CommanderRuntime.cs`）初始化配置目录、示例文件、全部服务与工具集，并注册到静态 `Instance`。GUI 外壳单例 `AppShell`（ViewModels/AppShell.cs）持有 Runtime。

## 关键机制

- **引擎循环**：`AgentEngine` 将人格 + Roster 提示词发给 LLM，执行工具调用直至回合结束，事件流 `AgentEngineEvent` 驱动 UI。
- **子 Agent 工具**：均同步执行。`run_<agent>`（单个）、`assign_task`（分派返回 assignmentId）、`run_subagents`（并发多任务，`Task.WhenAll` 后汇总）。无 `mode` 参数。
- **Compact Subagent**：会话级子代理配置（AgentPanel 每条目独立开关，存于 roster.json `CompactEnabled`），开启后该子代理超 2000 字符的输出先经 LLM 压缩为纪要再返回；无全局开关。
- **右侧栏与工具可见性**：聊天页右侧栏关闭时，`CommanderRuntime.SetSubagentToolsVisible(false)` 从 ToolRegistry 移除 `run_<agent>` / `assign_task` / `run_subagents` 并清空 Roster 注入（下一回合生效），重新打开时重建并恢复 Roster。
- **MCP 服务器**：配置于 `mcp-servers.toml`（stdio 传输，command/args/env）。`CommanderRuntime.Boot` 后台连接启用的服务器并把其工具桥接为 `mcp_<serverId>_<toolName>` 注册进 ToolRegistry（MCP 工具默认需批准）。设置页可增删/开关/重连，调用 `Runtime.RefreshMcpToolsAsync()`。协议版本 "2025-06-18"。
- **工具结果出口**：所有 Agent 执行工具统一走 `AgentExecutor.ExecuteAsync(..., llm, ct)`，压缩在该出口生效。
- **聊天页**：支持手动停止按钮 + 双击 ESC 终止生成、「继续输出」续写。
- **首页**：用量统计含 ScottPlot 折线图（固定坐标轴 + 标尺 + 折点悬浮详情）与 26 周活跃热力图，配色全部跟随主题资源。
- **设置页**：卡片使用自绘 `Views/WaterfallPanel.cs` 自适应瀑布流布局。
- **Linux 输入法**：Program.cs 的 `FixupLinuxImeEnvironment()` 启动时清洗 IME 环境变量弯引号、缺失时探测 fcitx/ibus 进程补写 `AVALONIA_IM_MODULE`，并显式启用 X11 IME。

## 常用命令

```bash
# 构建（输出到 artifacts/）
./build.sh linux          # 本平台
./build.sh all            # 所有平台 (linux/macos/windows x x64/arm64)
ARCH=arm64 ./build.sh linux
AOT_MODE=off ./build.sh linux   # 关闭 AOT 回退单文件裁剪

# Debug 构建并运行（最常用的本地开发方式）
./debug.sh                # 构建 Debug 版 GUI 并运行
./debug.sh --version      # 查看版本
./debug.sh doctor         # 环境诊断

# 版本来源：根目录 VERSION 文件（当前 1.0.0）
# 环境变量可覆盖：CONFIGURATION / VERSION / ARCH / AOT_MODE
# Windows 使用 build.ps1 / debug.ps1，参数等价
```

## 运行方式

```bash
./AIShikikan.Gui           # Avalonia 图形界面
./AIShikikan.Gui --version # 查看版本
./AIShikikan.Gui doctor    # 环境诊断
```

## 技术栈与关键约束

- .NET 10 (`net10.0`)，`PublishAot=true`：全链路 Native AOT 兼容。
- UI：Avalonia 12 + CCSWE.Avalonia.Material (Material3)、Markdown.Avalonia、Material.Icons、ScottPlot.Avalonia（趋势图）。
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
| `mcp-servers.toml` | MCP 服务器定义（stdio：command/args/env/enabled） |
| `preferences.toml` | 界面偏好（明暗/语言/字体/背景） |
| `personas/` | 人格/专家 Markdown(YAML frontmatter) |
| `templates/` | 任务模板 |
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
- 本地构建辅助：`.github/workflows/debug.yml`（仅构建产物）。

## 注意事项

- 不要直接提交 `artifacts/`、`bin/`、`obj/`（已在 `.gitignore` 中）。
- 不要提交用户配置（providers.toml 等含 API Key）。
- Native AOT 交叉编译受限：`AOT_MODE=auto` 下仅对宿主 RID 使用 AOT，其余目标回退单文件裁剪发布，这是预期行为，不要"修复"。
- 图表交互类需求注意保持"固定坐标系"语义（禁用平移缩放），悬浮层用 Canvas 叠加避免影响布局测量。
