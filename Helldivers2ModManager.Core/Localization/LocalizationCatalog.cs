using System.Globalization;
using System.Resources;

namespace Helldivers2ModManager.Core.Localization;

public sealed class LocalizationCatalog
{
    private readonly ResourceManager _resourceManager = new(
        "Helldivers2ModManager.Core.Localization.StringResources",
        typeof(LocalizationCatalog).Assembly);

    public string GetString(string key, CultureInfo? culture = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _resourceManager.GetString(key, culture ?? CultureInfo.CurrentUICulture) ?? string.Empty;
    }

    public bool Contains(string key, CultureInfo? culture = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var value = _resourceManager.GetString(key, culture);
        return !string.IsNullOrEmpty(value);
    }
}
