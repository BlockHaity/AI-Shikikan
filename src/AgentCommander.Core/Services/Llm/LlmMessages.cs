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

    public static JsonNode? ToNode(string argsJson) =>
        JsonNode.Parse(ParseArgs(argsJson).GetRawText());
}

public class LlmApiException(string message) : Exception(message);