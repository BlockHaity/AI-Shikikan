using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Git;

/// <summary>
/// 检查点持久化存储: 按仓库根目录分目录存储 JSON 文件, 原子写入, 支持按仓库/ID 查询。
///
/// <para><b>加载策略: 按仓库懒加载</b>。本类型<b>不在构造函数里全量扫描</b>
/// <c>checkpoints/</c> 下的每一个仓库子目录, 而是等某个仓库第一次被访问
/// (<see cref="Get"/> / <see cref="GetAll"/> / <see cref="Save"/> / <see cref="Delete"/>)
/// 时, 只把这个仓库自己的目录读进内存; 之后命中"已载入"集合就不再碰磁盘。
///
/// <para><b>为什么</b>: 引入多进程 Worker(每个"工作目录 × 会话"一个进程)后,
/// 旧实现让每个新进程在构造期付一次"扫全部仓库 × 全部 json 文件"的代价, 启动时间随进程数
/// 线性增长, 而绝大多数 Worker 一生只会碰一个仓库 —— 全量扫描是纯浪费。</para>
///
/// <para><b>对外语义不变</b>: 懒加载只挪动了"何时读盘", 公开方法的返回值与副作用同
/// "构造即全量加载"完全一致(见各处 <see cref="EnsureRepoLoaded"/> 调用点), 调用方无需感知,
/// 也不需要主动预热。特别注意: 写路径同样必须先载入, 否则淘汰计数会漏掉磁盘上已有的记录
/// 而永不触发(见 <see cref="Save"/>)。</para>
///
/// <para><b>唯一的全量路径</b>: <see cref="GetBySession"/> 是跨仓库查询, 无法只读一个仓库,
/// 因此它内部走 <see cref="LoadAll"/> —— 那是<b>显式的全量操作, 只在需要跨仓库视图时调用</b>。</para>
///
/// <para><b>跨进程写</b>: 内存缓存没有任何自动失效机制, 别的进程改了文件本进程不会知道,
/// 需要时由调用方显式调 <see cref="Invalidate"/> 强制重载。</para>
/// </summary>
public sealed class GitCheckpointStore
{
    /// <summary>
    /// 单仓库检查点记录上限。每条用户消息都会产生一条记录 + 一个 git tag,
    /// 不设上限时长期使用会让 <c>checkpoints/</c> 目录与内存缓存无界增长(对比 UsageData.MaxEntries = 5000)。
    /// 超出后按 <see cref="GitCheckpointRecord.CreatedAt"/> 淘汰最旧的, 并连带删除其 tag。
    /// </summary>
    private const int MaxRecordsPerRepo = 500;

    private readonly string _baseDir;
    private readonly Dictionary<string, GitCheckpointRecord> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>已从磁盘载入过的仓库(用缓存键前缀 = 仓库哈希 + ":" 表示), 命中则不再读盘。</summary>
    private readonly HashSet<string> _loadedRepos = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 正在载入中的仓库前缀。让同一仓库的并发首次访问只扫一次盘, 后到者等待而不是重复扫描 ——
    /// 顺带把"载入"与"写入"在同仓库上串行化(见 <see cref="EnsureRepoLoaded"/> 的时序说明)。
    /// </summary>
    private readonly HashSet<string> _loadingRepos = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>正在执行淘汰的仓库键前缀, 避免并发 Save 对同一仓库重复淘汰。</summary>
    private readonly HashSet<string> _evictingRepos = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"未注入 TagDeleter" 只警告一次, 避免每次淘汰刷屏。</summary>
    private bool _warnedMissingTagDeleter;

    /// <summary>
    /// 构造函数。<b>刻意不在此处读盘</b>(旧实现会调 <c>LoadAll()</c> 全量扫描所有仓库子目录),
    /// 理由见类注释。
    ///
    /// <para><b>唯一残留的副作用</b>: 创建 <c>checkpoints/</c> 目录(幂等的单个 mkdir, 不遍历)。
    /// 保留是为了让本类型可以被独立构造(未来的 Worker 不必先调
    /// <c>AppPaths.EnsureDirectoriesExist</c>); 主路径上 <c>CommanderRuntime.Boot</c>
    /// 在 <c>new GitCheckpointStore()</c> 之前已建过一次, 这里等于空操作。</para>
    /// </summary>
    public GitCheckpointStore()
    {
        _baseDir = AppPaths.CheckpointsDir;
        Directory.CreateDirectory(_baseDir);
    }

    /// <summary>
    /// 删除检查点 tag 的委托(可选), 签名为 <c>(仓库根目录, tag 名) => 是否删除成功</c>。
    ///
    /// <para><b>为什么用委托注入而不是直接依赖 <c>GitService</c></b>: <c>GitService</c> 本身
    /// 依赖本 Store(要调 <c>Save</c>), 反向依赖会成环。委托保持 Core.Git 内部单向依赖。</para>
    ///
    /// <para><b>当前状态: 已注入</b>(<c>CommanderRuntime.Boot</c> 在 <c>new GitService(checkpoints)</c>
    /// 之后立即赋值一个调用 <c>GitService.DeleteCheckpointTag</c> 的闭包)。若装配处忘了注入,
    /// 淘汰只删 JSON 记录, 对应 tag 会残留为孤儿(doctor 的 ScanLegacyArtifacts 会持续把它
    /// 统计进"检查点 tag"而不自动清理)。</para>
    ///
    /// <para><b>与懒加载的关系</b>: 本委托是<b>实例级注入</b>, 只在 <see cref="DeleteTag"/>
    /// 被调用(淘汰记录)时读取, 与缓存何时载入无关, 因此按仓库懒加载不会让它丢失。
    /// 但注入时序仍须早于"第一次可能触发淘汰的 <see cref="Save"/>"—— 构造期不再有 IO 之后,
    /// 这个时序要求比旧实现更紧(旧实现靠构造期的扫描把"首条 Save"推迟到注入之后)。</para>
    /// </summary>
    public Func<string, string, bool>? TagDeleter { get; set; }

    /// <summary>保存检查点记录(原子写入, 并更新内存缓存)。</summary>
    public void Save(GitCheckpointRecord record)
    {
        if (record is null) throw new ArgumentNullException(nameof(record));
        if (string.IsNullOrWhiteSpace(record.RepositoryRoot))
            throw new ArgumentException("RepositoryRoot 不能为空", nameof(record));

        // 懒加载: 写盘之前先把本仓库载入。Save 之后紧接着 EvictIfNeeded, 而淘汰是拿
        // "缓存里本仓库的条数" 与上限比较的 —— 未载入就计数会漏掉磁盘上早已存在的记录,
        // 淘汰永不触发, checkpoints/ 目录与内存缓存无界增长。这是懒加载最容易踩的坑,
        // 所以写路径与读路径一样必须先过 EnsureRepoLoaded。
        EnsureRepoLoaded(record.RepositoryRoot);

        var repoDir = GetRepoDir(record.RepositoryRoot);
        Directory.CreateDirectory(repoDir);

        var file = Path.Combine(repoDir, $"{record.Id}.json");

        try
        {
            var json = JsonSerializer.Serialize(record, AppJsonContext.Default.GitCheckpointRecord);

            // 原子写: 写 .tmp -> 强制刷盘 -> rename 覆盖, 并留 .bak 供损坏回退。
            // 检查点是回滚能力的依据, 不能容忍半截文件(原先无 Flush(true) 时,
            // 断电可能让 rename 出一个内容不全的正式文件)。
            // 这里用非 Try 版: 写失败必须上抛, 让 GitService.MarkCheckpoint 记录到日志,
            // 否则"tag 已建但记录不存在"会让该条消息彻底失去回滚能力。
            AtomicFile.WriteAllText(file, json);

            lock (_lock)
            {
                _cache[GetCacheKey(record.RepositoryRoot, record.Id)] = record;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("GitCheckpoint", ex, $"保存检查点失败: {record.Id}");
            throw;
        }

        // 迁移期: 旧版目录里可能还留着同 id 的历史副本, 写入新目录后清掉(逐步排空旧目录),
        // 否则副本会在下次加载该仓库时(懒加载或全量扫描)被重新载入, 覆盖掉刚写入的新内容。
        RemoveLegacyFile(record.RepositoryRoot, record.Id);

        // IO 刻意放在锁外, 因此存在一个已知的低概率不一致窗口: 两个 Save 并发处理同一 id 时,
        // 谁先完成 rename 谁的文件生效, 而缓存写入顺序可能与之相反(后写缓存者未必持有最新文件)。
        // 概率极低(同一 id 只在回滚时才会被二次 Save), 且下次加载该仓库时从文件重建缓存即自愈,
        // 故保留此设计。
        EvictIfNeeded(record.RepositoryRoot, record.Id);
    }

    /// <summary>按 ID 获取检查点(需提供仓库根目录以定位目录)。</summary>
    public GitCheckpointRecord? Get(string repositoryRoot, string id)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || string.IsNullOrWhiteSpace(id))
            return null;

        // 懒加载: 回滚 / 分叉前按 ID 查记录, 首次访问即触发本仓库载入。
        EnsureRepoLoaded(repositoryRoot);

        var key = GetCacheKey(repositoryRoot, id);
        lock (_lock)
        {
            return _cache.TryGetValue(key, out var record) ? record : null;
        }
    }

    /// <summary>获取指定仓库的所有检查点(按创建时间倒序)。</summary>
    public IReadOnlyList<GitCheckpointRecord> GetAll(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
            return [];

        // 懒加载: Git 面板首次打开时才第一次触碰本仓库, 之后全部命中缓存。
        EnsureRepoLoaded(repositoryRoot);

        var prefix = GetCacheKeyPrefix(repositoryRoot);
        lock (_lock)
        {
            return _cache
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value)
                .OrderByDescending(r => r.CreatedAt)
                .ToList();
        }
    }

    /// <summary>
    /// 获取指定会话的所有检查点(跨仓库查询, 按创建时间倒序)。
    /// </summary>
    /// <remarks>
    /// 目前无生产调用方。Git 面板(GitPanelViewModel)直接用 <see cref="GetAll"/> 列出当前仓库的全部检查点,
    /// 不按会话过滤。若后续要按会话隔离视图(例如会话切换时只显示本会话的检查点), 这里就是现成入口。
    /// </remarks>
    public IReadOnlyList<GitCheckpointRecord> GetBySession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return [];

        // 跨仓库查询: 无法只载入一个仓库, 这里走全量扫描。
        // 这是<b>显式的全量操作, 只在真正需要跨仓库视图时才会发生</b>(本方法目前无生产调用方)。
        LoadAll();

        lock (_lock)
        {
            return _cache
                .Values
                .Where(r => string.Equals(r.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.CreatedAt)
                .ToList();
        }
    }

    /// <summary>
    /// 删除检查点记录(文件与缓存)。
    /// </summary>
    /// <remarks>
    /// 目前无生产调用方 —— GUI 没有"删除检查点"入口, 因此检查点实际只增不减
    /// (单仓库记录数由 <see cref="MaxRecordsPerRepo"/> 兜底, tag 则依赖 <see cref="TagDeleter"/>)。
    /// 建议 GUI 侧(检查点卡片 / Git 面板)补一个删除入口, 接线时直接调本方法。
    /// </remarks>
    public bool Delete(string repositoryRoot, string id)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || string.IsNullOrWhiteSpace(id))
            return false;

        // 懒加载: 返回值 existed 的语义是"这条记录本来存不存在", 不载入就直接返回 false
        // 会让调用方误判成"本来就没有", 与旧的全量加载语义不一致。
        EnsureRepoLoaded(repositoryRoot);

        var key = GetCacheKey(repositoryRoot, id);

        bool existed;
        lock (_lock)
        {
            existed = _cache.Remove(key);
        }

        // IO 放在锁外: 持锁做磁盘操作会阻塞所有检查点查询。
        // 用 AtomicFile.Delete 而非 File.Delete: 必须连带清掉 .bak/.tmp 残留。
        // 迁移期同时清旧版目录, 否则残留副本会在下次加载该仓库时"复活"这条记录。
        AtomicFile.Delete(Path.Combine(GetRepoDir(repositoryRoot), $"{id}.json"));
        RemoveLegacyFile(repositoryRoot, id);
        return existed;
    }

    /// <summary>
    /// 失效缓存: 只清内存, <b>不删任何文件</b>; 下次访问该仓库时重新从磁盘载入。
    /// </summary>
    /// <param name="repositoryRoot">
    /// 指定仓库则只失效该仓库; 传 <c>null</c>/空白 = 失效全部(下次访问各自触发重扫)。
    /// </param>
    /// <remarks>
    /// <para><b>为什么必须有它</b>: 本 Store 的缓存<b>只在进程内失效</b>。多进程形态下,
    /// 另一个进程(另一个 Worker, 或主进程)可能新建 / 删除 / 回滚了检查点记录文件,
    /// 而本进程的内存缓存没有任何自动感知机制 —— 不调本方法就会一直读到过期视图,
    /// 典型症状是"刚在别处产生的检查点不出现"、"已被别处删除的记录仍能 <see cref="Get"/> 到"。
    /// 旧实现(构造即全量加载)也有同样的问题, 只是"重启进程"顺带掩盖了它。</para>
    ///
    /// <para><b>什么时候调</b>: 只在确实怀疑"别的进程改过文件"时(Worker 与主进程之间需要
    /// 对齐视图、会话切到别的 worktree、外部手动删过 checkpoints/ 下的文件),
    /// 不要拿它当定时轮询 —— 全量失效会让下一次访问退化成全盘扫描。</para>
    ///
    /// <para><b>与进行中的载入的关系</b>: 若该仓库此刻正在被 <see cref="EnsureRepoLoaded"/> 载入,
    /// 那次载入完成后仍会把结果并回缓存(且把仓库重新标记为"已载入")。需要严格一致时,
    /// 应在载入结束后再调用本方法。</para>
    /// </remarks>
    public void Invalidate(string? repositoryRoot = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            lock (_lock)
            {
                _cache.Clear();
                _loadedRepos.Clear();
            }

            return;
        }

        var prefix = GetCacheKeyPrefix(repositoryRoot);
        lock (_lock)
        {
            // 必须先收集键再删除: 不能一边枚举 _cache 一边改它。
            var stale = _cache.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var key in stale)
            {
                _cache.Remove(key);
            }

            // 清掉"已载入"标记 = 下次访问重新读盘。
            _loadedRepos.Remove(prefix);
        }
    }

    /// <summary>
    /// 确保某仓库已从磁盘载入内存缓存(懒加载入口)。已载入则立即返回。
    ///
    /// <para><b>为什么后到者要"等"而不是各扫各的</b>: 同一仓库上的"载入"与"写入"必须串行,
    /// 否则会出现这种交错 —— 载入线程刚把某条<b>旧</b>内容写回缓存, 而 <see cref="Save"/>
    /// 的磁盘写入发生在它之前, 结果磁盘是新数据、内存是旧数据, 且没有任何自愈机会。
    /// 让后到者等前一次载入结束, 顺带也避免了对同一目录重复扫盘。</para>
    ///
    /// <para><b>IO 在锁外</b>: 持锁读盘会把所有检查点查询(乃至别的仓库)一起堵住;
    /// 只有"抢载入权"和"合并结果"两段进临界区。</para>
    ///
    /// <para><b>失败也标记为已载入</b>: 不自动重试, 理由同 <see cref="LoadAll"/> ——
    /// 一次偶发 IO 错误不该让此后每次访问都重扫; 需要重来时显式调 <see cref="Invalidate"/>。</para>
    ///
    /// <para><b>禁止重入</b>: <see cref="LoadRepo"/> 及其调用链不得回调任何会再次走到本方法的入口,
    /// 否则同一线程会卡在 <see cref="Monitor.Wait(object)"/> 上等自己。</para>
    /// </summary>
    private void EnsureRepoLoaded(string repositoryRoot)
    {
        var prefix = GetCacheKeyPrefix(repositoryRoot);

        lock (_lock)
        {
            while (true)
            {
                if (_loadedRepos.Contains(prefix)) return;

                // Add 返回 true = 本线程抢到载入权; false = 已有线程在载入, 让出锁等它完成。
                if (_loadingRepos.Add(prefix)) break;

                Monitor.Wait(_lock);
            }
        }

        try
        {
            LoadRepo(repositoryRoot);
        }
        finally
        {
            lock (_lock)
            {
                _loadingRepos.Remove(prefix);
                _loadedRepos.Add(prefix);
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>
    /// 显式的全量操作: 扫描 <c>checkpoints/</c> 下<b>每一个</b>仓库子目录并载入缓存。
    ///
    /// <para><b>何时才需要</b>: 只有"跨仓库视图"的查询(目前仅 <see cref="GetBySession"/>)
    /// 无法用按仓库懒加载满足。除此之外所有公开 API 都只读自己那个仓库的目录,
    /// <b>不要为了"保险"在启动路径上调用本方法</b> —— 那正是懒加载要消除的成本。</para>
    ///
    /// <para><b>刻意不按目录名过滤</b>: 目录名算法从 8 位 FNV 换成 12 位 SHA-256 后(见
    /// <see cref="GetRepoHash"/>), 旧数据仍在 FNV 目录里, 全量扫描才能把它们一起载入,
    /// 否则用户会"丢失"全部历史检查点。缓存键由记录自身的 RepositoryRoot 重新计算,
    /// 与所在目录名无关, 因此新旧记录天然对齐。</para>
    /// </summary>
    private void LoadAll()
    {
        try
        {
            if (!Directory.Exists(_baseDir)) return;

            var loaded = new List<GitCheckpointRecord>();
            foreach (var repoDir in Directory.GetDirectories(_baseDir))
            {
                CollectRecords(repoDir, loaded);
            }

            MergeLoadedRecords(loaded);

            int repos;
            lock (_lock)
            {
                repos = _loadedRepos.Count;
            }

            Log.Info("GitCheckpoint", $"全量加载 {loaded.Count} 个检查点记录(覆盖 {repos} 个仓库)");
        }
        catch (Exception ex)
        {
            // 失败时<b>不能</b>照旧实现那样 _cache.Clear(): 缓存里可能已经躺着别的仓库按需载入的
            // 记录, 而它们的仓库仍被标记为"已载入", 清空后不会再重扫 = 数据静默消失。
            // 反过来清掉"已载入"标记, 让后续访问重试。
            lock (_lock)
            {
                _loadedRepos.Clear();
            }

            Log.Error("GitCheckpoint", ex, "检查点全量加载失败");
        }
    }

    /// <summary>
    /// 按仓库懒加载: 只读该仓库自己的目录(现用目录 + 旧版 FNV 目录), 见 <see cref="GetLoadDirs"/>。
    /// </summary>
    private void LoadRepo(string repositoryRoot)
    {
        try
        {
            var loaded = new List<GitCheckpointRecord>();
            foreach (var dir in GetLoadDirs(repositoryRoot))
            {
                if (Directory.Exists(dir)) CollectRecords(dir, loaded);
            }

            MergeLoadedRecords(loaded);
            Log.Debug("GitCheckpoint", $"按需加载仓库 {repositoryRoot}: {loaded.Count} 条检查点");
        }
        catch (Exception ex)
        {
            // 同 LoadAll: 不清缓存(会连带丢掉已按需载入的其他仓库); 是否重试交给调用方 Invalidate。
            Log.Error("GitCheckpoint", ex, $"按需加载仓库检查点失败: {repositoryRoot}");
        }
    }

    /// <summary>
    /// 某仓库可能存放历史记录的目录: 现用目录 + 旧版(FNV)目录。
    ///
    /// <para><b>为什么只扫这两个</b>: 全量扫描"不按目录名过滤"是为了兜住目录名算法换过,
    /// 但懒加载必须能按名字定位 —— 历史上只存在过两种目录名(8 位 FNV-1a 与 12 位 SHA-256,
    /// 见 <see cref="GetRepoHash"/>), 两者都能由 <paramref name="repositoryRoot"/> 直接算出,
    /// 所以按仓库定位不会漏数据。两者长度不同(8 vs 12), 不存在互相误判。</para>
    ///
    /// <para><b>将来若再换目录名算法, 必须在这里补上"上一代"目录</b>, 否则历史检查点会静默消失
    /// (<see cref="GetAll"/> 只会少给记录, 不会报任何错)。</para>
    /// </summary>
    private IEnumerable<string> GetLoadDirs(string repositoryRoot)
    {
        yield return GetRepoDir(repositoryRoot);
        yield return GetLegacyRepoDir(repositoryRoot);
    }

    /// <summary>把一个目录下所有 <c>*.json</c> 读进 <paramref name="sink"/>(不含缓存写入, 由调用方统一合并)。</summary>
    private static void CollectRecords(string dir, List<GitCheckpointRecord> sink)
    {
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            // 解析失败时回退 .bak: 检查点损坏等于该条消息失去回滚能力,
            // 而 .bak 往往还留着上一次成功写入的完整记录。
            if (!AtomicFile.TryReadText(file, out var content, IsValidCheckpointJson))
            {
                Log.Warn("GitCheckpoint", $"检查点文件损坏且无可用备份(已跳过): {file}");
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize(content, AppJsonContext.Default.GitCheckpointRecord);
                if (record is not null) sink.Add(record);
            }
            catch (Exception ex)
            {
                Log.Warn("GitCheckpoint", ex, $"检查点文件解析失败(已跳过): {file}");
            }
        }
    }

    /// <summary>
    /// 把读到的记录并入缓存, 并把这些仓库标记为"已载入"(整体一次加锁)。
    ///
    /// <para><b>为什么先收集再合并</b>: 逐条加锁会让每条记录都过一次锁; 而把 <c>_lock</c>
    /// 持到读完整个目录则会把所有检查点查询堵死。折中: 读盘在锁外, 只在最后合并时进临界区。</para>
    ///
    /// <para><b>键仍然由记录自身的 RepositoryRoot 计算</b>, 与所在目录名无关 —— 与旧的全量加载
    /// 保持一致, 因此从旧版 FNV 目录读出的记录同样能被按新哈希前缀的查询命中。</para>
    /// </summary>
    private void MergeLoadedRecords(List<GitCheckpointRecord> loaded)
    {
        if (loaded.Count == 0) return;

        lock (_lock)
        {
            foreach (var record in loaded)
            {
                _loadedRepos.Add(GetCacheKeyPrefix(record.RepositoryRoot));
                _cache[GetCacheKey(record.RepositoryRoot, record.Id)] = record;
            }
        }
    }

    /// <summary>
    /// 单仓库记录数超过 <see cref="MaxRecordsPerRepo"/> 时, 按创建时间淘汰最旧的记录:
    /// 从缓存移除 + 删除 JSON 文件(新旧目录都删) + 尽力删除对应 git tag。
    /// </summary>
    /// <para><b>为什么删 tag 是安全的</b>: 回滚/分叉真正依赖的是
    /// <see cref="GitCheckpointRecord.CommitSha"/>(GitService 的 ResetHardToCheckpoint /
    /// RevertToCheckpoint / Fork 都只传 CommitSha, 不使用 TagName), tag 只是一个标记。
    /// 删掉它不会削弱任何仍可用的回滚能力, 反而让被 tag 引住的旧 commit 能被 git gc 回收。
    /// 反之"只删 JSON 不删 tag"会留下孤儿 tag, 而 doctor 的 ScanLegacyArtifacts 只统计数量、
    /// 不会自动清理, 孤儿会一直被报成"可清理"。</para>
    ///
    /// <para><b>保护</b>: 本次刚保存的记录(currentId)永不参与淘汰; 正在淘汰中的仓库直接跳过,
    /// 避免并发 Save 重复删除同一批文件。</para>
    ///
    /// <para><b>前置条件</b>: 计数只看内存缓存, 因此调用方(目前只有 <see cref="Save"/>)
    /// 必须已经通过 <see cref="EnsureRepoLoaded"/> 载入了本仓库 —— 否则计数会漏掉磁盘上的
    /// 存量记录, 淘汰永不触发。这是本方法唯一依赖"缓存完整性"的地方。</para>
    /// </summary>
    private void EvictIfNeeded(string repositoryRoot, string currentId)
    {
        var prefix = GetCacheKeyPrefix(repositoryRoot);

        List<GitCheckpointRecord> victims;
        lock (_lock)
        {
            if (_evictingRepos.Contains(prefix)) return;

            // 先只计数: 绝大多数 Save 都不会触发淘汰, 避免每次都分配中间列表
            var count = _cache.Count(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (count <= MaxRecordsPerRepo) return;

            // 超出多少就淘汰多少条最旧的; 刚保存的这条永不参与(它按构造就是最新的)
            victims = _cache
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value)
                .Where(r => !string.Equals(r.Id, currentId, StringComparison.Ordinal))
                .OrderBy(r => r.CreatedAt)
                .Take(count - MaxRecordsPerRepo)
                .ToList();

            if (victims.Count == 0) return;

            _evictingRepos.Add(prefix);
            foreach (var victim in victims)
            {
                _cache.Remove(GetCacheKey(victim.RepositoryRoot, victim.Id));
            }
        }

        try
        {
            foreach (var victim in victims)
            {
                // 逐条 try: 单条记录的文件/tag 删除失败不应中断整批淘汰
                try
                {
                    AtomicFile.Delete(Path.Combine(GetRepoDir(victim.RepositoryRoot), $"{victim.Id}.json"));
                    RemoveLegacyFile(victim.RepositoryRoot, victim.Id);
                    DeleteTag(victim);
                }
                catch (Exception ex)
                {
                    Log.Warn("GitCheckpoint", ex, $"淘汰旧检查点记录失败(已跳过): {victim.Id}");
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _evictingRepos.Remove(prefix);
            }
        }
    }

    /// <summary>尽力删除记录对应的 git tag; 未注入 <see cref="TagDeleter"/> 时只警告一次。</summary>
    private void DeleteTag(GitCheckpointRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.TagName)) return;

        var deleter = TagDeleter;
        if (deleter is null)
        {
            if (!_warnedMissingTagDeleter)
            {
                _warnedMissingTagDeleter = true;
                Log.Warn("GitCheckpoint",
                    "未注入 TagDeleter, 淘汰检查点将残留孤儿 tag(需在装配处接入 GitService.DeleteCheckpointTag)");
            }

            return;
        }

        if (!deleter(record.RepositoryRoot, record.TagName))
        {
            // tag 没删掉: 记录仍会被删除(回滚只用 CommitSha, 不依赖 tag), 但 tag 会变成孤儿
            Log.Warn("GitCheckpoint", $"删除检查点 tag 失败(将残留孤儿 tag): {record.TagName}");
        }
    }

    /// <summary>删除旧版目录中的同 id 文件, 并在目录被掏空后移除它。</summary>
    private void RemoveLegacyFile(string repositoryRoot, string id)
    {
        var legacyDir = GetLegacyRepoDir(repositoryRoot);
        if (!Directory.Exists(legacyDir)) return;

        AtomicFile.Delete(Path.Combine(legacyDir, $"{id}.json"));
        TryRemoveEmptyDir(legacyDir);
    }

    private static void TryRemoveEmptyDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch (Exception ex)
        {
            // 残留空目录不影响加载(懒加载按目录名定位、全量扫描扫全部子目录, 两者都不要求目录被删干净),
            // 清理失败无需上抛
            Log.Debug("GitCheckpoint", $"清理空目录失败: {dir} ({ex.Message})");
        }
    }

    /// <summary>
    /// 校验检查点 JSON 是否可用: 能反序列化, 且关键字段(Id / RepositoryRoot)非空。
    /// 缓存键与仓库定位都依赖这两个字段, 缺失则记录无法被查询到, 等同于损坏。
    /// </summary>
    private static bool IsValidCheckpointJson(string content)
    {
        try
        {
            var record = JsonSerializer.Deserialize(content, AppJsonContext.Default.GitCheckpointRecord);
            return record is not null
                && !string.IsNullOrWhiteSpace(record.Id)
                && !string.IsNullOrWhiteSpace(record.RepositoryRoot);
        }
        catch
        {
            return false;
        }
    }

    private string GetRepoDir(string repositoryRoot)
    {
        // 用仓库根目录的哈希作为子目录名, 避免过长路径
        return Path.Combine(_baseDir, GetRepoHash(repositoryRoot));
    }

    /// <summary>旧版(FNV-1a 32 位)目录名。仅用于迁移期定位历史文件, 新写入一律走 <see cref="GetRepoDir"/>。</summary>
    private string GetLegacyRepoDir(string repositoryRoot)
        => Path.Combine(_baseDir, repositoryRoot.GetDeterministicHashCode().ToString("x8"));

    private string GetCacheKey(string repositoryRoot, string id)
        => $"{GetCacheKeyPrefix(repositoryRoot)}{id}";

    private string GetCacheKeyPrefix(string repositoryRoot)
        => $"{GetRepoHash(repositoryRoot)}:";

    /// <summary>
    /// 仓库根目录 -> 子目录名 / 缓存键前缀(12 位小写十六进制)。目录名与缓存键前缀必须同源,
    /// 因此统一在这里计算, 避免算法变更时只改一处(GetAll 的前缀过滤会与磁盘目录错位)。
    ///
    /// <para><b>为什么不用 32 位 FNV-1a</b>: 32 位哈希在数万仓库量级下生日碰撞不可忽略,
    /// 碰撞后两个仓库共用一个子目录与缓存前缀, <c>GetAll(repoA)</c> 会混入 repoB 的检查点,
    /// <c>Delete</c> 更可能删掉别的仓库的文件。取 SHA-256 前 6 字节(48 位)后碰撞概率可忽略。</para>
    ///
    /// <para><b>迁移策略</b>: 目录名变了, 旧数据仍在 8 位 FNV 目录下。
    /// <see cref="GetLoadDirs"/> 会同时扫现用目录与旧版目录(按仓库懒加载照样载入历史记录),
    /// 全量扫描 <see cref="LoadAll"/> 本就不按目录名过滤; 写入落到新目录后由
    /// <see cref="RemoveLegacyFile"/> 逐条排空旧目录, 旧目录空了就删掉。
    /// 新旧目录名长度不同(12 vs 8), 不存在互相误判的可能。缓存键只存在于内存,
    /// 加载时按记录自身的 RepositoryRoot 重建, 无需迁移。</para>
    ///
    /// <para><b>⚠ 跨进程一致性</b>: 本算法只吃 <paramref name="repositoryRoot"/> 的<b>原始字符串</b>
    /// (不做任何规范化)。主进程与 Worker 必须传入<b>逐字相同</b>的路径串, 否则会算出两个不同的
    /// 目录名, 检查点的读与写就分裂到两处。调用方(<c>GitWorkspaceContext.RepositoryRoot</c>)
    /// 负责规范化, 本方法刻意不重复规范化 —— 一旦这里偷偷 trim / 补尾斜杠 / 折叠大小写,
    /// 已落盘的目录名就再也定位不到了。</para>
    /// </summary>
    private static string GetRepoHash(string repositoryRoot)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(repositoryRoot));
        return Convert.ToHexStringLower(digest.AsSpan(0, 6));
    }
}

/// <summary>
/// 字符串确定性哈希码扩展(跨进程稳定)。
/// </summary>
/// <remarks>
/// 仅 <see cref="GitCheckpointStore"/> 的旧版目录名迁移使用; 新的仓库目录名走 SHA-256,
/// 本实现保留是为了让历史数据目录仍可被定位, 不再用于任何新写入。
/// </remarks>
internal static class StringExtensions
{
    public static int GetDeterministicHashCode(this string str)
    {
        unchecked
        {
            int hash = (int)2166136261u;
            foreach (var c in str)
            {
                hash = (hash ^ c) * 16777619;
            }
            return hash;
        }
    }
}
