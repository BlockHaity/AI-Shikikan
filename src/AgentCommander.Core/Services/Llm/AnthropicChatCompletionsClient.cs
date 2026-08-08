using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCommander.Core.Services.Llm;

public class AnthropicChatCompletionsClient : IChatCompletionsClient
{
    private const string ApiVersion = "2023-06-01";

    private readonly string _providerId;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public AnthropicChatCompletionsClient(ProviderConfig config)
    {
        _providerId = config.Id;
        var baseUrl = config.BaseUrl.TrimEnd('/');
        _http.BaseAddress = new Uri(baseUrl);
        _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            _http.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
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

        if (final is not null) return final;
        return error is not null
            ? new ChatCompletionResult { IsError = true, Error = error }
            : new ChatCompletionResult { IsError = true, Error = "未收到任何模型输出" };
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
        var toolArgs = new StringBuilder();
        ToolCallData? activeTool = null;
        var usage = new ChatUsage();
        string stopReason = string.Empty;

        await foreach (var data in SseParser.ReadDataAsync(stream, ct))
        {
            using var json = JsonDocument.Parse(data);
            var root = json.RootElement;

            if (root.TryGetProperty("error", out var err))
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "未知错误";
                yield return new ChatStreamEvent { Kind = StreamEventKind.Error, Error = msg };
                yield break;
            }

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : string.Empty;
            switch (type)
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("usage", out var usage0))
                    {
                        if (usage0.TryGetProperty("input_tokens", out var it)) usage.InputTokens = it.GetInt32();
                    }

                    break;

                case "content_block_start":
                    if (root.TryGetProperty("content_block", out var block))
                    {
                        var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : string.Empty;
                        if (blockType == "tool_use")
                        {
                            activeTool = new ToolCallData
                            {
                                Id = block.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                                Name = block.TryGetProperty("name", out var nm) ? nm.GetString() ?? string.Empty : string.Empty
                            };
                            toolArgs.Clear();
                            yield return new ChatStreamEvent { Kind = StreamEventKind.ToolCallStarted, ToolCall = activeTool };
                        }
                    }

                    break;

                case "content_block_delta":
                    if (root.TryGetProperty("delta", out var delta))
                    {
                        var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : string.Empty;
                        switch (deltaType)
                        {
                            case "text_delta":
                                var chunk = delta.TryGetProperty("text", out var txt) ? txt.GetString() : null;
                                if (!string.IsNullOrEmpty(chunk))
                                {
                                    text.Append(chunk);
                                    yield return new ChatStreamEvent { Kind = StreamEventKind.TextDelta, Text = chunk };
                                }

                                break;

                            case "input_json_delta":
                                if (activeTool is not null &&
                                    delta.TryGetProperty("partial_json", out var pj) && pj.ValueKind == JsonValueKind.String)
                                {
                                    toolArgs.Append(pj.GetString());
                                }

                                break;
                        }
                    }

                    break;

                case "content_block_stop":
                    if (activeTool is not null)
                    {
                        activeTool.Arguments = toolArgs.ToString();
                        yield return new ChatStreamEvent { Kind = StreamEventKind.ToolCallCompleted, ToolCall = activeTool };
                        activeTool = null;
                    }

                    break;

                case "message_delta":
                    if (root.TryGetProperty("delta", out var mdelta) &&
                        mdelta.TryGetProperty("stop_reason", out var sr))
                    {
                        stopReason = sr.GetString() ?? string.Empty;
                    }

                    if (root.TryGetProperty("usage", out var musage) &&
                        musage.TryGetProperty("output_tokens", out var ot))
                    {
                        usage.OutputTokens = ot.GetInt32();
                    }

                    break;

                case "message_stop":
                    yield return new ChatStreamEvent
                    {
                        Kind = StreamEventKind.Done,
                        Final = new ChatCompletionResult
                        {
                            Content = text.Length > 0 ? text.ToString() : null,
                            Usage = usage,
                            FinishReason = stopReason
                        }
                    };
                    yield break;
            }
        }
    }

    private static string BuildBody(ChatRequest request, bool stream)
    {
        var messages = new JsonArray();

        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case ChatMsgRole.Assistant when m.ToolCalls is { Count: > 0 }:
                {
                    var blocks = new JsonArray();
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = m.Content
                    });
                    foreach (var call in m.ToolCalls)
                    {
                        blocks.Add(JsonNode.Parse(JsonSerializer.Serialize(LlmJson.BuildAnthropicToolUse(call))));
                    }

                    messages.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = blocks
                    });
                    break;
                }

                case ChatMsgRole.Tool:
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = m.ToolCallId,
                                ["content"] = m.Content
                            }
                        }
                    });
                    break;

                default:
                    messages.Add(new JsonObject
                    {
                        ["role"] = m.Role == ChatMsgRole.Assistant ? "assistant" : "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "text", ["text"] = m.Content }
                        }
                    });
                    break;
            }
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
            ["stream"] = stream
        };

        if (!string.IsNullOrEmpty(request.System))
        {
            body["system"] = request.System;
        }

        if (request.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
            {
                var tool = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description
                };

                if (t.Parameters.ValueKind == JsonValueKind.Object)
                {
                    tool["input_schema"] = JsonNode.Parse(t.Parameters.GetRawText());
                }

                tools.Add(tool);
            }

            body["tools"] = tools;
        }

        return body.ToJsonString();
    }
}