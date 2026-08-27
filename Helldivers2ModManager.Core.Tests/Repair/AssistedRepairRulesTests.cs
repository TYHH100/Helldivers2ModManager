using Helldivers2ModManager.Core.Preview;
using Helldivers2ModManager.Core.Repair;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Repair;

[TestClass]
public sealed class AssistedRepairRulesTests
{
    [TestMethod]
    public void ClassifyAutomaticLodActions_ShouldPreserveStrongCustomAndSignatureGroups()
    {
        var strong = new AssistedUnitRepairAction("a.patch_0", 1, 100, 1, 2, 1, 1, 64, 32, true, "AA", true, ModelPreviewBodyShape.Slim, ModelPreviewCustomizationSlot.Torso, AssistedLodStrategy.UseGameReference, true);
        var sameSignature = strong with { FileId = 101, EntryIndex = 2, StrongCustomModelSignal = false };
        var unrelated = strong with { FileId = 200, EntryIndex = 3, MeshIdsDiffer = false, CurrentMeshSignature = "BB", StrongCustomModelSignal = false };
        var (preserve, strongCustom, automatic) = AssistedRepairRules.ClassifyAutomaticLodActions([strong, sameSignature, unrelated]);
        CollectionAssert.AreEquivalent(new[] { 100L, 101L, 200L }, preserve.ToArray());
        CollectionAssert.AreEquivalent(new[] { 100L }, strongCustom.ToArray());
        CollectionAssert.AreEquivalent(new[] { 100L, 101L, 200L }, automatic.ToArray());
    }

    [TestMethod]
    public void LegacyPackRule_ShouldApplyToEveryVersionOneUnit()
    {
        Assert.IsTrue(AssistedRepairRules.RequiresCurrentGameLodForLegacyPack(true, 1, AssistedRepairRules.LegacyCharacterReferenceVersion));
        Assert.IsFalse(AssistedRepairRules.RequiresCurrentGameLodForLegacyPack(true, 10800438, AssistedRepairRules.LegacyCharacterReferenceVersion));
        Assert.IsFalse(AssistedRepairRules.RequiresCurrentGameLodForLegacyPack(false, 1, AssistedRepairRules.LegacyCharacterReferenceVersion));
    }
}


