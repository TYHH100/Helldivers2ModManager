using System.Globalization;
using System.IO;
using Helldivers2ModManager.Core.Persistence;

namespace Helldivers2ModManager.Frontend.Services;

public sealed class ApplicationSettingsService(
    ApplicationPaths paths,
    PreferenceRepository preferences)
{
    private const string SettingsKey = "frontend.app";
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public AppSettings Current { get; private set; } = CreateDefault();

    public event EventHandler<AppSettings>? Saved;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.Root);
        Directory.CreateDirectory(paths.Data);
        Directory.CreateDirectory(paths.ModStorage);
        Directory.CreateDirectory(paths.Temp);
        Directory.CreateDirectory(paths.GameData);

        if (!File.Exists(paths.Boot))
        {
            await BootConfigurationStore.SaveAsync(new BootConfiguration
            {
                StorageDirectory = paths.Data,
                TempDirectory = paths.Temp,
            }, paths.Boot, cancellationToken).ConfigureAwait(false);
        }

        Current = await preferences.GetAppSettingsAsync(SettingsKey, cancellationToken).ConfigureAwait(false) ?? CreateDefault();
        var normalizedStorage = NormalizeStorageDirectory(Current.StorageDirectory);
        var normalizedTemp = NormalizeTempDirectory(Current.TempDirectory);
        if (!string.Equals(Current.StorageDirectory, normalizedStorage, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Current.TempDirectory, normalizedTemp, StringComparison.OrdinalIgnoreCase))
        {
            Current.StorageDirectory = normalizedStorage;
            Current.TempDirectory = normalizedTemp;
            await preferences.SetAppSettingsAsync(SettingsKey, Current, cancellationToken).ConfigureAwait(false);
        }

        var boot = await BootConfigurationStore.LoadAsync(paths.Boot, cancellationToken).ConfigureAwait(false);
        if (boot is not null)
        {
            boot = boot with
            {
                StorageDirectory = NormalizeStorageDirectory(boot.StorageDirectory),
                TempDirectory = NormalizeTempDirectory(boot.TempDirectory),
            };
            await BootConfigurationStore.SaveAsync(boot, paths.Boot, cancellationToken).ConfigureAwait(false);
        }

        ApplyCulture(Current.Language);
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            settings.StorageDirectory = NormalizeStorageDirectory(settings.StorageDirectory);
            settings.TempDirectory = NormalizeTempDirectory(settings.TempDirectory);

            if (string.IsNullOrWhiteSpace(settings.StorageDirectory))
            {
                settings.StorageDirectory = paths.Data;
            }
            if (string.IsNullOrWhiteSpace(settings.TempDirectory))
            {
                settings.TempDirectory = paths.Temp;
            }

            Directory.CreateDirectory(settings.StorageDirectory);
            Directory.CreateDirectory(Path.Combine(settings.StorageDirectory, "Mods"));
            Directory.CreateDirectory(settings.TempDirectory);
            await preferences.SetAppSettingsAsync(SettingsKey, settings, cancellationToken).ConfigureAwait(false);
            Current = settings;
            ApplyCulture(settings.Language);
            Saved?.Invoke(this, settings);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public static void ApplyCulture(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return;
        }

        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
    }

    private static AppSettings CreateDefault() => new()
    {
        Language = "zh-CN",
        DeleteToRecycleBin = true,
        AutoCleanLogs = true,
        MaxLogFiles = 20,
        EnableFuzzySearch = true,
    };

    private string NormalizeStorageDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return paths.Data;
        }

        var fullPath = Path.GetFullPath(value);
        var dataPath = Path.GetFullPath(paths.Data);
        var legacyModStorage = Path.GetFullPath(paths.ModStorage);
        var legacyLowercaseStorage = Path.GetFullPath(Path.Combine(paths.Data, "mods"));
        var duplicatedStorage = Path.GetFullPath(Path.Combine(paths.Data, "data", "mods"));

        return fullPath.Equals(dataPath, StringComparison.OrdinalIgnoreCase) ||
               fullPath.Equals(legacyModStorage, StringComparison.OrdinalIgnoreCase) ||
               fullPath.Equals(legacyLowercaseStorage, StringComparison.OrdinalIgnoreCase) ||
               fullPath.Equals(duplicatedStorage, StringComparison.OrdinalIgnoreCase)
            ? paths.Data
            : fullPath;
    }

    private string NormalizeTempDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return paths.Temp;
        }

        var fullPath = Path.GetFullPath(value);
        var duplicatedTemp = Path.GetFullPath(Path.Combine(paths.Data, "data", "temp"));
        return fullPath.Equals(duplicatedTemp, StringComparison.OrdinalIgnoreCase) ? paths.Temp : fullPath;
    }
}
