using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Git;

/// <summary>
/// 检查点持久化存储: 按仓库根目录分目录存储 JSON 文件, 原子写入, 支持按仓库/ID 查询。
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

    /// <summary>正在执行淘汰的仓库键前缀, 避免并发 Save 对同一仓库重复淘汰。</summary>
    private readonly HashSet<string> _evictingRepos = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"未注入 TagDeleter" 只警告一次, 避免每次淘汰刷屏。</summary>
    private bool _warnedMissingTagDeleter;

    public GitCheckpointStore()
    {
        _baseDir = AppPaths.CheckpointsDir;
        Directory.CreateDirectory(_baseDir);
        LoadAll();
    }

    /// <summary>
    /// 删除检查点 tag 的委托(可选), 签名为 <c>(仓库根目录, tag 名) => 是否删除成功</c>。
    ///
    /// <para><b>为什么用委托注入而不是直接依赖 <c>GitService</c></b>: <c>GitService</c> 本身
    /// 依赖本 Store(要调 <c>Save</c>), 反向依赖会成环。委托保持 Core.Git 内部单向依赖。</para>
    ///
    /// <para><b>当前状态: 未注入</b>(CommanderRuntime 直接 <c>new GitCheckpointStore()</c>)。
    /// 未注入时淘汰只删 JSON 记录, 对应 tag 会残留为孤儿(doctor 的 ScanLegacyArtifacts
    /// 会持续把它统计进"检查点 tag"而不自动清理)。需由装配处(CommanderRuntime.Boot)注入
    /// 一个调用 <c>GitService.DeleteCheckpointTag</c> 的委托, 该 API 目前尚未提供。</para>
    /// </summary>
    public Func<string, string, bool>? TagDeleter { get; set; }

    /// <summary>保存检查点记录(原子写入, 并更新内存缓存)。</summary>
    public void Save(GitCheckpointRecord record)
    {
        if (record is null) throw new ArgumentNullException(nameof(record));
        if (string.IsNullOrWhiteSpace(record.RepositoryRoot))
            throw new ArgumentException("RepositoryRoot 不能为空", nameof(record));

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
        // 否则副本会在下次启动时被重新载入, 覆盖掉刚写入的新内容。
        RemoveLegacyFile(record.RepositoryRoot, record.Id);

        // IO 刻意放在锁外, 因此存在一个已知的低概率不一致窗口: 两个 Save 并发处理同一 id 时,
        // 谁先完成 rename 谁的文件生效, 而缓存写入顺序可能与之相反(后写缓存者未必持有最新文件)。
        // 概率极低(同一 id 只在回滚时才会被二次 Save), 且下次启动从文件重建缓存即自愈, 故保留此设计。
        EvictIfNeeded(record.RepositoryRoot, record.Id);
    }

    /// <summary>按 ID 获取检查点(需提供仓库根目录以定位目录)。</summary>
    public GitCheckpointRecord? Get(string repositoryRoot, string id)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || string.IsNullOrWhiteSpace(id))
            return null;

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

        var key = GetCacheKey(repositoryRoot, id);

        bool existed;
        lock (_lock)
        {
            existed = _cache.Remove(key);
        }

        // IO 放在锁外: 持锁做磁盘操作会阻塞所有检查点查询。
        // 用 AtomicFile.Delete 而非 File.Delete: 必须连带清掉 .bak/.tmp 残留。
        // 迁移期同时清旧版目录, 否则残留副本会在下次启动时"复活"这条记录。
        AtomicFile.Delete(Path.Combine(GetRepoDir(repositoryRoot), $"{id}.json"));
        RemoveLegacyFile(repositoryRoot, id);
        return existed;
    }

    private void LoadAll()
    {
        try
        {
            if (!Directory.Exists(_baseDir)) return;

            // 刻意不按目录名过滤: 目录名算法从 8 位 FNV 换成 12 位 SHA-256 后(见 GetRepoHash),
            // 旧数据仍在 FNV 目录里, 全量扫描才能把它们一起载入, 否则用户会"丢失"全部历史检查点。
            // 缓存键由记录自身的 RepositoryRoot 重新计算, 与所在目录名无关, 因此新旧记录天然对齐。
            foreach (var repoDir in Directory.GetDirectories(_baseDir))
            {
                foreach (var file in Directory.GetFiles(repoDir, "*.json"))
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
                        if (record is not null)
                        {
                            var key = GetCacheKey(record.RepositoryRoot, record.Id);
                            _cache[key] = record;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("GitCheckpoint", ex, $"检查点文件解析失败(已跳过): {file}");
                    }
                }
            }

            Log.Info("GitCheckpoint", $"加载 {_cache.Count} 个检查点记录");
        }
        catch (Exception ex)
        {
            Log.Error("GitCheckpoint", ex, "检查点存储初始化失败");
            _cache.Clear();
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
            // 残留空目录不影响加载(LoadAll 扫的是全部子目录), 清理失败无需上抛
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
    /// <see cref="LoadAll"/> 本就扫描 <c>checkpoints/</c> 下所有子目录, 因此旧记录照常载入;
    /// 写入落到新目录后由 <see cref="RemoveLegacyFile"/> 逐条排空旧目录, 旧目录空了就删掉。
    /// 新旧目录名长度不同(12 vs 8), 不存在互相误判的可能。缓存键只存在于内存,
    /// 启动时按记录自身的 RepositoryRoot 重建, 无需迁移。</para>
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
