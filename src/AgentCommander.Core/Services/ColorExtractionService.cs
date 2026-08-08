namespace AgentCommander.Core.Services;

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

public class ColorExtractionService
{
    public ExtractedPalette? ExtractFromPixels(uint[] pixels, int width, int height)
    {
        if (pixels.Length == 0) return null;

        var sampled = SamplePixels(pixels, width, height, 10000);
        var dominantColors = MedianCut(sampled, 5);

        if (dominantColors.Count == 0) return null;

        var sorted = dominantColors
            .OrderByDescending(c => c.count)
            .ToList();

        var primary = sorted[0].color;
        var secondary = sorted.Count > 1 ? sorted[1].color : primary;
        var tertiary = sorted.Count > 2 ? sorted[2].color : secondary;

        var primaryHsl = RgbToHsl(primary);
        var isLight = primaryHsl.l < 0.5;

        return new ExtractedPalette
        {
            Primary = primary,
            OnPrimary = isLight ? 0xFFFFFFFF : 0xFF000000,
            PrimaryContainer = Lighten(primary, isLight ? 0.3 : -0.3),
            Secondary = secondary,
            OnSecondary = isLight ? 0xFFFFFFFF : 0xFF000000,
            Surface = isLight ? 0xFFFAFAFA : 0xFF1C1B1F,
            OnSurface = isLight ? 0xFF1C1B1F : 0xFFE6E1E5,
            SurfaceVariant = Blend(tertiary, isLight ? 0xFFF3EFF4 : 0xFF49454F, 0.3)
        };
    }

    private List<(uint color, int count)> MedianCut(List<uint> pixels, int targetColors)
    {
        var boxes = new List<List<uint>> { pixels };

        while (boxes.Count < targetColors)
        {
            var maxRange = -1;
            var maxIdx = -1;

            for (var i = 0; i < boxes.Count; i++)
            {
                var range = GetMaxChannelRange(boxes[i]);
                if (range > maxRange)
                {
                    maxRange = range;
                    maxIdx = i;
                }
            }

            if (maxIdx < 0 || maxRange <= 0) break;

            var box = boxes[maxIdx];
            boxes.RemoveAt(maxIdx);

            var channel = GetMaxChannel(box);
            var sorted = box.OrderBy(p => GetChannel(p, channel)).ToList();
            var mid = sorted.Count / 2;

            boxes.Add(sorted[..mid]);
            boxes.Add(sorted[mid..]);
        }

        return boxes.Select(b => (GetAverageColor(b), b.Count)).ToList();
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

    private static int GetMaxChannelRange(List<uint> pixels)
    {
        if (pixels.Count == 0) return 0;

        var rMin = 255; var rMax = 0;
        var gMin = 255; var gMax = 0;
        var bMin = 255; var bMax = 0;

        foreach (var p in pixels)
        {
            var r = (int)(p & 0xFF);
            var g = (int)((p >> 8) & 0xFF);
            var b = (int)((p >> 16) & 0xFF);

            rMin = Math.Min(rMin, r); rMax = Math.Max(rMax, r);
            gMin = Math.Min(gMin, g); gMax = Math.Max(gMax, g);
            bMin = Math.Min(bMin, b); bMax = Math.Max(bMax, b);
        }

        return Math.Max(Math.Max(rMax - rMin, gMax - gMin), bMax - bMin);
    }

    private static int GetMaxChannel(List<uint> pixels)
    {
        var rMin = 255; var rMax = 0;
        var gMin = 255; var gMax = 0;
        var bMin = 255; var bMax = 0;

        foreach (var p in pixels)
        {
            var r = (int)(p & 0xFF);
            var g = (int)((p >> 8) & 0xFF);
            var b = (int)((p >> 16) & 0xFF);

            rMin = Math.Min(rMin, r); rMax = Math.Max(rMax, r);
            gMin = Math.Min(gMin, g); gMax = Math.Max(gMax, g);
            bMin = Math.Min(bMin, b); bMax = Math.Max(bMax, b);
        }

        var rRange = rMax - rMin;
        var gRange = gMax - gMin;
        var bRange = bMax - bMin;

        if (rRange >= gRange && rRange >= bRange) return 0;
        if (gRange >= rRange && gRange >= bRange) return 1;
        return 2;
    }

    private static int GetChannel(uint pixel, int channel) => channel switch
    {
        0 => (int)(pixel & 0xFF),
        1 => (int)((pixel >> 8) & 0xFF),
        _ => (int)((pixel >> 16) & 0xFF)
    };

    private static uint GetAverageColor(List<uint> pixels)
    {
        if (pixels.Count == 0) return 0;

        long rSum = 0, gSum = 0, bSum = 0;
        foreach (var p in pixels)
        {
            rSum += p & 0xFF;
            gSum += (p >> 8) & 0xFF;
            bSum += (p >> 16) & 0xFF;
        }

        var n = pixels.Count;
        var r = (uint)(rSum / n);
        var g = (uint)(gSum / n);
        var b = (uint)(bSum / n);

        return 0xFF000000 | (b << 16) | (g << 8) | r;
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

    private static uint Lighten(uint color, double amount)
    {
        var r = (int)(color & 0xFF);
        var g = (int)((color >> 8) & 0xFF);
        var b = (int)((color >> 16) & 0xFF);

        r = Math.Clamp((int)(r + amount * 255), 0, 255);
        g = Math.Clamp((int)(g + amount * 255), 0, 255);
        b = Math.Clamp((int)(b + amount * 255), 0, 255);

        return 0xFF000000 | ((uint)b << 16) | ((uint)g << 8) | (uint)r;
    }

    private static uint Blend(uint color1, uint color2, double t)
    {
        var r1 = (color1 & 0xFF) / 255.0;
        var g1 = ((color1 >> 8) & 0xFF) / 255.0;
        var b1 = ((color1 >> 16) & 0xFF) / 255.0;
        var r2 = (color2 & 0xFF) / 255.0;
        var g2 = ((color2 >> 8) & 0xFF) / 255.0;
        var b2 = ((color2 >> 16) & 0xFF) / 255.0;

        var r = (uint)Math.Round((r1 * (1 - t) + r2 * t) * 255);
        var g = (uint)Math.Round((g1 * (1 - t) + g2 * t) * 255);
        var b = (uint)Math.Round((b1 * (1 - t) + b2 * t) * 255);

        return 0xFF000000 | (b << 16) | (g << 8) | r;
    }
}
