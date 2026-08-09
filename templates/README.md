# 模板文件夹

本目录存放 AI-Shikikan 的配置模板与人格/专家文件模板，
方便快速上手、复制与导入。

## 结构

```
templates/
├── config/       配置模板(对应 ~/.config/ai-shikikan/ 下的文件)
│   ├── providers.example.toml      LLM Provider 配置
│   ├── agents.example.toml         自定义 Agent 定义 + 分派规则
│   └── roster.prompt.example       分派 Roster 提示词模板
└── personas/     人格文件模板(可通过 UI/CLI 直接导入)
    ├── expert.example.toml         专家人格模板
    └── roleplay.example.toml      角色扮演人格模板
```

## 快速开始

### 1. 使用人格/专家文件(推荐: 直接导入)

- GUI: 设置页 → 专家与模板 → 导入专家文件, 选择 personas/*.toml
- CLI: `/persona import templates/personas/expert.example.toml`
- 导入后可在聊天会话面板选择该专家
- id 冲突时自动追加后缀, 不会覆盖已有文件

| 字段 | 说明 |
|------|------|
| id | 唯一标识, 决定文件名; 缺省时用文件名 |
| name | 显示名称(必填) |
| kind | `Expert` 专家 / `Roleplay` 角色扮演 |
| description | 列表里展示的说明 |
| system_prompt | 系统提示词(必填), 支持多行文本(TOML 三引号) |

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
- `agents.toml` 中 `injection` 为字符串: `None` 不注入 / `PromptFlag` 命令行参数 /
  `MemoryFile` 记忆文件 / `Both` 两者。用户定义按 `id` 覆盖同名内置 Agent。
- `roster.prompt` 支持占位符: `{agents}` `{personas}` `{templates}`
  `{rules}` `{git}`。