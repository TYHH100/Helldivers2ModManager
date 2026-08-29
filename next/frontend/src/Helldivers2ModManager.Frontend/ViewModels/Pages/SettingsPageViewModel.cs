using System.IO;
using System.Windows.Input;
using System.Text.RegularExpressions;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Services;
using Microsoft.Win32;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed class SettingsPageViewModel : FrontendPageViewModel
{
    private readonly ApplicationSettingsService _settings;
    private readonly LocalizationCatalog _localization;
    private bool _isBusy;
    private string _status = string.Empty;
    private string _gameDirectory = string.Empty;
    private string _storageDirectory = string.Empty;
    private string _tempDirectory = string.Empty;
    private string _language = "zh-CN";
    private bool _useSymbolicLinks;
    private bool _deleteToRecycleBin;
    private bool _enableFuzzySearch;
    private bool _autoCheckVersionOnStartup;
    private bool _enableAutoTagging;
    private bool _autoTagCreateMissingTags;
    private string _nexusApiKey = string.Empty;

    public IReadOnlyList<string> Languages { get; } = ["zh-CN", "en-US"];

    public string GameDirectory { get => _gameDirectory; set => SetProperty(ref _gameDirectory, value); }
    public string StorageDirectory { get => _storageDirectory; set => SetProperty(ref _storageDirectory, value); }
    public string TempDirectory { get => _tempDirectory; set => SetProperty(ref _tempDirectory, value); }
    public string Language { get => _language; set => SetProperty(ref _language, value); }
    public bool UseSymbolicLinks { get => _useSymbolicLinks; set => SetProperty(ref _useSymbolicLinks, value); }
    public bool DeleteToRecycleBin { get => _deleteToRecycleBin; set => SetProperty(ref _deleteToRecycleBin, value); }
    public bool EnableFuzzySearch { get => _enableFuzzySearch; set => SetProperty(ref _enableFuzzySearch, value); }
    public bool AutoCheckVersionOnStartup { get => _autoCheckVersionOnStartup; set => SetProperty(ref _autoCheckVersionOnStartup, value); }
    public bool EnableAutoTagging { get => _enableAutoTagging; set => SetProperty(ref _enableAutoTagging, value); }
    public bool AutoTagCreateMissingTags { get => _autoTagCreateMissingTags; set => SetProperty(ref _autoTagCreateMissingTags, value); }
    public string NexusApiKey { get => _nexusApiKey; set => SetProperty(ref _nexusApiKey, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public ICommand BrowseGameCommand { get; }
    public ICommand DetectGameCommand { get; }
    public ICommand BrowseStorageCommand { get; }
    public ICommand BrowseTempCommand { get; }
    public ICommand SaveCommand { get; }

    public override string Title => _localization.GetString("Nav.Settings");

    public SettingsPageViewModel(ApplicationSettingsService settings, LocalizationCatalog localization)
    {
        _settings = settings;
        _localization = localization;
        BrowseGameCommand = new DelegateCommand(_ => GameDirectory = BrowseFolder(GameDirectory));
        DetectGameCommand = new DelegateCommand(async _ => await DetectGameAsync(), _ => !IsBusy);
        BrowseStorageCommand = new DelegateCommand(_ => StorageDirectory = BrowseFolder(StorageDirectory));
        BrowseTempCommand = new DelegateCommand(_ => TempDirectory = BrowseFolder(TempDirectory));
        SaveCommand = new DelegateCommand(async _ => await SaveAsync());
        LoadCurrent();
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        LoadCurrent();
        return Task.CompletedTask;
    }

    private void LoadCurrent()
    {
        var value = _settings.Current;
        GameDirectory = value.GameDirectory;
        StorageDirectory = value.StorageDirectory;
        TempDirectory = value.TempDirectory;
        Language = string.IsNullOrWhiteSpace(value.Language) ? "zh-CN" : value.Language;
        UseSymbolicLinks = value.UseSymbolicLinks;
        DeleteToRecycleBin = value.DeleteToRecycleBin;
        EnableFuzzySearch = value.EnableFuzzySearch;
        AutoCheckVersionOnStartup = value.AutoCheckVersionOnStartup;
        EnableAutoTagging = value.EnableAutoTagging;
        AutoTagCreateMissingTags = value.AutoTagCreateMissingTags;
        NexusApiKey = value.NexusApiKey ?? string.Empty;
    }

    private async Task SaveAsync()
    {
        SetBusy(true, _localization.GetString("Settings.Saving"));
        try
        {
            var settings = _settings.Current;
            settings.GameDirectory = GameDirectory;
            settings.StorageDirectory = StorageDirectory;
            settings.TempDirectory = TempDirectory;
            settings.Language = Language;
            settings.UseSymbolicLinks = UseSymbolicLinks;
            settings.DeleteToRecycleBin = DeleteToRecycleBin;
            settings.EnableFuzzySearch = EnableFuzzySearch;
            settings.AutoCheckVersionOnStartup = AutoCheckVersionOnStartup;
            settings.EnableAutoTagging = EnableAutoTagging;
            settings.AutoTagCreateMissingTags = AutoTagCreateMissingTags;
            settings.NexusApiKey = string.IsNullOrWhiteSpace(NexusApiKey) ? null : NexusApiKey;
            await _settings.SaveAsync(settings).ConfigureAwait(true);
            Status = _localization.GetString("Settings.Saved");
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private static string BrowseFolder(string initialPath)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(initialPath) ? initialPath : AppContext.BaseDirectory };
        return dialog.ShowDialog() == true ? dialog.FolderName : initialPath;
    }

    private async Task DetectGameAsync()
    {
        if (IsBusy)
        {
            return;
        }

        SetBusy(true, _localization.GetString("Next.Settings.DetectingGame"));
        try
        {
            var gameDirectory = await Task.Run(FindGameDirectory).ConfigureAwait(true);
            if (gameDirectory is null)
            {
                Status = _localization.GetString("Next.Settings.GameNotFound");
                return;
            }

            GameDirectory = gameDirectory;
            Status = _localization.GetString("Next.Settings.GameFound");
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private static string? FindGameDirectory()
    {
        var steamPath = GetSteamInstallPath();
        if (!string.IsNullOrWhiteSpace(steamPath))
        {
            foreach (var library in GetSteamLibraryFolders(steamPath))
            {
                var candidate = Path.Combine(library, "steamapps", "common", "Helldivers 2");
                if (IsValidGameDirectory(candidate))
                {
                    return candidate;
                }
            }
        }

        foreach (var drive in Environment.GetLogicalDrives())
        {
            foreach (var libraryName in (string[])["Steam", "SteamLibrary"])
            {
                var candidate = Path.Combine(drive, libraryName, "steamapps", "common", "Helldivers 2");
                if (IsValidGameDirectory(candidate))
                {
                    return candidate;
                }
            }

            if (drive.Equals(@"C:\", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(drive, "Program Files (x86)", "Steam", "steamapps", "common", "Helldivers 2");
                if (IsValidGameDirectory(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool IsValidGameDirectory(string path)
    {
        return Directory.Exists(Path.Combine(path, "data")) &&
               Directory.Exists(Path.Combine(path, "bin")) &&
               File.Exists(Path.Combine(path, "bin", "helldivers2.exe"));
    }

    private static string? GetSteamInstallPath()
    {
        try
        {
            using var currentUser = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (currentUser?.GetValue("SteamPath") is string userPath && Directory.Exists(userPath))
            {
                return userPath;
            }

            using var localMachine = Registry.LocalMachine.OpenSubKey(@"Software\Valve\Steam");
            if (localMachine?.GetValue("InstallPath") is string installPath && Directory.Exists(installPath))
            {
                return installPath;
            }

            using var wow64 = Registry.LocalMachine.OpenSubKey(@"Software\Wow6432Node\Valve\Steam");
            if (wow64?.GetValue("InstallPath") is string wow64Path && Directory.Exists(wow64Path))
            {
                return wow64Path;
            }
        }
        catch
        {
        }

        return null;
    }

    private static IReadOnlyList<string> GetSteamLibraryFolders(string steamPath)
    {
        List<string> libraries = [steamPath];
        try
        {
            var libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile))
            {
                return libraries;
            }

            var matches = Regex.Matches(File.ReadAllText(libraryFile), "\"path\"\\s*\"([^\"]+)\"");
            foreach (Match match in matches)
            {
                var path = match.Groups[1].Value.Replace(@"\\", @"\");
                if (!libraries.Contains(path, StringComparer.OrdinalIgnoreCase) && Directory.Exists(path))
                {
                    libraries.Add(path);
                }
            }
        }
        catch
        {
        }

        return libraries;
    }

    private void SetBusy(bool busy, string status)
    {
        IsBusy = busy;
        ((DelegateCommand)DetectGameCommand).NotifyCanExecuteChanged();
        Status = status;
    }
}
