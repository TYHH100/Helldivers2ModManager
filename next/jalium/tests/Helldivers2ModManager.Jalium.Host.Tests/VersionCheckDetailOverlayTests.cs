using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Host;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Host.Tests;

[TestClass]
public sealed class VersionCheckDetailOverlayTests
{
    [TestMethod]
    public void Detail_ShowsStructuralIssueAndCompleteReport()
    {
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            GetLanguageDirectory());
        localization.SelectedLanguage = "zh-CN";
        var settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(Path.GetTempPath(), "jalium-version-detail-test-settings.json"), Path.GetTempPath());
        using var messageBoxOverlay = new MessageBoxOverlay(localization);
        using var overlay = new VersionCheckDetailOverlay(localization, settings,
            () => null, () => Task.CompletedTask, () => null, messageBoxOverlay);
        var mod = new ModData(new DirectoryInfo(Path.GetTempPath()),
            new LegacyModManifest { Guid = Guid.NewGuid(), Name = "Fixture Mod", Description = string.Empty, Options = [] });
        var analysis = new ModDetailedAnalysis
        {
            TotalPatchFiles = 1,
            CorruptedFileCount = 1,
            PatchFiles =
            [
                new PatchFileAnalysis
                {
                    FileName = "fixture.patch_0", HealthStatus = PatchHealthStatus.Corrupted,
                    HeaderValid = false, FileEntriesInBounds = false, Message = "Invalid TOC",
                },
            ],
        };
        var result = new ModVersionCheckResult
        {
            Status = ModVersionStatus.Incompatible,
            GameVersion = 10800438,
            DetailedAnalysis = analysis,
        };

        overlay.Show(mod, result);

        Assert.IsTrue(overlay.IsOpen);
        Assert.IsTrue(overlay.IssueCount > 0);
        StringAssert.Contains(overlay.ReportText, "Fixture Mod");
        StringAssert.Contains(overlay.ReportText, "fixture.patch_0");
        overlay.Close();
        Assert.IsFalse(overlay.IsOpen);
    }

    [TestMethod]
    public void Detail_GroupsVersionMismatchAndShowsAllStructuralIssues()
    {
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance, GetLanguageDirectory());
        localization.SelectedLanguage = "en-US";
        var settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(Path.GetTempPath(), "jalium-version-detail-test-settings.json"), Path.GetTempPath());
        using var messageBoxOverlay = new MessageBoxOverlay(localization);
        using var overlay = new VersionCheckDetailOverlay(localization, settings,
            () => null, () => Task.CompletedTask, () => null, messageBoxOverlay);
        var mod = new ModData(new DirectoryInfo(Path.GetTempPath()),
            new LegacyModManifest { Guid = Guid.NewGuid(), Name = "Fixture", Description = string.Empty, Options = [] });
        var analysis = new ModDetailedAnalysis
        {
            TotalPatchFiles = 1,
            PatchFiles =
            [
                new PatchFileAnalysis
                {
                    FileName = "fixture.patch_0", HealthStatus = PatchHealthStatus.Corrupted,
                    HeaderValid = false, FileEntriesInBounds = false, TypeDistributionValid = false,
                    MainDataBoundsValid = false, EntryIndicesValid = false,
                    RequiresGpuResources = true, RequiresStream = true,
                    GpuResourceBoundsValid = false, StreamBoundsValid = false,
                    Message = "Structural warning",
                    UnitDetails = [new UnitResourceDetail
                    {
                        EntryIndex = 7, FileId = 0x1234, DataSize = 10, ExpectedDataSize = 20,
                        IsTruncated = true, UnitDataInBounds = true, LODGroupInBounds = true,
                    }],
                },
            ],
        };
        var result = new ModVersionCheckResult
        {
            Status = ModVersionStatus.Incompatible, GameVersion = 2, DetailedAnalysis = analysis,
            PatchUnits = [new PatchUnitInfo { FileId = 0x1234, Version = 1 },
                new PatchUnitInfo { FileId = 0x5678, Version = 1 }],
        };

        overlay.Show(mod, result);

        Assert.AreEqual(10, overlay.IssueCount);
        Assert.AreEqual(1, overlay.IssueTitles.Count(title => title == "Unit version mismatch"));
        CollectionAssert.Contains(overlay.IssueTitles.ToList(), "Unit #7 data is truncated");
        Assert.IsFalse(overlay.IssueTitles.Any(title => title.Contains("{index}", StringComparison.Ordinal)));
        Assert.IsFalse(overlay.IssueTitles.Contains("Patch file warning"));
    }

    [TestMethod]
    public void RollbackPreview_CountsOnlyRestorableChangedFilesAtSelectedMinute()
    {
        var point = new DateTime(2026, 9, 25, 12, 30, 0);
        ModBackupEntry Entry(string path, DateTime time, bool restorable, bool matches) => new()
        {
            BackupPath = path + ".backup", OriginalPath = path, CreatedLocal = time,
            BackupSize = 1, BackupSha256 = "hash", CanRestore = restorable, CurrentMatchesBackup = matches,
        };
        var history = new ModBackupHistory { Entries =
        [
            Entry("a.patch", point.AddSeconds(-1), true, false),
            Entry("a.patch", point.AddMinutes(-2), true, false),
            Entry("b.patch", point, true, true),
            Entry("c.patch", point.AddMinutes(-1), false, false),
            Entry("d.patch", point.AddMinutes(1), true, false),
            Entry("e.patch", point.AddSeconds(45), true, false),
        ] };

        Assert.AreEqual(2, VersionCheckDetailOverlay.CountPendingRollback(history, point));
        Assert.AreEqual(point.AddMinutes(1).AddTicks(-1), VersionCheckDetailOverlay.RollbackCutoff(point));
    }

    [TestMethod]
    public void Detail_SwitchingModClearsPreviousIssuesAndReport()
    {
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance, GetLanguageDirectory());
        var settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(Path.GetTempPath(), "jalium-version-detail-test-settings.json"), Path.GetTempPath());
        using var messageBoxOverlay = new MessageBoxOverlay(localization);
        using var overlay = new VersionCheckDetailOverlay(localization, settings,
            () => null, () => Task.CompletedTask, () => null, messageBoxOverlay);
        ModData Mod(string name) => new(new DirectoryInfo(Path.GetTempPath()),
            new LegacyModManifest { Guid = Guid.NewGuid(), Name = name, Description = string.Empty, Options = [] });
        overlay.Show(Mod("Old mod"), new ModVersionCheckResult
        {
            Status = ModVersionStatus.Incompatible, GameVersion = 2,
            PatchUnits = [new PatchUnitInfo { FileId = 1, Version = 1 }],
        });
        Assert.AreEqual(1, overlay.IssueCount);

        overlay.Show(Mod("New mod"), new ModVersionCheckResult
        {
            Status = ModVersionStatus.Compatible, GameVersion = 2,
        });

        Assert.AreEqual(0, overlay.IssueCount);
        Assert.IsFalse(overlay.ReportText.Contains("Old mod", StringComparison.Ordinal));
        StringAssert.Contains(overlay.ReportText, "New mod");
    }

    private static string GetLanguageDirectory([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!); current is not null;
             current = current.Parent)
        {
            var path = Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("Language resources were not found.");
    }
}
