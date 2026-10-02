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
using AIShikikan.Core.Services.Tools;
using AIShikikan.Core.Services.Usage;
using AIShikikan.Core.Services.Worker;

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
///
/// <para><b>工具卡片数据要登记两处, 缺一不可</b>: 派生类型清单在
/// <see cref="ToolCardDetail"/> 的 <c>[JsonDerivedType]</c> 上(决定 <c>$type</c> 判别符与多态分派),
/// 独立的 <c>[JsonSerializable]</c> 只是额外拿到该具体类型的 <c>JsonTypeInfo</c>(可按声明类型直接
/// 序列化/反序列化)。多态路径本身靠前者就够, 但漏了后者会在"按具体类型读写"时抛
/// "no metadata for type"。注意 <c>[JsonDerivedType]</c> 写在<b>派生类型自己</b>身上对基类属性路径无效 ——
/// 派生类型清单不继承。</para>
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
// ToolResult.Detail 是跨进程(Worker 协议)要传的多态属性; 其派生类型清单在 ToolCardDetail 上。
[JsonSerializable(typeof(ToolResult))]
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
// ── Worker 协议(独立进程 AIShikikan.Worker, NDJSON over stdio)────────────────────
// 契约常量与字段语义见 Services/Worker/WorkerProtocol.cs 与 WorkerMessages.cs。
//
// 登记规则(与其他条目完全一致, 不因跨进程而放宽):
//   1. 每个 params/result 载荷 DTO 都单独登记 —— 传输层要按 "读/写这一种帧" 直接取
//      Default.WorkerXxx, 而不是靠 JsonNode/字典中转;
//   2. 协议里出现的引用类型(AgentRosterEntry / Assignment / ToolCardDetail 及其派生)
//      **本来就已经在上面登记过**, 源生成器会自动复用它们的元数据, 无需重复登记。
//      其中 ToolCardDetail 的 12 个派生类型必须"逐个按具体类型可读可写"这一点,
//      正是上面那一串登记存在的理由 —— WorkerToolCallResponse.DetailJson 只能按
//      具体类型的 JsonTypeInfo 序列化, 用基类的会抛 "no metadata for type";
//   3. 协议不引入新的跨进程序列化枚举, 故本组无需登记任何枚举类型。
//      已出现的唯一枚举是 Assignment.Status(SubagentStatus), 作为属性的元数据由生成器内联处理;
//      上面那两条 [JsonSerializable(typeof(SomeEnum))] 是为了拿到"裸枚举 ↔ 值" 的独立访问器, 用途不同。
//
// ⚠️ 本组 DTO 与上面所有条目共享同一条不可改的约束: 枚举以**数字**落盘(未开
// UseStringEnumConverter), 而数字枚举跨进程错位是静默的。因此协议层必须校验
// WorkerProtocol.ProtocolVersion(当前 1), 不一致直接终止 Worker, 绝不"先跑起来再说"。
// 新增可选字段兼容(未知字段被忽略); 改动任何跨进程出现的枚举则**必须**抬 ProtocolVersion。
[JsonSerializable(typeof(WorkerHelloRequest))]
[JsonSerializable(typeof(WorkerHelloResponse))]
[JsonSerializable(typeof(WorkerToolDescriptor))]
[JsonSerializable(typeof(WorkerToolsResponse))]
[JsonSerializable(typeof(WorkerToolsSyncRequest))]
[JsonSerializable(typeof(WorkerToolCallRequest))]
[JsonSerializable(typeof(WorkerToolCallResponse))]
[JsonSerializable(typeof(WorkerToolOutputNotification))]
[JsonSerializable(typeof(WorkerHeartbeatNotification))]
[JsonSerializable(typeof(WorkerCancelNotification))]
[JsonSerializable(typeof(WorkerAssignmentNotification))]
[JsonSerializable(typeof(WorkerAskUserRequest))]
[JsonSerializable(typeof(WorkerAskUserResponse))]
[JsonSerializable(typeof(WorkerOkResponse))]
public sealed partial class AppJsonContext : JsonSerializerContext;
