using System.Windows;
using System.ComponentModel;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace Helldivers2ModManager.Services;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class ThemeService
{
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public string CurrentTheme { get; private set; } = "Dark";

    public bool AnimationsEnabled { get; private set; }

    private string _requestedTheme = "System";
    private bool _requestedAnimations;
    private readonly HashSet<string> _highContrastOverrideKeys = [];

    public ThemeService()
    {
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
    }

    public void Apply(string requestedTheme, bool enableAnimations)
    {
        _requestedTheme = requestedTheme;
        _requestedAnimations = enableAnimations;
        var highContrast = IsHighContrastActive();
        var effectiveTheme = highContrast ? "HighContrast" : requestedTheme switch
        {
            "Light" => "Light",
            "Dark" => "Dark",
            _ => IsSystemLightTheme() ? "Light" : "Dark"
        };
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains("Theme.", StringComparison.OrdinalIgnoreCase) == true);
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"Resources/Styles/Theme.{effectiveTheme}.xaml", UriKind.Relative)
        };
        if (existing is null)
            dictionaries.Insert(1, replacement);
        else
            dictionaries[dictionaries.IndexOf(existing)] = replacement;

        ClearHighContrastOverrides();
        if (highContrast)
            ApplyHighContrastOverrides();

        CurrentTheme = effectiveTheme;
        AnimationsEnabled = enableAnimations && SystemParameters.ClientAreaAnimation && !highContrast;
        Application.Current.Resources["AnimationsEnabled"] = AnimationsEnabled;
        Application.Current.Resources["EffectiveThemeName"] = effectiveTheme;
        Application.Current.MainWindow?.InvalidateVisual();
    }

    private static bool IsSystemLightTheme()
    {
        var value = Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 0);
        return value is int intValue && intValue != 0;
    }

    private void ApplyHighContrastOverrides()
    {
        SetMany(
            [
                "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2",
                "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2",
                "SystemAccentColorDark3"
            ],
            SystemColors.HighlightColor);
        SetMany(
            [
                "SystemAccentBrush", "SystemAccentLight1Brush", "SystemAccentLight2Brush",
                "SystemAccentLight3Brush", "SystemAccentDark1Brush", "SystemAccentDark2Brush",
                "SystemAccentDark3Brush"
            ],
            SystemColors.HighlightBrush);

        SetColorAndBrush("NeutralBackgroundColor", "NeutralBackgroundBrush", SystemColors.WindowColor, SystemColors.WindowBrush);
        SetColorAndBrush("NeutralBackgroundAltColor", "NeutralBackgroundAltBrush", SystemColors.ControlColor, SystemColors.ControlBrush);
        SetColorAndBrush("NeutralBackgroundFillColor", "NeutralBackgroundFillBrush", SystemColors.WindowColor, SystemColors.WindowBrush);
        SetColorAndBrush("NeutralBackgroundFillColorSecondary", "NeutralBackgroundFillSecondaryBrush", SystemColors.ControlColor, SystemColors.ControlBrush);
        SetColorAndBrush("NeutralForegroundColor", "NeutralForegroundBrush", SystemColors.WindowTextColor, SystemColors.WindowTextBrush);
        SetColorAndBrush("NeutralForegroundColorSecondary", "NeutralForegroundSecondaryBrush", SystemColors.ControlTextColor, SystemColors.ControlTextBrush);
        SetColorAndBrush("NeutralForegroundColorTertiary", "NeutralForegroundTertiaryBrush", SystemColors.ControlTextColor, SystemColors.ControlTextBrush);
        SetColorAndBrush("NeutralForegroundColorDisabled", "NeutralForegroundDisabledBrush", SystemColors.GrayTextColor, SystemColors.GrayTextBrush);
        SetColorAndBrush("NeutralBorderColor", "NeutralBorderBrush", SystemColors.WindowTextColor, SystemColors.WindowTextBrush);
        SetColorAndBrush("NeutralBorderColorStrong", "NeutralBorderStrongBrush", SystemColors.WindowTextColor, SystemColors.WindowTextBrush);
        SetColorAndBrush("NeutralStrokeColor", "NeutralStrokeBrush", SystemColors.WindowTextColor, SystemColors.WindowTextBrush);
        SetColorAndBrush("NeutralStrokeColorAlternate", "NeutralStrokeAlternateBrush", SystemColors.WindowTextColor, SystemColors.WindowTextBrush);

        SetColorAndBrush("LayerBackgroundColor", "LayerBackgroundBrush", SystemColors.WindowColor, SystemColors.WindowBrush);
        SetColorAndBrush("LayerBackgroundFillColor", "LayerBackgroundFillBrush", SystemColors.ControlColor, SystemColors.ControlBrush);
        SetResource("LayerOnMicaBackgroundColor", SystemColors.WindowColor);
        SetColorAndBrush("LayerAltBackgroundColor", "LayerAltBackgroundBrush", SystemColors.ControlColor, SystemColors.ControlBrush);
        SetColorAndBrush("MicaBackgroundColor", "MicaBackgroundBrush", SystemColors.WindowColor, SystemColors.WindowBrush);
        SetColorAndBrush("MicaBorderColor", "MicaBorderBrush", SystemColors.WindowTextColor, SystemColors.WindowTextBrush);
        SetResource("AcrylicBackgroundColor", SystemColors.WindowColor);
        SetResource("AcrylicBackgroundFallbackColor", SystemColors.WindowColor);

        SetColorAndBrush("DangerColor", "DangerBrush", SystemColors.HotTrackColor, SystemColors.HotTrackBrush);
        SetColorAndBrush("SuccessColor", "SuccessBrush", SystemColors.HighlightColor, SystemColors.HighlightBrush);
        SetColorAndBrush("WarningColor", "WarningBrush", SystemColors.HighlightColor, SystemColors.HighlightBrush);

        SetColorAndBrush("ThemeWindowColor", "ThemeWindowBrush", SystemColors.WindowColor, SystemColors.WindowBrush);
        SetColorAndBrush("ThemeSurfaceColor", "ThemeSurfaceBrush", SystemColors.ControlColor, SystemColors.ControlBrush);
        SetColorAndBrush("ThemeTextColor", "ThemeTextBrush", SystemColors.WindowTextColor, SystemColors.WindowTextBrush);
        SetColorAndBrush("ThemeSecondaryTextColor", "ThemeSecondaryTextBrush", SystemColors.ControlTextColor, SystemColors.ControlTextBrush);
        SetColorAndBrush("ThemeBorderColor", "ThemeBorderBrush", SystemColors.WindowTextColor, SystemColors.WindowTextBrush);
    }

    private void SetColorAndBrush(string colorKey, string brushKey, Color color, Brush brush)
    {
        SetResource(colorKey, color);
        SetResource(brushKey, brush);
    }

    private void SetMany(IEnumerable<string> keys, object value)
    {
        foreach (var key in keys)
            SetResource(key, value);
    }

    private void SetResource(string key, object value)
    {
        Application.Current.Resources[key] = value;
        _highContrastOverrideKeys.Add(key);
    }

    private void ClearHighContrastOverrides()
    {
        foreach (var key in _highContrastOverrideKeys)
            Application.Current.Resources.Remove(key);
        _highContrastOverrideKeys.Clear();
    }

    private void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(SystemParameters.HighContrast) and
            not nameof(SystemParameters.ClientAreaAnimation))
        {
            return;
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;
        dispatcher.BeginInvoke(() => Apply(_requestedTheme, _requestedAnimations));
    }

    private static bool IsHighContrastActive() =>
        SystemParameters.HighContrast ||
        (string.Equals(Environment.GetEnvironmentVariable("HD2MM_RUN_UI_TESTS"), "1", StringComparison.Ordinal) &&
         string.Equals(Environment.GetEnvironmentVariable("HD2MM_TEST_FORCE_HIGH_CONTRAST"), "1", StringComparison.Ordinal));
}
