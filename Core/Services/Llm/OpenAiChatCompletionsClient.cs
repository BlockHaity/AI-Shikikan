using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.AI.OpenAI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Net.Http.Headers;
using AIShikikan.Core.Logging;

namespace AIShikikan.Core.Services.Llm;

public class OpenAiChatCompletionsClient : ChatCompletionsClientBase
{
    private static readonly HttpClient s_http = new()
    {
        // 长任务(子代理)下流式响应可能持续很久
        Timeout = TimeSpan.FromMinutes(30)
    };

    /// <summary>流空闲阈值: 两次 <c>data:</c> 之间超过该时长仍无新数据即判定为挂死。
    /// ⚠ 与上面的总请求超时语义不同(那个管"整个请求最多多久", 这个管"两次事件之间隔多久"):
    /// 深度思考 + 工具调用的长回答总时长可能远超该阈值, 但只要还在持续吐数据就不该中断;
    /// 反之上游断流/代理挂起时总超时迟迟不到, 只能干等 —— 空闲检测补的就是这一段。</summary>
    private static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// 已知不接受 <c>stream_options.include_usage</c> 的端点(键为 base_url 规范化后的绝对地址)。
    /// 老版 vLLM / 严格网关会因未知字段直接 400, 命中后本进程内不再附带该字段, 避免每次请求都吃一次重试。
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> NoStreamUsageEndpoints =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"未知/不支持的请求参数"类错误措辞关键词(配合字段名一起判定降级)。</summary>
    private static readonly string[] UnknownParamHints =
    [
        "unknown", "unrecognized", "unsupported", "not supported", "not permitted",
        "invalid", "unexpected", "additional", "extra", "未知", "不支持", "无效", "未定义"
    ];

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

    /// <summary>把思考等级映射为 OpenAI reasoning_effort; Off/Auto 或非推理模型返回 null(不下发参数)。
    /// ⚠ High/XHigh/Max 三档都塌缩为 High: OpenAI 公开的 reasoning_effort 取值只有 low/medium/high,
    /// SDK 的 ChatReasoningEffortLevel 枚举里也没有 xhigh 成员, 而把 XHigh/Max 近似映射成别的值
    /// 会让不支持的模型直接 400 —— 代价远大于"这几档在网络层区分不出来"本身, 因此保持塌缩。</summary>
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

    /// <summary>原生 SSE 路径下发的 reasoning_effort 取值。
    /// ⚠ 与 <see cref="ToReasoningEffort"/> 同一个 API 能力边界: 只认 low/medium/high,
    /// High/XHigh/Max 都下发 "high", 所以 GUI 选"极高/满"与选"高"的网络行为完全一致。
    /// 差异只体现在注入系统提示词的思考深度指令上(见 ThinkingLevels.Directive)。</summary>
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
        // 空闲超时需要主动掐断底层读取(StreamReader 的异步读无法从外部取消),
        // 而用户取消必须原样透传给上层, 因此用内部可取消 CTS 把两者分开:
        // 超时 → streamCts.Cancel() 让卡在 ReadLineAsync 的那次读立刻以取消收场;
        // 用户取消 → ct.ThrowIfCancellationRequested() 抛 OperationCanceledException 走正常取消路径。
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        using var resp = await SendStreamingAsync(request, streamCts.Token).ConfigureAwait(false);

        var text = new StringBuilder();
        var toolCalls = new Dictionary<int, ToolCallData>();
        var usage = new ChatUsage();
        string finishReason = string.Empty;

        using var stream = await resp.Content.ReadAsStreamAsync(streamCts.Token);
        using var reader = new StreamReader(stream);

        await foreach (var json in ReadSseEventsAsync(reader, streamCts, ct).ConfigureAwait(false))
        {
            if (json.TryGetProperty("error", out var errEl))
            {
                throw new HttpRequestException(
                    $"LLM 返回错误: {(errEl.ValueKind == JsonValueKind.String ? errEl.GetString() : errEl.GetRawText())}");
            }

            // 用量块: 官方在 [DONE] 之前追加一个 choices 为空的 usage 块,
            // 因此这里必须先读 usage 再看 choices, 空 choices 走 continue 不会丢用量
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
                    // 少数端点不下发 index: 不能一律用 toolCalls.Count 兜底, 同一 chunk 内多个无 index 的
                    // tool_call 会取到同一个 Count → 挤进同一条记录, 参数互相串接, 最终只发得出一个工具调用
                    var idx = tc.TryGetProperty("index", out var iEl) && iEl.TryGetInt32(out var explicitIdx)
                        ? explicitIdx
                        : NextFreeToolSlot(toolCalls);
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

    /// <summary>为缺 index 的 tool_call 找一个还没被占用的槽位: 从当前条数起步递增, 保证同一 chunk 内互不撞键。</summary>
    private static int NextFreeToolSlot(Dictionary<int, ToolCallData> toolCalls)
    {
        var slot = toolCalls.Count;
        while (toolCalls.ContainsKey(slot)) slot++;
        return slot;
    }

    /// <summary>
    /// 把 SSE 行流归一化为「一个事件 = 一个已解析的 JSON」。两个现实约束决定了实现方式:
    /// 1) SSE 规范允许一个事件由多行 <c>data:</c> 拼成(用 <c>\n</c> 连接), 逐行独立解析会直接 JsonException,
    ///    整段内容被静默丢弃(旧实现就是这个行为);
    /// 2) 但也有端点不插空行分隔事件, 若无条件"攒到空行再拼", 整条流会黏成一大坨解析失败。
    /// 因此这里采用"能独立解析就立刻产出, 否则先缓存等下一行拼接"——两种流都覆盖, 代价只是每行多一次解析尝试。
    /// <c>[DONE]</c> 不是 JSON, 单独逐行识别(它不会被拆行)。
    /// </summary>
    private static async IAsyncEnumerable<JsonElement> ReadSseEventsAsync(
        TextReader reader,
        CancellationTokenSource streamCts,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var pending = new StringBuilder();

        while (await ReadLineWithIdleTimeoutAsync(reader, streamCts, ct).ConfigureAwait(false) is { } line)
        {
            // 空行 = 事件边界: 冲刷仍然拼不完整的残片
            if (line.Length == 0)
            {
                if (pending.Length > 0 && TryParseJson(pending.ToString(), out var atBoundary))
                {
                    yield return atBoundary;
                }

                pending.Clear();
                continue;
            }

            // 注释行(以 ':' 开头)与 event:/id:/retry: 等字段本项目不使用
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var part = line[5..].Trim();
            if (part.Length == 0) continue;
            if (part is "[DONE]") yield break;

            if (pending.Length == 0)
            {
                if (TryParseJson(part, out var single))
                {
                    yield return single;
                }
                else
                {
                    pending.Append(part);
                }

                continue;
            }

            // 有残片: 先按"规范式多行"试着拼接, 再退一步假设残片本身是解析不出来的坏行
            if (TryParseJson(pending + "\n" + part, out var merged))
            {
                pending.Clear();
                yield return merged;
            }
            else if (TryParseJson(part, out var solo))
            {
                pending.Clear();
                yield return solo;
            }
            else
            {
                pending.Append('\n').Append(part);
            }
        }

        // 末尾没有空行收尾(部分端点直接 EOF)时冲刷残片
        if (pending.Length > 0 && TryParseJson(pending.ToString(), out var tail))
        {
            yield return tail;
        }
    }

    /// <summary>读一行, 超过 <see cref="StreamIdleTimeout"/> 没有任何新数据就抛 TimeoutException(由基类转成 Error 事件)。
    /// ⚠ 与总请求超时(HttpClient.Timeout = 30 分钟)语义不同: 那个管"整个请求最多多久",
    /// 长任务/深度思考下总时长本就可以很长; 这里管"两次事件之间隔多久", 用来兜住上游断流、
    /// 代理挂起这类会让读取永久悬挂的场景(技术债: 无首 token 超时 / 无流空闲超时)。
    /// 代价: 每行一个 Task.Delay(共享计时器队列, 与一次 JSON 解析同量级的开销)。</summary>
    private static async Task<string?> ReadLineWithIdleTimeoutAsync(
        TextReader reader, CancellationTokenSource streamCts, CancellationToken ct)
    {
        var pending = reader.ReadLineAsync(streamCts.Token).AsTask();
        var finished = await Task.WhenAny(pending, Task.Delay(StreamIdleTimeout, streamCts.Token)).ConfigureAwait(false);
        if (!ReferenceEquals(finished, pending))
        {
            // 掐断底层读取, 让这次 ReadLineAsync 立刻以取消收场(否则只能干等 30 分钟总超时);
            // 该读取的 Task 结束时状态是"已取消"而非"异常", 不会变成 UnobservedTaskException
            streamCts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"LLM 流空闲超过 {StreamIdleTimeout.TotalSeconds:0} 秒未返回数据, 已中断");
        }

        return await pending.ConfigureAwait(false);
    }

    private static bool TryParseJson(string text, out JsonElement json)
    {
        try
        {
            json = JsonDocument.Parse(text).RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            json = default;
            return false;
        }
    }

    /// <summary>发起流式请求并返回已成功的响应(失败抛 HttpRequestException)。
    /// 首次请求携带 <c>stream_options.include_usage</c>, 否则官方端点不会在流末尾返回 usage
    /// (没有 usage → AgentEngine 不发 EngineUsageRecorded → 用量统计/上下文环/自动压缩/成本全失效)。
    /// 若端点因"未知字段"拒绝该参数, 则记住该端点并去掉它重试一次。</summary>
    private async Task<HttpResponseMessage> SendStreamingAsync(ChatRequest request, CancellationToken ct)
    {
        var endpoint = _baseUrl.AbsoluteUri;
        var wantUsage = !NoStreamUsageEndpoints.ContainsKey(endpoint);
        var (resp, errBody) = await PostAsync(BuildRequestBody(request, wantUsage), ct).ConfigureAwait(false);

        if (wantUsage && !resp.IsSuccessStatusCode && IsUnknownParamError(resp.StatusCode, errBody))
        {
            resp.Dispose();
            NoStreamUsageEndpoints[endpoint] = 0;
            Log.Info("LLM", $"端点 {endpoint} 不接受 stream_options.include_usage, 已降级(本进程内该端点不再请求用量统计)");
            (resp, errBody) = await PostAsync(BuildRequestBody(request, includeUsage: false), ct).ConfigureAwait(false);
        }

        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"LLM 请求失败 ({(int)resp.StatusCode}): {Truncate(errBody, 400)}");
        }

        return resp;
    }

    /// <summary>POST chat/completions; 失败时把错误体读出来(响应释放后仍可读), 便于判定与展示。</summary>
    private async Task<(HttpResponseMessage Response, string ErrorBody)> PostAsync(JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUrl, "chat/completions"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        var resp = await s_http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var errBody = resp.IsSuccessStatusCode ? string.Empty : await resp.Content.ReadAsStringAsync(ct);
        return (resp, errBody);
    }

    /// <summary>判断响应是否为"未知/不支持的请求参数"所致的失败(用于 stream_options 降级)。
    /// 只认 400 / 422(参数级拒绝), 且错误体需同时提到字段名与未知参数类措辞;
    /// 401/403(鉴权失败)、404、429 等一律不降级, 照常抛出。</summary>
    private static bool IsUnknownParamError(HttpStatusCode status, string errorBody)
    {
        if (status is not (HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity))
        {
            return false;
        }

        if (string.IsNullOrEmpty(errorBody)) return false;

        if (!errorBody.Contains("stream_options", StringComparison.OrdinalIgnoreCase)
            && !errorBody.Contains("include_usage", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return UnknownParamHints.Any(h => errorBody.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    private JsonObject BuildRequestBody(ChatRequest request, bool includeUsage)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = BuildMessagesArray(request),
            ["stream"] = true
        };

        // 请求流末尾返回 usage; 部分严格端点不支持, 由 SendStreamingAsync 降级重试
        if (includeUsage)
        {
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

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
                    if (m.Images is { Count: > 0 })
                    {
                        arr.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = BuildUserContentArray(m)
                        });
                    }
                    else
                    {
                        AddMsg("user", m.Content);
                    }

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

    /// <summary>带图片的用户消息 content: 数组形式 [{image_url: dataURL...}, {text}] (图片在前, 与官方多模态示例一致)。</summary>
    private static JsonArray BuildUserContentArray(ChatTurnMessage m)
    {
        var parts = new JsonArray();
        foreach (var img in m.Images!)
        {
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = $"data:{img.MimeType};base64,{img.Base64Data}"
                }
            });
        }

        if (!string.IsNullOrEmpty(m.Content))
        {
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
        }

        return parts;
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
                    if (m.Images is { Count: > 0 })
                    {
                        var parts = new List<ChatMessageContentPart>();
                        foreach (var img in m.Images)
                        {
                            parts.Add(ChatMessageContentPart.CreateImagePart(
                                BinaryData.FromBytes(Convert.FromBase64String(img.Base64Data)), img.MimeType));
                        }

                        if (!string.IsNullOrEmpty(m.Content))
                        {
                            parts.Add(ChatMessageContentPart.CreateTextPart(m.Content));
                        }

                        list.Add(ChatMessage.CreateUserMessage(parts));
                    }
                    else
                    {
                        list.Add(ChatMessage.CreateUserMessage(m.Content));
                    }

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
