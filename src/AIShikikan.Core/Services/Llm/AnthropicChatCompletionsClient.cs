using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using CommonTool = Anthropic.SDK.Common.Tool;
using Message = Anthropic.SDK.Messaging.Message;

namespace AIShikikan.Core.Services.Llm;

public class AnthropicChatCompletionsClient : ChatCompletionsClientBase
{
    /// <summary>流空闲阈值: 两次流式事件之间超过该时长仍无数据即判定为挂死。
    /// ⚠ 与 <c>HttpClient.Timeout</c>(10 分钟, 管"整个请求最多多久")语义不同: 这个管"两次事件之间隔多久"。
    /// 深度思考 + 工具调用的长回合总时长可以远超该阈值, 但只要还在持续吐事件就不该中断;
    /// 反之上游断流/代理挂起时总超时迟迟不到, 只能干等 —— 空闲检测补的就是这一段。</summary>
    private static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromSeconds(120);

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

    public override string ProviderId => _providerId;

    protected override IAsyncEnumerable<ChatStreamEvent> EmitAsync(
        ChatRequest request, CancellationToken ct) => EmitCore(request, ct);

    private async IAsyncEnumerable<ChatStreamEvent> EmitCore(
        ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // 注: 思考深度不通过 thinking 参数下发——Anthropic 扩展思考与工具调用互斥,
        // 而本引擎始终携带工具, 因此仅通过系统提示词指令体现思考深度。
        // ⚠ 因此 High/XHigh/Max 三档在 Anthropic 侧完全不可区分(连 reasoning_effort 都没有对应字段),
        // 与 OpenAI 侧"三档塌缩成 high"是同一个 API 能力边界, 不要在这里补映射。
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
        // 工具调用按产生顺序累积(Final.ToolCalls 直接取这份列表, 顺序即模型给出顺序)
        var toolCalls = new List<ToolCallData>();
        // 内部键 → 工具调用, 以及 tool_use id → 内部键。
        // 键用自增序号而不是直接用 tool_use id: id 为空时(异常响应/代理端点裁剪字段)所有工具调用
        // 会挤在同一个 "" 键上互相覆盖, 并行工具调用最后只发得出一个
        var toolCallIndex = new Dictionary<string, ToolCallData>(StringComparer.Ordinal);
        var keyByToolUseId = new Dictionary<string, string>(StringComparer.Ordinal);
        var keySeq = 0;

        // 已发过 ToolCallCompleted 的条目(ToolCallData 未重写 Equals, 集合按引用去重)
        var completedCalls = new HashSet<ToolCallData>();
        // 当前正在累积参数的 tool_use 块。Anthropic 的 content block 严格串行下发(前一块 stop 完才开下一块),
        // 因此"最近一个开着的 tool_use"就是 input_json_delta 该归属的对象; SDK 5.10 不暴露块 index。
        ToolCallData? openToolCall = null;
        var usage = new ChatUsage();
        string stopReason = string.Empty;
        var doneSent = false;

        // 找到/补建与该 tool_use 对应的内部条目; 已在则就地补名(起点事件与参数增量可能分两条到达)
        ToolCallData TrackToolCall(string? id, string name)
        {
            if (id is { Length: > 0 } && keyByToolUseId.TryGetValue(id, out var known)
                && toolCallIndex.TryGetValue(known, out var knownCall))
            {
                if (knownCall.Name.Length == 0 && name.Length > 0) knownCall.Name = name;
                return knownCall;
            }

            var key = "k" + keySeq++;
            var call = new ToolCallData { Id = id ?? string.Empty, Name = name };
            toolCallIndex[key] = call;
            toolCalls.Add(call);
            if (id is { Length: > 0 }) keyByToolUseId[id] = key;
            return call;
        }

        // 收尾: 此时所有 input_json_delta 都已到达, 参数已完整, 可以发 ToolCallCompleted。
        // 旧实现在 stop_reason 事件里发, 但那时同样没有任何可用的参数来源, 只能发空参数。
        List<ToolCallData> CollectCompleted()
        {
            var done = new List<ToolCallData>();
            foreach (var call in toolCalls)
            {
                if (completedCalls.Add(call)) done.Add(call);
            }

            return done;
        }

        ChatStreamEvent BuildDone() => new()
        {
            Kind = StreamEventKind.Done,
            Final = new ChatCompletionResult
            {
                Content = text.Length > 0 ? text.ToString() : null,
                ToolCalls = toolCalls.Count > 0 ? toolCalls.ToList() : null,
                Usage = usage,
                FinishReason = stopReason
            }
        };

        // 空闲超时需要主动掐断底层读取, 而用户取消必须原样透传, 故用内部可取消 CTS 区分两者
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 超时后 MoveNext 仍可能在飞行中, 此时对枚举器调 DisposeAsync 行为未定义(可能二次抛
        // OperationCanceledException 盖掉真实原因), 因此超时路径放弃 Dispose, 连接交给
        // HttpClient 自身的 10 分钟总超时回收
        var abandoned = false;
        var enumerator = _client.Messages.StreamClaudeMessageAsync(parameters, streamCts.Token)
            .GetAsyncEnumerator(streamCts.Token);
        try
        {
            while (true)
            {
                var pending = enumerator.MoveNextAsync().AsTask();
                var finished = await Task.WhenAny(pending, Task.Delay(StreamIdleTimeout, streamCts.Token))
                    .ConfigureAwait(false);
                if (!ReferenceEquals(finished, pending))
                {
                    abandoned = true;
                    streamCts.Cancel();
                    ct.ThrowIfCancellationRequested();
                    throw new TimeoutException(
                        $"LLM 流空闲超过 {StreamIdleTimeout.TotalSeconds:0} 秒未返回数据, 已中断");
                }

                if (!await pending.ConfigureAwait(false)) break;
                var message = enumerator.Current;

                if (message.ContentBlock is { Type: "tool_use" })
                {
                    var blockId = message.ContentBlock.Id;
                    var seen = blockId is { Length: > 0 } && keyByToolUseId.ContainsKey(blockId);
                    openToolCall = TrackToolCall(blockId, message.ContentBlock.Name ?? string.Empty);
                    if (!seen)
                    {
                        yield return new ChatStreamEvent { Kind = StreamEventKind.ToolCallStarted, ToolCall = openToolCall };
                    }
                }

                if (!string.IsNullOrEmpty(message.Delta?.Thinking))
                {
                    yield return new ChatStreamEvent { Kind = StreamEventKind.ThinkingDelta, Thinking = message.Delta.Thinking };
                }

                if (!string.IsNullOrEmpty(message.Delta?.Text))
                {
                    text.Append(message.Delta.Text);
                    yield return new ChatStreamEvent { Kind = StreamEventKind.TextDelta, Text = message.Delta.Text };
                }

                if (message.ToolCalls is { Count: > 0 })
                {
                    // 前向兼容: 当前 SDK(5.10)这条路恒为空(StreamMessage.ToolCalls 从不被填充),
                    // 但若将来 SDK 开始填充, 这里就地覆盖参数并立即收尾, 与 PartialJson 累积不冲突
                    foreach (var f in message.ToolCalls)
                    {
                        var completed = TrackToolCall(f.Id, f.Name ?? string.Empty);
                        var args = f.Arguments;
                        if (args is not null)
                        {
                            completed.Arguments = args.ToJsonString();
                        }

                        if (completedCalls.Add(completed))
                        {
                            yield return new ChatStreamEvent { Kind = StreamEventKind.ToolCallCompleted, ToolCall = completed };
                        }
                    }
                }

                // 工具参数只能自己攒: SDK 只在 input_json_delta 事件里给片段(Delta.PartialJson),
                // 既不填 StreamMessage.ToolCalls, 也不会在 ContentBlock 上回填完整 Input
                // (Message 属性在流里直接抛异常)。文本/思考增量走 Text/Thinking 字段, 不会与本分支混淆。
                if (!string.IsNullOrEmpty(message.Delta?.PartialJson) && openToolCall is not null)
                {
                    openToolCall.Arguments += message.Delta.PartialJson;
                }

                if (message.StreamStartMessage?.Usage is { } start)
                {
                    usage.InputTokens = start.InputTokens;
                    usage.CachedInputTokens = start.CacheReadInputTokens;
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
                    doneSent = true;
                    // 到这里所有 content_block 事件都结束, 参数已完整, 统一收尾工具调用
                    foreach (var settled in CollectCompleted())
                    {
                        yield return new ChatStreamEvent { Kind = StreamEventKind.ToolCallCompleted, ToolCall = settled };
                    }

                    yield return BuildDone();
                    yield break;
                }
            }
        }
        finally
        {
            if (!abandoned) await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        if (!doneSent)
        {
            // 流里没有 message_delta(部分端点发到 content_block_stop 就 EOF):
            // 不补发 Done 的话 AgentEngine 一个 Done 都收不到, 直接落到"未收到模型响应"
            foreach (var leftover in CollectCompleted())
            {
                yield return new ChatStreamEvent { Kind = StreamEventKind.ToolCallCompleted, ToolCall = leftover };
            }

            yield return BuildDone();
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
        // 连续的 tool 结果必须合并进**同一条** user 消息: Anthropic Messages API 要求所有 tool_result
        // 块位于一条 user 消息内, 且 user/assistant 角色严格交替。AgentEngine 对并行 tool_calls 是逐条
        // 追加 Tool 消息的(见 RunTurnCoreAsync), 不合并就会产出连续 user 消息 → 400。
        List<ContentBase>? pendingResults = null;

        // 把累积的 tool_result 落成一条 user 消息; 单工具调用时与旧行为完全等价
        void FlushToolResults()
        {
            if (pendingResults is null) return;
            list.Add(new Message { Role = RoleType.User, Content = pendingResults });
            pendingResults = null;
        }

        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case ChatMsgRole.Assistant when m.ToolCalls is { Count: > 0 }:
                    FlushToolResults();
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
                    // 不在这里落消息: 先累积, 遇到非 Tool 消息(或遍历结束)时统一 flush 成一条 user 消息
                    (pendingResults ??= []).Add(new ToolResultContent
                    {
                        ToolUseId = m.ToolCallId ?? string.Empty,
                        Content = [new TextContent { Text = m.Content }]
                    });
                    break;

                case ChatMsgRole.System:
                    // 防御性分支: 调用方都把系统提示放 request.System, Messages 里不会出现 System 角色
                    // (OpenAI 侧真用得到)。保留是因为 ChatMsgRole 是共享枚举, 将来有调用方塞进来不至于被静默丢弃
                    FlushToolResults();
                    list.Add(new Message { Role = RoleType.User, Content = [new TextContent { Text = m.Content }] });
                    break;

                default:
                    FlushToolResults();
                    var content = new List<ContentBase>();
                    // 图片在前、文本在后(与 Anthropic vision 示例一致)
                    var hasImages = m.Role == ChatMsgRole.User && m.Images is { Count: > 0 };
                    if (hasImages)
                    {
                        foreach (var img in m.Images!)
                        {
                            content.Add(new ImageContent
                            {
                                // SDK 预置 source.type=base64, 只需 media_type + data
                                Source = new ImageSource
                                {
                                    MediaType = img.MimeType,
                                    Data = img.Base64Data
                                }
                            });
                        }
                    }

                    // Anthropic 拒绝空 text block: 有图片时文本为空则省略
                    if (!string.IsNullOrEmpty(m.Content) || !hasImages)
                    {
                        content.Add(new TextContent { Text = m.Content });
                    }

                    list.Add(new Message
                    {
                        Role = m.Role == ChatMsgRole.Assistant ? RoleType.Assistant : RoleType.User,
                        Content = content
                    });
                    break;
            }
        }

        // 末尾可能还挂着一批 tool 结果(对话在工具回合被截断/取消时)
        FlushToolResults();

        return list;
    }
}