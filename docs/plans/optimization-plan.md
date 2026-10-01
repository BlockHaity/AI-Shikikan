# AI-Shikikan 优化方案（2026-09-30）

> **状态：第 1 批 + 第 2 批（Git 体系）+ i18n + 工程治理已实施完成，`dotnet build` 0 错误 0 警告，`doctor` 全绿。**
> 本文件是 `/home/blockhaity/.opencode/plan` 的项目内备份，随代码一起进版本库。

## 背景

对项目做了 4 路并行探索（核心引擎 / LLM 与工具层 / GUI 与 MVVM / 构建与代码质量），
产出 30 项问题清单。用户选择推进 **第 1 批（修 Bug）+ 第 3 批（健壮性）+ 第 4 批（工程治理）**，
明确跳过第 2 批（UI 冻结性能优化）。

探索期间发现 `AGENTS.md` / `README.md` 与代码存在**设计与实现冲突**，经逐项确认后形成下列决策基线。

---

## 一、决策基线（用户已确认，不可擅自更改）

| # | 决策 | 依据 |
|---|---|---|
| D1 | **所有子代理在同一分支工作**，不 `git switch -c`，不建 worktree | 文档早已声称如此，代码却仍在切分支 |
| D2 | **删除 `max_concurrent` 字段** | 该字段从未被引擎使用（仅出现在 Roster 提示词文案里） |
| D3 | 同会话并发回合 → **Core 层排队执行**，UI 期间禁用发送 | 原设计会并发写 `AgentEngine._conversation`（普通 List） |
| D4 | **审批机制完整保留**（分派 + 写操作都审批），`run_subagents` 改为与其他一致 | 修正策略不对称 |
| D5 | **删掉工具卡的「回滚」按钮** | 它依赖即将删除的 `ac/` 分支机制 |
| D6 | **全面删除 `GitStepService`**（490 行）+ 旧 5 个 `git_*` 工具，换成 4 个基于 `GitService` 的新工具 | 子代理不再建分支后，这套机制失去意义 |
| D7 | 启动时**提示清理** `ac/*` 分支 + `steps/*.json` + `ai-shikikan/checkpoint/*` tag | 旧机制残留 + 检查点 tag 随每条消息累积 |

### D1 的根因（关键发现）

`AGENTS.md:78` 与 `README.md:36,150` 都写着「子 Agent 不再自动 `git switch -c`，直接在当前分支运行」，
但 `AssignmentManager.cs:129` 仍在调 `Git.BeginStep(...)`，而 `GitStepService.cs:220` 真的执行
`Run("switch", "-c", record.StepBranch)`。

后果：
- 子代理跑完后工作区停留在 `ac/<stepId>` 分支（`MarkCompleted` 只改状态不 checkout）
- 并发子代理链式套娃（A 的 `BaseBranch` = B 切过去的分支）
- 与协调器「同 worktree 同分支可并发」规则直接冲突

这同时解释了 `TryReserve` / `WaitGitWriteAsync` 为何从未接线：新协调器写完后旧路径没被替换。

### D6 的新 git 工具集

| 工具 | 底层 | 需审批 |
|---|---|---|
| `git_status` | `GitService.ResolveContext` / `GetStatusFiles` | 否 |
| `git_add` | `StageFile` / `StageAll` | 是 |
| `git_commit` | `Commit` / `CommitFiles` | 是 |
| `git_create_checkpoint` | `MarkCheckpoint` | 是 |
| `git_diff` | `GetDiff` / `GetDiffStat` | 否 |

被删除：`git_status`(旧) / `git_create_checkpoint`(旧) / `git_merge_step` / `git_drop_step` / `git_revert_step`。

---

## 二、涉及文件清单（D1 + D5 + D6 共 12 个文件，约 900 行）

| 文件 | 改动 | 行数 |
|---|---|---|
| `Core/Services/Git/GitStepService.cs` | 整体删除 | -490 |
| `Core/Services/Git/LegacyGitMigrationService.cs` | 整体删除（死代码 + 违反 AOT 约束） | -528 |
| `Core/Services/Git/GitTypes.cs` | 删 `GitStepStatus` / `GitStepRecord` | -30 |
| `Core/Serialization/AppJsonContext.cs` | 删 `[JsonSerializable(typeof(GitStepRecord))]` | -1 |
| `Core/AppPaths.cs` | 删 `StepsDir` + 其目录创建 | -2 |
| `Core/Services/CommanderRuntime.cs` | 删 `Git` 属性 + `new GitStepService(root)` | -3 |
| `Core/Services/Engine/AgentEngine.cs` | 删 `_git` 字段 / 构造参数 / `Git` 属性 | -5 |
| `Core/Services/Engine/AssignmentManager.cs` | 删 `Git` 依赖 + `BeginStep`/`MarkRunning`/`MarkCompleted` | -20 |
| `Core/Services/Engine/RosterBuilder.cs` | `BuildGitSection` 改用 `GitService`，去掉「待合并步骤」段 | ±10 |
| `Core/Services/Runtime/AgentToolFactory.cs` | 删 5 个旧 git 工具，新增 5 个新工具 | -180/+160 |
| `ViewModels/ChatItemViewModel.cs` | 删回滚按钮 UI + 相关属性 | -60 |
| `Views/ChatPageView.axaml` | 删回滚按钮绑定区块 | -20 |

> `Assignment.StepId` 与 `ToolResult.StepId`：删掉 `BeginStep` 后不再有值。
> 建议**保留字段定义**（序列化兼容 + 结构体稳定），只是不再赋值。

---

## 三、实施批次（按提交顺序）

### 批次 1 — 会话回合排队执行（D3）

`Core/Services/Session/SessionRuntimeRegistry.cs`

- `SessionRuntime` 增加 `Channel<PendingTurn>` + 消费者 Task
- `RunTurnAsync` 改为入队返回 `Task<string>`
- `EndTurn` 增加活动条目身份校验（防御旧回合 finally 清掉新回合状态）
- `ViewModels/ChatPageViewModel.cs`：新增 `IsQueued` / 排队计数，`CanSendMessage` 禁用发送，
  会话面板显示「排队中 (N)」

### 批次 2 — Git 体系重构（D1 + D5 + D6）

见上节 12 文件清单。建议合并为**一个提交**便于 review：
`refactor(git): 子代理统一在当前分支工作, 废弃步骤分支机制`

### 批次 3 — Core 健壮性修复（原第 1 批剩余）

| 项 | 文件 | 要点 |
|---|---|---|
| 事件缓冲队列化 | `ViewModels/ChatPageViewModel.cs` | `ConcurrentQueue` + `Interlocked` 节流，消除引擎线程 ↔ UI 线程共享 `List`/`Dictionary` |
| 取消/超时区分 | `Core/Services/Agents/CliAgentDefinition.cs`、`Core/Services/Engine/AssignmentManager.cs` | 新增 `CliAgentRunResult.Cancelled`；`catch (OperationCanceledException)` 内用 `ct.IsCancellationRequested` 区分来源；`AssignmentManager` 改三分支 |
| MCP 超时 | `Core/Services/Mcp/McpClientBase.cs` | `RequestAsync` 加 `TimeSpan timeout` 参数（`tools/call` 建议 300s），顺手修掉 `Task.Delay(Infinite, ct)` 的 CTS 注册泄漏 |
| 审批/AskUser 超时 | `Core/Services/Engine/AgentEngine.cs` | `tcs.Task.WaitAsync(ct, timeout)`，超时走现有「用户拒绝」分支 |
| 原子写 | 12 处 + `ChatService.ReadMetadata` | 见下表 |
| 压缩服务 | `Core/Services/Engine/SubagentCompactService.cs` | `catch (OperationCanceledException) { throw; }` 放在 `catch (Exception)` 之前；改用当前回合 provider/model；`TargetLength` 要么用要么删 |
| i18n | `Resources/Strings.en.resx` | 补 `Settings_AgentArgs` / `Settings_AgentArgsPlaceholder`（英文界面现显示字面 key）；对齐 `SubAgent_AddHint` 中英语义 |

#### 原子写待迁移清单

`AtomicFile`（`Core/Serialization/AtomicFile.cs`，190 行）已由提交 `60273cd` 建立，
已应用于 `ChatService` + `UsageStatsService`。剩余：

| 优先级 | 文件 | 行 |
|---|---|---|
| 高 | `Core/Services/Git/GitStepService.cs` | 487（**本批次会删掉该文件，跳过**） |
| 高 | `Core/Services/Engine/RosterConfigService.cs` | 41 |
| 高 | `Core/Services/Agents/AgentConfigService.cs` | 144 |
| 高 | `Core/Services/Llm/ProviderConfig.cs` | 134 |
| 中 | `Core/Services/Engine/AssignmentManager.cs` | 200 |
| 中 | `Core/Services/Templates/AgentTemplateService.cs` | 194 |
| 中 | `Core/Services/Personas/PersonaService.cs` | 148 |
| 中 | `Core/Services/Mcp/McpServerConfig.cs` | 85 |
| 中 | `Core/Services/ThemeService.cs` | 126 |
| 低 | `Core/Services/Usage/ModelProfileService.cs` | 373 |
| 低 | `Core/Services/Engine/RosterBuilder.cs` | 176 |
| 低 | `Core/Services/DefaultConfig.cs` | 72 |

另：`ChatService.ReadMetadata`（`:445`）仍裸 `File.ReadAllText`，主文件损坏但 `.bak` 完好时
会话会从列表消失 —— 改用 `AtomicFile.TryReadText`。

已验证 `.bak` 不会被 `Directory.GetFiles(dir, "*.json")` 误匹配（实测确认）。

### 批次 4 — 删除 `max_concurrent`（D2）

| 文件 | 改动 |
|---|---|
| `Core/Services/Agents/CliAgentDefinition.cs:30` | 删字段 |
| `Core/Services/Engine/RosterBuilder.cs:110,133` | 去掉 `, 并发: N` |
| `ViewModels/AgentPanelViewModel.cs:352,501` | 删赋值 |
| `ViewModels/SettingsPageViewModel.cs:951` | 删赋值 |
| `templates/config/agents.toml` | 删字段 + 加注释 |

`CliAgentRunner` 中无任何按此字段的信号量 —— 删掉零行为变化。

### 批次 5 — doctor 自检 + 废弃产物提示（D7 + 第 4 批）

`Program.cs` `RunDoctor` + `Core/Services/Git/GitService.cs`

- 接入 `WorkspaceExecutionCoordinator.SelfCheck()`（现有但从未被调用）
- resx 中英文 key 一致性检查（防 i18n 回归）
- 扫描并提示清理：`ac/*` 分支、`steps/*.json`、`ai-shikikan/checkpoint/*` tag

---

## 四、与未推送提交的关系

分支 `vibe` 领先 `origin/vibe` 8 个提交，其中 3 个与本方案有交集：

| 提交 | 交集处理 |
|---|---|
| `60273cd` 原子写入修复会话与用量 | **已前置完成** AtomicFile 基础设施 + 2 个服务，剩余 12 处待迁移 |
| `c6cebd1` git 命令加超时与禁交互 | **部分前置**了第 2 批 #8/#12（永久冻结已解决，UI 线程同步阻塞仍在——第 2 批已跳过） |
| `793cd05` 恢复 OpenAI 流式用量 | 为第 3 批的 429/5xx 重试提供了 `PostAsync` / `IsUnknownParamError` 参考实现 |

⚠️ `ac87b19` 修正文档失实描述时，遗漏了 `AGENTS.md:79` 的
「回滚 / Fork 确认期用 reservation 挡住该 worktree 的新回合」——
`TryReserve` 全仓库零调用方。本方案 D7 会补上清理提示，但 reservation 本身仍需后续补实现或改文档。

---

## 五、后续批次（未纳入本轮）

### 第 3 批剩余（健壮性）

| 项 | 位置 |
|---|---|
| MCP 三客户端全部无重连，`IsConnected` 谎报存活 | `McpService.cs:104` |
| OpenAI SSE 不支持跨行 `data:`，静默丢 chunk；中途断流仍报 `Done` | `OpenAiChatCompletionsClient.cs:221-238` |
| Anthropic 断流时既无 Done 也无 Error，已收文本被丢弃 | `AnthropicChatCompletionsClient.cs:136-151` |
| LLM 层无 429/5xx 重试退避，无首 token 超时 | 全 `Llm/` |
| 每 agent 一个 `run_<id>` 工具无上限，挤占上下文 | `AgentToolFactory.cs:48-51` |
| `ResolveAgent` 两份实现语义分叉 | `AgentToolFactory.cs:387` vs `:539` |

### 第 4 批剩余（工程治理）

| 项 | 说明 |
|---|---|
| 零测试 + CI 无 push/PR 触发 | 21k 行核心逻辑零回归保护 |
| 浮动版本 `12.*` / `3.*` + 无 lock 文件 | 构建不可复现；`Tomlyn` 停在 0.19.0（最新 2.10.1） |
| `SuppressAotAnalysisWarnings` + `SuppressTrimAnalysisWarnings` | AOT 兼容实际无人验证 |
| 无 `.editorconfig` | 无风格门禁 |
| `ChatPageViewModel.cs` 1722 行 / `ChatPageView.axaml` 875 行 | God-ViewModel |
| 重复代码 | `Truncate` 5 份 / `EnsureUniqueId` 2 份 / `Get(args,name)` 4 份 / 路径规范化 4 套 |

### 第 2 批（UI 冻结，用户已明确跳过）

发消息前 UI 线程同步跑 ~6 个 git 进程；流式文本无时间节流导致每 token 全量 Markdown 重解析；
图片附件 PNG 编码在 UI 线程（单张可达 1 秒）；背景图取色全量像素搬运（4K 图 ~100MB 分配）。

---

## 六、验证清单

### 已通过（编译期 + 静态核查）

| # | 项 | 结果 |
|---|---|---|
| 1 | `dotnet build AIShikikan.Gui.csproj` | 0 错误（1 个改动前既存警告：`ImageAttachmentService` 过时 API） |
| 2 | `doctor` 全项 | 7 项全 ✔，含新接入的「并发协调规则 6 组场景全部通过」 |
| 3 | D1 子代理不再切分支 | `AssignmentManager` 中 `BeginStep`/`MarkRunning`/`MarkCompleted` 残留 **0** |
| 4 | D5 工具卡回滚按钮删除 | `ChatItemViewModel` 中 `CanRollback`/`IsRolledBack`/`RollbackNote` 残留 **0** |
| 5 | D6 GitStepService 体系清除 | 全项目 `GitStepService` 引用残留 **0** |
| 6 | D2 max_concurrent 删除 | 全项目（含两个 toml）残留 **0** |
| 7 | 原子写全覆盖 | 全项目裸 `File.WriteAllText` 残留 **0**（17 处全部走 `AtomicFile`） |
| 8 | i18n 中英 key 对齐 | `Strings.resx` 324 / `Strings.en.resx` 324，`diff` 干净 |
| 9 | `--version` | `0.9.0-vibe` 正常输出 |

### 待人工验证（需真实 GUI 会话）

1. 同会话连发两条消息 → 第二条排队，第一个完成后自动开始
2. `run_subagents` 3+ 子代理 → 工具卡输出行完整无交错、无 `InvalidOperationException`
3. 子代理执行中点停止 → 卡片显示「已取消」，不再误报「超时」
4. 跑一个 `run_<agent>` → **工作区分支不变**（D1 核心验收点）
5. 配一个必定挂起的 MCP 服务器 → 回合在超时后返回错误，不卡死
6. 杀掉进程模拟断电 → 重启后配置从 `.bak` 恢复
7. 英文界面 → 设置页 Agent 参数 Placeholder 显示正常英文

### 已知遗留（未在本轮处理）

- `IsQueued` / `QueuedTurnCount` 无 XAML 绑定，缺「排队中 (N)」的可视化（功能已生效，仅未展示）
- `WorkspaceExecutionCoordinator.TryReserve` / git 写门仍是死代码，`AGENTS.md:79` 关于它的描述仍失实
- `AgentPanelViewModel` / `SettingsPageViewModel` 中 3 个 `Core/DefaultConfig/agents.toml` 的旧 `ac/*` 提示未清理
- `Tools/ToolCard_Rollback*` 4 个 resx 键已成孤儿（本轮未删，避免与 i18x 改动冲突）
