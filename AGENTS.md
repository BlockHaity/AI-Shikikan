# AGENTS.md

面向 AI 编码助手 / Agent 的项目指南。开始工作前请阅读本文件。

## 项目概览

AI-Shikikan 是一个用 **.NET 10 / C#** 开发的「Agent 指挥官」：把 Claude Code、OpenCode、Codex CLI、Gemini CLI、Reasonix 等终端 Agent 集合起来，由 AI 统一调度完成复杂任务。

用户请求 → AI 指挥官 → 分析任务 → 调用子 Agent → 汇总结果 → 返回用户。

## 架构

三个项目，位于 `src/`：

```
AIShikikan.Core    - 核心逻辑（无 UI 依赖，AOT 兼容）
AIShikikan.Cli     - 终端界面 (Spectre.Console) + REST API 服务器
AIShikikan.Gui     - 图形界面 (Avalonia + CommunityToolkit.Mvvm)
```

`Core` 内部结构（`src/AIShikikan.Core/Services/`）：

```
Engine/      - Agent 调度引擎 (AgentEngine, AssignmentManager, RosterBuilder)
Agents/      - Agent 定义与配置加载 (CliAgentDefinition, AgentConfigService)
Llm/         - LLM 客户端抽象 (OpenAI / Anthropic 双实现)
Personas/    - 人格/专家管理 (YAML frontmatter + Markdown)
Templates/   - 任务模板
Tools/       - 内置工具框架 + 文件工具 (Glob/Grep/ReadFile/ListDirectory)
Git/         - Git 步骤管理 (自动分支/回滚/合并)
Runtime/     - 工具工厂 (AgentToolFactory)
```

启动外观：`CommanderRuntime.Boot()`（`src/AIShikikan.Core/Services/CommanderRuntime.cs`）初始化配置目录、示例文件、所有服务与工具集，CLI/GUI 共用。

## 文件索引

### 根目录

| 文件 | 说明 |
|------|------|
| `VERSION` | 单一版本源（软件与 CI 共用，勿在代码中硬编码） |
| `Directory.Build.props` | 全局 MSBuild 属性（target、版本注入等） |
| `build.sh` | 发布构建脚本（`./build.sh linux` / `all`，支持 `ARCH` / `AOT_MODE`） |
| `debug.sh` | Debug 构建并运行 CLI / GUI（本地开发最常用） |
| `templates/config/` | 配置 example 文件（providers.toml / agents.toml / roster.prompt） |
| `templates/personas/` | 人格 example Markdown |
| `.github/workflows/release.yml` | 手动触发的 GitHub Release 发布流程 |
| `.github/workflows/debug.yml` | 仅构建产物的辅助 workflow |

### AIShikikan.Core（核心逻辑，AOT 兼容）

| 文件 | 说明 |
|------|------|
| `AppPaths.cs` | 跨平台用户配置目录解析 |
| `AppInfo.cs` | 应用名称等常量 |
| `Models/` | 数据模型：`ChatSession` / `ChatMessage` / `AgentRosterEntry` / `MessageRole` |
| `Serialization/AppJsonContext.cs` | 源生成 `System.Text.Json` 上下文（AOT 必需） |
| `Serialization/TomlBridge.cs` | TOML ↔ JSON 桥接（Tomlyn） |
| `Serialization/YamlFrontmatterParser.cs` | 解析 Markdown 的 YAML frontmatter（人格） |
| `Services/CommanderRuntime.cs` | 启动外观：装配配置、服务、工具集（Boot 入口） |
| `Services/DefaultConfig.cs` | 读取内嵌默认 TOML，首次启动初始化用户配置 |
| `Services/ChatService.cs` | 会话持久化与消息存储 |
| `Services/ChatTitleService.cs` | 调用 LLM 生成会话标题 |
| `Services/ColorExtractionService.cs` | 从图片提取主题色（Material Color Utilities） |
| `Services/ThemeService.cs` | 主题配置读写 |
| `Services/I18nService.cs` | 国际化（文化/本地化资源） |
| `Services/Engine/AgentEngine.cs` | 对话引擎：人格+Roster → LLM → 工具循环，`AgentEngineEvent` 事件流 |
| `Services/Engine/AssignmentManager.cs` | 任务分派（子 Agent 执行/批准/回滚）持久化 |
| `Services/Engine/RosterBuilder.cs` | 组装 Roster 提示词（注入 agents 定义） |
| `Services/Engine/RosterConfigService.cs` | Session 级 Agent 编目（`sessions/{id}/roster.json`） |
| `Services/Agents/CliAgentDefinition.cs` | 子 Agent 定义模型（命令、参数、规则） |
| `Services/Agents/AgentConfigService.cs` | 加载 `agents.toml` 分派规则 |
| `Services/Llm/` | LLM 抽象：`IChatCompletionsClient` 接口 + OpenAI / Anthropic 双实现、`LlmService`（provider 路由）、`ProviderConfig`（providers.toml）、`ModelListService`（拉取模型列表）、`ChatTypes` / `LlmMessages`（请求/消息类型） |
| `Services/Personas/PersonaService.cs` | 人格/专家管理（frontmatter + Markdown） |
| `Services/Templates/AgentTemplateService.cs` | 任务模板加载 |
| `Services/Tools/ToolFramework.cs` | 内置工具框架（`ITool` / `ToolResult`） |
| `Services/Tools/ToolPathSanitizer.cs` | 工具路径安全：禁止越出工作区 |
| `Services/Tools/Builtin/` | 内置文件工具：`GlobTool` / `GrepTool` / `ReadFileTool` / `ListDirectoryTool` |
| `Services/Runtime/AgentToolFactory.cs` | 按 Agent 定义创建工具集 |
| `Services/Git/GitStepService.cs` | Git 步骤管理：自动分支 / 回滚 / 合并 / 提交 |
| `Services/Usage/UsageStatsService.cs` | LLM 与子 Agent 调用用量统计持久化 |
| `Services/Usage/ModelProfileService.cs` | 模型档案（上下文窗口 / 价格） |

### AIShikikan.Cli（TUI + REST API）

| 文件 | 说明 |
|------|------|
| `Program.cs` | 入口：解析命令行（TUI / api / doctor / --persona） |
| `Api/ApiServer.cs` | 内嵌 REST API 服务器（任务提交等） |
| `Api/ApiModels.cs` | API 请求/响应模型 |
| `Api/ApiJsonContext.cs` | API 的源生成 JSON 上下文 |
| `Tui/TuiApp.cs` | Terminal.Gui 主界面装配 |
| `Tui/Services/InputService.cs` | 输入读取与历史记录 |
| `Tui/Services/CommandService.cs` | `/` 前缀命令分发 |
| `Tui/Services/Commands/` | 命令处理器：`AgentCommandHandler`（/agent）、`AgentConfigCommandHandler`、`GitCommandHandler`（/git）、`ProviderCommandHandler`（/provider）、`SessionCommandHandler`（/session）、`StatusCommandHandler`（/status）、`UsageCommandHandler`（/usage）、`ICommandHandler`（接口）、`CommandUi`（公共工具） |
| `Tui/Ui/` | Terminal.Gui 视图：`ChatLogView`（聊天日志）、`InputBar`、`SidebarView` + `AgentTabView` / `StatusTabView` / `GitTabView`、`StatusSnapshot`（与 GUI /status 共用数据）、`TuiUiOutput`（`IUiOutput` 实现）、`UiTheme` / `MarkupConverter` / `MarkupLabelView`（着色渲染） |
| `Tui/Renderers/` | `MessageRenderer` / `StatusBarRenderer` |
| `Models/` | `Message` / `MessageRole`（TUI 聊天模型） |

### AIShikikan.Gui（Avalonia 图形界面，MVVM）

| 文件 | 说明 |
|------|------|
| `Program.cs` / `App.axaml(.cs)` | Avalonia 启动与应用配置 |
| `ViewLocator.cs` | VM ↔ View 映射（MVVM 约定） |
| `ViewModels/ViewModelBase.cs` | 所有 VM 基类（ObservableObject） |
| `ViewModels/AppShell.cs` / `MainWindowViewModel.cs` | 主窗口 / 页面导航壳 |
| `ViewModels/HomePageViewModel.cs` | 首页（状态概览） |
| `ViewModels/ChatPageViewModel.cs` | 聊天页 |
| `ViewModels/SettingsPageViewModel.cs` | 设置页 |
| `ViewModels/AgentPanelViewModel.cs` | Agent 信息面板 |
| `ViewModels/SessionPanelViewModel.cs` | 会话列表面板 |
| `ViewModels/StatusPanelViewModel.cs` | 状态面板（与 CLI StatusSnapshot 同数据源） |
| `ViewModels/GitPanelViewModel.cs` | Git 面板 |
| `Views/` | 对应 XAML 视图 + code-behind（`MainWindow` / `ChatPageView` / `HomePageView` / `SettingsPageView` / 各 PanelView / `Converters.cs` / `PageBackground.axaml`） |
| `Services/DynamicThemeService.cs` | 动态主题（配合 Core 的 ColorExtraction/Theme） |
| `Resources/Strings.cs` | 资源字符串访问器 |

## 常用命令

```bash
# 构建（输出到 artifacts/）
./build.sh linux          # 本平台
./build.sh all            # 所有平台 (linux/macos/windows x x64/arm64)
ARCH=arm64 ./build.sh linux
AOT_MODE=off ./build.sh linux

# Debug 构建并运行（最常用的本地开发方式）
./debug.sh cli            # 构建 Debug 版 CLI 并运行
./debug.sh cli api --port 8090
./debug.sh gui            # 构建 Debug 版 GUI 并运行

# 版本来源：根目录 VERSION 文件（单一版本源，软件与 CI 共用）
# 环境变量可覆盖：CONFIGURATION / VERSION / ARCH / AOT_MODE
```

## 运行方式

```bash
./AIShikikan.Cli                     # TUI 聊天界面
./AIShikikan.Cli --persona <id>      # 指定人格
./AIShikikan.Cli api --port 8090     # REST API 服务器
./AIShikikan.Cli doctor              # 环境诊断
./AIShikikan.Gui                     # Avalonia 图形界面
```

## 配置与数据路径

用户配置在用户目录（`AppPaths`，见 `src/AIShikikan.Core/AppPaths.cs`）：

| 平台 | 配置目录 |
|------|----------|
| Linux | `~/.config/ai-shikikan/` |
| macOS | `~/Library/Application Support/AI-Shikikan/Config/` |
| Windows | `%APPDATA%\AI-Shikikan\Config\` |

关键文件：

| 文件 | 说明 |
|------|------|
| `providers.toml` | LLM Provider（API Key、模型、端点） |
| `agents.toml` | Agent 定义 + 分派规则 |
| `personas/` | 人格/专家 Markdown(YAML frontmatter) |
| `templates/` | 任务模板 |
| `roster.prompt` | Roster 注入模板 |

示例模板在 `templates/`（config 与 personas 子目录）。添加新配置字段时需同步更新 `templates/` 中的 example 文件。

## 编码约定

- 语言：**C#**，target `net10.0`，`Nullable` + `ImplicitUsings` 开启。
- `Core` 必须保持 **AOT 兼容**（`IsAotCompatible=true`）：禁用反射式序列化等 AOT 不安全写法；JSON 序列化用源生成 `System.Text.Json` 上下文（`Serialization/AppJsonContext.cs`），TOML 用 `Tomlyn`，YAML 用 `YamlDotNet`。
- 新增服务放入 `Core/Services/<领域>/`，并通过 `CommanderRuntime.Boot` 装配。
- 代码注释使用中文（与现有代码一致）。
- GUI 遵循 MVVM：ViewModels 继承 `ViewModelBase`，视图绑定 `Views/*.axaml`，资源字符串在 `Resources/Strings.resx`。
- 不得在代码中硬编码版本号，统一读取根目录 `VERSION`（见 `Directory.Build.props`）。
- 不得在代码中硬编码 API Key 等机密；Key 通过 `providers.toml` 或环境变量注入。

## 提交规范

遵循 Conventional Commits，scope 用项目/模块名，说明用中文。参考现有历史：

```
feat(core): 描述
fix(cli): 描述
build: 构建脚本/CI 相关
ci: GitHub Actions 相关
```

## 发布

- 版本：更新根目录 `VERSION` 文件。
- 发布流程：`.github/workflows/release.yml`（手动触发），按 `v<版本>` 标签去重构建并发布 GitHub Release（含 AOT / dotnet / selfcontained 三种包）。发布前请确认 `VERSION` 已更新、对应 tag 不存在。
- 本地构建辅助：`.github/workflows/debug.yml`（仅构建产物）。

## 注意事项

- 不要直接提交 `artifacts/`、`bin/`、`obj/`（已在 `.gitignore` 中）。
- 不要提交用户配置（`providers.toml` 等含 API Key）。
- CLI 的 Native AOT 交叉编译受限：`AOT_MODE=auto` 下仅对宿主 RID 使用 AOT，其余目标回退到单文件裁剪发布，这是预期行为，不要"修复"。
