using System.Text;
using System.Text.Json;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Git;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Personas;
using AIShikikan.Core.Services.Templates;
using AIShikikan.Core.Services.Tools;
using AIShikikan.Core.Services.Usage;

namespace AIShikikan.Core.Services.Engine;

public abstract record AgentEngineEvent;

public sealed record EngineTextDelta(string Text) : AgentEngineEvent;

public sealed record EngineThinkingDelta(string Thinking) : AgentEngineEvent;

public sealed record EngineToolStarted(string ToolCallId, string ToolName, string Arguments) : AgentEngineEvent;

public sealed record EngineToolOutput(string ToolCallId, string ToolName, string Line) : AgentEngineEvent;

public sealed record EngineToolFinished(string ToolCallId, string ToolName, ToolResult Result) : AgentEngineEvent;

public sealed record EngineApprovalRequested(
    string ToolCallId, string ToolName, string Arguments,
    TaskCompletionSource<bool> UserDecision) : AgentEngineEvent;

public sealed record EngineDone(string? Content, string? Error) : AgentEngineEvent;

public sealed record EngineAssignmentChanged(Assignment Assignment) : AgentEngineEvent;

public sealed record EngineUsageRecorded(string Provider, string Model, ChatUsage Usage) : AgentEngineEvent;

/// <summary>上下文自动压缩完成事件(达到阈值后由引擎在发起请求前触发)。</summary>
public sealed record EngineContextCompacted : AgentEngineEvent;

public sealed class EngineOptions
{
    public int MaxTurns { get; set; } = 10;
    public bool AutoApprove { get; set; }
    public string? Model { get; set; }
    public string? ProviderId { get; set; }
    public int MaxHistoryMessages { get; set; } = 40;
    public string? SystemExtra { get; set; }
    public ThinkingLevel Thinking { get; set; } = ThinkingLevel.Auto;
    public string? WorkDir { get; set; }
    public bool IsPlanMode { get; set; }

    /// <summary>是否启用上下文自动压缩(达到阈值后在发起请求前压缩历史)。</summary>
    public bool AutoCompactEnabled { get; set; } = true;

    /// <summary>自动压缩触发阈值(最近一次 input tokens / 上下文窗口大小), 默认 85%。</summary>
    public double AutoCompactThreshold { get; set; } = 0.85;
}

/// <summary>对话引擎: 组装 system(人格 + Roster) → LLM → 工具(批准/只读/子代理) → 循环至完成。</summary>
public sealed class AgentEngine
{
    private readonly LlmService _llm;
    private readonly ToolRegistry _registry;
    private readonly GitStepService _git;
    private readonly AssignmentManager _assignments;
    private readonly IReadOnlyList<Persona> _personas;
    private readonly IReadOnlyList<AgentTemplate> _templates;
    private readonly IReadOnlyList<CliAgentDefinition> _agents;
    private readonly string _workspaceRoot;
    private string? _personaText;
    private readonly EngineOptions _options;
    private readonly List<ChatTurnMessage> _conversation = [];
    private IReadOnlyList<AgentRosterEntry>? _rosterEntries;

    /// <summary>最近一次 LLM 调用的输入 token 数(即上下文占用), 供自动压缩阈值判断。</summary>
    private int _lastInputTokens;

    public AgentEngine(
        LlmService llm,
        ToolRegistry registry,
        GitStepService git,
        AssignmentManager assignments,
        IReadOnlyList<Persona> personas,
        IReadOnlyList<AgentTemplate> templates,
        IReadOnlyList<CliAgentDefinition> agents,
        string workspaceRoot,
        string? personaText = null,
        EngineOptions? options = null,
        IReadOnlyList<AgentRosterEntry>? rosterEntries = null)
    {
        _llm = llm;
        _registry = registry;
        _git = git;
        _assignments = assignments;
        _assignments.AssignmentChanged += a => OnEvent?.Invoke(new EngineAssignmentChanged(a));
        _personas = personas;
        _templates = templates;
        _agents = agents;
        _workspaceRoot = workspaceRoot;
        _personaText = personaText;
        _rosterEntries = rosterEntries;
        _options = options ?? new EngineOptions();
    }

    public event Action<AgentEngineEvent>? OnEvent;

    public EngineOptions Options => _options;

    public AssignmentManager Assignments => _assignments;

    public GitStepService Git => _git;

    public string? PersonaText => _personaText;

    public IReadOnlyList<AgentRosterEntry>? RosterEntries => _rosterEntries;

    public void SetPersonaText(string? text)
    {
        _personaText = string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public void SetRosterEntries(IReadOnlyList<AgentRosterEntry>? entries)
    {
        _rosterEntries = entries;
    }

    public void ClearConversation()
    {
        _conversation.Clear();
        _lastInputTokens = 0;
    }

    /// <summary>解析模型上下文窗口大小: 模型设置手配值优先, 回退模型档案(API/内置); 无信息返回 0。</summary>
    private long ResolveContextTokens(string model, ProviderConfig provider)
    {
        return provider.GetContextTokens(model)
            ?? ModelProfileService.Resolve(model, provider.Id).ContextTokens;
    }

    /// <summary>压缩主对话上下文: 保留最近 KeepRecent 条消息, 其余历史交由 LLM 摘要为一条
    /// 上下文消息替换。返回是否发生压缩(历史不足以压缩时 false)。失败时保持原样不误删。</summary>
    public async Task<bool> CompactConversationAsync(CancellationToken ct = default)
    {
        const int KeepRecent = 6;
        if (_conversation.Count <= KeepRecent + 1)
        {
            return false; // 历史太短, 无需压缩
        }

        // 边界回退: 保留段不得以 tool 结果开头, 也不得切断 assistant(tool_calls) 与其结果
        var keepFrom = _conversation.Count - KeepRecent;
        while (keepFrom > 0)
        {
            var first = _conversation[keepFrom];
            var prev = _conversation[keepFrom - 1];
            var breaksPair = first.Role == ChatMsgRole.Tool ||
                             (prev.Role == ChatMsgRole.Assistant && prev.ToolCalls is { Count: > 0 });
            if (!breaksPair) break;
            keepFrom--;
        }

        if (keepFrom <= 0)
        {
            return false; // 全部属于最近交互, 无完整可压缩前缀
        }

        var toCompact = _conversation.Take(keepFrom).ToList();
        var provider = _llm.GetProvider(_options.ProviderId);
        if (provider is null) return false;

        var model = _llm.ResolveModel(_options.Model, _options.ProviderId);

        try
        {
            var sb = new StringBuilder();
            foreach (var m in toCompact)
            {
                var (label, body) = m.Role switch
                {
                    ChatMsgRole.User => ("用户", m.Content),
                    ChatMsgRole.Assistant when m.ToolCalls is { Count: > 0 } =>
                        ("助手", $"(调用工具: {string.Join(", ", m.ToolCalls.Select(t => t.Name))})"),
                    ChatMsgRole.Assistant => ("助手", m.Content),
                    ChatMsgRole.Tool => ("工具结果", m.Content),
                    _ => ("其他", m.Content)
                };
                // 摘要为纯文本: 图片不进入摘要, 但标注数量避免后续上下文误判"用户从未发图"
                if (m.Images is { Count: > 0 })
                {
                    body = string.IsNullOrWhiteSpace(body)
                        ? $"(发送了 {m.Images.Count} 张图片)"
                        : $"{body}\n(并发送了 {m.Images.Count} 张图片)";
                }

                if (string.IsNullOrWhiteSpace(body)) continue;
                sb.Append($"\n[{label}] {(body.Length > 4000 ? body[..4000] + "..." : body)}");
            }

            // 摘要请求自身也要防超长: 中段截断保留头尾
            var transcript = sb.ToString();
            if (transcript.Length > 24000)
            {
                transcript = transcript[..12000] + "\n...(中段过长已省略)...\n" + transcript[^6000..];
            }

            var request = new ChatRequest
            {
                Model = model,
                MaxTokens = 2048,
                Temperature = 0.1,
                System = """
                    你是对话历史压缩器。把给定的多轮对话历史压缩为一份结构化摘要, 供后续对话作为上下文继续。要求:
                    1. 保留: 用户的总体目标、已达成的关键结论、修改/创建的文件路径、工具执行结果要点、未决事项与约束;
                    2. 剔除: 寒暄、冗余过程、重复内容;
                    3. 用简洁的分点中文输出, 不要开场白, 不要臆造未出现的信息。
                    """,
                Messages =
                [
                    new ChatTurnMessage { Role = ChatMsgRole.User, Content = transcript }
                ]
            };

            var response = await _llm.GetClient(provider.Id).CompleteAsync(request, ct)
                .ConfigureAwait(false);
            if (response.IsError || string.IsNullOrWhiteSpace(response.Content))
            {
                Log.Warn("Engine", $"上下文压缩未生效: {response.Error ?? "空内容"}");
                return false;
            }

            // 用摘要替换被压缩的历史, 保留 keepFrom 之后的消息
            var summary = new ChatTurnMessage
            {
                Role = ChatMsgRole.User,
                Content = $"# 前情摘要(已压缩 {toCompact.Count} 条历史)\n{response.Content.Trim()}"
            };
            var recent = _conversation.Skip(keepFrom).ToList();
            _conversation.Clear();
            _conversation.Add(summary);
            _conversation.AddRange(recent);
            _lastInputTokens = 0; // 压缩后占用待下一次真实用量刷新, 避免连续误触发

            Log.Info("Engine", $"上下文已压缩: {toCompact.Count} 条 → 摘要 1 条, 保留最近 {recent.Count} 条");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Engine", ex, "上下文压缩失败, 保持原对话不变");
            return false;
        }
    }

    /// <summary>用 UI 会话消息重建引擎内部对话(删除/fork 编辑消息后调用, 保证 LLM 上下文一致)。
    /// 工具分段压缩为简短摘要并入助手回合文本; 图片分段作为多模态附件保留。</summary>
    public void RebuildConversation(IReadOnlyList<ChatMessage> messages)
    {
        _conversation.Clear();
        _lastInputTokens = 0; // 历史已重建, 旧的占用读数作废
        foreach (var m in messages)
        {
            var text = string.Join("\n", m.Segments
                .Where(s => s.Kind == MessageSegmentKind.Text)
                .Select(s => s.Content));

            var tools = m.Segments.Where(s => s.Kind == MessageSegmentKind.Tool && s.Tool is not null).ToList();
            if (tools.Count > 0)
            {
                var sb = new StringBuilder(text);
                foreach (var t in tools)
                {
                    var tool = t.Tool!;
                    sb.Append($"\n\n[已执行工具 {tool.Name}");
                    if (tool.IsError) sb.Append(" (失败)");
                    if (!string.IsNullOrWhiteSpace(tool.Result))
                    {
                        var r = tool.Result.Length > 1500 ? tool.Result[..1500] + "..." : tool.Result;
                        sb.Append($": {r}");
                    }

                    sb.Append(']');
                }

                text = sb.ToString();
            }

            var images = m.Segments
                .Where(s => s.Kind == MessageSegmentKind.Image && !string.IsNullOrEmpty(s.ImageData))
                .Select(s => new ChatImagePart { Base64Data = s.ImageData!, MimeType = s.ImageMimeType ?? "image/png" })
                .ToList();

            if (string.IsNullOrWhiteSpace(text) && images.Count == 0) continue;

            _conversation.Add(new ChatTurnMessage
            {
                Role = m.Role == MessageRole.User ? ChatMsgRole.User : ChatMsgRole.Assistant,
                Content = text,
                Images = images.Count > 0 ? images : null
            });
        }
    }

    public Task<string> RunTurnAsync(string userMessage, CancellationToken ct = default) =>
        RunTurnAsync(userMessage, null, ct);

    /// <summary>发起一轮对话; images 为用户消息附带的多模态图片(base64)。</summary>
    public async Task<string> RunTurnAsync(string userMessage, IReadOnlyList<ChatImagePart>? images, CancellationToken ct = default)
    {
        // 连续用户消息合并为一条(继续输出场景), 避免部分 API 要求严格角色交替
        var last = _conversation.Count > 0 ? _conversation[^1] : null;
        if (last is { Role: ChatMsgRole.User })
        {
            last.Content += $"\n\n{userMessage}";
            if (images is { Count: > 0 })
            {
                last.Images ??= [];
                last.Images.AddRange(images);
            }
        }
        else
        {
            _conversation.Add(new ChatTurnMessage
            {
                Role = ChatMsgRole.User,
                Content = userMessage,
                Images = images is { Count: > 0 } ? images.ToList() : null
            });
        }

        try
        {
            var provider = _llm.GetProvider(_options.ProviderId);
            if (provider is null)
            {
                var msg = "未配置 Provider。请在 providers.toml 中添加(见 ConfigDir)或设置 OPENAI_API_KEY / ANTHROPIC_API_KEY。";
                OnEvent?.Invoke(new EngineDone(null, msg));
                return msg;
            }

            var model = _llm.ResolveModel(_options.Model, _options.ProviderId);
            var thinking = ResolveThinking(model, _options.ProviderId);
            var system = BuildSystemPrompt(thinking);

            Log.Debug("LLM", $"回合开始: provider={_options.ProviderId ?? "默认"}, model={model}, thinking={thinking}");

            for (var turn = 0; turn < _options.MaxTurns; turn++)
            {
                ct.ThrowIfCancellationRequested();

                // 自动压缩: 上下文占用达到阈值(默认 85%)时, 发起请求前先压缩较早历史
                if (_options.AutoCompactEnabled && _lastInputTokens > 0)
                {
                    var total = ResolveContextTokens(model, provider);
                    if (total > 0 && _lastInputTokens >= total * _options.AutoCompactThreshold)
                    {
                        if (await CompactConversationAsync(ct).ConfigureAwait(false))
                        {
                            OnEvent?.Invoke(new EngineContextCompacted());
                        }
                    }
                }

                var request = new ChatRequest
                {
                    Model = model,
                    System = system,
                    MaxTokens = 4096,
                    Temperature = 0.2,
                    Tools = _registry.ToSpecs(),
                    Thinking = thinking,
                    Messages = _conversation
                        .TakeLast(_options.MaxHistoryMessages)
                        .ToList()
                };

                var client = _llm.GetClient(_options.ProviderId);
                ChatCompletionResult? response = null;

                await foreach (var sse in client.StreamAsync(request, ct).ConfigureAwait(false))
                {
                    switch (sse.Kind)
                    {
                        case StreamEventKind.TextDelta:
                            OnEvent?.Invoke(new EngineTextDelta(sse.Text ?? ""));
                            break;
                        case StreamEventKind.ThinkingDelta:
                            OnEvent?.Invoke(new EngineThinkingDelta(sse.Thinking ?? ""));
                            break;
                        case StreamEventKind.Error:
                            var sErr = $"流式错误: {sse.Error}";
                            Log.Warn("LLM", sErr);
                            OnEvent?.Invoke(new EngineDone(null, sErr));
                            return $"模型调用失败: {sErr}";
                        case StreamEventKind.Done:
                            response = sse.Final;
                            break;
                    }
                }

                response ??= new ChatCompletionResult { IsError = true, Error = "未收到模型响应" };

                if (response.Usage is { InputTokens: > 0 } or { OutputTokens: > 0 })
                {
                    _lastInputTokens = response.Usage.InputTokens;
                    OnEvent?.Invoke(new EngineUsageRecorded(provider.Id, model, response.Usage));
                }

                if (response.IsError)
                {
                    Log.Warn("LLM", $"响应错误: {response.Error}");
                    OnEvent?.Invoke(new EngineDone(null, response.Error));
                    return $"模型调用失败: {response.Error}";
                }

                if (response.ToolCalls is { Count: > 0 })
                {
                    _conversation.Add(new ChatTurnMessage
                    {
                        Role = ChatMsgRole.Assistant,
                        Content = string.Empty,
                        ToolCalls = response.ToolCalls
                    });

                    foreach (var call in response.ToolCalls)
                    {
                        try
                        {
                            var result = await ExecuteToolAsync(call, ct);
                            _conversation.Add(new ChatTurnMessage
                            {
                                Role = ChatMsgRole.Tool,
                                Content = result,
                                ToolCallId = call.Id
                            });
                        }
                        catch (OperationCanceledException)
                        {
                            // 取消时补齐占位结果并闭环事件, 保持 tool_calls/tool 配对完整, 便于后续继续输出
                            OnEvent?.Invoke(new EngineToolFinished(call.Id, call.Name,
                                ToolResult.Error("用户中断了此工具调用。")));
                            _conversation.Add(new ChatTurnMessage
                            {
                                Role = ChatMsgRole.Tool,
                                Content = "用户中断了此工具调用。",
                                ToolCallId = call.Id
                            });
                            throw;
                        }
                    }

                    continue;
                }

                var content = response.Content ?? string.Empty;
                _conversation.Add(new ChatTurnMessage
                {
                    Role = ChatMsgRole.Assistant,
                    Content = content
                });

                OnEvent?.Invoke(new EngineDone(content, null));
                return content;
            }

            var stopMsg = $"工具迭代超过 {_options.MaxTurns} 轮, 已停止。";
            OnEvent?.Invoke(new EngineDone(null, stopMsg));
            return stopMsg;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var msg = $"发生错误: {ex.Message}";
            OnEvent?.Invoke(new EngineDone(null, msg));
            return msg;
        }
    }

    private async Task<string> ExecuteToolAsync(ToolCallData call, CancellationToken ct)
    {
        OnEvent?.Invoke(new EngineToolStarted(call.Id, call.Name, call.Arguments));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (!_registry.TryGet(call.Name, out var tool))
        {
            var err = $"工具不存在: {call.Name}";
            Log.Warn("Engine", err);
            OnEvent?.Invoke(new EngineToolFinished(call.Id, call.Name, ToolResult.Error(err)));
            return $"工具调用失败: {err}";
        }

        if (tool.RequiresApproval && !_options.AutoApprove)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            OnEvent?.Invoke(new EngineApprovalRequested(call.Id, call.Name, call.Arguments, tcs));
            var approved = await tcs.Task.WaitAsync(ct);
            if (!approved)
            {
                var declined = $"用户拒绝了工具调用 {call.Name}。请向用户说明并询问替代方案。";
                OnEvent?.Invoke(new EngineToolFinished(call.Id, call.Name, ToolResult.Error(declined)));
                return declined;
            }
        }

        ToolResult result;
        try
        {
            JsonElement args;
            try
            {
                args = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments).RootElement.Clone();
            }
            catch (JsonException)
            {
                args = LlmJson.ParseArgs(call.Arguments);
            }

            var context = new ToolContext
            {
                WorkspaceRoot = _workspaceRoot,
                IsPlanMode = _options.IsPlanMode,
                OnToolOutput = line => OnEvent?.Invoke(new EngineToolOutput(call.Id, call.Name, line))
            };

            result = await tool.ExecuteAsync(args, context, ct);

            if (result.IsError)
            {
                Log.Warn("Engine", $"工具 {call.Name} 失败({sw.ElapsedMilliseconds}ms): {TruncateOneLine(result.Content)}");
            }
            else
            {
                Log.Info("Engine", $"工具 {call.Name} 完成 ({sw.ElapsedMilliseconds}ms)");
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("Engine", $"工具 {call.Name} 被用户中断");
            throw; // 用户终止需要立即上抛
        }
        catch (Exception ex)
        {
            Log.Error("Engine", ex, $"工具 {call.Name} 执行异常");
            result = ToolResult.Error($"工具执行异常: {ex.Message}");
        }

        OnEvent?.Invoke(new EngineToolFinished(call.Id, call.Name, result));
        return result.Content;

        static string TruncateOneLine(string s)
        {
            var line = s.Replace('\n', ' ').Replace('\r', ' ');
            return line.Length <= 160 ? line : line[..160] + "...";
        }
    }

    /// <summary>解析实际生效的思考等级: 自动档位解析为当前模型配置的最大思考等级。</summary>
    private ThinkingLevel ResolveThinking(string model, string? providerId)
    {
        if (_options.Thinking is not ThinkingLevel.Auto)
        {
            return _options.Thinking;
        }

        return _llm.GetProvider(providerId)?.GetMaxThinking(model) ?? ThinkingLevel.Max;
    }

    private string BuildSystemPrompt(ThinkingLevel thinking)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(_personaText))
        {
            parts.Add(_personaText);
        }

        var roster = RosterBuilder.Build(_agents, _personas, _templates,
            AgentConfigService.LoadUserFile().Rules, _git,
            rosterEntries: _rosterEntries,
            enabled: true,
            planMode: _options.IsPlanMode);
        if (!string.IsNullOrWhiteSpace(roster))
        {
            parts.Add(roster);
        }

        if (!string.IsNullOrWhiteSpace(_options.SystemExtra))
        {
            parts.Add(_options.SystemExtra);
        }

        var directives = new List<string>
        {
            ThinkingLevels.Directive(thinking)
        };

        if (_options.IsPlanMode)
        {
            directives.Add("当前模式: Plan(计划)。分析需求、拆解任务、制定方案, 但不要直接执行代码修改。");
        }
        else
        {
            directives.Add("当前模式: Build(构建)。直接执行任务、编写代码、完成目标。");
        }

        if (!string.IsNullOrWhiteSpace(_options.WorkDir))
        {
            directives.Add($"工作目录: {_options.WorkDir}");
        }

        if (directives.Count > 0)
        {
            parts.Add(string.Join("\n", directives));
        }

        return parts.Count == 0
            ? "你是 AI-Shikikan 的指挥官, 负责分析需求、调用工具与子代理完成任务。"
            : string.Join("\n\n", parts);
    }
}
