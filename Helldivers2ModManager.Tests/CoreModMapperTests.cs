using Helldivers2ModManager.Adapters;
using Helldivers2ModManager.Models;
using CoreMods = Helldivers2ModManager.Core.Mods;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class CoreModMapperTests
{
    [TestMethod]
    public void Map_V1Manifest_PreservesIdentityOptionsAndNexusData()
    {
        var option = new CoreMods.ModOption(
            "Option",
            "Description",
            ["include-a"],
            "option.png",
            [new("Sub", "Sub description", ["include-b"], "sub.png")]);
        var core = new CoreMods.V1ModManifest
        {
            Guid = Guid.NewGuid(),
            Name = "V1 mod",
            Description = "Description",
            IconPath = "icon.png",
            Options = [option],
            NexusData = new(1234, "1.2.3"),
        };

        var mapped = CoreModMapper.Map(core);

        Assert.IsInstanceOfType(mapped, typeof(Models.V1ModManifest));
        var v1 = (Models.V1ModManifest)mapped;
        Assert.AreEqual(core.Guid, v1.Guid);
        Assert.AreEqual(core.Name, v1.Name);
        Assert.AreEqual(core.IconPath, v1.IconPath);
        Assert.AreEqual(1, v1.Options!.Count);
        Assert.AreEqual("include-a", v1.Options[0].Include!.Single());
        Assert.AreEqual("sub.png", v1.Options[0].SubOptions![0].Image);
        Assert.AreEqual(1234, v1.NexusData!.ModId);
    }

    [TestMethod]
    public void MapProblem_MapsAllSupportedImportKinds()
    {
        var mapped = CoreModMapper.MapProblem(new(
            @"C:\archives\mod.zip",
            CoreMods.ArchiveImportProblemKind.Duplicate,
            "duplicate"));

        Assert.AreEqual(ModProblemKind.Duplicate, mapped.Kind);
        Assert.AreEqual(@"C:\archives", mapped.Directory.FullName);
        Assert.AreEqual("duplicate", mapped.ExtraData);
    }
}
