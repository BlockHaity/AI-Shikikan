using System.Globalization;

namespace AIShikikan.Core.Services;

public class I18nService
{
    private CultureInfo _currentCulture;

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

    public I18nService()
    {
        _currentCulture = CultureInfo.CurrentCulture;
    }

    public void SetLanguage(string cultureName)
    {
        CurrentCulture = new CultureInfo(cultureName);
    }
}
