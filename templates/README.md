# 模板文件夹

本目录存放 AI-Shikikan 的配置模板与人格/专家文件模板，
方便快速上手、复制与导入。

## 结构

```
templates/
├── config/       配置模板(对应 ~/.config/ai-shikikan/ 下的文件)
│   ├── providers.example.toml      LLM Provider 配置
│   ├── agents.example.toml         自定义 Agent 定义 + 分派规则
│   ├── models.example.toml         模型价格与上下文窗口配置(可选)
│   └── roster.prompt.example       分派 Roster 提示词模板
└── personas/     人格文件模板(可通过 UI/CLI 直接导入)
    ├── expert.example.md           专家人格模板(YAML frontmatter + Markdown)
    └── roleplay.example.md         角色扮演人格模板(YAML frontmatter + Markdown)
```

## 快速开始

### 1. 使用人格/专家文件(推荐: 直接导入)

- GUI: 设置页 → 专家与模板 → 导入专家文件, 选择 personas/*.md
- CLI: `/persona import templates/personas/expert.example.md`
- 导入后可在聊天会话面板选择该专家
- id 冲突时自动追加后缀, 不会覆盖已有文件

人格文件采用 **带 YAML frontmatter 的 Markdown** 格式:
frontmatter 存放元数据, Markdown 正文作为系统提示词(system prompt)。

```markdown
---
id: "expert-example"
name: "专家示例"
kind: "Expert"
description: "专家人格文件模板"
---

你是一位资深专家。请遵循：
1. 先理解需求背景与目标
2. 采用结构化的方式输出（步骤、要点、结论）
```

| 字段 | 说明 |
|------|------|
| id | 唯一标识, 决定文件名; 缺省时用文件名 |
| name | 显示名称(必填) |
| kind | `Expert` 专家 / `Roleplay` 角色扮演 |
| description | 列表里展示的说明 |
| 正文 | 系统提示词(必填), 支持任意多行文本 |

> 兼容性: 仍可读取/导入旧 `.json` 与 `.toml` 人格文件, 导入后统一保存为 `.md`。

### 2. 使用配置文件模板(手动复制)

```bash
mkdir -p ~/.config/ai-shikikan
cp templates/config/providers.example.toml ~/.config/ai-shikikan/providers.toml
cp templates/config/agents.example.toml    ~/.config/ai-shikikan/agents.toml
cp templates/config/roster.prompt.example  ~/.config/ai-shikikan/roster.prompt
```

注意:

- `providers.toml` 中 `kind` 为字符串: `OpenAi` = OpenAI 兼容(含 DeepSeek 等),
  `Anthropic` = Anthropic。`apiKey` 留空时读环境变量
  `OPENAI_API_KEY` / `ANTHROPIC_API_KEY`。
- `agents.toml` 中 `args` 为参数模板数组, 支持 `{prompt}` 占位符(指代任务提示词);
  `description` 即子代理封装为工具后的工具描述。应用首次启动会自动生成默认
  `providers.toml` 与 `agents.toml`, 用户以文件为准, 可自由增删任意条目。
- `roster.prompt` 支持占位符: `{agents}` `{personas}` `{templates}`
  `{rules}` `{git}`。
- `models.toml` 可选: 配置模型上下文窗口与价格(USD/百万 token), 键支持
  精确 id 或 `"前缀*"` 通配; 未配置时尝试从 Provider 的 `/v1/models`
  (OpenRouter 兼容端点)拉取。价格影响 GUI 右侧「状态」面板的成本统计。
- **Git 检查点**：每条用户消息自动创建检查点(Commit/tag 标记), 支持
  Reset/Revert/Fork 卡片操作。非 Git 仓库降级为警告, 聊天仍可用。
  会话分支绑定, 同分支并发隔离。旧 `steps/*.json` 自动无感迁移。
- **旧步骤迁移**：启动时自动扫描旧 `steps/*.json`, 优先 MergeCommit
  可解析, 其次 StepBranch 取 tip。建立 oldStepId→newCheckpointId 映射,
  更新 assignments/sessions JSON。旧 `ac/*` 分支永不自动删除。
