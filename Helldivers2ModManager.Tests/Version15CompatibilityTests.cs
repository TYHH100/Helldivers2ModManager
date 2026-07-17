using Helldivers2ModManager.Infrastructure.Settings;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Helldivers2ModManager.Tests;

public sealed class Version15CompatibilityTests
{
    [Fact]
    public async Task Version15ModDirectoryPreservesManifestIdentityAndUserMetadata()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var modDirectory = Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "Legacy Mod"));
        var modId = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(modDirectory.FullName, "manifest.json"), $$"""
			{
			  "Version": 1,
			  "Guid": "{{modId}}",
			  "Name": "Legacy Visual Pack",
			  "Description": "Created with Helldivers2ModManager 1.5",
			  "IconPath": "preview.png"
			}
			""");
        await File.WriteAllBytesAsync(Path.Combine(modDirectory.FullName, "preview.png"), [1, 2, 3, 4]);

        var manifest = ModManifest.DeserializeFromDirectory(
            modDirectory,
            NullLogger.Instance);

        Assert.Equal(ManifestVersion.V1, manifest.Version);
        Assert.Equal(modId, manifest.Guid);
        Assert.Equal("Legacy Visual Pack", manifest.Name);
        Assert.Equal("Created with Helldivers2ModManager 1.5", manifest.Description);
        Assert.Equal("preview.png", manifest.IconPath);
    }

    [Fact]
    public async Task Version15BackupFilenameRemainsDiscoverableInHistory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var backupPath = Path.Combine(
            temporaryDirectory.Path,
            "data.patch-backup_0.20250102-030405.hd2mm-backup");
        await File.WriteAllBytesAsync(backupPath, [1, 2, 3, 4]);
        using var settingsStore = new AtomicJsonSettingsStore(
            Path.Combine(temporaryDirectory.Path, "settings.json"));
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, settingsStore);
        settings.InitDefault();
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance);
        var service = new VersionCheckService(
            NullLogger<VersionCheckService>.Instance,
            settings,
            localization);

        var history = await service.GetBackupHistoryAsync(
            new DirectoryInfo(temporaryDirectory.Path));

        var entry = Assert.Single(history.Entries);
        Assert.Equal(Path.GetFullPath(backupPath), entry.BackupPath);
        Assert.Equal("data.patch_0", Path.GetFileName(entry.OriginalPath));
        Assert.Equal(4, entry.BackupSize);
    }
}
