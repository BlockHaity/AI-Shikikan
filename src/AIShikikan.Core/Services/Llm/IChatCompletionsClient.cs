namespace AIShikikan.Core.Services.Llm;

public interface IChatCompletionsClient
{
    string ProviderId { get; }

    Task<ChatCompletionResult> CompleteAsync(ChatRequest request, CancellationToken ct = default);

    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, CancellationToken ct = default);
}