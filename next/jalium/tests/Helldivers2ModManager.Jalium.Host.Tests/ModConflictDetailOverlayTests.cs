using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Host;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Host.Tests;

[TestClass]
public sealed class ModConflictDetailOverlayTests
{
    [TestMethod]
    public void DetailKeepsOriginalRegionsAndUpdatesLanguage()
    {
        var localization = CreateLocalization();
        using var overlay = new ModConflictDetailOverlay(localization);
        var dialog = (Border)overlay.Children[0];
        Assert.AreEqual(820, dialog.MaxWidth);
        Assert.AreEqual(650, dialog.MaxHeight);
        var first = Participant(Guid.NewGuid(), "First", 0, 1);
        var second = Participant(Guid.NewGuid(), "Second", 1, 2);
        overlay.Show("First", [new ModConflictRecord
        {
            UnitId = 42, FriendlyName = "Armor", OriginalName = "0x000000000000002A",
            Participants = [first, second],
        }]);

        Assert.AreEqual(Visibility.Visible, overlay.Visibility);
        Assert.AreEqual(1, overlay.DisplayedConflictCount);
        Assert.AreEqual(localization["ConflictDetail.ConflictSummary"], overlay.SummaryText);
        localization.SelectedLanguage = "en-US";
        Assert.AreEqual(localization["ConflictDetail.ConflictSummary"], overlay.SummaryText);
        overlay.Close();
        Assert.AreEqual(Visibility.Collapsed, overlay.Visibility);

        overlay.Show("First", []);
        Assert.AreEqual(0, overlay.DisplayedConflictCount);
        Assert.AreEqual(localization["ConflictDetail.NoConflictsDescription"], overlay.SummaryText);
    }

    [TestMethod]
    public void ToastLimitsVisibleMessages()
    {
        using var toast = new ToastOverlay(Dispatcher.CurrentDispatcher);
        for (var index = 0; index < 5; index++)
            toast.Show("scan", index.ToString());
        Assert.AreEqual(4, toast.VisibleCount);
        Assert.AreEqual(Visibility.Visible, toast.Visibility);
    }

    private static ModConflictParticipant Participant(Guid guid, string name, int order, uint version)
        => new()
        {
            ModGuid = guid, ModName = name, DeploymentOrder = order,
            PatchFileName = "0011223344556677.patch_0", UnitId = 42,
            Version = version, DataSize = 48, GpuSize = 0,
        };

    private static LocalizationService CreateLocalization([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!);
             current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
            {
                var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
                    Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language"));
                localization.SelectedLanguage = "zh-CN";
                return localization;
            }
        throw new DirectoryNotFoundException("Repository language files not found.");
    }
}
