using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace AIShikikan.Core.Services.Llm;

public class OpenAiChatCompletionsClient : IChatCompletionsClient
{
    private readonly string _providerId;
    private readonly bool _isAzure;
    private readonly Uri _baseUrl;
    private readonly ApiKeyCredential _credential;

    public OpenAiChatCompletionsClient(ProviderConfig config)
    {
        _providerId = config.Id;
        _isAzure = config.BaseUrl.Contains("azure.com", StringComparison.OrdinalIgnoreCase);
        _baseUrl = new Uri(_isAzure ? config.BaseUrl.TrimEnd('/') : (config.BaseUrl.TrimEnd('/') + "/"));
        _credential = new ApiKeyCredential(config.ApiKey);
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
        var messages = BuildMessages(request);
        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = request.MaxTokens,
            Temperature = (float)request.Temperature,
            ToolChoice = ChatToolChoice.CreateAutoChoice()
        };

        if (request.Tools is { Count: > 0 })
        {
            foreach (var t in request.Tools)
            {
                options.Tools.Add(ChatTool.CreateFunctionTool(
                    t.Name, t.Description,
                    t.Parameters.ValueKind == JsonValueKind.Object
                        ? BinaryData.FromString(t.Parameters.GetRawText())
                        : BinaryData.FromString("{}"),
                    null));
            }
        }

        var text = new StringBuilder();
        var toolCalls = new Dictionary<int, ToolCallData>();
        var usage = new ChatUsage();
        string finishReason = string.Empty;

        var chat = CreateChatClient(request.Model);
        await foreach (var update in chat.CompleteChatStreamingAsync(messages, options, ct))
        {
            if (update.ContentUpdate is { Count: > 0 })
            {
                foreach (var part in update.ContentUpdate)
                {
                    if (part.Kind == ChatMessageContentPartKind.Text && !string.IsNullOrEmpty(part.Text))
                    {
                        text.Append(part.Text);
                        yield return new ChatStreamEvent { Kind = StreamEventKind.TextDelta, Text = part.Text };
                    }
                }
            }

            foreach (var tc in update.ToolCallUpdates)
            {
                if (!toolCalls.TryGetValue(tc.Index, out var existing))
                {
                    existing = new ToolCallData();
                    toolCalls[tc.Index] = existing;
                    yield return new ChatStreamEvent { Kind = StreamEventKind.ToolCallStarted, ToolCall = existing };
                }

                if (!string.IsNullOrEmpty(tc.ToolCallId)) existing.Id = tc.ToolCallId;
                if (!string.IsNullOrEmpty(tc.FunctionName)) existing.Name += tc.FunctionName;
                if (tc.FunctionArgumentsUpdate is { } args)
                {
                    existing.Arguments += args.ToString();
                }
            }

            if (update.Usage is { } u)
            {
                usage.InputTokens = u.InputTokenCount;
                usage.OutputTokens = u.OutputTokenCount;
            }

            if (update.FinishReason is { } fr && string.IsNullOrEmpty(finishReason))
            {
                finishReason = fr switch
                {
                    ChatFinishReason.ToolCalls => "tool_calls",
                    ChatFinishReason.Length => "length",
                    ChatFinishReason.ContentFilter => "content_filter",
                    _ => "stop"
                };
            }
        }

        yield return new ChatStreamEvent
        {
            Kind = StreamEventKind.Done,
            Final = new ChatCompletionResult
            {
                Content = text.Length > 0 ? text.ToString() : null,
                ToolCalls = toolCalls.Values.Where(t => t.Name.Length > 0).ToList(),
                Usage = usage,
                FinishReason = finishReason
            }
        };
    }

    private ChatClient CreateChatClient(string model) => _isAzure
        ? new AzureOpenAIClient(_baseUrl, _credential).GetChatClient(model)
        : new OpenAIClient(_credential, new OpenAIClientOptions { Endpoint = _baseUrl }).GetChatClient(model);

    private static List<ChatMessage> BuildMessages(ChatRequest request)
    {
        var list = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(request.System))
        {
            list.Add(ChatMessage.CreateSystemMessage(request.System));
        }

        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case ChatMsgRole.System:
                    list.Add(ChatMessage.CreateSystemMessage(m.Content));
                    break;

                case ChatMsgRole.User:
                    list.Add(ChatMessage.CreateUserMessage(m.Content));
                    break;

                case ChatMsgRole.Assistant when m.ToolCalls is { Count: > 0 }:
                    var calls = new List<ChatToolCall>(m.ToolCalls.Count);
                    foreach (var call in m.ToolCalls)
                    {
                        calls.Add(ChatToolCall.CreateFunctionToolCall(
                            call.Id, call.Name, BinaryData.FromString(LlmJson.ToNode(call.Arguments)?.ToJsonString() ?? "{}")));
                    }

                    list.Add(ChatMessage.CreateAssistantMessage(calls));
                    break;

                case ChatMsgRole.Assistant:
                    list.Add(ChatMessage.CreateAssistantMessage(m.Content));
                    break;

                case ChatMsgRole.Tool:
                    list.Add(ChatMessage.CreateToolMessage(m.ToolCallId ?? string.Empty, m.Content));
                    break;
            }
        }

        return list;
    }
}