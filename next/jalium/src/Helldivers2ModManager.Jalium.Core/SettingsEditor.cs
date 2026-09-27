using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Win32;

namespace Helldivers2ModManager.Jalium.Core;

internal sealed class SettingsEditor(SettingsService settings, LocalizationService localization)
{
    public SettingsService Settings => settings;

    public void SetGameDirectory(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (directory.Name.Equals("data", StringComparison.OrdinalIgnoreCase))
            directory = directory.Parent ?? directory;
        ValidateGameDirectory(directory);
        settings.GameDirectory = directory.FullName;
    }

    public void SetStorageDirectory(string path) => settings.StorageDirectory = Path.GetFullPath(path);
    public void SetTempDirectory(string path) => settings.TempDirectory = Path.GetFullPath(path);

    public Task<string?> DetectGameAsync() => Task.Run(() =>
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var keyName in new[] { @"Software\Valve\Steam", @"Software\Wow6432Node\Valve\Steam" })
            {
                try
                {
                    using var key = hive.OpenSubKey(keyName);
                    var steam = key?.GetValue("SteamPath") as string ?? key?.GetValue("InstallPath") as string;
                    if (string.IsNullOrWhiteSpace(steam) || !Directory.Exists(steam)) continue;
                    libraries.Add(steam);
                    var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                    if (!File.Exists(vdf)) continue;
                    foreach (System.Text.RegularExpressions.Match match in
                             System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf),
                                 "\"path\"\\s*\"([^\"]+)\""))
                        libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
                }
                catch (Exception) { }
            }
        }
        foreach (var drive in Environment.GetLogicalDrives())
        {
            libraries.Add(Path.Combine(drive, "Steam"));
            libraries.Add(Path.Combine(drive, "SteamLibrary"));
            if (drive.Equals(@"C:\", StringComparison.OrdinalIgnoreCase))
                libraries.Add(Path.Combine(drive, "Program Files (x86)", "Steam"));
        }
        foreach (var library in libraries)
        {
            var game = Path.Combine(library, "steamapps", "common", "Helldivers 2");
            try
            {
                ValidateGameDirectory(new DirectoryInfo(game));
                return game;
            }
            catch (IOException) { }
            catch (InvalidDataException) { }
        }
        return null;
    });

    public void SetSymbolicLinks(bool enabled)
    {
        settings.UseSymbolicLinks = enabled;
        if (enabled) settings.UseHardLinks = false;
    }

    public void SetHardLinks(bool enabled)
    {
        settings.UseHardLinks = enabled;
        if (enabled) settings.UseSymbolicLinks = false;
    }

    public bool AddOrganizationFolder(string name)
    {
        var normalized = name.Trim();
        if (normalized.Length == 0 || normalized.Length > 100)
            throw new ArgumentException("Folder name must contain 1 to 100 characters.", nameof(name));
        if (settings.OrganizationalFolderNames.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            return false;
        settings.OrganizationalFolderNames.Add(normalized);
        return true;
    }

    public bool RemoveOrganizationFolder(string name)
    {
        if (name.Equals("Models", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Model", StringComparison.OrdinalIgnoreCase))
            return false;
        return settings.OrganizationalFolderNames.Remove(name);
    }

    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(settings.GameDirectory))
            throw new InvalidOperationException(localization["SettingsPage.ValidateGameDirEmpty"]);
        if (string.IsNullOrWhiteSpace(settings.StorageDirectory))
            throw new InvalidOperationException(localization["SettingsPage.ValidateStorageDirEmpty"]);
        if (string.IsNullOrWhiteSpace(settings.TempDirectory))
            throw new InvalidOperationException(localization["SettingsPage.ValidateTempDirEmpty"]);
        ValidateGameDirectory(new DirectoryInfo(settings.GameDirectory));
        if (!settings.Validate())
            throw new InvalidOperationException(localization["SettingsPage.SettingsValid"]);
        await settings.SaveAsync();
        settings.CleanExcessLogs();
    }

    public async Task CancelAsync()
    {
        await settings.ReloadAsync();
        localization.SelectedLanguage = settings.Language;
    }

    private void ValidateGameDirectory(DirectoryInfo directory)
    {
        if (!directory.Exists)
            throw new DirectoryNotFoundException(localization["SettingsPage.ValidateGameDirNotExist"]);
        if (!directory.Name.Equals("Helldivers 2", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(localization["SettingsPage.ValidateGameDirInvalid"]);
        if (!Directory.Exists(Path.Combine(directory.FullName, "data")))
            throw new InvalidDataException(localization["SettingsPage.ValidateGameDirNoData"]);
        if (!Directory.Exists(Path.Combine(directory.FullName, "tools")))
            throw new InvalidDataException(localization["SettingsPage.ValidateGameDirNoTools"]);
        var bin = Path.Combine(directory.FullName, "bin");
        if (!Directory.Exists(bin))
            throw new InvalidDataException(localization["SettingsPage.ValidateGameDirNoBin"]);
        if (!File.Exists(Path.Combine(bin, "helldivers2.exe")))
            throw new InvalidDataException(localization["SettingsPage.ValidateGameDirNoExe"]);
    }
}
