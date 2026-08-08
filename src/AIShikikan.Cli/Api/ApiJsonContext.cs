using System.Text.Json.Serialization;

namespace AIShikikan.Cli.Api;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TaskSubmitRequest))]
[JsonSerializable(typeof(TaskSubmitResponse))]
[JsonSerializable(typeof(AssignmentDto))]
[JsonSerializable(typeof(TaskListResponse))]
[JsonSerializable(typeof(RuntimeStatusResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;