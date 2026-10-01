using System.Text.Json;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Git;

/// <summary>检查点持久化存储: 按仓库根目录分目录存储 JSON 文件, 原子写入, 支持按仓库/ID 查询。</summary>
public sealed class GitCheckpointStore
{
    private readonly string _baseDir;
    private readonly Dictionary<string, GitCheckpointRecord> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public GitCheckpointStore()
    {
        _baseDir = AppPaths.CheckpointsDir;
        Directory.CreateDirectory(_baseDir);
        LoadAll();
    }

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
            // .tmp 路径同名竞争由 FileShare.None + 失败清理处理, 失败时 AtomicFile 自行清理并上抛。
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

    /// <summary>获取指定会话的所有检查点(跨仓库查询, 按创建时间倒序)。</summary>
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

    /// <summary>删除检查点记录(文件与缓存)。</summary>
    public bool Delete(string repositoryRoot, string id)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || string.IsNullOrWhiteSpace(id))
            return false;

        var repoDir = GetRepoDir(repositoryRoot);
        var file = Path.Combine(repoDir, $"{id}.json");
        var key = GetCacheKey(repositoryRoot, id);

        bool existed;
        lock (_lock)
        {
            existed = _cache.Remove(key);
        }

        // IO 放在锁外: 持锁做磁盘操作会阻塞所有检查点查询。
        // 用 AtomicFile.Delete 而非 File.Delete: 必须连带清掉 .bak/.tmp 残留。
        AtomicFile.Delete(file);
        return existed;
    }

    private void LoadAll()
    {
        try
        {
            if (!Directory.Exists(_baseDir)) return;

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
        // 用仓库根目录的相对路径 hash 作为子目录名, 避免过长路径
        var hash = repositoryRoot.GetDeterministicHashCode().ToString("x8");
        return Path.Combine(_baseDir, hash);
    }

    private string GetCacheKey(string repositoryRoot, string id)
        => $"{GetCacheKeyPrefix(repositoryRoot)}{id}";

    private string GetCacheKeyPrefix(string repositoryRoot)
        => $"{repositoryRoot.GetDeterministicHashCode():x8}:";

    /// <summary>获取所有仓库的检查点(用于调试/迁移)。</summary>
    internal IReadOnlyList<GitCheckpointRecord> GetAllAcrossRepos()
    {
        lock (_lock)
        {
            return _cache.Values.OrderByDescending(r => r.CreatedAt).ToList();
        }
    }
}

/// <summary>字符串确定性哈希码扩展(跨进程稳定)。</summary>
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