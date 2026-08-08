using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCommander.Core.Services.Llm;

public class OpenAiChatCompletionsClient : IChatCompletionsClient
{
    private readonly string _providerId;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public OpenAiChatCompletionsClient(ProviderConfig config)
    {
        _providerId = config.Id;
        var baseUrl = config.BaseUrl.TrimEnd('/');
        _http.BaseAddress = new Uri(baseUrl);
        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }
    }

    public string ProviderId => _providerId;

    public async Task<ChatCompletionResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        ChatCompletionResult? final = null;
        var text = new StringBuilder();
        var toolCalls = new List<ToolCallData>();
        string? error = null;

        await foreach (var e in StreamAsync(request, ct))
        {
            switch (e.Kind)
            {
                case StreamEventKind.TextDelta:
                    text.Append(e.Text);
                    break;
                case StreamEventKind.ToolCallStarted:
                    toolCalls.Add(e.ToolCall!);
                    break;
                case StreamEventKind.ToolCallCompleted:
                    if (e.ToolCall is { } call)
                    {
                        var idx = toolCalls.FindIndex(t => t.Id == call.Id);
                        if (idx >= 0)
                        {
                            toolCalls[idx] = call;
                        }
                        else
                        {
                            toolCalls.Add(call);
                        }
                    }

                    break;
                case StreamEventKind.Done when e.Final is not null:
                    final = e.Final;
                    break;
                case StreamEventKind.Error:
                    error = e.Error;
                    break;
            }
        }

        if (final is not null)
        {
            return final;
        }

        return error is not null
            ? new ChatCompletionResult { IsError = true, Error = error }
            : (text.Length > 0 || toolCalls.Count > 0
                ? new ChatCompletionResult { Content = text.Length > 0 ? string.Concat(text) : null, ToolCalls = toolCalls.Count > 0 ? toolCalls : null }
                : new ChatCompletionResult { IsError = true, Error = "未收到任何模型输出，请检查网络与配置" });
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildBody(request, stream: true);
        using var payloadContent = new StringContent(body, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, string.Empty) { Content = payloadContent };

        using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        using var resp = await LlmJson.ReadResponseAsync(response, ct);

        var stream = await response.Content.ReadAsStreamAsync(ct);

        var text = new StringBuilder();
        var toolCallsById = new Dictionary<int, ToolCallData>();
        var usage = new ChatUsage();

        await foreach (var data in SseParser.ReadDataAsync(stream, ct))
        {
            if (data == "[DONE]")
            {
                break;
            }

            using var json = JsonDocument.Parse(data);
            var root = json.RootElement;

            if (root.TryGetProperty("error", out var err))
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "未知错误";
                yield return new ChatStreamEvent { Kind = StreamEventKind.Error, Error = msg };
                yield break;
            }

            if (root.TryGetProperty("usage", out var usageEl))
            {
                if (usageEl.TryGetProperty("prompt_tokens", out var p)) usage.InputTokens = p.GetInt32();
                if (usageEl.TryGetProperty("completion_tokens", out var co)) usage.OutputTokens = co.GetInt32();
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                continue;
            }

            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var chunk = c.GetString();
                if (!string.IsNullOrEmpty(chunk))
                {
                    text.Append(chunk);
                    yield return new ChatStreamEvent { Kind = StreamEventKind.TextDelta, Text = chunk };
                }
            }

            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in toolCalls.EnumerateArray())
                {
                    var index = call.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
                    if (!toolCallsById.TryGetValue(index, out var existing))
                    {
                        existing = new ToolCallData();
                        toolCallsById[index] = existing;
                        yield return new ChatStreamEvent { Kind = StreamEventKind.ToolCallStarted, ToolCall = existing };
                    }

                    if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && existing.Id.Length == 0)
                    {
                        existing.Id = id.GetString() ?? string.Empty;
                    }

                    if (call.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                    {
                        if (fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                        {
                            existing.Name += name.GetString();
                        }

                        if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                        {
                            existing.Arguments += args.GetString();
                        }
                    }
                }
            }
        }

        var completed = toolCallsById.Values.Where(t => t.Name.Length > 0).ToList();
        foreach (var call in completed)
        {
            yield return new ChatStreamEvent { Kind = StreamEventKind.ToolCallCompleted, ToolCall = call };
        }

        yield return new ChatStreamEvent
        {
            Kind = StreamEventKind.Done,
            Final = new ChatCompletionResult
            {
                Content = text.Length > 0 ? text.ToString() : null,
                ToolCalls = completed.Count > 0 ? completed : null,
                Usage = usage
            }
        };
    }

    private static string BuildBody(ChatRequest request, bool stream)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(request.System))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.System });
        }

        foreach (var m in request.Messages)
        {
            var obj = new JsonObject
            {
                ["role"] = ToApiRole(m.Role),
                ["content"] = m.Content
            };

            if (m.ToolCalls is { Count: > 0 })
            {
                var calls = new JsonArray();
                foreach (var call in m.ToolCalls)
                {
                    calls.Add(JsonNode.Parse(JsonSerializer.Serialize(LlmJson.BuildOpenAiToolCall(call))));
                }

                obj["tool_calls"] = calls;
            }

            if (m.Role == ChatMsgRole.Tool)
            {
                obj["tool_call_id"] = m.ToolCallId;
            }

            messages.Add(obj);
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["temperature"] = request.Temperature,
            ["stream"] = stream
        };

        if (request.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
            {
                var fn = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description
                };

                if (t.Parameters.ValueKind == JsonValueKind.Object)
                {
                    fn["parameters"] = JsonNode.Parse(t.Parameters.GetRawText());
                }

                tools.Add(new JsonObject { ["type"] = "function", ["function"] = fn });
            }

            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }

        body["max_completion_tokens"] = request.MaxTokens;
        return body.ToJsonString();
    }

    private static string ToApiRole(ChatMsgRole role) => role switch
    {
        ChatMsgRole.System => "system",
        ChatMsgRole.User => "user",
        ChatMsgRole.Assistant => "assistant",
        ChatMsgRole.Tool => "tool",
        _ => "user"
    };
}