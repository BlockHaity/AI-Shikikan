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

/// <summary>源生成 JSON 上下文: 用于数据文件(会话/分派/roster/检查点)与旧 JSON 配置兼容读取。</summary>
///
/// <para><b>命名/枚举策略不可改</b>: <c>CamelCase</c> 属性名 + <em>默认数字枚举</em>(未开
/// <c>UseStringEnumConverter</c>)。已落盘的会话/分派/检查点文件里 <c>MessageRole</c>、
/// <c>MessageSegmentKind</c> 等枚举都是数字; 一旦改成字符串, 这些文件全部读不出。
/// 与 TOML 侧 <c>TomlJsonContext</c>(snake_case + 字符串枚举)的差异同理, 统一需先做迁移。</para>
///
/// <para><b>新增 public 属性不需要在这里注册</b> —— 源生成器会沿已注册类型自动带上。
/// 只有<em>新增类型</em>(被某个已注册类型作为属性/集合元素引用)才需要补 <c>[JsonSerializable]</c>,
/// 补错或漏补的表现都是运行时 "no metadata for type" 异常, 而非编译错误。</para>
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
[JsonSerializable(typeof(Assignment))]
[JsonSerializable(typeof(ChatSession))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(MessageSegment))]
[JsonSerializable(typeof(MessageSegmentKind))]
[JsonSerializable(typeof(ToolSegment))]
[JsonSerializable(typeof(ToolCardDetail))]
[JsonSerializable(typeof(FileReadDetail))]
[JsonSerializable(typeof(DirectoryListDetail))]
[JsonSerializable(typeof(DirectoryEntry))]
[JsonSerializable(typeof(GlobDetail))]
[JsonSerializable(typeof(FileMatchEntry))]
[JsonSerializable(typeof(GrepDetail))]
[JsonSerializable(typeof(GrepMatchEntry))]
[JsonSerializable(typeof(SubagentsDetail))]
[JsonSerializable(typeof(SubagentResultEntry))]
[JsonSerializable(typeof(RosterConfig))]
[JsonSerializable(typeof(AgentRosterEntry))]
[JsonSerializable(typeof(UsageData))]
[JsonSerializable(typeof(LlmUsageEntry))]
[JsonSerializable(typeof(AgentCallEntry))]
[JsonSerializable(typeof(ModelProfileCache))]
[JsonSerializable(typeof(ApiModelProfile))]
[JsonSerializable(typeof(GitCheckpointRecord))]
[JsonSerializable(typeof(GitCheckpointSource))]
[JsonSerializable(typeof(CheckpointRollbackMode))]
[JsonSerializable(typeof(GitWorkspaceContext))]
[JsonSerializable(typeof(CheckpointDetail))]
[JsonSerializable(typeof(GitServiceError))]
public sealed partial class AppJsonContext : JsonSerializerContext;
