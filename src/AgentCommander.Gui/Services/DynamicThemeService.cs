using System.Collections.Generic;
using AgentCommander.Core.Services;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace AgentCommander.Gui.Services;

public class DynamicThemeService
{
    public void ApplyPalette(ExtractedPalette? palette)
    {
        if (Application.Current is not { } app) return;

        if (palette is null)
        {
            ResetToDefault();
            return;
        }

        SetResource(app, "Primary", palette.Primary);
        SetResource(app, "OnPrimary", palette.OnPrimary);
        SetResource(app, "PrimaryContainer", palette.PrimaryContainer);
        SetResource(app, "Secondary", palette.Secondary);
        SetResource(app, "OnSecondary", palette.OnSecondary);
        SetResource(app, "Surface", palette.Surface);
        SetResource(app, "OnSurface", palette.OnSurface);
        SetResource(app, "SurfaceVariant", palette.SurfaceVariant);
    }

    public void ResetToDefault()
    {
        if (Application.Current is not { } app) return;

        var keys = new[] { "Primary", "OnPrimary", "PrimaryContainer", "Secondary", "OnSecondary", "Surface", "OnSurface", "SurfaceVariant" };
        foreach (var key in keys)
        {
            app.Resources.Remove(key);
        }
    }

    private static void SetResource(Application app, string key, uint argb)
    {
        var r = (byte)(argb & 0xFF);
        var g = (byte)((argb >> 8) & 0xFF);
        var b = (byte)((argb >> 16) & 0xFF);
        var a = (byte)((argb >> 24) & 0xFF);

        app.Resources[key] = new SolidColorBrush(new Color(a, r, g, b));
    }
}
