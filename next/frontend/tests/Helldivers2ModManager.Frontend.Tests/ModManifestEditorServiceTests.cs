using System.IO;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Frontend.Models;
using Helldivers2ModManager.Frontend.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Frontend.Tests;

[TestClass]
public sealed class ModManifestEditorServiceTests
{
    private string? _root;

    [TestCleanup]
    public void Cleanup()
    {
        if (_root is not null && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SaveAsync_UpgradesLegacyWhenV1FeatureIsUsedAndPreservesIdentity()
    {
        _root = Path.Combine(Path.GetTempPath(), "Helldivers2ModManagerFrontendTests", Guid.NewGuid().ToString("N"));
        var modDirectory = Directory.CreateDirectory(Path.Combine(_root, "Test Mod"));
        var guid = Guid.NewGuid();
        var item = new ModItem(new DiscoveredMod(modDirectory, new LegacyModManifest
        {
            Guid = guid,
            Name = "Test Mod",
            Description = "before",
            Options = ["OptionA"],
        }));
        var editor = new ModManifestEditorService();

        var upgraded = await editor.SaveAsync(item, new ManifestEditDraft(
            "Renamed Mod",
            "new description",
            null,
            [new("OptionA", "described", ["OptionA"], null, [new("Red", "alternate", ["red"], null)])]));

        Assert.IsTrue(upgraded);
        var manifest = AssertManifest(modDirectory);
        Assert.AreEqual(guid, manifest.Guid);
        Assert.AreEqual("Renamed Mod", manifest.Name);
        Assert.AreEqual("described", manifest.Options![0].Description);
        Assert.AreEqual("red", manifest.Options[0].SubOptions![0].Include[0]);
    }

    [TestMethod]
    public async Task SaveAsync_PreservesV1NexusData()
    {
        _root = Path.Combine(Path.GetTempPath(), "Helldivers2ModManagerFrontendTests", Guid.NewGuid().ToString("N"));
        var modDirectory = Directory.CreateDirectory(Path.Combine(_root, "Nexus Mod"));
        var nexus = new NexusManifestData(42, "1.2.3");
        var item = new ModItem(new DiscoveredMod(modDirectory, new V1ModManifest
        {
            Guid = Guid.NewGuid(),
            Name = "Nexus Mod",
            Description = string.Empty,
            Options = [],
            NexusData = nexus,
        }));
        var editor = new ModManifestEditorService();

        await editor.SaveAsync(item, new ManifestEditDraft("Nexus Mod", "changed", null, []));

        Assert.AreEqual(nexus, AssertManifest(modDirectory).NexusData);
    }

    private static V1ModManifest AssertManifest(DirectoryInfo directory) =>
        (V1ModManifest)ModManifest.DeserializeFromDirectory(directory);
}
