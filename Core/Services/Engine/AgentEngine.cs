using System.Text.Json;
using AIShikikan.Core.Models;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Git;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Personas;
using AIShikikan.Core.Services.Templates;
using AIShikikan.Core.Services.Tools;

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

    public void ClearConversation() => _conversation.Clear();

    public async Task<string> RunTurnAsync(string userMessage, CancellationToken ct = default)
    {
        // 连续用户消息合并为一条(继续输出场景), 避免部分 API 要求严格角色交替
        var last = _conversation.Count > 0 ? _conversation[^1] : null;
        if (last is { Role: ChatMsgRole.User })
        {
            last.Content += $"\n\n{userMessage}";
        }
        else
        {
            _conversation.Add(new ChatTurnMessage { Role = ChatMsgRole.User, Content = userMessage });
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

            for (var turn = 0; turn < _options.MaxTurns; turn++)
            {
                ct.ThrowIfCancellationRequested();

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
                    OnEvent?.Invoke(new EngineUsageRecorded(provider.Id, model, response.Usage));
                }

                if (response.IsError)
                {
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

        if (!_registry.TryGet(call.Name, out var tool))
        {
            var err = $"工具不存在: {call.Name}";
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
                OnToolOutput = line => OnEvent?.Invoke(new EngineToolOutput(call.Id, call.Name, line))
            };

            result = await tool.ExecuteAsync(args, context, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // 用户终止需要立即上抛
        }
        catch (Exception ex)
        {
            result = ToolResult.Error($"工具执行异常: {ex.Message}");
        }

        OnEvent?.Invoke(new EngineToolFinished(call.Id, call.Name, result));
        return result.Content;
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
            enabled: true);
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
