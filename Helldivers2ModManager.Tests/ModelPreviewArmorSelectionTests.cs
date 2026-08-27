using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelPreviewMesh = Helldivers2ModManager.Core.Preview.ModelPreviewMesh;
using ModelPreviewResult = Helldivers2ModManager.Core.Preview.ModelPreviewResult;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewArmorSelectionTests
{
    [TestMethod]
    public void ApplyPackageNames_BuildsArmorAlternativesAndKeepsSharedUnitsVisible()
    {
        var armorAUnit = CreateMesh(1);
        var armorBUnit = CreateMesh(2);
        var sharedUnit = CreateMesh(3);
        var sharedByBothUnit = CreateMesh(4);
        var result = new ModelPreviewResult();
        result.Meshes.AddRange([armorAUnit, armorBUnit, sharedUnit, sharedByBothUnit]);

        ModelPreviewBackend.ApplyPackageNames(
            result,
            new Dictionary<long, IReadOnlyList<string>>
            {
                [1] = ["content/armor/aaaaaaaaaaaaaaaa.unit"],
                [2] = ["content/armor/bbbbbbbbbbbbbbbb.unit"],
                [4] = ["content/armor/aaaaaaaaaaaaaaaa.unit", "content/armor/bbbbbbbbbbbbbbbb.unit"]
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["aaaaaaaaaaaaaaaa"] = "Armor A",
                ["bbbbbbbbbbbbbbbb"] = "Armor B"
            });

        Assert.AreEqual(3, result.Armors.Count, "All plus two named armor alternatives are expected.");
        Assert.AreEqual("Armor A", result.Armors.Single(armor => armor.Id == "aaaaaaaaaaaaaaaa").Name);

        var armorAMeshes = ModelPreviewBackend.FilterByArmor(result.Meshes, "aaaaaaaaaaaaaaaa");
        CollectionAssert.AreEquivalent(
            new[] { armorAUnit, sharedUnit, sharedByBothUnit },
            armorAMeshes.ToArray());

        var armorBMeshes = ModelPreviewBackend.FilterByArmor(result.Meshes, "bbbbbbbbbbbbbbbb");
        CollectionAssert.AreEquivalent(
            new[] { armorBUnit, sharedUnit, sharedByBothUnit },
            armorBMeshes.ToArray());
    }

    [TestMethod]
    public void FilterByArmor_AllKeepsTheCompleteSelectedPatchSet()
    {
        var meshes = new[] { CreateMesh(1), CreateMesh(2) };
        meshes[0].SetArmorIds(["aaaaaaaaaaaaaaaa"]);
        meshes[1].SetArmorIds(["bbbbbbbbbbbbbbbb"]);

        var filtered = ModelPreviewBackend.FilterByArmor(meshes, ModelPreviewArmorSelection.AllId);

        CollectionAssert.AreEquivalent(meshes, filtered.ToArray());
    }

    private static ModelPreviewMesh CreateMesh(ulong unitId) => new()
    {
        PatchFile = "selected.patch_0",
        UnitId = unitId,
        StreamIndex = 0,
        Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0],
        TriangleIndices = [0, 1, 2]
    };
}
