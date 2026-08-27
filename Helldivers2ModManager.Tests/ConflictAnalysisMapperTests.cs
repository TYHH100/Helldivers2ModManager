using Helldivers2ModManager.Adapters;
using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ConflictAnalysisMapperTests
{
    [TestMethod]
    public void Map_PreservesCountsParticipantsAndWinner()
    {
        var firstMod = Guid.NewGuid();
        var secondMod = Guid.NewGuid();
        var first = new Core.Analysis.ConflictParticipant(firstMod, "First", "a.patch_0", 10, 7, 100, 200, 0);
        var second = new Core.Analysis.ConflictParticipant(secondMod, "Second", "b.patch_0", 10, 8, 100, 200, 1);
        var coreResult = new Core.Analysis.ConflictAnalysisResult(
            2,
            2,
            2,
            [new(10, "Unit", [first, second], "0x000000000000000A")]);

        var result = ConflictAnalysisMapper.Map(coreResult);

        Assert.AreEqual(2, result.ScannedModCount);
        Assert.AreEqual(2, result.ScannedPatchCount);
        Assert.AreEqual(2, result.ScannedUnitCount);
        Assert.AreEqual(1, result.Conflicts.Count);
        Assert.IsTrue(result.Conflicts[0].IsDefiniteConflict);
        Assert.AreEqual("Unit", result.Conflicts[0].FriendlyName);
        Assert.AreEqual("Second", result.Conflicts[0].Winner.ModName);
        Assert.AreEqual(secondMod, result.Conflicts[0].Winner.ModGuid);
        Assert.AreEqual(100, result.Conflicts[0].Winner.DataSize);
    }
}