using System.IO;
using System.Text.Json;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services;

public class ThemeService
{
    private static readonly string ConfigDir = AppPaths.ConfigDir;
    private static readonly string ConfigPath = AppPaths.PreferencesPath;

    private Preferences _prefs = new();

    public bool IsDarkTheme
    {
        get => _prefs.IsDarkTheme;
        set
        {
            if (_prefs.IsDarkTheme == value) return;
            _prefs.IsDarkTheme = value;
            Save();
            ThemeChanged?.Invoke(this, value);
        }
    }

    public string Language
    {
        get => _prefs.Language;
        set
        {
            if (_prefs.Language == value) return;
            _prefs.Language = value;
            Save();
        }
    }

    public string? BackgroundImagePath
    {
        get => _prefs.BackgroundImagePath;
        set
        {
            _prefs.BackgroundImagePath = value;
            Save();
            BackgroundChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<bool>? ThemeChanged;
    public event EventHandler<string?>? BackgroundChanged;

    public ThemeService()
    {
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var json = File.ReadAllText(ConfigPath);
            _prefs = JsonSerializer.Deserialize(json, AppJsonContext.Default.Preferences) ?? new Preferences();
        }
        catch
        {
            _prefs = new Preferences();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(_prefs, AppJsonContext.Default.Preferences);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
        }
    }

    public class Preferences
    {
        public bool IsDarkTheme { get; set; } = true;
        public string Language { get; set; } = "zh-CN";
        public string? BackgroundImagePath { get; set; }
    }
}
