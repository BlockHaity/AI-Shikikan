using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.AI.OpenAI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Net.Http.Headers;

namespace AIShikikan.Core.Services.Llm;

public class OpenAiChatCompletionsClient : ChatCompletionsClientBase
{
    private static readonly HttpClient s_http = new()
    {
        // 长任务(子代理)下流式响应可能持续很久
        Timeout = TimeSpan.FromMinutes(30)
    };

    private readonly string _providerId;
    private readonly bool _isAzure;
    private readonly Uri _baseUrl;
    private readonly ApiKeyCredential _credential;
    private readonly string _apiKey;

    public OpenAiChatCompletionsClient(ProviderConfig config)
    {
        _providerId = config.Id;
        _isAzure = config.BaseUrl.Contains("azure.com", StringComparison.OrdinalIgnoreCase);
        _baseUrl = new Uri(_isAzure ? config.BaseUrl.TrimEnd('/') : (config.BaseUrl.TrimEnd('/') + "/"));
        _credential = new ApiKeyCredential(config.ApiKey);
        _apiKey = config.ApiKey;
    }

    public override string ProviderId => _providerId;

    protected override IAsyncEnumerable<ChatStreamEvent> EmitAsync(
        ChatRequest request, CancellationToken ct) => EmitCore(request, ct);

    private async IAsyncEnumerable<ChatStreamEvent> EmitCore(
        ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_isAzure)
        {
            await foreach (var e in EmitViaSdk(request, ct))
            {
                yield return e;
            }
        }
        else
        {
            // 原生 SSE 解析: 官方 SDK 会丢弃第三方端点(DeepSeek/OpenRouter 等)的
            // reasoning_content 字段, 导致思考增量丢失无法展示。
            await foreach (var e in EmitViaHttp(request, ct))
            {
                yield return e;
            }
        }
    }

    #region SDK 路径(Azure)

    private async IAsyncEnumerable<ChatStreamEvent> EmitViaSdk(
        ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = BuildMessages(request);
        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = request.MaxTokens,
            Temperature = (float)request.Temperature,
            ToolChoice = ChatToolChoice.CreateAutoChoice()
        };

        // 思考深度 → reasoning_effort: 仅对具备推理能力的模型且在非自动档位时发送,
        // 避免向不支持该参数的普通模型下发导致请求失败。
        var effort = ToReasoningEffort(request);
        if (effort is not null)
        {
            options.ReasoningEffortLevel = effort.Value;
        }

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
                usage.CachedInputTokens = u.InputTokenDetails?.CachedTokenCount ?? 0;
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

        yield return FinalEvent(text, toolCalls.Values.Where(t => t.Name.Length > 0).ToList(), usage, finishReason);
    }

    private ChatClient CreateChatClient(string model) => _isAzure
        ? new AzureOpenAIClient(_baseUrl, _credential).GetChatClient(model)
        : new OpenAIClient(_credential, new OpenAIClientOptions { Endpoint = _baseUrl }).GetChatClient(model);

    /// <summary>把思考等级映射为 OpenAI reasoning_effort; Off/Auto 或非推理模型返回 null(不下发参数)。</summary>
    private static ChatReasoningEffortLevel? ToReasoningEffort(ChatRequest request)
    {
        if (request.Thinking is ThinkingLevel.Off or ThinkingLevel.Auto ||
            !ThinkingLevels.IsReasoningModel(request.Model))
        {
            return null;
        }

        return request.Thinking switch
        {
            ThinkingLevel.Low => ChatReasoningEffortLevel.Low,
            ThinkingLevel.Medium => ChatReasoningEffortLevel.Medium,
            _ => ChatReasoningEffortLevel.High
        };
    }

    /// <summary>是否按推理模型请求(影响参数命名与温度省略)。Off/Auto 不按推理请求。</summary>
    private static bool IsReasoningMode(ChatRequest request)
        => request.Thinking is not (ThinkingLevel.Off or ThinkingLevel.Auto)
           && ThinkingLevels.IsReasoningModel(request.Model);

    private static string EffortString(ChatRequest request) => request.Thinking switch
    {
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        _ => "high"
    };

    #endregion

    #region 原生 SSE 路径(OpenAI 兼容端点)

    private async IAsyncEnumerable<ChatStreamEvent> EmitViaHttp(
        ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildRequestBody(request);

        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUrl, "chat/completions"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await s_http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"LLM 请求失败 ({(int)resp.StatusCode}): {Truncate(errBody, 400)}");
        }

        var text = new StringBuilder();
        var toolCalls = new Dictionary<int, ToolCallData>();
        var usage = new ChatUsage();
        string finishReason = string.Empty;

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;

            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            if (payload is "[DONE]") break;

            JsonElement json;
            try
            {
                json = JsonDocument.Parse(payload).RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            if (json.TryGetProperty("error", out var errEl))
            {
                throw new HttpRequestException(
                    $"LLM 返回错误: {(errEl.ValueKind == JsonValueKind.String ? errEl.GetString() : errEl.GetRawText())}");
            }

            if (json.TryGetProperty("usage", out var u))
            {
                if (u.TryGetProperty("prompt_tokens", out var pt)) usage.InputTokens = pt.GetInt32();
                if (u.TryGetProperty("completion_tokens", out var ct2)) usage.OutputTokens = ct2.GetInt32();
                if (u.TryGetProperty("prompt_tokens_details", out var ptd)
                    && ptd.TryGetProperty("cached_tokens", out var cached))
                {
                    usage.CachedInputTokens = cached.GetInt32();
                }
            }

            if (!json.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                continue;
            }

            var choice = choices[0];

            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String
                && string.IsNullOrEmpty(finishReason))
            {
                finishReason = MapFinishReason(fr.GetString());
            }

            if (!choice.TryGetProperty("delta", out var delta)) continue;

            // 思考增量: DeepSeek reasoning_content / OpenRouter reasoning 等变体
            if (TryGetString(delta, "reasoning_content", out var rc) && rc.Length > 0)
            {
                yield return new ChatStreamEvent { Kind = StreamEventKind.ThinkingDelta, Thinking = rc };
            }
            else if (TryGetString(delta, "reasoning", out var rg) && rg.Length > 0)
            {
                yield return new ChatStreamEvent { Kind = StreamEventKind.ThinkingDelta, Thinking = rg };
            }

            if (TryGetString(delta, "content", out var content) && content.Length > 0)
            {
                text.Append(content);
                yield return new ChatStreamEvent { Kind = StreamEventKind.TextDelta, Text = content };
            }

            if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    var idx = tc.TryGetProperty("index", out var iEl) ? iEl.GetInt32() : toolCalls.Count;
                    if (!toolCalls.TryGetValue(idx, out var existing))
                    {
                        existing = new ToolCallData();
                        toolCalls[idx] = existing;
                        yield return new ChatStreamEvent { Kind = StreamEventKind.ToolCallStarted, ToolCall = existing };
                    }

                    if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        && idEl.GetString() is { Length: > 0 } id)
                    {
                        existing.Id = id;
                    }

                    if (tc.TryGetProperty("function", out var fn))
                    {
                        if (TryGetString(fn, "name", out var n) && n.Length > 0)
                        {
                            existing.Name += n;
                        }

                        if (TryGetString(fn, "arguments", out var args))
                        {
                            existing.Arguments += args;
                        }
                    }
                }
            }
        }

        yield return FinalEvent(text, toolCalls.Values.Where(t => t.Name.Length > 0).ToList(), usage, finishReason);
    }

    private JsonObject BuildRequestBody(ChatRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = BuildMessagesArray(request),
            ["stream"] = true
        };

        var reasoning = IsReasoningMode(request);
        if (reasoning)
        {
            body["reasoning_effort"] = EffortString(request);
            // 推理模型统一使用 max_completion_tokens 且不接受自定义温度
            body["max_completion_tokens"] = request.MaxTokens;
        }
        else
        {
            body["max_tokens"] = request.MaxTokens;
            body["temperature"] = request.Temperature;
        }

        if (request.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = t.Parameters.ValueKind == JsonValueKind.Object
                            ? JsonNode.Parse(t.Parameters.GetRawText()) ?? new JsonObject()
                            : new JsonObject()
                    }
                });
            }

            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }

        return body;
    }

    private static JsonArray BuildMessagesArray(ChatRequest request)
    {
        var arr = new JsonArray();

        void AddMsg(string role, string? content) => arr.Add(new JsonObject
        {
            ["role"] = role,
            ["content"] = content ?? string.Empty
        });

        if (!string.IsNullOrEmpty(request.System))
        {
            AddMsg("system", request.System);
        }

        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case ChatMsgRole.System:
                    AddMsg("system", m.Content);
                    break;

                case ChatMsgRole.User:
                    AddMsg("user", m.Content);
                    break;

                case ChatMsgRole.Assistant when m.ToolCalls is { Count: > 0 }:
                    var calls = new JsonArray();
                    foreach (var call in m.ToolCalls)
                    {
                        calls.Add(new JsonObject
                        {
                            ["id"] = call.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = call.Name,
                                ["arguments"] = call.Arguments
                            }
                        });
                    }

                    arr.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = string.IsNullOrEmpty(m.Content) ? null : m.Content,
                        ["tool_calls"] = calls
                    });
                    break;

                case ChatMsgRole.Assistant:
                    AddMsg("assistant", m.Content);
                    break;

                case ChatMsgRole.Tool:
                    arr.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = m.ToolCallId ?? string.Empty,
                        ["content"] = m.Content
                    });
                    break;
            }
        }

        return arr;
    }

    private static bool TryGetString(JsonElement el, string name, out string value)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            value = v.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string MapFinishReason(string? reason) => reason switch
    {
        "tool_calls" or "function_call" => "tool_calls",
        "length" => "length",
        "content_filter" => "content_filter",
        _ => "stop"
    };

    private static ChatStreamEvent FinalEvent(StringBuilder text, List<ToolCallData> tools, ChatUsage usage,
        string finishReason) => new()
    {
        Kind = StreamEventKind.Done,
        Final = new ChatCompletionResult
        {
            Content = text.Length > 0 ? text.ToString() : null,
            ToolCalls = tools,
            Usage = usage,
            FinishReason = finishReason
        }
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    #endregion

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
