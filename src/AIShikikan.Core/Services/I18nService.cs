using System.Globalization;

namespace AIShikikan.Core.Services;

public class I18nService
{
    private CultureInfo _currentCulture = CultureInfo.CurrentCulture;

    public CultureInfo CurrentCulture
    {
        get => _currentCulture;
        set
        {
            if (_currentCulture.Name == value.Name) return;
            _currentCulture = value;
            CultureInfo.DefaultThreadCurrentCulture = value;
            CultureInfo.DefaultThreadCurrentUICulture = value;
            LanguageChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<CultureInfo>? LanguageChanged;

    public static readonly CultureInfo[] SupportedLanguages =
    [
        new("zh-CN"),
        new("en-US")
    ];

    public I18nService()
    {
        _currentCulture = CultureInfo.CurrentCulture;
    }

    public void SetLanguage(string cultureName)
    {
        var culture = new CultureInfo(cultureName);
        CurrentCulture = culture;
    }
}
