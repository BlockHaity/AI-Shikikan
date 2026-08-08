using System.Text.Json;

namespace AgentCommander.Core.Services.Llm;

public static class LlmJson
{
    public static JsonElement ParseArgs(string raw)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw).RootElement;
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { _raw = raw });
        }
    }

    public static JsonElement JsonSchema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    public static object BuildAnthropicToolUse(ToolCallData call)
    {
        return new
        {
            type = "tool_use",
            id = call.Id,
            name = call.Name,
            input = ParseArgs(call.Arguments)
        };
    }

    public static object BuildOpenAiToolCall(ToolCallData call)
    {
        return new
        {
            id = call.Id,
            type = "function",
            function = new
            {
                name = call.Name,
                arguments = ParseArgs(call.Arguments)
            }
        };
    }

    public sealed class LlmHttpResponse : IDisposable
    {
        public required JsonDocument Doc { get; init; }
        public required string Body { get; init; }

        public void Dispose() => Doc.Dispose();
    }

    public static async Task<LlmHttpResponse> ReadResponseAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new LlmApiException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 600)}");
        }

        try
        {
            return new LlmHttpResponse
            {
                Doc = JsonDocument.Parse(body),
                Body = body
            };
        }
        catch (JsonException)
        {
            throw new LlmApiException($"无效 JSON 响应: {Truncate(body, 400)}");
        }
    }

    private static string Truncate(string s, int len) => s.Length <= len ? s : s[..len] + "...";
}

public class LlmApiException(string message) : Exception(message);