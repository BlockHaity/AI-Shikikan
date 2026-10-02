# Changelog

本文件记录 AI-Shikikan 的版本变更。
格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [1.0.0-vibe] - 2026-10-02

本版本主线是把「Git 检查点」从概念变成唯一回滚入口：每条用户消息自动落一个
HEAD commit + `ai-shikikan/checkpoint/<id>` tag，卡片上提供 Reset / Revert / Fork；
与之配套，`ac/<stepId>` 步骤分支机制被整体废弃，子代理统一在当前分支同目录工作。

另一条主线是持久化与并发语义：新增 `AtomicFile` 并覆盖全部配置/状态写入点，把
「文件损坏被下一次任意写放大为永久数据丢失」降级为可自动回退；会话改为一会话一引擎、
同会话回合串行入队，并补齐 MCP、子代理压缩、子代理终止的超时与取消传播。

同时修掉了一批长期静默失效的路径（Anthropic 工具调用恒为空参数、跨会话流式消息被截断落盘、
Roster 空列表回退导致 AI 持续调用已注销的 `run_<id>`），以及两个发布包级别的致命问题
（dotnet 变体系统包缺 DLL、单文件变体丢英文语言包）。会话列表改为常按工作目录分组，
模型配置改为「检出后挑选或手动添加」。

### 破坏性变更

- **git** 子代理统一在当前分支工作，废弃 `ac/<stepId>` 步骤分支机制（`9225ac2`）
  - 删除 `GitStepService`（490 行：ac/ 分支的创建、合并、丢弃、回滚全套机制）与 `LegacyGitMigrationService`（528 行，零引用，且用反射式 `JsonSerializerOptions` 违反 AOT 约束）
  - 删除工具卡上的「回滚」按钮（其实现依赖「切分支 + 删分支」）与 `git_merge_step` / `git_drop_step` / `git_revert_step` 三个 AI 工具
  - 新增 5 个基于 `GitService` 的工具（写操作需批准，与 `run_subagents` 审批策略对齐）：`git_status`(免批) / `git_add` / `git_commit` / `git_create_checkpoint` / `git_diff`(免批)
  - 根因是文档与实现的长期背离：`AGENTS.md` 与 `README.md` 早已声明「子 Agent 不再自动 `git switch -c`」，但 `AssignmentManager` 仍在调 `GitStepService.BeginStep` 且它真的执行 `Run("switch","-c",...)`。后果有三：子代理跑完后工作区停留在 `ac/<stepId>` 分支（`MarkCompleted` 只改状态不 checkout）；并发子代理链式套娃（A 切走后 B 的 `BaseBranch` 就是 A 的临时分支）；与协调器「同 worktree 同分支可并发」规则直接冲突——这也解释了新的 `TryReserve` / git 写门写完之后为何从未接线
  - **迁移方式**：回滚入口从「工具卡 + 检查点」收敛为「仅检查点卡片」（Reset / Revert / Fork），回滚能力不变——每条用户消息仍自动创建检查点。自定义 `agents.toml` 中的 `max_concurrent` 字段已删除（零行为变化，见重构）。历史遗留的 `ac/*` 分支与 `steps/*.json` **不会自动删除**，`doctor` 会扫描并提示手动清理（检查点 tag 是当前回滚依据，同样只提示）

- **core / mcp** MCP 请求与子代理输出压缩补齐超时与取消语义（`b560bd2`）
  - API 变更：`RequestAsync` 增加 `timeout` 参数（`tools/call` 300s、`initialize`/`tools-list` 30s）；`ToolContext` 增加 `ProviderId` / `Model` 两个字段；`CompactIfNeededAsync` 增加可选 `providerId` / `model` 并不再吞掉取消
  - 迁移方式：新增参数均有默认值，`ToolContext` 只加字段，`CompactIfNeededAsync` 不传即回退全局默认，现有调用方无需改动；若自建 MCP 客户端需自行传 `timeout`
  - 修复的问题：`tools/call` 此前完全没有超时（只有 `initialize` 加了 30s），一个卡死的 MCP 服务器会让 `run_subagents` 的 `Task.WhenAll` 永不返回、整个回合卡死；每次请求创建 `Task.Delay(Timeout.Infinite, ct)` 造成 `CancellationTokenSource` 回调注册泄漏；超时清理与迟到响应竞态抛 `InvalidOperationException`（`DispatchMessage` 改用 `TrySet*`）。子代理压缩侧，此前 `catch(Exception)` 吞掉 `OperationCanceledException`，用户停止后还要白等一次完整 LLM 往返，且恒走全局默认 provider/model

### 新增

- **core** Git 检查点核心数据模型、枚举与源生成注册（`0be97dc`）
  - 新增 `GitCheckpointSource` / `CheckpointRollbackMode` 枚举与 `GitCheckpointRecord` / `GitWorkspaceContext` / `CheckpointDetail` 模型
  - `ChatSession` 增加 `RepositoryRoot` / `BranchName`；`ChatMessage` 与 `ToolSegment` 增加 `CheckpointId`（`StepId` 保留为只读兼容）；`AppPaths` 增加 `CheckpointsDir`；全部注册进 `AppJsonContext` 以维持 AOT 源生成

- **git** CheckpointStore 与显式上下文的 GitService 基础命令（`beacdd1`）
  - `GitService` 一律传显式 `GitWorkspaceContext`，禁止全局可变 `RepositoryRoot`；写操作按仓库根 `SemaphoreSlim` 串行
  - 覆盖四类命令：基础（Stage/Unstage/Commit、EnsureInitialCommit、CommitFiles）、检查点（`MarkCheckpoint` 轻量 tag + 持久化、`GetDiff`）、回滚（Fork、`ResetHardToCheckpoint`、`RevertToCheckpoint` 含失败恢复）、查询（本地分支、提交图、状态文件）
  - 新增 `GitTypes.cs` 共享类型定义；`GitCheckpointStore` 按仓库分目录原子 JSON 存储，支持按仓库/会话/ID 查询

- **chat** ChatService 支持检查点关联与会话 Fork/截断（`c1aadfc`）
  - 新增 `SetMessageCheckpoint` / `SetToolSegmentCheckpoint` / `GetMessageCheckpoints` / `SetSessionRepositoryInfo`
  - 新增 `ForkSession`（基于对话截断点复制会话，继承 `WorkDir` / `RepositoryRoot` / `BranchName`）与 `TruncateMessages`（供继续输出与回滚后续写）
  - `SaveSession` 改为返回 `bool`，`AddMessage` 保存失败时回滚内存并抛出，发送流程可感知失败

- **core** WorkspaceExecutionCoordinator 工作区执行协调器（`ed66e55`）
  - 以规范化 `WorkTreeRoot`+`Branch` 为键：同键多会话并发、同 worktree 跨分支拒绝、不同 worktree 允许
  - 提供按 worktree 串行的 Git 写临界区（`WaitGitWriteAsync` / `TryEnterGitWrite`）、回滚/Fork 确认期的操作 reservation、活动回合与活动 Assignment 跟踪
  - 抽象出 `IWorkspaceResolver` + `GitWorkspaceResolver`（走 git CLI，AOT 安全）；`SelfCheck` 覆盖 9 组并发场景

- **core** SessionRuntimeRegistry：每会话独立引擎与事件归属（`789c7b2`）
  - 每个 `SessionId` 持独立 `AgentEngine`（对话 / `EngineOptions` / Roster 快照）与独立取消入口；注册表管理活动会话，GUI 仍可通过 `CommanderRuntime.Engine` 指向活动会话引擎（转发属性）
  - `AgentEngineEvent` 增加 `Scope`（SessionId/TurnId/SessionTitle/Branch，旧 record 构造保持兼容），经共享 `EngineEventHub` 过滤投递：审批与 `ask_user` 强制定向送达，流式增量只投活动会话
  - 回合开始向协调器申请执行权并在 `finally` 释放，会话级 CTS 与外部停止合并；后台会话用量由注册表按会话独立落盘（源生成序列化，AOT 安全）

- **gui** 增加工作目录与 Git 发送前置状态（`cf6f637`）

- **gui** 渲染持久化 Git 检查点卡片（`33ab996`）

- **gui** 增加检查点回滚双层确认流程（`75dfb49`）

- **gui** 实现检查点 Fork 对话与会话截断（`f3dcd87`）

- **gui** 接入检查点面板与同分支会话并发状态（`163b92c`）

- **core** 旧步骤无感迁移服务（`b3688d4`、`74d727b`）— ⚠️ **该服务随后被 `9225ac2` 移除**，本条仅记录版本内历史
  - 扫描旧 `steps/*.json`（优先 `MergeCommit` 可解析，其次 `StepBranch` 取 tip），只对当前仓库可验证记录迁移、不猜 `BaseBranch`
  - 创建新 Checkpoint 并建立 `oldStepId → newCheckpointId` 映射，用 `JsonNode` 递归改写 `assignments/` 与 `sessions/` JSON 中的旧 stepId
  - 持久化迁移状态文件防重复，部分成功不删除旧源文件；无法迁移时返回结构化汇总，不在 Core 阻塞启动
  - `74d727b` 修正 `JsonKnownNamingPolicy → JsonNamingPolicy` 类型错误与 `ReplaceStepIds` 的 CS8604 空引用警告

- **settings** 模型配置改为检出后挑选或手动添加（`fdfd72f`）
  - **移除**自动检测思考等级（不只是隐藏按钮）：删除 `DetectThinkingLevels` 命令与按钮、`FetchModels` 里的自动探测循环（关键——原来每次点「获取模型列表」都会把 `DetectMaxLevel` 的猜测结果写进 `ModelMaxThinking`，用户没主动操作也被钉死档位）、以及基于模型名的字符串启发式 `DetectMaxLevel`（含 o1 的普通模型名如 `qwen-o1xx` 会被误判成推理模型）
  - 现在思考档位只有两条路：显式配 `model_max_thinking`，或留空走 Auto（交回模型自身决定）
  - 模型列表拉取只填候选池 `DetectedModels`（不再 `EnabledModels = models.ToList()` 把含嵌入模型、重排版、过期型号的全量灌进配置），配下拉框 +「添加所选」+「全部添加」，另加手动输入模型 id 路径以适配私有部署与反代网关
  - 补上删除能力（此前已配置模型只能禁用不能删除）：每行加删除按钮，连带清掉该模型的思考档位与上下文窗口，避免留下永远读不到的死条目

- **gui** 会话列表常按工作目录分组并修若干界面缺陷（`2a762c3`）
  - 分组功能早已实现但默认关闭、藏在图标开关后面；删掉 `GroupByWorkDir` / `BuildFlat` / `ToggleButton`，`Reload` 无条件 `BuildGrouped`（分组行为本身未变）
  - i18n：清理 10 个死机制孤儿键（工具卡回滚按钮、`ac/*` 步骤列表、`git merge/drop/revert`——那些 UI 早已不存在），新增 12 个（7 个思考档位映射 + 5 个模型添加）；思考档位文案此前是 Core 层硬编码中文、英文界面下菜单显示中文，改为 Core 返回稳定标识（off/auto/low/...）由 GUI 侧映射
  - 修 AgentPanel 分配按钮永久禁用：`CanAssign` 依赖的 `SelectedAgent` / `AssignTaskText` / `IsAssigning` 都没有 `NotifyCanExecuteChanged`，而 Avalonia Button 只在 Command 赋值或 `CanExecuteChanged` 时重算，`SelectedAgent` 为 null 时首次求值为 false 后按钮就再也起不来
  - 修首页中文日期格式：`{date:M月d日}` 里的「月」「日」在 .NET 自定义格式串中是普通字面量，英文界面原样输出 `"Oct月1日"`
  - 修 WaterfallPanel 行间距判定用「全局已放元素数」，使第 2..n 列的首个元素也带上 RowSpacing、累计高度凭空多一截
  - GitPanel / StatusPanel 加防抖：`AppShell.DataChanged` 有 3 个订阅者，每次 reload 触发 3 次刷新，而 `GitPanel.Refresh` 每次要起 6 个 git 子进程
  - `ChatPageView` 的 icon 资源被 `AgentPanelView` 跨视图 `StaticResource` 引用，靠逻辑树上溯碰巧解析，补上本地定义使其自洽

### 修复

- **gui** 完善检查点卡片与对话框视觉细节（`61becd1`）

- **gui** 同步会话切换后的上下文与面板状态（`a46dd77`）

- **core** Doctor 非 Git 仓库降级为警告而非失败（`a9b0ee2`）
  - Git 不可用时显示 ⚠ 而非 ✘，提示「聊天可用，检查点/Git 写工具不可用」；非 Git 不影响 doctor 退出码，仅实际失败项导致返回 1

- **core** 修正 LegacyGitMigrationService 的类型错误与空引用警告（`74d727b`）— 该服务随后被 `9225ac2` 移除

- **gui** 运行中禁止跨工作目录切换会话（`1319518`）

- **core** 接入显式 Git 检查点服务（`7e44e46`）

- **gui** 更新 Avalonia 占位文本属性（`5760cb4`）

- **gui** 修复删除子 Agent 时右侧栏回收崩溃（`efa20e7`）

- **core** 原子写入修复会话与用量数据的静默清零（`60273cd`）
  - 新增 `AtomicFile`（写 `.tmp` → `Flush(flushToDisk: true)` 强制刷盘 → `Move(overwrite: true)`，覆盖前留 `.bak`，读取失败自动回退）
  - `ChatService`：消息加载失败标记 `IsCorrupted` 并锁定写回，不再把空消息列表写回原文件；删除会话时连带清理 `.tmp` / `.bak`
  - `UsageStatsService`：补 load 失败日志（此前完全静默），损坏时进入只读保护、`Save` 拒绝覆盖

- **git** 为 git 命令加超时与禁交互提示，修复 UI 永久冻结（`c6cebd1`）
  - 所有 git 调用点都可能在 UI 线程且原实现用无超时的同步 `WaitForExit()`；`git pull`/`push` 需要凭据时会等待终端输入，导致 Avalonia 界面完全无响应
  - 设 `GIT_TERMINAL_PROMPT=0` / `GCM_INTERACTIVE=never`（需要凭据时直接失败）；改为 `WaitForExitAsync` + 超时，超时后 `Kill(entireProcessTree)`；重定向 stdin 并异步读干双管道，避免缓冲区写满死锁
  - `pull`/`push` 用 60s 网络超时，其余 15s
  - 修 `GetCommitGraph` 把 `"-n 60"` 当单个 argv（依赖 git 未文档化的短选项解析）改为 `"-n" "60"`；`--all` 改为仅当前分支，避免检查点 tag 膨胀拖慢图谱
  - 修 `RevertToCheckpoint` 失败恢复用 `reset --hard HEAD@{1}`：`revert --no-commit` 不移动 HEAD，`HEAD@{1}` 是上一个提交，会丢掉用户最新提交；改为 `revert --quit` + `reset --hard HEAD`

- **gui** 修复会话切换引擎错位与 Fork 崩溃（`4e06a1e`）
  - 切会话时必须先 `AgentPanel.SetSession` 再 `RebuildConversation`：`_runtime.Engine` 是 `Sessions.ActiveEngine` 的转发属性，而 `SetSession` 内部才调用 `SetActiveSession`，顺序颠倒会把新会话历史写进上一个会话的引擎；Fork 复制新会话路径存在同样问题，一并修正
  - `TruncateCurrentSessionAtCheckpoint` 显式处理 `cutoff < 0`（检查点创建时对话为空会使 `ConversationCutoff = MessageCount-1 = -1`），旧代码把它当非法索引传给 `TruncateMessages` 返回 false 并抛异常
  - Fork 会话操作失败不再让异常逃逸（此时 git 分支已切换、状态已不一致），改为记录日志 + 提示 + 同步刷新界面

- **llm** 恢复 OpenAI 流式用量并修正模型档案 URL 双重 `/v1`（`793cd05`）
  - OpenAI 流式请求补 `stream_options.include_usage`：官方端点在 `stream:true` 下必须显式请求才返回 usage，缺失导致用量统计 / 上下文环 / 自动压缩 / 成本估算全部失效
  - 对严格端点做降级：仅当 400/422 且错误体明确指向未知参数时才去掉该字段并重试一次缓存结果；401/403/429/5xx 绝不降级，保持原错误呈现
  - `ModelProfileService` 硬拼 `base_url + "/v1/models"` 而 `base_url` 已含 `/v1`，必然 404；抽出 `BuildModelsUrl` / `IsAzureEndpoint` helper 覆盖含/不含 `/v1`、版本段结尾、本地端口、Azure 部署端点等形态
  - 档案拉取失败原因存入 `ModelProfileCache.Error` 并在状态栏展示，不再被空 catch 吞掉；失败时保留同一 Provider 的旧档案

- **i18n** 补齐英文资源缺失键并修正语义漂移（`926077c`）
  - `Settings_AgentArgsPlaceholder` 在中文 resx 有、英文 resx 缺，而 `Strings.Get` 在找不到时静默回退为 key 本身——英文界面设置页两个 TextBox 的 Placeholder 直接显示字面量 `"Settings_AgentArgsPlaceholder"`；补齐后中英各 324 键，diff 干净
  - `SubAgent_AddHint` 中英语义完全不同：中文说「创建后将在左侧聊天栏可见」（但该面板实际在右侧），英文是内置 Agent 列表（该信息在 UI 上别处没有出现），两者都含有效信息，合并为一条正确文案
  - 新增 `Strings.FindMissingEnglishKeys()`（仅 Debug）供 doctor 自检；并提供 `EnglishResourceSetLoaded` 以区分「英文资源没打包」与「真的没缺失」（`GetResourceSet` 在卫星程序集缺失时会静默回退到中文并报告「零缺失」）

- **agents** 区分子代理的「用户取消」与「超时」（`e20e6d4`）
  - `CliAgentRunner` 用 linked token 同时承接外部取消（用户点停止）与 `CancelAfter(TimeoutMinutes)`，两者进同一个 `catch(OperationCanceledException)`，一律返回 `TimedOut=true` 和「[超时]」文案
  - 后果是用户主动点停止会被误报为超时，且 `AssignmentStatus.Cancelled` 从未被写入过——该状态在数据模型里存在但不可达；新增 `CliAgentRunResult.Cancelled` 区分两者，`Succeeded` 补上 `!Cancelled`

- **core** 同会话回合改为 Core 层串行队列（`7d9ac1d`）
  - `SessionRuntime.TryBeginTurn` 只向工作区协调器申请执行权，不检查本会话是否已有活动回合；而协调器规则是「同 worktree 同分支允许多会话并发」，于是同一会话连发两条消息也会通过
  - 后果：`AgentEngine._conversation`（普通 `List`）并发 Add、`_currentTurnId` 互相覆盖导致事件归属错乱、`EndTurn` 的 `finally` 提前清空活动条目
  - 改为每个 `SessionRuntime` 持一个单消费者 `Channel`，回合请求入队后串行执行；`EndTurn` 增加活动条目身份校验（旧回合的 `finally` 晚于新回合时不再覆盖新状态）；`Dispose` 时关闭 writer 并排空未开始的回合，否则调用方的 `await` 会永久挂起

- **gui** 接线回合排队队列并消除事件缓冲竞态（`0f5c435`）
  - 回合改调 `SessionRuntime.EnqueueTurnAsync` 而非 `Engine.RunTurnAsync`，有排队时禁用发送；关键细节是必须用局部 `engine` 变量而非 `_runtime.Engine`（后者是转发属性，订阅事件与设置 Options 若落在转发属性上，切会话时会订阅/退订错对象）；排队计数用 500ms DispatcherTimer 轮询，仅在确有排队时运行
  - 事件缓冲竞态（本轮最隐蔽的问题）：`OnEngineEvent` 在引擎线程写 `entries` / `toolOutputs`（`List` / `Dictionary`），`FlushUi` 在 UI 线程读它们；且 `run_subagents` 的 `Task.WhenAll` 下每个子代理的 `Progress<string>` 在无 SyncContext 的线程池线程回调，导致 `OnEngineEvent` 被多线程并发进入。现改为引擎线程只入队（`ConcurrentQueue` + `Interlocked` 节流），所有集合只在 UI 线程访问，不再需要任何锁
  - 修正消息增量判定的一处 desync：A 会话后台流式时切到 B 会话发消息，B 的用户气泡会被「任一会话在跑」的早退吞掉，导致下一条消息必然走全量重建（`ItemsSource` 换实例导致 ListBox 容器全部销毁重建）

- **serialization** 补齐原子写覆盖并加进程内路径锁（`c19cb9e`）
  - `ChatService.ReadMetadata`（会话列表主加载路径）走 `TryReadText` + validate，不再因主文件半截而让会话从列表静默消失；`AgentTemplateService` / `PersonaService` 的空 catch 补日志（原先零痕迹）；`ModelProfileService.LoadManualConfig` 改走 `AtomicFile`
  - validate 判据从「反序列化非 null」加强为「带非空 id」：合法 JSON 但结构不对（顶层是数组/字符串）同样会反序列化成非 null 的空对象，原判据放行后 `full?.Messages ?? []` 会用空列表覆盖用户原始对话
  - `AtomicFile` 增加进程内按路径的锁：原先 `FileShare.None` 只让并发者抛 `IOException`（谁后写谁赢，先写的内容直接丢），现在同路径写串行；读侧也取锁——写入中途会先 `File.Move` 主文件到 `.bak`，此刻读主文件会看到「文件不存在」而误判为需回退

- **llm** 修 Anthropic 工具调用空参数与并行调用角色交替（`6593c5e`）
  - 最严重的一处：Anthropic 路径下工具调用一直是坏的，且静默。反射探针实测 SDK 5.10 的 `StreamMessage.ToolCalls` 恒为空、`ContentBlock` 只在 `content_block_start` 出现一次、`Message` 属性在流里直接抛异常——原代码从 `f.Arguments` 取参数是死代码，每个工具调用都以 `{}` 空参数下发。改用 `Delta.PartialJson` 累积；`tool_calls` 的键也从 `ContentBlock.Id` 换成内部序号（Id 为空时全部挤在 `""` 一个键上互相覆盖，并行调用只发得出一个）
  - 并行工具调用违反角色交替：每个 tool 结果都做成独立 User 消息，而 Anthropic 要求所有 `tool_result` 合并在同一条 user 消息内；改为累积后合并，循环结束再补一次 flush（对话在工具回合被取消/截断时末尾挂着的 tool 结果不能丢）
  - 并发安全：`_clients` 是无锁 `Dictionary`，而 `run_subagents` 的 `Task.WhenAll` 会让多个子代理并发调 `GetClient`；改 `ConcurrentDictionary` 并加锁（只加锁不够，Settings setter 的 `Clear` 与「写回缓存」之间无顺序保证，会把按旧配置构造的 client 写回）；缓存键补上 Kind/BaseUrl（设置页是就地改 `ProviderConfig` 实例，`_settings` 对象身份不变，`Clear` 根本收不到，改配置只能重启才生效）
  - 思考档位兜底从 Max 改为 Auto：用户没配 `model_max_thinking` 时每个模型都命中兜底，Auto 会让推理模型一律下发 `reasoning_effort=high` 并丢弃 temperature——没配任何东西就付成倍 token 成本

- **mcp** 消除双锁写入并发并修协议层若干缺陷（`742de07`）
  - 最高风险一处：`McpService` 写 `_clients`/`_toolsByServer` 在 `_connectLock` 下、读在 `lock(_clients)` 下，两把不同的锁护同一批可变 `Dictionary`，并发读写可致内部结构损坏；统一为 `ConcurrentDictionary`。另 `EnumerateTools` 是 `yield` 迭代器，`lock` 被编译器放进 `MoveNext` 的 try/finally，锁会跨越两次 `MoveNext` 持有整个消费时长，改为返回 `IReadOnlyList` 快照
  - **SSRF**：SSE `endpoint` 事件无来源校验，恶意服务器可把客户端 POST 引到 169.254.169.254 等任意地址；加同源校验（端口刻意放宽：容器端口映射与反向代理会给出不同端口，误杀合法 endpoint 等于让 SSE 服务器不可用）
  - HTTP/SSE 传输原先完全不支持认证，只能连无鉴权公网端点；新增 `headers` 与 `token` 配置项，统一由 `ResolveHttpHeaders` 供传输层取值
  - 响应 id 只接受 JSON 数字，服务器回显字符串 id 时消息被丢弃 → 挂到 300s 超时；`nextCursor` 返回空串（非 null）时分页永不退出，每次迭代 30s 超时 → 无限循环
  - server→client 请求（sampling/roots/elicotiation）原先静默丢弃，依赖 roots 的服务器会永久挂起，现记 Warn 并注明服务器侧将超时；按「有无 method」区分「迟到响应」与「请求」，不误报超时
  - `McpService` 增加 60s 后台探活，修复进程崩溃后 UI 仍显示已连接；探活刻意放在后台（`IsConnected` 在 UI 线程被高频轮询，同步探活会卡界面）

- **git** async 化并把回滚前置校验移入临界区（`927ea0b`）
  - **数据丢失路径**：Fork/Reset/Revert 的 `IsClean` 与 `merge-base --is-ancestor` 校验原先都在 `sem.Wait()` 之外，校验通过到进临界区之间工作区可被并发子代理改动——`reset --hard` 会抹掉「校验之后才出现」的改动，`git switch -c` 会把未预期变更带进新分支；三处的校验全部移入 `WithRepoLockAsync` 内
  - git 写操作并未真正串行：`StageFile`/`StageAll`/`Commit`/`MarkCheckpoint` 不取仓库锁，与取锁的 `CommitFiles`/Fork/Reset/Revert 可交叉，统一纳入
  - `MarkCheckpoint` 在 tag 已存在时原先先 `git tag -d` 再重建，即无条件覆盖；检查点 tag 是 Reset/Revert/Fork 的唯一回滚依据，改为直接拒绝
  - `git diff` 的 from/to 拼成单个 argv 且无校验，`from = "--output=/path"` 会被 git 当选项解析、把 diff 写进仓库外任意文件；加白名单校验
  - 新增全量 async API，同步方法降为 wrapper（调用方签名零变更、可分批迁移），顺带修掉 `WithRepoLockAsync` 的 `Wait` 第二种冻结；`WorkspaceResolver.GitOutput` 读 stdout 与 `WaitForExit` 顺序反了，stderr 写满管道时会互锁到超时、合法结果被降级为 null（当作非 git 目录）
  - 检查点存储：仓库目录从 32-bit FNV 哈希改 SHA-256（生日碰撞会让 `GetAll(repoA)` 混入 repoB，`Delete` 可能删掉别的仓库的文件）；加 `MaxRecordsPerRepo=500` 淘汰并在淘汰时回收 tag（回滚只用 `CommitSha` 不用 `TagName`，删 tag 不削弱回滚能力，反而让旧 commit 可被 git gc 回收）；`LoadAll` 刻意不按目录名过滤，保证旧 FNV 目录的记录照常载入
  - `WorkspaceExecutionCoordinator`：git 写门按引用计数淘汰（原先永不回收）；`git rev-parse` 结果加 2s TTL 缓存，避免每回合起 2 个 git 进程；`SelfCheck` 补第 10 组场景

- **engine** 修 tool_calls 配对截断并明确子代理同分支同目录工作（`f119c61`）
  - 请求截断会破坏工具调用配对：`TakeLast(MaxHistoryMessages)` 的切点若落在 `assistant(tool_calls)` 与其 tool 结果之间，会发出孤立 tool 消息（OpenAI 400）或孤立 `tool_use`（Anthropic 400）；讽刺的是同文件的自动压缩路径专门写了边界回退循环处理这问题，请求路径却直接 `TakeLast`。抽出 `FindToolSafeCutoff` 供两处共用，顺带修掉摘要插 User 角色导致的连续 user 消息
  - Roster 判据歧义（P0）：`rosterEntries is { Count: > 0 }` 把「无限制」和「用户清空」混为一谈——关闭右侧栏传空列表会回退到 `BuildAgentsSection` 列出全部 `run_<id>`，而工具此时已从 `ToolRegistry` 注销，AI 持续调用不存在的工具；新建会话（传 null）同样命中，用户在 AgentPanel 关掉的子代理仍被广告。改为 null 唯一表示无限制
  - `AssignmentManager` 遇异常时状态永久卡在 Running、不发终态事件 → `RecordAgentCall` 不触发、Agent 面板一直转圈、还被持久化成 Running；改用 try/catch 收敛到终态，`Get` 未加锁也补上（`Task.WhenAll` 下并发 Create 与 UI 侧裸读竞争）
  - 子代理输出压缩不跟随当前回合 provider/model：`ToolContext` 已在引擎侧正确注入，但 `CompactIfNeededAsync` 调用点漏传，压缩恒走 `ActiveProvider`；同时把 `llm!` 空 forgiving 改为显式判空（null 时 NRE 发生在 try 内会被 catch 吞成「执行失败」，把一次成功的子代理调用报成失败）
  - 子代理工作目录空时回退到进程 CWD 而非 `ctx.WorkspaceRoot`，子代理会在工作区外运行；改在 `AgentExecutor` 侧兜底
  - `assign_task` 工具描述称「返回 assignmentId」与实现不符，会直接误导 LLM；阻塞式调用返回时子代理已跑完，ID 没有任何查询入口——真正的问题是描述承诺了一个不存在的异步语义，故改描述而非暴露 ID
  - `GlobTool` 的 `Regex.IsMatch` 未设 `matchTimeout`，glob 模式完全由 LLM 控制，可构造回溯型正则卡死引擎线程（ReDoS）；加 15s 整场预算（单文件 500ms × 10 万文件仍是十几小时）。`ToolPathSanitizer` 改为逐段解析符号链接（软链目录指向 `/etc` 是最常见的一类）

- **chat** 修跨会话流式截断并加时间节流（`2c5865d`）
  - **数据损坏**：`engine.OnEvent` 在引擎带 Hub 时实际订阅的是全局 `EngineEventHub`，而 Hub 按「活动会话」过滤；流式中切会话 → `pendingEvents` 立刻停止收到旧会话增量 → Segments 冻结 → 持久化出被截断的助手消息，反向则完全无输出、分段结构与工具卡全丢。改订引擎的本地事件出口（绑定具体引擎、不过滤）
  - 流式文本无时间节流：原有 `Interlocked` 只做「排队→排空」节流、没有时间维度，于是每个 token 都触发全量 `RebuildSegments → SetBody(全文) → Markdown 全文重新解析`，总代价 O(n²)；加 60ms 门限，时间门限只作用于增量刷新、回合收尾一律强制（不强制的话按 Segments 持久化的仍是被截断的消息，正是本次要消灭的损坏模式）
  - `Bitmap` 从不 Dispose 的实际范围比文档记录更大（5 处）：分段位图、附件缩略图、PageBackground 三份实例（每次换背景泄漏 3 张 4K 图）、设置页背景预览、剪贴板位图；给分段/附件/预览加 `IDisposable`，换绑延后一帧再释放（立刻释放会让渲染线程碰到已释放的原生句柄），终结器兜底
  - 附件缓存释放改用差集而非整体释放：同一批对象常常既在待发送条带上、又在 `_sessionAttachments` 缓存里，切会话的 Clear 之后马上会把它们加回去，整体释放会把还在界面上的缩略图一起释放掉
  - view 重建导致的订阅泄漏：`ViewLocator` 每次切页都 `new ChatPageView`，而 `PropertyChanged` 用 lambda 订阅且永不解绑 → VM 上堆积 N 个处理器；改为存字段并在 DataContext 变化与 Detach 时成对解绑，分段事件的解绑路径补齐 `RemoveMessagesAfter` 等
  - 排队轮询表永不启动（启停条件自指，而 `QueuedTurnCount` 只由轮询或三处手动同步更新，首次进页面恒为 0）；构造函数补一次点火，并给 `IsQueued` 接上 XAML 可视化；删除重叠的单一 `_turnCts`（其回退分支可能停掉另一个会话的回合）

- **usage** 用量统计防抖落盘并修正 doctor 诊断（`65f60a1`）
  - 每次 `RecordLlmUsage` / `RecordAgentCall` / `MarkSessionDeleted` 都在静态类锁内做真正的 fsync，高频调用下每次都刷盘且阻塞所有其他用量操作；改为标记脏 + 1.5s 合并写（内存立即更新、落盘延后）
  - 锁序是这份改动里最要紧的部分：`Record`(持 Lock) → SaveGate 与 `Flush`(持 SaveGate) → Lock 是经典反序死锁；统一为 SaveGate → Lock 单向，且 `Flush` 先清脏标记再取快照，否则「先取快照后清零」会让并发记录的用量被静默吞掉；序列化必须在锁内，磁盘 IO 移到锁外
  - 退出路径必须 Flush（挂在 Program 的 try/finally 与 doctor 早退路径），顺带补上 doctor 一直没调 `Log.Flush` 的洞
  - doctor 修正两处失实：硬编码的「9 组场景」改为引用 `SelfCheckScenarioCount`（自检已加到 10 组）；「首次发送消息将自动执行 git init 与 first commit」——全仓无 `git init` 调用，`EnsureInitialCommit` 零调用方

- **gui** 会话分组键归一化路径，组内显示完整工作目录（`efdc351`）
  - 分组键直接取 `session.WorkDir` 原始字符串，同一处目录的多种写法（`/proj/foo` 与 `/proj/foo/` 或 `/proj/./foo`）会裂成多个同名组；改为经 `GitWorkspaceResolver.Normalize` 归一化（全路径 + 去尾分隔符 + Windows 盘符保护），复用 Core 已有实现而非另写一份
  - 字典比较器同步改为平台感知（Windows/macOS 大小写不敏感、Linux 敏感），与 `WorkspaceResolver.PathComparer` 保持一致，否则大小写不同的目录在 Windows 上会因 Ordinal 比较而裂组
  - 组头仍只显示末级目录名，但会话项内保留完整路径，避免两个同名目录无法区分
  - 修一处排序失效：未指定目录组的排序键原本硬编码为 `"\0unassigned"`，而 `GroupKeyOf` 对空目录返回空串，两者对不上，该组不再固定排最后、会被当作普通组按活跃时间插到中间

### 重构

- **storage** 配置与状态文件全量改用原子写（`f61513f`）
  - `AtomicFile`（写 `.tmp` → `Flush(true)` → rename + `.bak` 回退）此前只有 `ChatService` 与 `UsageStatsService` 在用，其余 15 处仍是裸 `File.WriteAllText`；纯持久化层改动，不触及任何调用方，可独立回滚
  - 覆盖 `RosterConfigService` / `AgentConfigService` / `ProviderConfig` / `McpServerConfig`（这几处的 setter 都是「读失败 → 空对象 → 改一个开关 → 写回」的模式，会清空用户配置）、以及 `PersonaService` / `AgentTemplateService` / `ThemeService` / `ModelProfileService` / `DefaultConfig`
  - `GitCheckpointStore` 补 Flush + `.bak`，并把文件写移出锁（原先持锁并发 Save 会竞争同一 `.tmp` 路径）
  - 同时修 `DeleteIfExists` 用裸 `File.Delete` 不清 `.bak` 的问题：「还原默认设置」删掉 `providers.toml` 后旧 `.bak` 仍保存着旧 API Key，且重建后的默认文件一旦解析异常，`TryReadText` 会回退到那个陈旧备份，把用户刚清掉的配置复活

- **agents** 删除 `max_concurrent` 字段（`cf74dcd`）
  - 该字段从未被引擎使用：`AgentEngine` / `CliAgentRunner` / `AgentToolFactory` 中都没有按它做信号量或节流，`run_subagents` 的并发只由 `Task.WhenAll` 决定；它仅出现在 Roster 提示词文案里，让用户误以为单个 agent 存在并发控制
  - 删除属零行为变化的纯粹减负：定义、两处 UI 赋值、Roster 文案、以及两个 toml（内嵌默认配置与面向用户的示例）。**存量 `agents.toml` 中的同名字段会被忽略，无需手工清理**

### 文档

- **docs** 更新 README / AGENTS / templates 文档（`0be6f66`）
  - 新增 Git 检查点系统说明（Commit/tag 检查点、每条用户消息检查点）、非 Git 降级说明（聊天可用，检查点/Git 写工具不可用）、会话分支绑定与同分支并发说明、Checkpoint 卡片 Reset/Revert/Fork 操作说明、旧步骤迁移说明（扫描规则、映射更新、`ac/*` 分支不删除）
  - `roster.prompt.example` 新增检查点信息段；`Core/Services/Git` 结构新增 `LegacyGitMigrationService`（该服务后被 `9225ac2` 移除）

- **docs** 修正构建命令、Git 机制与迁移链路的失实描述（`ac87b19`）
  - 背景：`AGENTS.md` 被当作 AI 行为契约，漂移会主动引导 AI 执行不存在的命令；本次修正均以脚本源码与产物实测为准
  - `build.sh` 实际只支持 `all`/`aot`/`selfcontained`/`dotnet`/`clean`/`help`，且无 `ARCH`、无 `AOT_MODE`；原文档的 `./build.sh linux` 与 `ARCH=arm64` 均为幻觉（实测 exit 1）
  - `./build.sh all` 语义是「三变体」而非「所有平台」，跨平台构建已移除
  - 补 Windows 与 sh 版不等价的实证：`debug.ps1` 的 `-App` ValidateSet 拒绝 `doctor`，且 `--version` 会被 PowerShell 绑定成 `-Version`（改的是构建版本号）
  - 删除 `LegacyGitMigrationService`「启动时无感迁移」描述（该服务全仓无实例化点）；Git 机制改为实际语义（子 Agent 不再自动 `git switch -c`，产出由检查点兜底；并发隔离以「工作树 + 当前分支」为键）；技术栈版本同步为 Avalonia 12.1.3，补 `models.toml` 说明

- **docs** 补全模板文档修正与发布打包细节（`3d81421`）
  - `templates/README.md`：「直接导入人格」四处描述均为幻觉（`PersonaService.ImportFile` 零调用方、设置页无 Persona 区块、`Program.cs` 无 `/persona` 子命令），改为复制到配置目录的实际流程；修正 `apiKey → api_key` 键名，补 id 缺失会被忽略的说明
  - 补齐配置目录缺失项：`mcp-servers.toml` / `models.toml`
  - `release.yml`：Windows zip 排除 `*.pdb`/`*.dbg`（`Compress-Archive` 无 `-Exclude`，改为打包前清理 stage）
  - `packagers`：补 `libxkbcommon` 作防御性声明（产物未直接 dlopen，但与桌面/输入法链路保持一致更安全）

### CI/构建

- **ci** 修复 dotnet 变体系统包缺 DLL 导致的无法启动（`38f9f65`）
  - dotnet 变体是框架依赖「多文件」布局（apphost + 主 DLL + 依赖 DLL + `.deps.json` + `.runtimeconfig.json` + 卫星资源目录），而打包逻辑对三种变体套用同一套「拷 apphost + `*.so`」模板，实测 77 个文件只装进 3 个，装完报 `The application to execute does not exist`
  - 按变体分支安装：dotnet 整目录安装并排除 `*.pdb`，aot/selfcontained 保持 apphost + 同目录原生库；rpm spec 同步按 `@VARIANT@` 分支，补 `@VARIANT@` 占位符替换
  - 补齐 X11 平台实际 `dlopen` 的系统库依赖：`libxext` / `libxfixes` / `libxi`，以及输入法硬依赖 `libice` / `libsm`（缺失会导致 X11 平台起不来）
  - tar 打包排除 `*.dbg` / `*.pdb`（Native AOT 的 `.dbg` 常达百 MB 级，会主导包体积并泄露构建机路径）；`fail_on_unmatched_files` 改为 `true`，让路径写错立即暴露

- **ci** 修正单文件变体丢失英文语言包（`cf40afa`）
  - `install_app_tree` 的注释称「卫星资源已内嵌进单文件」，经产物核查不成立：aot/selfcontained 变体的 `en/AIShikikan.Gui.resources.dll` 是独立文件（`Strings.en.resx` 编译产物）
  - 单文件分支只拷 apphost + `*.so`，会漏掉语言资源目录，导致英文用户界面静默回退为中文（不崩溃，但属本地化功能缺失）
  - 单文件分支补拷卫星资源目录，目录名按产物实际值遍历而非硬编码；修正两个 workflow 中错误的「已内嵌」注释；实跑验证 aot 变体安装后 `en/AIShikikan.Gui.resources.dll` 就位

### 已知问题

以下为 `AGENTS.md` 末尾「已知技术债」表格在本版本时的快照（18 条），供使用者了解限制：

| # | 问题 | 位置 | 本版本进展 |
|---|---|---|---|
| 1 | **无测试、无 CI 门禁**：仓库无任何测试项目，两个 workflow 均需手动触发，push/PR 不做验证 | 全仓 | 部分缓解：`WorkspaceExecutionCoordinator.SelfCheck`（10 组并发场景）已接入 `doctor`，是项目内唯一的自动化自检 |
| 2 | **AOT 兼容性无人验证**：`SuppressTrimAnalysisWarnings` + `SuppressAotAnalysisWarnings` 把裁剪/AOT 分析器全静音 | `AIShikikan.Gui.csproj` | 未处理 |
| 3 | **UI 线程同步跑 git 进程**：发消息前会在 UI 线程拉起约 6 个 git 进程（`GitService.Run` 用 `GetAwaiter().GetResult()`），最坏可冻结数十秒 | `GitService.cs` | 部分缓解：已加 15/60s 超时与 `GIT_TERMINAL_PROMPT=0`（不再无限等待凭据），且新增全量 async API；同步 wrapper 与调用方尚未迁移 |
| 4 | **流式文本无时间节流**：每个 token 触发一次全量 Markdown 重解析，长回复呈 O(n²) | `ChatPageViewModel.cs` | 部分缓解：增量刷新加 60ms 门限，回合收尾仍强制全量重建 |
| 5 | **图片附件全流程在 UI 线程**：解码 + PNG 编码 + 二次解码，单张可达 1 秒（最多连贴 4 张） | `ImageAttachmentService.cs` | 未处理 |
| 6 | **背景图取色全量像素搬运**：4K 图约 100MB 分配 + 830 万次循环，切换背景即触发 | `MainWindow.axaml.cs` | 未处理 |
| 7 | **`Bitmap` 从不 Dispose**：图片分段与附件缩略图泄漏原生内存 | `ChatItemViewModel.cs` / `ImageAttachmentService.cs` | 部分缓解：分段/附件/预览已加 `IDisposable`（换绑延后一帧释放 + 终结器兜底）；`PageBackground` 三份实例、剪贴板位图等路径仍在 |
| 8 | **MCP 客户端无自动重连**：进程崩溃后 UI 仍显示已连接，只能手动重连 | `Mcp/` | 部分缓解：新增 60s 后台探活，状态不再失真；仍无自动重连 |
| 9 | **LLM 层无 429/5xx 重试退避**，无首 token 超时 | `Core/Services/Llm/` | 未处理（仅 Anthropic 流路径加了 120s 流空闲超时） |
| 10 | **每 agent 一个 `run_<id>` 工具无上限**：agent 多了会挤占上下文并降低工具选择准确率 | `AgentToolFactory.cs` | 未处理 |
| 11 | **超大文件**：`ChatPageViewModel.cs` 约 1750 行、`ChatPageView.axaml` 875 行 | `ViewModels/` `Views/` | 未处理 |
| 12 | **重复代码**：`Truncate` 5 份、`EnsureUniqueId` 2 份、JSON 参数提取 4 份、路径规范化 4 套 | `Core/` `ViewModels/` | 未处理 |
| 13 | **csproj 冗余**：`<Folder Include="Models\" />` 指向不存在的目录；`logo.jpg` 被重复包含 | `AIShikikan.Gui.csproj` | 未处理 |
| 14 | **`TryReserve` 等协调器 API 无生产调用方**，回滚/Fork 确认期无 worktree 级保护 | `WorkspaceExecutionCoordinator.cs` | 未处理（目前只靠会话级 `Fork_SessionOccupied` 守卫） |
| 15 | **子代理输出未接检查点**：`Assignment.StepId` 已无人赋值，工具卡头部的「检查点:」会是空白 | `AgentToolFactory.cs` | 未处理 |
| 16 | **压缩未转发当前回合 provider/model** | `AgentToolFactory.cs` | **已修复**：调用点已补传 `providerId` / `model` |
| 17 | **原子写未全覆盖**：读取路径仍裸读 | `ChatService.cs` / `SessionRuntimeRegistry.cs` / `AgentTemplateService.cs` | 部分缓解：会话列表主加载路径（`ChatService.ReadMetadata`）已加固；会话标题与模板读取仍裸读 |
| 18 | **i18n 自检未接线**：`Strings.FindMissingEnglishKeys()` 已写好但无调用方 | `Resources/Strings.cs` | 部分缓解：随步骤分支机制废弃清掉了 10 个死机制孤儿键（含 4 个 `ToolCard_Rollback*`）；自检函数仍无调用方 |

更完整的分析与批次划分见 `docs/plans/`。
