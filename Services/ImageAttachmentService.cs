using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;

namespace AIShikikan.Gui.Services;

/// <summary>待发送的图片附件: 已规范化(降采样 + base64), 含缩略图展示数据。</summary>
public sealed class PendingImageAttachment
{
    public required string Name { get; init; }
    public required string MimeType { get; init; }

    /// <summary>规范化后的图片数据(base64, 不含 dataURL 前缀)。</summary>
    public required string Base64 { get; init; }

    /// <summary>输入区缩略图。</summary>
    public required Bitmap Thumbnail { get; init; }
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
            toEncode.Save(outMs);
            var png = outMs.ToArray();

            return new PendingImageAttachment
            {
                Name = name,
                MimeType = "image/png",
                Base64 = Convert.ToBase64String(png),
                // 独立副本: 源 Bitmap / RenderTargetBitmap 随后释放
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
