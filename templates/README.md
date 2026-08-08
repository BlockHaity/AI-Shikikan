# 模板文件夹

本目录存放 Agent Commander 的配置模板与人格/专家文件模板，
方便快速上手、复制与导入。

## 结构

```
templates/
├── config/       配置模板(对应 ~/.config/agent-commander/ 下的文件)
│   ├── providers.example.json      LLM Provider 配置
│   ├── agents.example.json         自定义 Agent 定义 + 分派规则
│   └── roster.prompt.example       分派 Roster 提示词模板
└── personas/     人格文件模板(可通过 UI/CLI 直接导入)
    ├── expert.example.json         专家人格模板
    └── roleplay.example.json      角色扮演人格模板
```

## 快速开始

### 1. 使用人格/专家文件(推荐: 直接导入)

- GUI: 设置页 → 专家与模板 → 导入专家文件, 选择 personas/*.json
- CLI: `/persona import templates/personas/expert.example.json`
- 导入后可在聊天会话面板选择该专家
- id 冲突时自动追加后缀, 不会覆盖已有文件

| 字段 | 说明 |
|------|------|
| id | 唯一标识, 决定文件名; 缺省时用文件名 |
| name | 显示名称(必填) |
| kind | `Expert` 专家 / `Roleplay` 角色扮演 |
| description | 列表里展示的说明 |
| systemPrompt | 系统提示词(必填), 支持 \n 换行 |

### 2. 使用配置文件模板(手动复制)

```bash
mkdir -p ~/.config/agent-commander
cp templates/config/providers.example.json ~/.config/agent-commander/providers.json
cp templates/config/agents.example.json    ~/.config/agent-commander/agents.json
cp templates/config/roster.prompt.example  ~/.config/agent-commander/roster.prompt
```

注意:

- `providers.json` 中 `kind` 为数字: `0` = OpenAI 兼容(含 DeepSeek 等),
  `1` = Anthropic。`apiKey` 留空时读环境变量
  `OPENAI_API_KEY` / `ANTHROPIC_API_KEY`。
- `agents.json` 中 `injection` 为数字: `0` 不注入 / `1` 命令行参数 /
  `2` 记忆文件 / `3` 两者。用户定义按 `id` 覆盖同名内置 Agent。
- `roster.prompt` 支持占位符: `{agents}` `{personas}` `{templates}`
  `{rules}` `{git}`。