<div align="center">
  <img src="static/logo.jpg" width="200" height="200" alt="logo"></img>
  <br>
  <span style="color: gray; font-size: small;">图标由AI生成</span>
  <h1>AI-Shikikan</h1>
</div>

<h4 align="center">一个使用 .NET 10 C# 开发的 Agent 指挥官</h4>

<p align="center">
  <img src="https://img.shields.io/badge/Vibe%20Coding-1b8c?style=flat&logo=tensorflow&logoColor=white" alt="Vibe Coding">
  <img src="https://img.shields.io/badge/.NET%2010-512BD4?style=flat&logo=dotnet&logoColor=white" alt=".NET 10">
  <img src="https://img.shields.io/badge/C%23-239120?style=flat&logo=csharp&logoColor=white" alt="C#">
  <img src="https://img.shields.io/badge/License-MIT-green.svg" alt="MIT License">
</p>

---

## 它是什么

AI-Shikikan 是一款可以将多个终端 Agent 集合在一起，并让 AI 统一指挥调度这些 Agent 的图形程序。

你可以把它理解为：**一个 AI 指挥官，能调度 Claude Code、OpenCode、DeepSeek Harness 等工具协同完成复杂任务。**

```
用户请求 → AI指挥官 → 分析任务 → 调用子Agent → 汇总结果 → 返回用户
                ↑
        人格/专家注入
```

## 功能

- **多 Agent 调度**：让 AI 调用主流 Agent 程序（Claude Code、OpenCode、DeepSeek Harness、Codex CLI、Gemini CLI）
- **可配置模型**：为不同 Agent 使用不同的模型完成对应任务
- **人格/专家注入**：对指挥官自身/Agent 程序注入角色扮演或专家文件
- **Git 步骤管理**：自动创建步骤分支，支持回滚与合并

## 支持的 LLM API

- **OpenAI Style**：OpenAI、Azure OpenAI、DeepSeek 等兼容接口
- **Anthropic Style**：Anthropic Claude 原生接口

## 快速开始

### 构建

```bash
# 构建所有平台
./build.sh all

# 仅构建 Linux
./build.sh linux

# 仅构建 ARM64
ARCH=arm64 ./build.sh linux

# 不使用 AOT
AOT_MODE=off ./build.sh linux
```

### 运行

```bash
# 启动图形界面
./AIShikikan.Gui

# 查看版本
./AIShikikan.Gui --version

# 诊断环境
./AIShikikan.Gui doctor
```

## 配置

配置文件存放在用户目录：

| 平台 | 配置目录 |
|------|----------|
| Linux | `~/.config/ai-shikikan/` |
| macOS | `~/Library/Application Support/AI-Shikikan/Config/` |
| Windows | `%APPDATA%\AI-Shikikan\Config\` |

### 核心配置文件

| 文件 | 说明 |
|------|------|
| `providers.toml` | LLM Provider 配置（API Key、模型、端点） |
| `agents.toml` | Agent 定义（可执行文件、参数、专长） |
| `personas/` | 人格/专家 Markdown(YAML frontmatter) 文件目录 |
| `templates/` | 任务模板 TOML 文件目录 |
| `roster.prompt` | Roster 注入模板 |

### 示例配置

参考 `templates/config/` 目录中的示例文件。

**providers.toml 示例：**
```toml
active_provider_id = "anthropic"

[[providers]]
id = "anthropic"
name = "Anthropic"
kind = "Anthropic"
base_url = "https://api.anthropic.com"
api_key = "sk-ant-..."
default_model = "claude-sonnet-4-20250514"
```

**agents.toml 示例：**
```toml
rules = "优先选择专长与任务匹配的 Agent"

[[agents]]
id = "claude"
name = "Claude Code"
executable = "claude"
args = ["-p", "{prompt}"]
description = "Anthropic 官方编码 Agent"
```

## 默认 Agent

首次启动会自动生成 `agents.toml`（含以下默认 Agent），可自由修改或删除；用户配置以文件为准，删除后不会恢复。

| Agent | 可执行文件 | 专长 |
|-------|-----------|------|
| Claude Code | `claude` | 编码、重构、测试 |
| Codex CLI | `codex` | 修复、小步修改、lint |
| Gemini CLI | `gemini` | 搜索、研究、多模态 |
| OpenCode | `opencode` | 通用、跨栈、脚本 |
| DeepSeek Harness | `dsh` | DeepSeek 官方 Harness、插件化运行时 |

## 架构

```
AIShikikan.Gui     - 图形界面 (Avalonia) + 核心逻辑
  ├── Core/                - 核心逻辑（AOT 兼容）
  │   ├── Services/Engine    - Agent 调度引擎
  │   ├── Services/Agents    - Agent 定义与配置
  │   ├── Services/Llm       - LLM 客户端 (OpenAI/Anthropic)
  │   ├── Services/Personas  - 人格/专家管理
  │   ├── Services/Templates - 任务模板
  │   └── Services/Tools     - 内置工具集
  ├── ViewModels/          - MVVM 视图模型
  └── Views/               - Avalonia XAML 视图
```

## 鸣谢

### 库

- [Anthropic.SDK](https://www.nuget.org/packages/Anthropic.SDK/)
- [Azure.AI.OpenAI](https://www.nuget.org/packages/Azure.AI.OpenAI/)
- [Avalonia](https://www.nuget.org/packages/Avalonia/)
- [Avalonia.Desktop](https://www.nuget.org/packages/Avalonia.Desktop/)
- [AvaloniaUI.DiagnosticsSupport](https://www.nuget.org/packages/AvaloniaUI.DiagnosticsSupport/)
- [CCSWE.Avalonia.Material](https://www.nuget.org/packages/CCSWE.Avalonia.Material/)
- [CommunityToolkit.Mvvm](https://www.nuget.org/packages/CommunityToolkit.Mvvm/)
- [Material.Icons.Avalonia](https://www.nuget.org/packages/Material.Icons.Avalonia/)
- [ScottPlot.Avalonia](https://www.nuget.org/packages/ScottPlot.Avalonia/)
- [Tomlyn](https://www.nuget.org/packages/Tomlyn/)
- [YamlDotNet](https://www.nuget.org/packages/YamlDotNet/)

### 字体

- [HarmonyOS Sans SC](https://developer.huawei.com/consumer/cn/design/resource/) — 华为, 依据 *HarmonyOS Sans 字体许可协议* 使用(默认标准字体)
- [CaskaydiaCove Nerd Font Mono](https://github.com/ryanoasis/nerd-fonts) — Nerd Fonts 项目, 依据 *SIL Open Font License 1.1* 使用(默认等宽字体)

字体文件与许可文本位于 `src/AIShikikan.Gui/Assets/Fonts/`。

## 贡献者

![贡献者图片列表](https://contrib.rocks/image?repo=BlockHaity/AI-Shikikan)
