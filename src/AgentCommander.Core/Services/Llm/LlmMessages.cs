using System.Text.Json;
using System.Text.Json.Nodes;

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
            return JsonDocument.Parse(new JsonObject { ["_raw"] = raw }.ToJsonString()).RootElement;
        }
    }

    public static JsonElement JsonSchema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    public static JsonObject BuildAnthropicToolUse(ToolCallData call)
    {
        return new JsonObject
        {
            ["type"] = "tool_use",
            ["id"] = call.Id,
            ["name"] = call.Name,
            ["input"] = ToNode(call.Arguments)
        };
    }

    public static JsonObject BuildOpenAiToolCall(ToolCallData call)
    {
        return new JsonObject
        {
            ["id"] = call.Id,
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = call.Name,
                ["arguments"] = ToNode(call.Arguments)
            }
        };
    }

    public static JsonNode? ToNode(string argsJson) =>
        JsonNode.Parse(ParseArgs(argsJson).GetRawText());

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