using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCommander.Core.Models;
using AgentCommander.Core.Services;
using AgentCommander.Core.Services.Agents;
using AgentCommander.Core.Services.Engine;
using AgentCommander.Core.Services.Git;
using AgentCommander.Core.Services.Llm;
using AgentCommander.Core.Services.Personas;
using AgentCommander.Core.Services.Templates;

namespace AgentCommander.Core.Serialization;

/// <summary>源生成 JSON 上下文: AOT/裁剪安全, 替代反射式 JsonSerializer 调用。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(LlmSettings))]
[JsonSerializable(typeof(ProviderConfig))]
[JsonSerializable(typeof(AgentConfigFile))]
[JsonSerializable(typeof(CliAgentDefinition))]
[JsonSerializable(typeof(Persona))]
[JsonSerializable(typeof(AgentTemplate))]
[JsonSerializable(typeof(GitStepRecord))]
[JsonSerializable(typeof(Assignment))]
[JsonSerializable(typeof(ChatSession))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ThemeService.Preferences))]
public sealed partial class AppJsonContext : JsonSerializerContext;
