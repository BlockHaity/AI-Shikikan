using MaterialColorUtilities.ColorAppearance;
using MaterialColorUtilities.Palettes;
using MaterialColorUtilities.Schemes;
using MaterialColorUtilities.Utils;

namespace AIShikikan.Core.Services;

public class ExtractedPalette
{
    public uint Primary { get; init; }
    public uint OnPrimary { get; init; }
    public uint PrimaryContainer { get; init; }
    public uint Secondary { get; init; }
    public uint OnSecondary { get; init; }
    public uint Surface { get; init; }
    public uint OnSurface { get; init; }
    public uint SurfaceVariant { get; init; }
}

/// <summary>基于 MaterialColorUtilities(Material3 动态取色) 从像素中提取种子色并生成配色方案。</summary>
public class ColorExtractionService
{
    public ExtractedPalette? ExtractFromPixels(uint[] pixels, int width, int height)
    {
        if (pixels.Length == 0) return null;

        var sampled = SamplePixels(pixels, width, height, 10000);
        if (sampled.Count == 0) return null;

        // 量化 + 评分, 选出最适合作为主题色的种子色
        var seeds = ImageUtils.ColorsFromImage([.. sampled]);
        if (seeds.Count == 0) return null;

        var seed = seeds[0];
        var core = CorePalette.Of(seed);

        // 依据种子色感知亮度选择明/暗方案
        var isLight = Hct.FromInt(seed).Tone >= 50;
        var scheme = isLight
            ? new LightSchemeMapper().Map(core)
            : new DarkSchemeMapper().Map(core);

        return new ExtractedPalette
        {
            Primary = scheme.Primary,
            OnPrimary = scheme.OnPrimary,
            PrimaryContainer = scheme.PrimaryContainer,
            Secondary = scheme.Secondary,
            OnSecondary = scheme.OnSecondary,
            Surface = scheme.Surface,
            OnSurface = scheme.OnSurface,
            SurfaceVariant = scheme.SurfaceVariant
        };
    }

    private List<uint> SamplePixels(uint[] pixels, int width, int height, int maxSamples)
    {
        if (pixels.Length <= maxSamples) return [.. pixels];

        var step = (int)Math.Ceiling((double)pixels.Length / maxSamples);
        var result = new List<uint>();

        for (var y = 0; y < height; y += step)
        {
            for (var x = 0; x < width; x += step)
            {
                var idx = y * width + x;
                if (idx < pixels.Length)
                {
                    var px = pixels[idx];
                    var a = (px >> 24) & 0xFF;
                    if (a < 128) continue;

                    var r = px & 0xFF;
                    var g = (px >> 8) & 0xFF;
                    var b = (px >> 16) & 0xFF;

                    var hsl = RgbToHsl(px);
                    if (hsl.s < 0.1) continue;
                    if (hsl.l < 0.1 || hsl.l > 0.9) continue;

                    result.Add(px);
                }
            }
        }

        return result.Count > 0 ? result : [.. pixels.Take(maxSamples)];
    }

    private static (double h, double s, double l) RgbToHsl(uint color)
    {
        var r = (color & 0xFF) / 255.0;
        var g = ((color >> 8) & 0xFF) / 255.0;
        var b = ((color >> 16) & 0xFF) / 255.0;

        var max = Math.Max(Math.Max(r, g), b);
        var min = Math.Min(Math.Min(r, g), b);
        var l = (max + min) / 2;

        if (Math.Abs(max - min) < 0.001) return (0, 0, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);

        double h;
        if (Math.Abs(max - r) < 0.001) h = (g - b) / d + (g < b ? 6 : 0);
        else if (Math.Abs(max - g) < 0.001) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h /= 6;

        return (h, s, l);
    }
}
