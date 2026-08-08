using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using CommonTool = Anthropic.SDK.Common.Tool;
using Message = Anthropic.SDK.Messaging.Message;

namespace AgentCommander.Core.Services.Llm;

public class AnthropicChatCompletionsClient : IChatCompletionsClient
{
    private readonly string _providerId;
    private readonly AnthropicClient _client;

    public AnthropicChatCompletionsClient(ProviderConfig config)
    {
        _providerId = config.Id;

        var baseUrl = config.BaseUrl.TrimEnd('/');
        _client = new AnthropicClient(
            new APIAuthentication(config.ApiKey),
            new HttpClient { Timeout = TimeSpan.FromMinutes(10) });

        if (!baseUrl.Equals("https://api.anthropic.com", StringComparison.OrdinalIgnoreCase))
        {
            _client.ApiUrlFormat = $"{baseUrl}/{{0}}/{{1}}";
        }
    }

    public string ProviderId => _providerId;

    public async Task<ChatCompletionResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        ChatCompletionResult? final = null;
        string? error = null;

        await foreach (var e in StreamAsync(request, ct))
        {
            if (e.Kind == StreamEventKind.Done && e.Final is not null) final = e.Final;
            if (e.Kind == StreamEventKind.Error) error = e.Error;
        }

        if (final is not null)
        {
            return final;
        }

        return error is not null
            ? new ChatCompletionResult { IsError = true, Error = error }
            : new ChatCompletionResult { IsError = true, Error = "未收到任何模型输出，请检查网络与配置" };
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? error = null;
        var enumerator = EmitAsync(request, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool more;
                try
                {
                    more = await enumerator.MoveNextAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = $"{ex.GetType().Name}: {ex.Message}";
                    break;
                }

                if (!more)
                {
                    break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        if (error is not null)
        {
            yield return new ChatStreamEvent { Kind = StreamEventKind.Error, Error = error };
        }
    }

    private async IAsyncEnumerable<ChatStreamEvent> EmitAsync(
        ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var parameters = new MessageParameters
        {
            Model = request.Model,
            Messages = BuildMessages(request),
            MaxTokens = request.MaxTokens,
            Temperature = (decimal)request.Temperature,
            Stream = true,
            Tools = BuildTools(request.Tools),
            ToolChoice = request.Tools is { Count: > 0 } ? new ToolChoice { Type = ToolChoiceType.Auto } : null
        };

        if (!string.IsNullOrEmpty(request.System))
        {
            parameters.System = [new SystemMessage(request.System)];
        }

        var text = new StringBuilder();
        var toolCalls = new Dictionary<string, ToolCallData>();
        var emittedToolIds = new HashSet<string>();
        var usage = new ChatUsage();
        string stopReason = string.Empty;

        await foreach (var message in _client.Messages.StreamClaudeMessageAsync(parameters, ct))
        {
            if (message.ContentBlock is { Type: "tool_use" })
            {
                var started = new ToolCallData
                {
                    Id = message.ContentBlock.Id ?? string.Empty,
                    Name = message.ContentBlock.Name ?? string.Empty
                };
                toolCalls[started.Id] = started;
                yield return new ChatStreamEvent { Kind = StreamEventKind.ToolCallStarted, ToolCall = started };
            }

            if (!string.IsNullOrEmpty(message.Delta?.Text))
            {
                text.Append(message.Delta.Text);
                yield return new ChatStreamEvent { Kind = StreamEventKind.TextDelta, Text = message.Delta.Text };
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var f in message.ToolCalls)
                {
                    if (f.Id is null || !emittedToolIds.Add(f.Id))
                    {
                        continue;
                    }

                    var completed = new ToolCallData
                    {
                        Id = f.Id,
                        Name = f.Name ?? string.Empty,
                        Arguments = f.Arguments?.ToJsonString() ?? string.Empty
                    };
                    toolCalls[completed.Id] = completed;
                    yield return new ChatStreamEvent { Kind = StreamEventKind.ToolCallCompleted, ToolCall = completed };
                }
            }
            else if (message.Delta?.StopReason == "tool_use")
            {
                foreach (var pending in toolCalls.Values.Where(t => !emittedToolIds.Contains(t.Id)))
                {
                    emittedToolIds.Add(pending.Id);
                    yield return new ChatStreamEvent { Kind = StreamEventKind.ToolCallCompleted, ToolCall = pending };
                }
            }

            if (message.StreamStartMessage?.Usage is { } start)
            {
                usage.InputTokens = start.InputTokens;
            }

            if (message.Usage is { } u)
            {
                usage.OutputTokens = u.OutputTokens;
            }

            var deltaStopReason = message.Delta?.StopReason;
            if (!string.IsNullOrEmpty(deltaStopReason))
            {
                stopReason = deltaStopReason;
            }
            else if (!string.IsNullOrEmpty(message.StopReason))
            {
                stopReason = message.StopReason;
            }

            if (message.Type is "message_delta")
            {
                yield return new ChatStreamEvent
                {
                    Kind = StreamEventKind.Done,
                    Final = new ChatCompletionResult
                    {
                        Content = text.Length > 0 ? text.ToString() : null,
                        ToolCalls = toolCalls.Count > 0 ? toolCalls.Values.ToList() : null,
                        Usage = usage,
                        FinishReason = stopReason
                    }
                };
                yield break;
            }
        }
    }

    private static List<CommonTool>? BuildTools(List<ToolSpec>? specs)
    {
        if (specs is not { Count: > 0 })
        {
            return null;
        }

        var tools = new List<CommonTool>(specs.Count);
        foreach (var t in specs)
        {
            var parameters = t.Parameters.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(t.Parameters.GetRawText())
                : new JsonObject();
            tools.Add(new CommonTool(new Anthropic.SDK.Common.Function(t.Name, t.Description, parameters)));
        }

        return tools;
    }

    private static List<Message> BuildMessages(ChatRequest request)
    {
        var list = new List<Message>();
        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case ChatMsgRole.Assistant when m.ToolCalls is { Count: > 0 }:
                    var blocks = new List<ContentBase>();
                    if (!string.IsNullOrEmpty(m.Content))
                    {
                        blocks.Add(new TextContent { Text = m.Content });
                    }

                    foreach (var call in m.ToolCalls)
                    {
                        blocks.Add(new ToolUseContent
                        {
                            Id = call.Id,
                            Name = call.Name,
                            Input = LlmJson.ToNode(call.Arguments)
                        });
                    }

                    list.Add(new Message { Role = RoleType.Assistant, Content = blocks });
                    break;

                case ChatMsgRole.Tool:
                    list.Add(new Message
                    {
                        Role = RoleType.User,
                        Content =
                        [
                            new ToolResultContent
                            {
                                ToolUseId = m.ToolCallId ?? string.Empty,
                                Content = [new TextContent { Text = m.Content }]
                            }
                        ]
                    });
                    break;

                case ChatMsgRole.System:
                    list.Add(new Message { Role = RoleType.User, Content = [new TextContent { Text = m.Content }] });
                    break;

                default:
                    list.Add(new Message
                    {
                        Role = m.Role == ChatMsgRole.Assistant ? RoleType.Assistant : RoleType.User,
                        Content = [new TextContent { Text = m.Content }]
                    });
                    break;
            }
        }

        return list;
    }
}