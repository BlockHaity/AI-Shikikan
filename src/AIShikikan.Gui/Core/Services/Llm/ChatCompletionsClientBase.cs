using System.Runtime.CompilerServices;

namespace AIShikikan.Core.Services.Llm;

/// <summary>LLM 客户端公共基类: 统一处理流式枚举与错误捕获, 子类只需实现 EmitAsync。</summary>
public abstract class ChatCompletionsClientBase : IChatCompletionsClient
{
    public abstract string ProviderId { get; }

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
        ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
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
                    more = await enumerator.MoveNextAsync().ConfigureAwait(false);
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
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        if (error is not null)
        {
            yield return new ChatStreamEvent { Kind = StreamEventKind.Error, Error = error };
        }
    }

    protected abstract IAsyncEnumerable<ChatStreamEvent> EmitAsync(
        ChatRequest request, CancellationToken ct);
}
