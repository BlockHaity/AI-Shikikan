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
            if (File.Exists(ConfigPath))
            {
                var content = File.ReadAllText(ConfigPath);
                _prefs = TomlBridge.Deserialize<Preferences>(content) ?? new Preferences();
                return;
            }

            // 兼容旧 JSON 配置: 若 TOML 不存在, 回退读取 preferences.json
            var legacyPath = Path.ChangeExtension(ConfigPath, ".json");
            if (File.Exists(legacyPath))
            {
                var content = File.ReadAllText(legacyPath);
                _prefs = JsonSerializer.Deserialize(content, AppJsonContext.Default.Preferences) ?? new Preferences();
            }
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
            var toml = TomlBridge.Serialize(_prefs);
            File.WriteAllText(ConfigPath, toml);
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
