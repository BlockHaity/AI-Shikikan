using AIShikikan.Cli.Tui.Services.Commands;
using AIShikikan.Cli.Tui.Ui;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using Terminal.Gui.Views;

namespace AIShikikan.Cli.Tui.Services;

/// <summary>输入自动补全: "/" 命令名 + 参数(人格/模型/Agent/会话等)。
/// 输入 "/" 即弹出全部命令, 继续输入按前缀过滤; 命令后空格自动进入参数补全。
/// 补全词规则与 IsWordChar 保持一致(字母/数字/若干符号, 含 "/")。</summary>
public class CommandSuggestionGenerator(CommanderRuntime runtime, ChatService chatService, CommandService commands)
    : ISuggestionGenerator
{
    public IEnumerable<Suggestion> GenerateSuggestions(AutocompleteContext context)
    {
        // 由单元格重建当前行, 只关心光标前的文本
        var line = string.Concat(context.CurrentLine.Select(c => c.Grapheme));
        var cursor = Math.Min(context.CursorPosition, line.Length);
        var beforeCursor = line[..cursor];

        // 仅行首以 "/" 开头时启用命令补全
        if (!beforeCursor.StartsWith('/'))
        {
            return [];
        }

        // 光标前正在输入的词(最后一个连续单词)
        var wordStart = cursor;
        while (wordStart > 0 && IsWordChar(beforeCursor[wordStart - 1].ToString()))
        {
            wordStart--;
        }

        var word = beforeCursor[wordStart..cursor];

        // 与内置生成器相同的游标约定: 供 InsertSelection 计算替换范围
        context.CursorPosition = wordStart < 1
            ? wordStart
            : Math.Min(wordStart + 1, context.CurrentLine.Count);

        // 尚未出现空格 → 补全命令名
        var spaceIndex = beforeCursor.IndexOf(' ');
        if (spaceIndex < 0)
        {
            return CompleteCommands(word);
        }

        // 已出现空格 → 按命令补全参数
        var commandName = beforeCursor[..spaceIndex].TrimStart('/').ToLowerInvariant();
        var argWords = beforeCursor[(spaceIndex + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return CompleteArguments(commandName, argWords, word);
    }

    public bool IsWordChar(string text) =>
        !string.IsNullOrEmpty(text) && (char.IsLetterOrDigit(text[0]) || text[0] is '-' or '_' or '+' or '.' or '/');

    /// <summary>按前缀(大小写不敏感)匹配全部命令名。word 含行首 "/"。</summary>
    private IReadOnlyList<Suggestion> CompleteCommands(string word)
    {
        var prefix = word.ToLowerInvariant();
        var list = new List<Suggestion>();
        foreach (var (cmd, help) in commands.AllCommands)
        {
            if (!cmd.StartsWith('/') || !cmd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 已完整输入的命令不再提示
            if (cmd.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            list.Add(new Suggestion(word.Length, cmd, $"{cmd} — {CommandUi.Shorten(help, 40)}"));
        }

        return list;
    }

    /// <summary>按命令名路由到参数候选(人格/模型/Agent/会话/模板/工具等), 并做前缀过滤。</summary>
    private IReadOnlyList<Suggestion> CompleteArguments(string commandName, IReadOnlyList<string> argWords, string word)
    {
        var prefix = word.ToLowerInvariant();
        var list = new List<Suggestion>();
        foreach (var candidate in ArgumentCandidates(commandName, argWords))
        {
            if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                !candidate.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new Suggestion(word.Length, candidate, candidate));
            }
        }

        return list;
    }

    /// <summary>命令的参数候选列表(子命令关键词 + 运行时数据)。</summary>
    private IReadOnlyList<string> ArgumentCandidates(string commandName, IReadOnlyList<string> argWords)
    {
        var first = argWords.Count > 0 ? argWords[0].ToLowerInvariant() : string.Empty;

        switch (commandName)
        {
            case "persona":
            case "personas":
            {
                var c = runtime.Personas.Select(p => p.Display).ToList();
                c.Add("import");
                return c;
            }

            case "model":
            case "models":
            {
                var c = new List<string>();
                foreach (var provider in runtime.Llm.Settings.Providers)
                {
                    var models = provider.EnabledModels is { Count: > 0 }
                        ? provider.EnabledModels
                        : new List<string> { provider.DefaultModel };
                    c.AddRange(models);
                }

                c.AddRange(["fetch", "enable-all", "disable-all"]);
                return c.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            case "provider":
            case "providers":
            {
                if (first is "add" or "remove" or "use")
                {
                    return runtime.Llm.Settings.Providers.Select(p => p.Id).ToList();
                }

                var c = runtime.Llm.Settings.Providers.Select(p => p.Id).ToList();
                c.AddRange(["add", "remove", "use"]);
                return c;
            }

            case "agent":
            case "agents":
            {
                if (first is "run" or "dispatch" or "stop" or "cancel" or "logs" or "log")
                {
                    return runtime.Agents.Select(a => a.Display).ToList();
                }

                var c = runtime.Agents.Select(a => a.Display).ToList();
                c.AddRange(["run", "stop", "logs", "assignments"]);
                return c;
            }

            case "session":
            case "sessions":
            {
                if (first is "rename" or "delete" or "rm")
                {
                    return chatService.Sessions.Select(s => s.DisplayTitle).ToList();
                }

                var c = chatService.Sessions.Select(s => s.DisplayTitle).ToList();
                c.AddRange(["new", "rename", "delete"]);
                return c;
            }

            case "agent-config":
            case "roster":
            {
                var c = runtime.Agents.Select(a => a.Display).ToList();
                c.AddRange(["reload", "enable", "disable"]);
                return c;
            }

            case "git":
                return ["status", "steps", "diff", "merge", "drop", "revert", "commit", "branch", "graph", "stage", "stage-all", "unstage"];

            case "templates":
                return runtime.Templates.Select(t => t.Display).ToList();

            case "tools":
                return runtime.Registry.All.Select(t => t.Name).OrderBy(n => n).ToList();

            default:
                return [];
        }
    }
}
