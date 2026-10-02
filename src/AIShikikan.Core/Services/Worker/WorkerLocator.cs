using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Worker;

/// <summary>
/// Worker 可执行文件自定位: 找出独立 Worker 进程的可执行文件路径, 供 <c>PipeTransport</c> 启动。
/// 找不到时<b>不抛异常、不写任何文件</b>, 而是返回一个带原因的失败结果, 由上层改用
/// <c>InlineTransport</c>(进程内执行)继续跑完今天的功能。
/// </summary>
/// <remarks>
/// <para><b>5 级候选, 按顺序探测, 命中即止</b>:</para>
/// <list type="number">
/// <item>环境变量 <see cref="WorkerEnvironmentVariable"/>(<c>debug.sh</c> 注入; 用户手动覆盖);</item>
/// <item><c>&lt;BaseDirectory&gt;/AIShikikan.Worker[.exe]</c>(与主程序平铺);</item>
/// <item><c>&lt;BaseDirectory&gt;/worker/AIShikikan.Worker[.exe]</c>(发布归档 / Linux 系统包);</item>
/// <item>自 <c>&lt;BaseDirectory&gt;</c> 上溯 <c>AIShikikan.slnx</c> 找仓库根, 再拼
/// <c>src/AIShikikan.Worker/bin/&lt;Configuration&gt;/net10.0/[&lt;rid&gt;]/AIShikikan.Worker[.exe]</c>(<c>dotnet run</c> 开发场景);</item>
/// <item>全未命中 → <see cref="WorkerLocationSource.NotFound"/>, 上层降级为 <c>InlineTransport</c>。</item>
/// </list>
///
/// <para><b>为什么是这个顺序(顺序本身是契约, 别随意调换)</b>:</para>
/// <para><b>① 为什么候选 1(环境变量)必须最优先</b>: 它是<b>唯一带用户意图</b>的一级 ——
/// <c>debug.sh</c> 之所以要注入它, 就是为了让开发场景「确定性地命中」而不是靠候选 4 猜路径。
/// 只要它被设置, 用户/脚本就是在<b>明确指定</b> Worker 的位置: 此时哪怕文件不存在, 也<b>不能</b>继续往下探,
/// 更不能伪装成「没找到」降级成进程内(那会让「脚本路径写错了」变成一个用户永远查不出来的性能回归)。
/// 所以这一级是「配错即报错」: 见 <see cref="WorkerLocationSource.EnvVarPathMissing"/>。</para>
///
/// <para><b>② 为什么 2 在 3 之前</b>: 2 是「与主程序同目录」的扁平布局(zip/tar.gz 解压即用, 或
/// 开发时两项目输出到同一目录), 3 是「子目录」布局。<b>发布产物的权威布局是 3</b>(<c>worker/</c> 子目录),
/// 因为框架依赖变体下 GUI 与 Worker 会带同名的 <c>AIShikikan.Core.dll</c> 等依赖, 同目录必然互相覆盖(见
/// <c>docs/plans/worker-architecture.md</c> 7.3)。2 只是「老式/自建布局」的兜底, 排在前面不产生歧义:
/// 两者不可能同时存在同一个文件名。</para>
///
/// <para><b>③ 为什么候选 2/3/4 都以 <c>AppContext.BaseDirectory</c> 为起点</b>(而不是
/// <c>Environment.ProcessPath</c> 的目录, 更不是工作目录):
/// <list type="bullet">
/// <item><b>AOT / 单文件下, <c>BaseDirectory</c> 指向「可执行文件真实所在的目录」</b>。
/// Linux 系统包里 GUI 装在 <c>/usr/lib/ai-shikikan/</c>, 桌面入口与 <c>PATH</c> 上的
/// <c>/usr/bin/ai-shikikan</c> 只是一个软链/包装器; 宿主解析 <c>BaseDirectory</c> 时用的是可执行文件的
/// <em>真实路径</em>(<c>/proc/self/exe</c> 语义), 因此拿到的就是 <c>/usr/lib/ai-shikikan/</c>,
/// 不是 <c>/usr/bin/</c>。而 Worker 按 7.3 的布局就装在 <c>/usr/lib/ai-shikikan/worker/</c> ——
/// 候选 3 正好在同一棵树下。<b>这正是候选 3 存在的原因</b>, 若换成「工作目录」或「PATH 查找」,
/// 这一级就必然失效。</item>
/// <item>工作目录是<b>用户选的</b>(<c>debug.sh</c> 刻意保持调用者的 cwd, 见该脚本注释), 与「程序装在哪」无关,
/// 拿它当起点会让同一个安装在不同启动目录下行为不同。</item>
/// <item>不用 <c>Assembly.GetEntryAssembly().Location</c>: Native AOT 下不可靠(裁剪 + 无 entry assembly 元数据)。
/// <c>AppContext.BaseDirectory</c> 与 <c>Environment.ProcessPath</c> 才是 AOT 安全的两个答案。</item>
/// </list></para>
///
/// <para><b>④ 为什么候选 4 要上溯 <c>AIShikikan.slnx</c> 而不是直接拼相对路径</b>:
/// <c>dotnet run</c> 启动时 <c>BaseDirectory</c> 落在 <b>GUI 自己的 bin 目录</b>
/// (<c>src/AIShikikan.Gui/bin/Debug/net10.0/</c>), 而 Worker 的产物在<b>另一个</b> bin 目录
/// (<c>src/AIShikikan.Worker/bin/Debug/net10.0/</c>) —— 两个项目各自的 <c>bin/</c> 互不相干, 相对
/// <c>../</c> 拼接要么越出仓库要么落进 GUI 的 bin, 都不可靠。<c>AIShikikan.slnx</c> 是仓库的<b>唯一权威锚点</b>
/// (仓库根只有一个解决方案文件), 自底向上找到它, 仓库根就确定了, 之后一切拼接都是确定的。</para>
///
/// <para><b>⑤ 为什么降级(候选 5)必须可见</b>: 降级的后果<b>不是功能失效, 而是无声的性能与隔离回归</b> ——
/// 工具照常能跑, 界面照常正常, 只是子代理又回到主进程、<c>grep</c>/git 又会卡 UI 线程。
/// 用户既没有报错可查, 也很难把「最近变慢了」和「Worker 没了」联系起来。
/// 所以本类<b>绝不静默降级</b>: 结果里始终带 <see cref="WorkerLocationResult.Source"/> 与
/// <see cref="WorkerLocationResult.Detail"/>, 并附 <see cref="WorkerLocationResult.AttemptedPaths"/>
/// 供上层在三处同时透出(状态栏常驻标记 / <c>doctor</c> 检查项 / <c>Log.Warn("Worker", …)</c>)。</para>
///
/// <para><b>纯读保证</b>: 全程只有 <c>File.Exists</c> / <c>Directory.GetFiles</c> / 读文件,
/// <b>不建目录、不写标记文件、不落任何状态</b>。因此它可以被反复调用(例如 doctor 与运行时各调一次),
/// 也绝不能因为探测失败而让应用起不来。</para>
/// </remarks>
public static class WorkerLocator
{
    /// <summary>显式覆盖用的环境变量名(候选 1)。值为整个可执行文件路径。</summary>
    public const string WorkerEnvironmentVariable = "AISHIKIKAN_WORKER_PATH";

    /// <summary>
    /// Worker 可执行文件名(不含扩展名)。同时也是 <c>src/</c> 下的项目目录名 ——
    /// MSBuild 的输出文件名与默认项目目录名一致, 两者本就该同步; 改程序集名时记得一并改项目目录名。
    /// </summary>
    public const string WorkerExecutableStem = "AIShikikan.Worker";

    /// <summary>仓库锚点: 仓库根唯一解决方案文件名(候选 4 上溯的终止条件)。</summary>
    public const string SolutionFileName = "AIShikikan.slnx";

    /// <summary>发布产物里 Worker 所在的子目录名(候选 3)。</summary>
    public const string WorkerSubdirectoryName = "worker";

    /// <summary>
    /// Worker 产物目录里的 TFM 段。与根 <c>Directory.Build.props</c> 的 <c>&lt;TargetFramework&gt;net10.0&lt;/TargetFramework&gt;</c>
    /// 对应; TFM 变了这里要跟着改。
    /// </summary>
    private const string TargetFrameworkDirectory = "net10.0";

    /// <summary>解析不出构建配置时的兜底值(与 <c>debug.sh</c> 的默认 <c>CONFIGURATION=Debug</c> 一致)。</summary>
    private const string DefaultConfiguration = "Debug";

    /// <summary>另一个约定俗成的配置名, 供候选 4 追加探测(见 <see cref="BuildConfigurationProbeList"/>)。</summary>
    private const string ReleaseConfiguration = "Release";

    /// <summary>
    /// 候选 4 向上查找 <c>AIShikikan.slnx</c> 的最大层数。
    /// 开发布局最深的起点是 <c>&lt;repo&gt;/src/AIShikikan.Gui/bin/&lt;配置&gt;/net10.0/&lt;rid&gt;/</c>,
    /// 距仓库根 6 层; 12 层留了一倍余量, 同时也给「产物被拷到仓库外」这种异常布局一个确定的终点,
    /// 免得上溯在深目录树上走很远(每个文件系统的每一层都可能是一次 stat)。
    /// </summary>
    private const int MaxAncestorLevels = 12;

    /// <summary>扫 <c>&lt;BaseDirectory&gt;/*.json</c> 时最多读几个候选文件(纯粹是失控保护)。</summary>
    private const int MaxConfigurationJsonCandidates = 32;

    /// <summary>配置名长度上限(同时防住「把一整个路径当配置名」这类坏数据)。</summary>
    private const int MaxConfigurationNameLength = 64;

    /// <summary>
    /// 读取构建配置时依次尝试的 JSON 键名。先 <c>System.Configuration.Configuration</c>
    /// (MSBuild 侧的规范名), 再退回裸 <c>Configuration</c>; 两者都取不到才算「读不到」。
    /// </summary>
    private static readonly string[] ConfigurationJsonKeys =
    {
        "System.Configuration.Configuration",
        "Configuration"
    };

    private const string ReasonMissing = "不存在";
    private const string ReasonIsDirectory = "是目录, 不是可执行文件";
    private const string ReasonNotExecutable = "缺少可执行位";
    private const string ReasonUnreadable = "无法读取";

    /// <summary>
    /// Worker 可执行文件名(<b>带扩展名</b>): Windows 是 <c>AIShikikan.Worker.exe</c>, 其他平台是无扩展名的
    /// <c>AIShikikan.Worker</c>。
    /// </summary>
    /// <remarks>
    /// <b>启动方必须复用本方法</b>: 扩展名逻辑一旦在两处各拼一份, 迟早会漂移成「定位用无扩展名、
    /// 启动用 .exe」这种只在某个平台上出现的偏差。返回非空: 这里没有「取不到」的场景。</remarks>
    public static string GetExecutableName() =>
        OperatingSystem.IsWindows() ? $"{WorkerExecutableStem}.exe" : WorkerExecutableStem;

    /// <summary>
    /// 执行 5 级定位。<b>纯读、不抛异常</b>(文件系统异常一律转成「该候选不可用」),
    /// 因此可以在任意线程随意调用。
    /// </summary>
    /// <returns>
    /// 命中时 <see cref="WorkerLocationResult.Found"/> 为 true 且 <see cref="WorkerLocationResult.ExecutablePath"/> 可直接
    /// 交给 <c>Process.Start</c>; 未命中时为 false, 上层<b>必须</b>切到 <c>InlineTransport</c>,
    /// 并把 <see cref="WorkerLocationResult.Detail"/> 透出给用户。
    /// </returns>
    public static WorkerLocationResult Locate()
    {
        var executableName = GetExecutableName();
        var baseDir = ResolveBaseDirectory();

        // attempted: 逐条试过的路径(给 Log.Warn / doctor)。anomalies: 「不是简单的不存在」的失败,
        // 值得单独拎出来说 —— 「文件在那儿但不可执行」和「压根没这个文件」的排查方向完全不同。
        var attempted = new List<string>();
        var anomalies = new List<string>();

        // ── 候选 1: 环境变量(唯一带用户意图的一级, 配错即报错) ─────────────────────────
        var envRaw = Environment.GetEnvironmentVariable(WorkerEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envRaw))
        {
            var envPath = ToFullPath(envRaw.Trim());
            attempted.Add(envPath);
            if (IsUsableExecutable(envPath, out var envReason))
            {
                return WorkerLocationResult.Hit(
                    WorkerLocationSource.EnvironmentVariable,
                    envPath,
                    $"环境变量 {WorkerEnvironmentVariable} 指定: {envPath}",
                    attempted);
            }

            return WorkerLocationResult.Miss(
                WorkerLocationSource.EnvVarPathMissing,
                $"环境变量 {WorkerEnvironmentVariable} 指向的路径不可用({envReason}): {envPath};" +
                "已按显式覆盖处理, 不再继续自动探测 —— 继续探测会把「环境变量配错了」伪装成「没装 Worker」, 用户无从排查",
                attempted);
        }

        // ── 候选 2: 与主程序同目录(扁平布局) ──────────────────────────────────────
        var flatPath = Path.Combine(baseDir, executableName);
        attempted.Add(flatPath);
        if (IsUsableExecutable(flatPath, out var flatReason))
        {
            return WorkerLocationResult.Hit(
                WorkerLocationSource.AppDirectoryFlat,
                flatPath,
                $"主程序目录下的同名可执行文件(扁平布局): {flatPath}",
                attempted);
        }

        RecordAnomaly(anomalies, flatPath, flatReason);

        // ── 候选 3: 主程序目录下的 worker/ 子目录(发布归档 / Linux 系统包) ──────────────
        var packagedPath = Path.Combine(baseDir, WorkerSubdirectoryName, executableName);
        attempted.Add(packagedPath);
        if (IsUsableExecutable(packagedPath, out var packagedReason))
        {
            return WorkerLocationResult.Hit(
                WorkerLocationSource.AppDirectoryWorkerSubdirectory,
                packagedPath,
                $"主程序目录 {WorkerSubdirectoryName}/ 子目录(发布归档 / 系统包布局): {packagedPath}",
                attempted);
        }

        RecordAnomaly(anomalies, packagedPath, packagedReason);

        // ── 候选 4: 上溯仓库根, 拼 Worker 的 bin 输出(开发场景) ──────────────────────
        var (configuration, configurationReason) = ResolveConfiguration(baseDir);
        var developmentPath = ProbeDevelopmentOutput(baseDir, executableName, configuration, attempted, out var developmentDetail);
        if (developmentPath is not null)
        {
            return WorkerLocationResult.Hit(
                WorkerLocationSource.DevelopmentBuildOutput,
                developmentPath,
                developmentDetail!,
                attempted);
        }

        // ── 候选 5: 降级(把「为什么」和「该做什么」都写清楚) ──────────────────────────
        var missDetail =
            $"未找到 {WorkerExecutableStem} 可执行文件, 工具将退回进程内执行(InlineTransport); " +
            $"探测起点 {baseDir}; 构建配置 {configuration}({configurationReason}); " +
            $"已尝试 {attempted.Count} 条路径; 候选4: {developmentDetail}";
        if (anomalies.Count > 0)
        {
            missDetail += $"; 非平凡失败: {string.Join("; ", anomalies)}";
        }

        return WorkerLocationResult.Miss(WorkerLocationSource.NotFound, missDetail, attempted);
    }

    /// <summary>
    /// 候选 4: 自 <paramref name="baseDir"/> 逐层向上找 <c>AIShikikan.slnx</c>, 命中后拼
    /// <c>src/AIShikikan.Worker/bin/&lt;配置&gt;/net10.0/[&lt;rid&gt;]/</c> 下的可执行文件。
    /// </summary>
    /// <remarks>
    /// <para><b>边界一: 上溯多少层、找不到怎么办</b> —— 最多 <see cref="MaxAncestorLevels"/> 层
    /// (或到文件系统根, 先到者为准)。<b>一个 <c>AIShikikan.slnx</c> 都没找到</b>就直接放弃这一级:
    /// 当前进程不在源码仓库布局里(典型是自建/拷贝的部署), 再往上探只是徒增 stat。</para>
    /// <para><b>边界二: 找到了 slnx 但下面没有产物, 继续往上走而不是立刻放弃</b> ——
    /// 用户家目录 / 挂载点里完全可能有另一个同名 <c>AIShikikan.slnx</c>(拷贝仓库、副本、备份目录)。
    /// 把「有 slnx」当成唯一的终止条件, 会在这种情况下误判成「没有 Worker」;
    /// 现在是「每个含 slnx 的祖先目录都试一遍, 命中即止」, 代价只是多几次 <c>File.Exists</c>。</para>
    /// <para><b>边界三: 为什么每级配置还要再退一档 <c>net10.0/&lt;rid&gt;/</c></b> ——
    /// 带 RID 构建(本项目的 build/pack 脚本就是 <c>-r linux-x64</c> 之类)时, MSBuild 会把产物多下沉一层。
    /// 先探不带 RID 的(最常见), 再探带 RID 的。</para>
    /// </remarks>
    private static string? ProbeDevelopmentOutput(
        string baseDir,
        string executableName,
        string primaryConfiguration,
        List<string> attempted,
        out string? detail)
    {
        var frameworkDirectories = new List<string>(2) { TargetFrameworkDirectory };
        var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        if (!string.IsNullOrEmpty(runtimeIdentifier))
        {
            frameworkDirectories.Add(Path.Combine(TargetFrameworkDirectory, runtimeIdentifier));
        }

        var configurations = BuildConfigurationProbeList(primaryConfiguration);
        var repositoryRoots = new List<string>();

        var directory = new DirectoryInfo(baseDir);
        for (var level = 0; level <= MaxAncestorLevels; level++)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                ProbeRepositoryRoot(
                    directory,
                    executableName,
                    frameworkDirectories,
                    configurations,
                    attempted,
                    out var hitPath,
                    out var hitDetail);

                if (hitPath is not null)
                {
                    detail = hitDetail;
                    return hitPath;
                }

                repositoryRoots.Add(directory.FullName);
            }

            // 到文件系统根就停(Parent 为 null), 不再靠层数硬撑
            var parent = directory.Parent;
            if (parent is null)
            {
                break;
            }

            directory = parent;
        }

        detail = repositoryRoots.Count == 0
            ? $"自 {baseDir} 向上最多 {MaxAncestorLevels} 层未找到 {SolutionFileName}(当前不是源码仓库布局, 例如产物被拷到了仓库外)"
            : $"在 {string.Join(" / ", repositoryRoots)} 找到 {SolutionFileName}, " +
              $"但其 src/{WorkerExecutableStem}/bin/<{string.Join("|", configurations)}>/{TargetFrameworkDirectory}/ 下无可执行文件(Worker 尚未构建?)";

        return null;
    }

    /// <summary>
    /// 在一个「含有 <c>AIShikikan.slnx</c> 的祖先目录」下拼出 Worker 的构建产物路径并逐一验证:
    /// <c>src/AIShikikan.Worker/bin/&lt;配置&gt;/net10.0[/rid]/AIShikikan.Worker[.exe]</c>。
    /// </summary>
    /// <remarks>
    /// 抽出来而不是内联在上溯循环里, 是为了让「仓库根 → bin 路径」这条唯一的拼接规则只写一遍:
    /// 上溯循环负责「找仓库根」, 本方法负责「仓库根下面有什么」, 两者职责分开后,
    /// 将来增删 TFM 段或改项目目录名只需改这一处。</remarks>
    private static void ProbeRepositoryRoot(
        DirectoryInfo repositoryRoot,
        string executableName,
        List<string> frameworkDirectories,
        List<string> configurations,
        List<string> attempted,
        out string? hitPath,
        out string? hitDetail)
    {
        hitPath = null;
        hitDetail = null;

        foreach (var configuration in configurations)
        {
            var binRoot = Path.Combine(
                repositoryRoot.FullName, "src", WorkerExecutableStem, "bin", configuration);

            foreach (var frameworkDirectory in frameworkDirectories)
            {
                var path = Path.Combine(binRoot, frameworkDirectory, executableName);
                attempted.Add(path);

                if (IsUsableExecutable(path, out _))
                {
                    hitPath = path;
                    hitDetail =
                        $"源码仓库构建输出(仓库根 {repositoryRoot.FullName}, 配置 {configuration}, {frameworkDirectory}): {path}";
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 候选 4 要试的配置名: 解析出的配置优先, 再补 <c>Debug</c> / <c>Release</c>。
    /// </summary>
    /// <remarks>
    /// <b>为什么允许跨配置命中</b>: 解析出的配置名只在「配置名恰好是 Debug/Release」或「能读到配置标记」时可靠,
    /// 源码构建完全可能用自定义配置名(<c>CONFIGURATION=Dev</c>), 此时解析会退到 <c>Debug</c>,
    /// 而 Worker 往往就构建在 <c>Debug</c> 或 <c>Release</c> 里。多探两档的代价是几次 <c>File.Exists</c>
    /// (纯读、无副作用), 而收益是把「开发时静默降级」这个最难排查的问题消掉。
    /// 跨配置命中是安全的: 两个进程各自带自己那份 Core, 协议是 JSON, 不共享程序集。
    /// 顺序上仍把解析出的配置放最前, 保证「猜对时优先用猜对的那个」。</remarks>
    private static List<string> BuildConfigurationProbeList(string primaryConfiguration)
    {
        var configurations = new List<string>(3) { primaryConfiguration };
        foreach (var name in new[] { DefaultConfiguration, ReleaseConfiguration })
        {
            if (!configurations.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                configurations.Add(name);
            }
        }

        return configurations;
    }

    /// <summary>
    /// 解析「当前进程是用哪个 <c>Configuration</c> 构建的」, 依次回退:
    /// <c>&lt;BaseDirectory&gt;/&lt;配置名&gt;.json</c> → <c>BaseDirectory</c> 路径里的
    /// <c>bin/&lt;配置&gt;/&lt;TFM&gt;</c> 段 → 固定 <see cref="DefaultConfiguration"/>。
    /// </summary>
    /// <returns>配置名, 以及「这个结论是怎么来的」(写进 Detail, 让 doctor 能解释为什么拼了那条路径)。</returns>
    private static (string Configuration, string Reason) ResolveConfiguration(string baseDir)
    {
        // ① <配置名>.json —— 文件名本身就是配置名, 所以只能枚举而不是拼一个已知名字;
        //    读两个键(见 ConfigurationJsonKeys)。
        foreach (var file in EnumerateConfigurationJsonCandidates(baseDir))
        {
            var value = ReadConfigurationFromJson(file);
            if (IsPlausibleConfigurationName(value))
            {
                return (value!.Trim(), $"取自 {Path.GetFileName(file)}");
            }
        }

        // ② 路径分段推断: 开发布局 <repo>/src/<proj>/bin/<配置>/<TFM>/ 里, 配置名就是路径的一段。
        var guessed = GuessConfigurationFromPath(baseDir);
        if (guessed is not null)
        {
            return (guessed, "由 BaseDirectory 路径中的 bin/<配置>/<TFM> 段推断");
        }

        // ③ 兜底。固定 Debug 而非「按编译期 DEBUG 猜」: 候选 4 只在源码布局里才可能成立,
        //    而源码布局里的路径段推断几乎必然成功, 真落到这一档说明已经是异常布局,
        //    此时选谁都大概率不中, 取 <c>debug.sh</c> 的默认值至少与开发习惯一致。
        return (DefaultConfiguration, "未找到配置标记, 兜底为 Debug(与 debug.sh 的默认配置一致)");
    }

    /// <summary>
    /// 枚举 <c>&lt;BaseDirectory&gt;</c> 下「文件名本身就是配置名」的 JSON 候选。
    /// </summary>
    /// <remarks>
    /// <b>为什么只认不含点号的文件名</b>: 我们要找的正是 <c>Debug.json</c> / <c>Release.json</c> /
    /// <c>&lt;自定义配置&gt;.json</c> 这类「以配置名命名」的文件; 而 <c>AIShikikan.Gui.deps.json</c>、
    /// <c>*.runtimeconfig.json</c>、<c>*.sourcelink.json</c> 的「主名」都带点号, 一律跳过。
    /// 一举两得: 不会误读(避免把别的 JSON 里的 <c>Configuration</c> 字段当构建配置),
    /// 也不会为了找一个几十 KB 的 deps.json 去解析它 —— 枚举 + 解析代价近似为零。
    /// </remarks>
    private static List<string> EnumerateConfigurationJsonCandidates(string baseDir)
    {
        var candidates = new List<string>();

        string[] files;
        try
        {
            files = Directory.GetFiles(baseDir, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            // 目录不可读(权限不足 / 已被删除 / 是软链到别处的路径): 当作「没有配置标记」,
            // 交给后续回退。定位器不因为探测失败而抛异常。
            return candidates;
        }

        foreach (var file in files)
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (stem.Length == 0 || stem.Length > MaxConfigurationNameLength || stem.IndexOf('.') >= 0)
            {
                continue;
            }

            candidates.Add(file);
            if (candidates.Count >= MaxConfigurationJsonCandidates)
            {
                break;
            }
        }

        // 排序只为可复现: 同时存在多个「以配置名命名」的 JSON 时, 结果不该取决于文件系统的枚举顺序。
        candidates.Sort(StringComparer.Ordinal);
        return candidates;
    }

    /// <summary>
    /// 从一个 JSON 里读构建配置。键取 <see cref="ConfigurationJsonKeys"/>; 也顺带看一层
    /// <c>runtimeOptions.configProperties</c>(同一批键有可能出现在 runtimeconfig 结构里)。
    /// </summary>
    /// <remarks>
    /// 用 <c>JsonDocument</c>(只读解析器)而不是 <c>JsonSerializer</c>: 前者不依赖反射/类型元数据,
    /// Native AOT 下完全安全, 也不需要往 <c>AppJsonContext</c> 里注册类型(那个上下文是给跨进程协议帧用的,
    /// 这里读的是 SDK 生成的构建产物, 两者无关)。</remarks>
    private static string? ReadConfigurationFromJson(string path)
    {
        if (!AtomicFile.TryReadRaw(path, out var text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return ExtractConfigurationValue(document.RootElement, depth: 0);
        }
        catch (JsonException)
        {
            // 不是 JSON / JSON 坏了: 忽略这个候选文件, 继续找下一个。
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>在 JSON 对象里找配置键; 最多再展开一层 <c>runtimeOptions.configProperties</c>。</summary>
    private static string? ExtractConfigurationValue(JsonElement root, int depth)
    {
        // 递归只发生在上面这一处(深度上限 1), 不是无界遍历
        if (depth > 1 || root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in ConfigurationJsonKeys)
        {
            if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }
        }

        if (root.TryGetProperty("runtimeOptions", out var runtimeOptions) &&
            runtimeOptions.ValueKind == JsonValueKind.Object &&
            runtimeOptions.TryGetProperty("configProperties", out var configProperties))
        {
            return ExtractConfigurationValue(configProperties, depth + 1);
        }

        return null;
    }

    /// <summary>
    /// 从 BaseDirectory 的路径分段里猜配置名(只认整段等于 <c>Debug</c> / <c>Release</c>)。
    /// </summary>
    /// <remarks>
    /// <b>为什么整段匹配而不是子串匹配</b>: 子串匹配会把 <c>/home/me/Debug-notes/…</c>、
    /// <c>/opt/Release-tools/…</c> 这类无关目录当成配置名, 然后拼出一条诡异的 bin 路径去 File.Exists
    /// —— 结果一样是「没找到」, 但 Detail 里的路径会离谱到没法读。
    /// <b>为什么取最靠近 BaseDirectory 的那一段</b>: 开发布局里 <c>bin/&lt;配置&gt;/</c> 一定在路径靠里侧,
    /// 用户家目录里的同名段反而更靠外, 靠里侧的可信度更高。</remarks>
    private static string? GuessConfigurationFromPath(string baseDir)
    {
        // 显式给 char[]: 目录分隔符在 Unix 上两个常量相同, 去重不必要但无害;
        // 分隔符显式传入而不是硬编码 '/', 是为了 Windows 上 BaseDirectory 用 '\' 时同样能切。
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var segments = baseDir.Split(separators, StringSplitOptions.RemoveEmptyEntries);

        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (segments[i].Equals(DefaultConfiguration, StringComparison.OrdinalIgnoreCase))
            {
                return DefaultConfiguration;
            }

            if (segments[i].Equals(ReleaseConfiguration, StringComparison.OrdinalIgnoreCase))
            {
                return ReleaseConfiguration;
            }
        }

        return null;
    }

    /// <summary>
    /// 校验读到的配置名是否可以拼进路径: 非空、不超长、不含路径分隔符、不是 <c>.</c>/<c>..</c>。
    /// </summary>
    /// <remarks>
    /// 这个值来自磁盘上的 JSON, 会直接进 <c>Path.Combine(…, 配置名, …)</c>。含分隔符的输入不是配置名而是路径
    /// (多半是那个 JSON 被写坏了), 此时判否让它落到下一档回退, 好过拼出一条诡异的 bin 路径再去
    /// <c>File.Exists</c> 静默失败。</remarks>
    private static bool IsPlausibleConfigurationName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxConfigurationNameLength || trimmed is "." or "..")
        {
            return false;
        }

        return trimmed.IndexOf('/') < 0 && trimmed.IndexOf('\\') < 0 && trimmed.IndexOf(':') < 0;
    }

    /// <summary>
    /// 判断某个路径能否当作 Worker 可执行文件交给 <c>Process.Start</c>。
    /// </summary>
    /// <param name="rejectReason">可用时为 null; 不可用时给出人类可读原因(供 Detail 区分「不存在」与「存在但不可用」)。</param>
    private static bool IsUsableExecutable(string path, out string? rejectReason)
    {
        rejectReason = null;

        try
        {
            if (!File.Exists(path))
            {
                // 环境变量写成目录(少写了可执行文件名)是高频手误, 单独说清楚比笼统说「不存在」有用
                rejectReason = Directory.Exists(path) ? ReasonIsDirectory : ReasonMissing;
                return false;
            }

            if (!OperatingSystem.IsWindows())
            {
                // Unix 上额外要求可执行位: 没有它的文件传给 Process.Start 必定失败
                // (Win32Exception: Permission denied)。与其把一条「必然启动失败」的路径交给上层,
                // 不如现在就判否, 并把「文件在但跑不了」这个真正需要 chmod 的信息写进 Detail。
                var mode = File.GetUnixFileMode(path);
                var executableBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                if ((mode & executableBits) == 0)
                {
                    rejectReason = ReasonNotExecutable;
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            // 连存在性检查都可能抛(权限不足 / 路径过长 / 文件系统故障): 定位器不能因此让应用起不来,
            // 判否 + 把异常类型带进原因, 让上层照常降级、doctor 仍能看出是文件系统问题而不是「没装」。
            rejectReason = $"{ReasonUnreadable}({ex.GetType().Name})";
            return false;
        }
    }

    /// <summary>把「文件在那儿但跑不了」这类非平凡失败单独记一笔, 供 Detail 使用。</summary>
    private static void RecordAnomaly(List<string> anomalies, string path, string? rejectReason)
    {
        // 「不存在」是常态, 全量列进 Detail 只会把它淹没(doctor 只有一行)
        if (rejectReason is not null && !string.Equals(rejectReason, ReasonMissing, StringComparison.Ordinal))
        {
            anomalies.Add($"{path}({rejectReason})");
        }
    }

    /// <summary>
    /// 取「主程序所在目录」: <c>AppContext.BaseDirectory</c>, 空时回退
    /// <c>Environment.ProcessPath</c> 的目录, 再回退当前工作目录。
    /// </summary>
    /// <remarks>
    /// <b>为什么不用 <c>Assembly.GetEntryAssembly().Location</c></b>: Native AOT 下它为空或不可靠
    /// (裁剪 + 无 entry assembly 元数据), 而 <c>AppContext.BaseDirectory</c> 与
    /// <c>Environment.ProcessPath</c> 都由宿主/OS 直接给出, 是这类「我在哪」问题在 AOT 下的可靠答案。
    /// 后两级回退只是防御性兜底: 正常运行时永远走不到。</remarks>
    private static string ResolveBaseDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDirectory))
        {
            return baseDirectory;
        }

        var processPath = Environment.ProcessPath;
        var processDirectory = string.IsNullOrEmpty(processPath) ? null : Path.GetDirectoryName(processPath);
        return string.IsNullOrEmpty(processDirectory) ? Environment.CurrentDirectory : processDirectory;
    }

    /// <summary>
    /// 转绝对路径(相对路径按<b>进程当前工作目录</b>解析, 与所有 CLI 惯例一致)。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不</b>展开 <c>~</c>: 环境变量的值是给脚本用的, 脚本应给绝对路径; 真写了 <c>~</c> 就让它以
    /// 「不存在」的形态原样暴露在 Detail 里, 比悄悄展开更能让人发现「这里写了 shell 语法」。
    /// 转换失败时原样返回: 让它落到 <c>File.Exists</c> 的失败分支, 而不是抛异常。</remarks>
    private static string ToFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}