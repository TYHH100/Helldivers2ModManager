using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class BisectCoreTests
{
	private static IReadOnlyList<Guid> MakeGuids(int count)
	{
		return Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
	}

	[TestMethod]
	[DataRow(2, 1, DisplayName = "2 candidates -> test 1")]
	[DataRow(3, 1, DisplayName = "3 candidates -> test 1")]
	[DataRow(4, 2, DisplayName = "4 candidates -> test 2")]
	[DataRow(5, 2, DisplayName = "5 candidates -> test 2")]
	[DataRow(8, 4, DisplayName = "8 candidates -> test 4")]
	[DataRow(9, 4, DisplayName = "9 candidates -> test 4")]
	public void SplitTestedGuids_TakesFirstHalf(int candidateCount, int expectedTestedCount)
	{
		var candidates = MakeGuids(candidateCount);

		var tested = BisectCore.SplitTestedGuids(candidates);

		Assert.AreEqual(expectedTestedCount, tested.Count);
		Assert.AreEqual(candidates.Take(expectedTestedCount).ToArray()[0], tested[0]);
		Assert.AreEqual(candidates[expectedTestedCount - 1], tested[^1]);
	}

	[TestMethod]
	public void SplitTestedGuids_SingleCandidate_ReturnsEmpty()
	{
		var candidates = MakeGuids(1);

		var tested = BisectCore.SplitTestedGuids(candidates);

		Assert.AreEqual(0, tested.Count);
	}

	[TestMethod]
	public void ApplyReport_Crashed_KeepsTestedSetOnly()
	{
		var candidates = MakeGuids(8);
		var tested = BisectCore.SplitTestedGuids(candidates);

		var result = BisectCore.ApplyReport(candidates, tested, crashed: true);

		CollectionAssert.AreEqual(tested.ToArray(), result.ToArray());
	}

	[TestMethod]
	public void ApplyReport_NotCrashed_ExcludesTestedSet()
	{
		var candidates = MakeGuids(8);
		var tested = BisectCore.SplitTestedGuids(candidates);
		var expectedRemaining = candidates.Skip(tested.Count).ToArray();

		var result = BisectCore.ApplyReport(candidates, tested, crashed: false);

		CollectionAssert.AreEqual(expectedRemaining, result.ToArray());
	}

	[TestMethod]
	public void SimulateBisect_OneBadMod_ConvergesToItInThreeRounds()
	{
		var candidates = MakeGuids(8);
		var badMod = candidates[6];
		var rounds = 0;

		while (candidates.Count > 1)
		{
			var tested = BisectCore.SplitTestedGuids(candidates);
			var crashed = tested.Contains(badMod);
			candidates = BisectCore.ApplyReport(candidates, tested, crashed).ToList();
			rounds++;
		}

		Assert.AreEqual(3, rounds);
		Assert.AreEqual(1, candidates.Count);
		Assert.AreEqual(badMod, candidates[0]);
	}

	[TestMethod]
	public void SimulateBisect_TwoBadMods_IterationFindsBoth()
	{
		var allMods = MakeGuids(8);
		var badMods = new[] { allMods[1], allMods[6] };
		var found = new List<Guid>();
		var allRemaining = allMods.ToList();
		var candidates = allRemaining.ToList();

		// 迭代排查：二分收敛到嫌疑后剔除它，剩余启用模组继续二分（前提：剩余仍崩溃）
		while (true)
		{
			while (candidates.Count > 1)
			{
				var tested = BisectCore.SplitTestedGuids(candidates);
				var crashed = tested.Any(badMods.Contains);
				candidates = BisectCore.ApplyReport(candidates, tested, crashed).ToList();
			}

			if (candidates.Count != 1)
				break;

			found.Add(candidates[0]);
			allRemaining.Remove(candidates[0]);
			if (!allRemaining.Any(badMods.Contains))
				break;

			candidates = allRemaining.ToList();
		}

		Assert.AreEqual(2, found.Count);
		CollectionAssert.AreEquivalent(badMods.ToArray(), found.ToArray());
	}
}
