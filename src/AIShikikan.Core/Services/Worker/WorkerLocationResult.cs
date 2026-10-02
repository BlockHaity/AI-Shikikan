namespace AIShikikan.Core.Services.Worker;

/// <summary>
/// Worker 可执行文件的定位来源, 与 <see cref="WorkerLocator"/> 的候选级别一一对应。
/// </summary>
/// <remarks>
/// <para><b>不要按数值大小做优先级判断</b>: 上层(状态栏 / <c>doctor</c> / <c>Log.Warn</c>)一律按<b>语义</b>分支 ——
/// 「有没有 <see cref="WorkerLocationResult.Found"/>」「是不是 <see cref="EnvVarPathMissing"/>」——
/// 因为数值只用于日志与序列化可读性, 不承载任何控制流。</para>
/// <para><b>为什么 <see cref="NotFound"/> 取 0</b>: 它是「什么都没找到」的默认语义,
/// 默认值就该是最需要警惕的那一档(退回进程内执行), 让「忘了判 <see cref="WorkerLocationResult.Found"/>」
/// 的调用方在开发期立刻暴露问题, 而不是静默命中某个看起来「成功」的值。</para>
/// </remarks>
public enum WorkerLocationSource
{
    /// <summary>
    /// 候选 5: 4 级候选全部未命中。
    /// <b>这不是错误, 是降级</b> —— 上层必须改用进程内执行(<c>InlineTransport</c>),
    /// 同时把 <see cref="WorkerLocationResult.Detail"/> 透出给用户(见类型的 remarks)。
    /// </summary>
    NotFound = 0,

    /// <summary>
    /// 候选 1 命中了「来源」但目标不可用: <c>AISHIKIKAN_WORKER_PATH</c> 指向的路径不存在
    /// (或存在却缺可执行位)。
    /// </summary>
    /// <remarks>
    /// <b>为什么要与 <see cref="NotFound"/> 分开而不是并进去</b>:
    /// 「用户配错了环境变量」与「这个环境压根没有 Worker」是两件事, 前者用户改一行就能修好,
    /// 后者要重新构建/安装。若都并成 <see cref="NotFound"/>, 用户看到的只会是「降级为进程内执行」,
    /// 完全无法判断是自己配错了还是没装 Worker —— 排查成本从「看一眼环境变量」变成「逐级重跑」。
    /// 因此这一档<b>不继续往下探测</b>(显式覆盖被否决, 继续探测等于把「配错」伪装成「没找到」),
    /// 但<b>必须可见</b>: <c>doctor</c> 报错误原因, <c>Log.Warn</c> 记原始路径。</remarks>
    EnvVarPathMissing = 1,

    /// <summary>候选 1 命中: 环境变量 <c>AISHIKIKAN_WORKER_PATH</c> 指向的可执行文件。</summary>
    EnvironmentVariable = 2,

    /// <summary>候选 2 命中: <c>&lt;AppContext.BaseDirectory&gt;/AIShikikan.Worker[.exe]</c>(与主程序平铺)。</summary>
    AppDirectoryFlat = 3,

    /// <summary>候选 3 命中: <c>&lt;AppContext.BaseDirectory&gt;/worker/AIShikikan.Worker[.exe]</c>(发布 / 系统包)。</summary>
    AppDirectoryWorkerSubdirectory = 4,

    /// <summary>候选 4 命中: 源码仓库里 <c>src/AIShikikan.Worker/bin/…</c> 的构建输出(<c>dotnet run</c> 场景)。</summary>
    DevelopmentBuildOutput = 5,
}

/// <summary>
/// Worker 定位结果: 一个值类型语义的数据载体, 由 <see cref="WorkerLocator.Locate"/> 产出。
/// </summary>
/// <remarks>
/// <para><b>降级必须可见</b>(本类型的全部设计目的): 定位失败时工具仍能跑、界面完全正常,
/// 只是子代理又回到主进程、<c>grep</c> 又能卡 UI、git 又在 UI 线程排队 ——
/// 用户没有任何线索, 事后也只会觉得「最近变慢了」。所以命中与未命中都必须携带
/// <b>来源</b>与<b>人类可读原因</b>, 上层才能在三处同时透出: 状态栏常驻标记、<c>doctor</c> 检查项、
/// <c>Log.Warn("Worker", …)</c>。<b>不要</b>在调用方把 <c>Source</c> 压成一个 bool。</para>
/// <para><b>刻意不含「如何启动」的任何信息</b>(命令行、管道名、工作目录): 那是 <c>PipeTransport</c> 的职责,
/// 本类型只回答「Worker 在哪 / 为什么没找到」。</para>
/// </remarks>
public sealed record WorkerLocationResult(
    string? ExecutablePath,
    WorkerLocationSource Source,
    string? Detail)
{
    /// <summary>
    /// 是否定位到可执行文件。判定只看 <c>ExecutablePath</c> 非空, 而不看 <see cref="Source"/>:
    /// <see cref="WorkerLocationSource.EnvVarPathMissing"/> 的 <c>Source</c> 不是「命中」,
    /// 但它的语义恰恰是「用户指定的路径不能用」, 同样 <c>Found == false</c>。
    /// </summary>
    public bool Found => ExecutablePath is not null;

    /// <summary>
    /// 本次探测逐条尝试过的路径(按顺序, 含最终命中的那条)。用于 <c>Log.Warn</c> / <c>doctor</c> 的
    /// 细节输出: 只给「没找到」而不给「找过哪些」, 用户无从判断是没构建、被改了名、还是装错了前缀。
    /// </summary>
    /// <remarks>约定: 只放路径本身, 判定原因(不存在 / 缺可执行位)写进 <see cref="Detail"/>, 不塞进路径串。</remarks>
    public IReadOnlyList<string> AttemptedPaths { get; init; } = Array.Empty<string>();

    /// <summary>把 <see cref="AttemptedPaths"/> 拼成单行文本, 供日志与 doctor 直接输出。</summary>
    public string AttemptedPathsText => AttemptedPaths.Count == 0
        ? "(无)"
        : string.Join("; ", AttemptedPaths);

    /// <summary>命中某一候选。</summary>
    public static WorkerLocationResult Hit(
        WorkerLocationSource source,
        string executablePath,
        string detail,
        IReadOnlyList<string> attemptedPaths) =>
        new(executablePath, source, detail) { AttemptedPaths = attemptedPaths };

    /// <summary>
    /// 未命中(降级)。<paramref name="detail"/> 必须写清「为什么没找到」以及「上层该做什么」,
    /// 因为这段文字是用户唯一能拿到的排查线索。
    /// </summary>
    public static WorkerLocationResult Miss(
        WorkerLocationSource source,
        string detail,
        IReadOnlyList<string> attemptedPaths) =>
        new(null, source, detail) { AttemptedPaths = attemptedPaths };
}