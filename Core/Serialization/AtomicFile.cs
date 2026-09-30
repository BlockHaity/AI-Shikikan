using System.Text;
using AIShikikan.Core.Logging;

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
/// </summary>
public static class AtomicFile
{
    private const string LogCategory = "AtomicFile";
    private const string TmpSuffix = ".tmp";
    private const string BakSuffix = ".bak";

    /// <summary>原子写入文本(UTF-8 无 BOM)。会先写 tmp、刷盘、再覆盖目标。</summary>
    /// <param name="path">目标文件路径, 父目录不存在时自动创建。</param>
    /// <param name="content">要写入的文本内容。</param>
    /// <exception cref="DirectoryNotFoundException">父目录创建失败。</exception>
    public static void WriteAllText(string path, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

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

    /// <summary>读取原始文本(不做回退), 用于流式/大文件场景。</summary>
    public static bool TryReadRaw(string path, out string content)
    {
        content = string.Empty;
        return TryReadOnce(path, out content, validate: null);
    }

    private static bool TryReadOnce(string path, out string content, Func<string, bool>? validate)
    {
        content = string.Empty;

        if (!File.Exists(path)) return false;

        try
        {
            var text = File.ReadAllText(path);

            // 空文件等价于损坏: 多半是上次写入被中断
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

    /// <summary>删除文件及其 .tmp/.bak 附属文件。</summary>
    public static void Delete(string path)
    {
        foreach (var target in new[] { path, path + TmpSuffix, path + BakSuffix })
        {
            TryDelete(target);
        }
    }
}
