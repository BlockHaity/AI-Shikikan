using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;

namespace AIShikikan.Gui.Services;

/// <summary>待发送的图片附件: 已规范化(降采样 + base64), 含缩略图展示数据。</summary>
/// <remarks>
/// 持有 <see cref="Thumbnail"/> 这张原生位图的所有权: 附件被移出 UI(移除/清空待发送条带)时
/// 必须由持有方调用 <see cref="Dispose"/> 归还, 否则缩略图会随消息数增长持续泄漏原生内存。
/// </remarks>
public sealed class PendingImageAttachment : IDisposable
{
    public required string Name { get; init; }
    public required string MimeType { get; init; }

    /// <summary>规范化后的图片数据(base64, 不含 dataURL 前缀)。</summary>
    public required string Base64 { get; init; }

    /// <summary>输入区缩略图(本实例独占, 随 <see cref="Dispose"/> 释放)。</summary>
    public required Bitmap Thumbnail { get; init; }

    // 0 = 未释放。用 Interlocked 保证 Dispose 与终结器并发时只有一个赢家
    // (Bitmap.Dispose 不保证可重入, 重复释放未释放的原生句柄会崩)。
    private int _released;

    /// <summary>释放缩略图。释放后 <see cref="Thumbnail"/> 不可再用于渲染, 调用方须确保已把它移出 UI。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;

        Thumbnail.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 兜底终结器: 显式 Dispose 才是主路径, 这里只保证原生句柄最终归还。
    /// 取舍: 托管资源不该在终结器里 Dispose, 但位图持有的是非托管内存, 而附件还会被
    /// ChatPageViewModel 跨会话缓存(_sessionAttachments), 整批丢弃时没有 Dispose 时机。
    /// </summary>
    ~PendingImageAttachment()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        Thumbnail.Dispose();
    }
}

/// <summary>
/// 多模态输入图片处理: 解码校验、长边降采样、统一重编码为 PNG(base64)。
/// JPEG 源也转 PNG 以规避第三方端点对 jpeg dataURL 的兼容差异; 降采样已显著压缩体积。
/// </summary>
public static class ImageAttachmentService
{
    /// <summary>送入模型的最大长边像素(超过则等比缩小; 与 Anthropic vision 建议一致)。</summary>
    private const int MaxEdge = 1568;

    /// <summary>单条消息最多附带图片数。</summary>
    public const int MaxAttachments = 4;

    /// <summary>按扩展名判断是否支持的图片格式。</summary>
    public static bool IsSupportedImage(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" => true,
        _ => false
    };

    /// <summary>从磁盘文件构建附件; 解码失败或格式不支持返回 null。位图处理保持在 UI 线程(避免后台线程渲染上下文问题)。</summary>
    public static async Task<PendingImageAttachment?> FromFileAsync(string path)
    {
        if (!IsSupportedImage(path)) return null;

        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            return FromBytes(bytes, Path.GetFileName(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从内存字节构建附件(自动识别格式并解码)。</summary>
    public static PendingImageAttachment? FromBytes(byte[] bytes, string name)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var bmp = new Bitmap(ms);
            return Encode(name, bmp);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从剪贴板位图构建附件。</summary>
    /// <remarks>
    /// 所有权约定: 入参 <paramref name="bmp"/> 仍属调用方 —— 本方法只读取它来降采样/编码,
    /// 自己另建缩略图副本, 因此不接管也不释放调用方的位图。调用方必须在用完后自行 Dispose
    /// (例如粘贴路径里的剪贴板位图)。
    /// </remarks>
    public static PendingImageAttachment? FromBitmap(Bitmap bmp, string name)
    {
        try
        {
            return Encode(name, bmp);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 批量释放附件的缩略图(移除单个附件 / 清空待发送条带 / 丢弃跨会话缓存时调用)。
    /// </summary>
    /// <remarks>
    /// 传入的应当是<strong>即将移出 UI 的快照</strong>(如 <c>PendingAttachments.ToList()</c>):
    /// 释放后这些缩略图不能再参与渲染。单个 <c>PendingImageAttachment.Dispose()</c> 幂等,
    /// 重复调用不会崩, 但仍应避免对仍在显示的附件调用。
    /// </remarks>
    public static void DisposeAll(IEnumerable<PendingImageAttachment>? attachments)
    {
        if (attachments is null) return;

        foreach (var attachment in attachments)
        {
            attachment.Dispose();
        }
    }

    /// <summary>降采样(长边 ≤ MaxEdge)后编码为 PNG, 并生成独立缩略图副本。</summary>
    private static PendingImageAttachment? Encode(string name, Bitmap src)
    {
        var w = src.PixelSize.Width;
        var h = src.PixelSize.Height;
        if (w <= 0 || h <= 0) return null;

        var longEdge = Math.Max(w, h);
        RenderTargetBitmap? scaled = null;
        var toEncode = (Bitmap)src;
        try
        {
            if (longEdge > MaxEdge)
            {
                var factor = (double)MaxEdge / longEdge;
                var tw = Math.Max(1, (int)Math.Round(w * factor));
                var th = Math.Max(1, (int)Math.Round(h * factor));
                scaled = new RenderTargetBitmap(new PixelSize(tw, th), new Vector(96, 96));
                using (var dc = scaled.CreateDrawingContext())
                {
                    dc.DrawImage(src, new Rect(0, 0, tw, th));
                }

                toEncode = scaled;
            }

            using var outMs = new MemoryStream();
            // 显式指定 PNG: 无参 Save(Stream) 在 Avalonia 12 已过时
            toEncode.Save(outMs, PngBitmapEncoderOptions.Default);
            var png = outMs.ToArray();

            return new PendingImageAttachment
            {
                Name = name,
                MimeType = "image/png",
                Base64 = Convert.ToBase64String(png),
                // 独立副本: 源 Bitmap / RenderTargetBitmap 随后释放; 缩略图所有权移交附件,
                // 由调用方在附件移出 UI 时 Dispose(PendingImageAttachment.Thumbnail)
                Thumbnail = new Bitmap(new MemoryStream(png))
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            scaled?.Dispose();
        }
    }
}
