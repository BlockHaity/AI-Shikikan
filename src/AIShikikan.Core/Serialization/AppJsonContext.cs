using System.Text.Json;
using System.Text.Json.Serialization;
using AIShikikan.Core.Models;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Git;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Personas;
using AIShikikan.Core.Services.Templates;
using AIShikikan.Core.Services.Usage;

namespace AIShikikan.Core.Serialization;

/// <summary>源生成 JSON 上下文: 用于数据文件(会话/步骤/分派/roster)与旧 JSON 配置兼容读取。</summary>
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
[JsonSerializable(typeof(ThemeService.Preferences))]
[JsonSerializable(typeof(GitStepRecord))]
[JsonSerializable(typeof(Assignment))]
[JsonSerializable(typeof(ChatSession))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(RosterConfig))]
[JsonSerializable(typeof(AgentRosterEntry))]
[JsonSerializable(typeof(UsageData))]
[JsonSerializable(typeof(LlmUsageEntry))]
[JsonSerializable(typeof(AgentCallEntry))]
public sealed partial class AppJsonContext : JsonSerializerContext;
