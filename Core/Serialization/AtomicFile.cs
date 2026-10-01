using System.Collections.Concurrent;
using System.Text;
using AIShikikan.Core.Logging;
using System.Runtime.InteropServices;

namespace AIShikikan.Core.Serialization;

/// <summary>
/// 原子文件写入/读取工具(Native AOT 兼容, 零依赖)。
///
/// <para><b>为什么需要</b>: <c>File.WriteAllText</c> 是"截断后逐字节写入"，
/// 写入过程中进程被杀 / 断电 / 磁盘满都会留下半截文件。
/// 本项目所有用户数据(会话、用量、roster、assignments、步骤记录、配置)都走裸写，
/// 一旦文件损坏，加载端的 <c>catch</c> 又普遍"返回空对象"，
/// 于是<em>下一次任意写操作就会把空数据覆盖回原文件</em> —— 损坏被放大为永久数据丢失。</para>
///
/// <para><b>写入策略</b>: 先写同目录下的 <c>.tmp</c> 临时文件(与目标同卷，rename 才可能是原子的)，
/// <c>Flush(true)</c> 强制刷盘后再 <c>File.Move(overwrite: true)</c> 覆盖目标。
/// 失败时清理 tmp 并抛出，绝不留下半截的目标文件。</para>
///
/// <para><b>读取策略</b>: <see cref="TryReadText"/> 在解析失败时自动回退到
/// <c>.bak</c>(上一次成功写入的备份)，让"崩溃丢最后一次增量"降级为"完全可恢复"。</para>
///
/// <para><b>进程内路径锁</b>: 同一进程内多个入口会写同一个文件(例如 providers.toml 既可能被
/// <c>DefaultConfig.WriteIfMissing</c> 首启动补写、也可能被 <c>ProviderSettingsService.Save</c>
/// 用户编辑时写; sessions/{id}/roster.json 同时被引擎与 GUI 的开关写)。这些写入没有跨进程
/// 互斥, 只能靠 <c>FileShare.None</c> 让并发者抛 <see cref="IOException"/> —— 也就是说
/// "谁后写谁赢", 先写的那份内容直接丢失(典型症状: 保存设置偶发不生效)。本类按规范化路径
/// 维护一个 <see cref="SemaphoreSlim"/>, 让同进程同路径的写<em>串行排队</em>而不是抛异常。
/// 该锁只覆盖进程内, 不解决多进程并发(本项目是单实例 GUI, 无此需求)。</para>
/// </summary>
public static class AtomicFile
{
    private const string LogCategory = "AtomicFile";
    private const string TmpSuffix = ".tmp";
    private const string BakSuffix = ".bak";

    /// <summary>按规范化路径的进程内写锁。条目数上限 = 曾被访问过的文件数(会话数级), 可忽略;
    /// 刻意不做回收, 因为回收需要判断"是否仍有人在等", 反而引入竞态。</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
        new(PathComparer.Instance);

    /// <summary>Windows / macOS 的文件系统默认大小写不敏感, Linux 敏感 —— 键的比较规则必须与之一致,
    /// 否则 "C:\a.toml" 与 "c:\A.TOML" 会拿到两把不同的锁, 互斥形同虚设。</summary>
    private sealed class PathComparer : IEqualityComparer<string>
    {
        public static readonly PathComparer Instance = new();

        private static readonly StringComparer Inner =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        public bool Equals(string? x, string? y) => Inner.Equals(x, y);

        public int GetHashCode(string obj) => Inner.GetHashCode(obj);
    }

    /// <summary>取(并按需创建)某路径的进程内锁。规范化到绝对路径, 避免同一文件因
    /// 相对/绝对、冗余 "." 写法不同而拿到两把锁。</summary>
    private static SemaphoreSlim AcquireLock(string path)
    {
        // GetFullPath 对畸形路径(空段/非法字符)会抛, 那时不该让取锁先炸:
        // 退回原串当键, 让后续真正的文件操作照常按老路径抛出原有异常。
        var key = TryGetFullPath(path);
        var gate = PathLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        return gate;
    }

    private static string TryGetFullPath(string path)
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

    private static void ReleaseLock(SemaphoreSlim gate) => gate.Release();

    /// <summary>原子写入文本(UTF-8 无 BOM)。会先写 tmp、刷盘、再覆盖目标。</summary>
    /// <param name="path">目标文件路径, 父目录不存在时自动创建。</param>
    /// <param name="content">要写入的文本内容。</param>
    /// <exception cref="DirectoryNotFoundException">父目录创建失败。</exception>
    public static void WriteAllText(string path, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // 路径锁: 让同进程内对同一文件的并发写排队, 而不是靠 FileShare.None 抛 IOException
        var gate = AcquireLock(path);
        try
        {
            WriteAllTextCore(path, content);
        }
        finally
        {
            ReleaseLock(gate);
        }
    }

    /// <summary><see cref="WriteAllText"/> 的实际实现。调用方必须已持有该路径的进程内锁,
    /// 内部只调用不取锁的私有助手(取锁会自死锁: SemaphoreSlim 不可重入)。</summary>
    private static void WriteAllTextCore(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // tmp 必须与目标同目录(同卷), 否则 File.Move 会退化为非原子的跨卷复制
        var tempPath = path + TmpSuffix;

        try
        {
            using (var stream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                // 强制刷到物理介质: 断电后 tmp 要么完整要么不存在, 不会是半截
                stream.Flush(flushToDisk: true);
            }

            // 覆盖前把上一份完好内容留作 .bak, 供读取端损坏回退
            TryBackupExisting(path);

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>原子写入文本, 吞掉所有异常并记日志(用于"写失败不应中断主流程"的场景)。</summary>
    /// <returns>写入成功返回 true。</returns>
    public static bool TryWriteAllText(string path, string content, string? context = null)
    {
        try
        {
            WriteAllText(path, content);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory, ex, $"原子写入失败{(context is null ? "" : $": {context}")} -> {path}");
            return false;
        }
    }

    /// <summary>
    /// 读取文本。优先读目标文件; 若目标文件损坏(空 / 非法内容无法读取)，
    /// 自动回退到 <c>.bak</c> 并记录 Warn。
    /// </summary>
    /// <param name="path">目标文件路径。</param>
    /// <param name="content">读到的文本。</param>
    /// <param name="validate">
    /// 可选的语义校验回调(例如"反序列化并确认结构完整")。
    /// 返回 false 表示内容不可用, 触发 <c>.bak</c> 回退。
    /// </param>
    /// <returns>读到可用内容返回 true; 文件不存在且无备份返回 false。</returns>
    public static bool TryReadText(string path, out string content, Func<string, bool>? validate = null)
    {
        content = string.Empty;

        // 与写入同一把锁: 写入过程中会先 File.Move 主文件到 .bak, 此刻读主文件会看到
        // "文件不存在", 回退 .bak 拿到的是上一份内容 —— 不算数据损坏, 但会让调用方误判。
        var gate = AcquireLock(path);
        try
        {
            var primaryOk = TryReadOnce(path, out content, validate);
            if (primaryOk) return true;

            var backupPath = path + BakSuffix;
            if (!File.Exists(backupPath)) return false;

            if (TryReadOnce(backupPath, out var backup, validate))
            {
                Log.Warn(LogCategory,
                    $"主文件损坏或不可用, 已回退到备份: {path} <- {backupPath}");
                content = backup;
                return true;
            }

            return false;
        }
        finally
        {
            ReleaseLock(gate);
        }
    }

    /// <summary>读取原始文本(不做回退), 用于流式/大文件场景。</summary>
    public static bool TryReadRaw(string path, out string content)
    {
        content = string.Empty;
        var gate = AcquireLock(path);
        try
        {
            return TryReadOnce(path, out content, validate: null);
        }
        finally
        {
            ReleaseLock(gate);
        }
    }

    /// <summary>
    /// 单次读取尝试(调用方须已持有该路径的进程内锁)。
    ///
    /// <para><b>不变式(勿改)</b>: 空 / 纯空白文件一律判为"损坏"。
    /// 这不是过度严格: 裸 <c>File.WriteAllText</c> 与早期实现的写入过程中被中断,
    /// 留下的正是 0 字节或只写了一半空白的目标文件, 而本类的原子写<em>不会</em>产生这种状态。
    /// 所以"空文件"在本项目里只有一个来源 —— 上一次写入没写完, 即数据已丢, 必须回退 .bak,
    /// 而不是把"空内容"当成合法的用户数据返回给调用方(调用方普遍会拿它覆盖写回)。</para>
    /// </summary>
    private static bool TryReadOnce(string path, out string content, Func<string, bool>? validate)
    {
        content = string.Empty;

        if (!File.Exists(path)) return false;

        try
        {
            var text = File.ReadAllText(path);

            // 空文件等价于损坏, 详见上方"不变式(勿改)"
            if (string.IsNullOrWhiteSpace(text)) return false;

            if (validate is not null && !validate(text)) return false;

            content = text;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(LogCategory, ex, $"读取失败: {path}");
            return false;
        }
    }

    private static void TryBackupExisting(string path)
    {
        if (!File.Exists(path)) return;

        var backupPath = path + BakSuffix;
        try
        {
            // 只在目标确实存在时覆盖备份, 避免首次创建产生空 .bak
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
            File.Move(path, backupPath);
        }
        catch (Exception ex)
        {
            // 备份失败不阻断主流程(例如权限受限), 继续写目标
            Log.Debug(LogCategory, $"创建备份失败(继续写入): {path} ({ex.Message})");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清理临时文件失败无需上抛, 残留的 .tmp 会在下次写入时被覆盖
        }
    }

    /// <summary>
    /// 删除文件及其 .tmp/.bak 附属文件。
    ///
    /// <para><b>不要用 <c>File.Delete</c> 替代本方法</b>: 只删主文件会把 <c>.bak</c> 留在原地,
    /// 下一次写入时 <see cref="TryBackupExisting"/> 不检查 <c>.bak</c> 是否与主文件同代, 直接
    /// 覆盖 —— 于是"用户删掉重置"的文件会在下一次写入时被旧备份复活。
    /// <c>CommanderRuntime.DeleteIfExists</c>("还原默认设置")已经踩过这个坑: 删掉 providers.toml
    /// 后旧 API Key 从 .bak 里回来了。同理, <c>.tmp</c> 残留会让下一次写入前多一次无谓覆盖。</para>
    /// </summary>
    public static void Delete(string path)
    {
        // 与写入同一把锁: 避免"删除"与"正在进行的原子写"交错成只剩 .tmp 的状态
        var gate = AcquireLock(path);
        try
        {
            foreach (var target in new[] { path, path + TmpSuffix, path + BakSuffix })
            {
                TryDelete(target);
            }
        }
        finally
        {
            ReleaseLock(gate);
        }
    }
}
