using Helldivers2ModManager.Adapters;
using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ArmorReuseMapperTests
{
    [TestMethod]
    public void Map_LegacyManifest_PreservesSelectedOptionState()
    {
        var directory = new DirectoryInfo(@"C:\mods\legacy");
        var manifest = new LegacyModManifest
        {
            Guid = Guid.NewGuid(),
            Name = "Legacy Mod",
            Description = "Test",
            Options = ["alpha", "beta"],
        };
        var mod = new ModData(directory, manifest);
        mod.ApplyData(new EnabledData
        {
            Guid = manifest.Guid,
            Enabled = true,
            Toggled = [],
            Selected = [1],
        });

        var mapped = ArmorReuseMapper.Map(mod, 3);

        Assert.AreEqual(mod.Manifest.Guid, mapped.Id);
        Assert.AreEqual("Legacy Mod", mapped.Name);
        Assert.IsTrue(mapped.Enabled);
        Assert.AreEqual(3, mapped.DeploymentOrder);
        var legacy = (Core.Mods.LegacyModManifest)mapped.Manifest!;
        CollectionAssert.AreEqual(new[] { "alpha", "beta" }, legacy.Options!.ToArray());
        CollectionAssert.AreEqual(new[] { 1 }, mapped.SelectedOptions!.ToArray());
    }

    [TestMethod]
    public void Map_V1Manifest_PreservesOptionsAndSubSelection()
    {
        var directory = new DirectoryInfo(@"C:\mods\v1");
        var manifest = new V1ModManifest
        {
            Guid = Guid.NewGuid(),
            Name = "V1 Mod",
            Description = "Test",
            Options =
            [
                new() { Name = "First", Include = ["first"] },
                new()
                {
                    Name = "Second",
                    Include = ["second-base"],
                    SubOptions =
                    [
                        new() { Name = "Slim", Include = ["slim"] },
                        new() { Name = "Stocky", Include = ["stocky"] },
                    ],
                },
            ],
        };
        var mod = new ModData(directory, manifest);
        mod.ApplyData(new EnabledData
        {
            Guid = manifest.Guid,
            Enabled = true,
            Toggled = [false, true],
            Selected = [0, 1],
        });

        var mapped = ArmorReuseMapper.Map(mod, 0);
        var v1 = (Core.Mods.V1ModManifest)mapped.Manifest!;

        Assert.AreEqual(2, v1.Options!.Count);
        Assert.IsFalse(mapped.EnabledOptions![0]);
        Assert.IsTrue(mapped.EnabledOptions![1]);
        Assert.AreEqual(1, mapped.SelectedOptions![1]);
        Assert.AreEqual("stocky", v1.Options![1].SubOptions![1].Include.Single());
    }

    [TestMethod]
    public void Map_AnalysisResult_PreservesCountsAndRecords()
    {
        var coreResult = new Core.Analysis.ArmorReuseAnalysisResult(
            2,
            5,
            9,
            [
                new(
                    Guid.NewGuid(), "Mod", "1111111111111111", "Source",
                    [new("2222222222222222", "Reused")], 3),
            ]);

        var result = ArmorReuseMapper.Map(coreResult);

        Assert.AreEqual(2, result.ScannedModCount);
        Assert.AreEqual(5, result.ScannedPatchCount);
        Assert.AreEqual(9, result.ScannedUnitCount);
        Assert.AreEqual(1, result.Records.Count);
        Assert.AreEqual("Source", result.Records[0].SourceArmorName);
        Assert.AreEqual("Reused", result.Records[0].ReusedBy.Single().ArmorName);
        Assert.AreEqual(3, result.Records[0].SharedUnitCount);
    }
}