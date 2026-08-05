using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewModSelectionTests
{
    [TestMethod]
    public void Resolve_NewlyImportedPreferredModMissingFromSnapshot_KeepsAndSelectsIt()
    {
        var existingMod = CreateMod("Existing");
        var newlyImportedMod = CreateMod("Newly imported");

        var state = ModelPreviewModSelection.Resolve([existingMod], newlyImportedMod);

        CollectionAssert.AreEqual(new[] { existingMod, newlyImportedMod }, state.Mods.ToArray());
        Assert.AreSame(newlyImportedMod, state.SelectedMod);
    }

    [TestMethod]
    public void Resolve_SnapshotHasSameGuid_PrefersTheDashboardModInstance()
    {
        var modId = Guid.NewGuid();
        var staleSnapshotMod = CreateMod("Stale", modId);
        var dashboardMod = CreateMod("Current", modId);

        var state = ModelPreviewModSelection.Resolve([staleSnapshotMod], dashboardMod);

        CollectionAssert.AreEqual(new[] { dashboardMod }, state.Mods.ToArray());
        Assert.AreSame(dashboardMod, state.SelectedMod);
    }

    private static ModData CreateMod(string name, Guid? guid = null) => new(
        new DirectoryInfo(Path.Combine(Path.GetTempPath(), "hd2mm-model-preview-tests", Guid.NewGuid().ToString("N"))),
        new LegacyModManifest
        {
            Guid = guid ?? Guid.NewGuid(),
            Name = name,
            Description = string.Empty
        });
}
