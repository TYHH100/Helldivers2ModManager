using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
        meshes[0].ArmorIds = ["aaaaaaaaaaaaaaaa"];
        meshes[1].ArmorIds = ["bbbbbbbbbbbbbbbb"];

        var filtered = ModelPreviewBackend.FilterByArmor(meshes, ModelPreviewArmorSelection.AllId);

        CollectionAssert.AreEquivalent(meshes, filtered.ToArray());
    }

    [TestMethod]
    public void ApplyPackageNames_FallsBackToHelmetNameBeforePlaceholder()
    {
        var result = new ModelPreviewResult();
        result.Meshes.AddRange([CreateMesh(7), CreateMesh(8)]);

        ModelPreviewBackend.ApplyPackageNames(
            result,
            new Dictionary<long, IReadOnlyList<string>>
            {
                [7] = ["content/helmet/cccccccccccccccc.unit"],
                [8] = ["content/helmet/dddddddddddddddd.unit"]
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["aaaaaaaaaaaaaaaa"] = "Armor A"
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["cccccccccccccccc"] = "Helmet C"
            });

        var options = result.Armors.ToDictionary(static option => option.Id, static option => option.Name);
        // 未收录的 ID 保留原有 "Armor {id}" 占位格式
        Assert.AreEqual("Armor dddddddddddddddd", options["dddddddddddddddd"]);
        Assert.AreEqual("Helmet C", options["cccccccccccccccc"]);
    }

    [TestMethod]
    public void ApplyPackageNames_MergesArmorAndHelmetWithSameNameIntoSingleSet()
    {
        // 同一套装的护甲本体与头盔是两个不同 archive ID、显示名相同，
        // 必须合并为单一选项；过滤时任一 ID 命中即保留，确保整套装备同时渲染。
        var bodyUnit = CreateMesh(1);
        var helmetUnit = CreateMesh(2);
        var sharedByBothUnit = CreateMesh(3);
        var result = new ModelPreviewResult();
        result.Meshes.AddRange([bodyUnit, helmetUnit, sharedByBothUnit]);

        ModelPreviewBackend.ApplyPackageNames(
            result,
            new Dictionary<long, IReadOnlyList<string>>
            {
                [1] = ["content/armor/aaaaaaaaaaaaaaaa.unit"],
                [2] = ["content/helmet/bbbbbbbbbbbbbbbb.unit"],
                [3] = ["content/armor/aaaaaaaaaaaaaaaa.unit", "content/helmet/bbbbbbbbbbbbbbbb.unit"]
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["aaaaaaaaaaaaaaaa"] = "Armor Set"
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["bbbbbbbbbbbbbbbb"] = "Armor Set"
            });

        Assert.AreEqual(2, result.Armors.Count, "All plus one merged armor set are expected.");
        var mergedSet = result.Armors.Single(static armor => !armor.IsAll);
        CollectionAssert.AreEquivalent(
            new[] { "aaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb" },
            mergedSet.Ids.ToArray());
        Assert.AreEqual(3, mergedSet.MeshCount, "Body, helmet and shared meshes all belong to the merged set.");

        var setMeshes = ModelPreviewBackend.FilterByArmor(result.Meshes, mergedSet.Ids);
        CollectionAssert.AreEquivalent(
            new[] { bodyUnit, helmetUnit, sharedByBothUnit },
            setMeshes.ToArray());

        // 旧的 string 签名保留且行为不变：单 ID 过滤只保留该 archive 的网格（不含头盔独占网格）
        var byRepresentativeId = ModelPreviewBackend.FilterByArmor(result.Meshes, mergedSet.Id);
        CollectionAssert.AreEquivalent(
            new[] { bodyUnit, sharedByBothUnit },
            byRepresentativeId.ToArray());
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
