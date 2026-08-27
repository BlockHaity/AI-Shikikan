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
Runtime/     - AgentToolFactory: 工具集组装 (run_<agent>/assign_task/run_subagents/
               persona_list/git_*)
Git/         - Git 步骤管理 (自动分支/回滚/合并/提交)
Usage/       - 用量统计持久化 (UsageStatsService) + 模型档案 (ModelProfileService)
```

启动外观：`CommanderRuntime.Boot()`（`Core/Services/CommanderRuntime.cs`）初始化配置目录、示例文件、全部服务与工具集，并注册到静态 `Instance`。GUI 外壳单例 `AppShell`（ViewModels/AppShell.cs）持有 Runtime。

## 常用命令

```bash
# 构建（输出到 artifacts/）
./build.sh linux          # 本平台
./build.sh all            # 所有平台 (linux/macos/windows x x64/arm64)
ARCH=arm64 ./build.sh linux
AOT_MODE=off ./build.sh linux

# Debug 构建并运行（最常用的本地开发方式）
./debug.sh                # 构建 Debug 版 GUI 并运行
./debug.sh --version      # 查看版本
./debug.sh doctor         # 环境诊断

# 版本来源：根目录 VERSION 文件（单一版本源，软件与 CI 共用）
# 环境变量可覆盖：CONFIGURATION / VERSION / ARCH / AOT_MODE
```

## 运行方式

```bash
./AIShikikan.Gui           # Avalonia 图形界面
./AIShikikan.Gui --version # 查看版本
./AIShikikan.Gui doctor    # 环境诊断
```

## 配置与数据路径

用户配置在用户目录（`AppPaths`，见 `Core/AppPaths.cs`）：

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
- `Core` 必须保持 **AOT 兼容**（`IsAotCompatible=true`）：禁用反射式序列化等 AOT 不安全写法；JSON 序列化用源生成 `System.Text.Json` 上下文（`Core/Serialization/AppJsonContext.cs`），TOML 用 `Tomlyn`，YAML 用 `YamlDotNet`。
- 新增服务放入 `Core/Services/<领域>/`，并通过 `CommanderRuntime.Boot` 装配。
- 代码注释使用中文（与现有代码一致）。
- GUI 遵循 MVVM：ViewModels 继承 `ViewModelBase`，视图绑定 `Views/*.axaml`，资源字符串在 `Resources/Strings.resx`。
- 不得在代码中硬编码版本号，统一读取根目录 `VERSION`（见 `Directory.Build.props`）。
- 不得在代码中硬编码 API Key 等机密；Key 通过 `providers.toml` 或环境变量注入。

## 提交规范

遵循 Conventional Commits，scope 用项目/模块名，说明用中文。参考现有历史：

```
feat(core): 描述
fix(gui): 描述
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
- Native AOT 交叉编译受限：`AOT_MODE=auto` 下仅对宿主 RID 使用 AOT，其余目标回退到单文件裁剪发布，这是预期行为，不要"修复"。
