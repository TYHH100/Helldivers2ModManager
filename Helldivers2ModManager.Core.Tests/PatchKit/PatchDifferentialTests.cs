using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.PatchKit;

[TestClass]
public sealed class PatchDifferentialTests
{
    [TestMethod]
    public async Task RealTestAssets_ShouldMatchLegacyPatchParsers()
    {
        var root = new DirectoryInfo(FindRepositoryRoot());
        Assert.IsTrue(root.Exists, $"Missing Test assets: {root.FullName}");

        var coreResults = await LegacyPatchDifferentialHarness.ParseWithCoreAsync(root);
        var legacyToc = await LegacyPatchDifferentialHarness.InspectLegacyTocAsync(root);
        var legacyGpuStreams = await LegacyPatchDifferentialHarness.InspectLegacyGpuStreamsAsync(root);
        var legacyStructure = await LegacyPatchDifferentialHarness.AnalyzeLegacyStructureAsync(root);

        Assert.AreEqual(147, coreResults.Count);

        var coreToc = coreResults
            .SelectMany(result => result.Snapshot!.Entries.Select(entry => (snapshot: result.Snapshot!, entry)))
            .Select(pair => LegacyPatchDifferentialHarness.CreateCoreToc(
                pair.snapshot,
                pair.entry,
                Path.GetRelativePath(root.FullName, pair.snapshot.Path)))
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ThenBy(item => item.EntryIndex)
            .ToArray();
        var expectedToc = legacyToc
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ThenBy(item => item.EntryIndex)
            .Select(LegacyPatchDifferentialHarness.NormalizeLegacyToc).ToArray();
        Assert.AreEqual(expectedToc.Length, coreToc.Length);
        for (var index = 0; index < expectedToc.Length; index++)
        {
            Assert.IsTrue(
                expectedToc[index].Equals(coreToc[index]),
                $"TOC mismatch at {index}.`nExpected: {expectedToc[index]}`nActual:   {coreToc[index]}");
        }

        var coreGpuStreams = coreResults
            .SelectMany(result =>
            {
                var relativePath = Path.GetRelativePath(root.FullName, result.Snapshot!.Path);
                return result.Snapshot.Units.SelectMany(unit => unit.Streams.Select(stream => (relativePath, unit, stream)));
            })
            .Select(pair => LegacyPatchDifferentialHarness.CreateCoreGpuStream(pair.relativePath, pair.unit, pair.stream))
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ThenBy(item => item.TocEntryIndex)
            .ThenBy(item => item.StreamIndex)
            .ToArray();
        var expectedGpuStreams = legacyGpuStreams
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ThenBy(item => item.TocEntryIndex)
            .ThenBy(item => item.StreamIndex)
            .Select(LegacyPatchDifferentialHarness.NormalizeLegacyGpuStream).ToArray();
        Assert.AreEqual(expectedGpuStreams.Length, coreGpuStreams.Length);
        CollectionAssert.AreEqual(expectedGpuStreams, coreGpuStreams);

        Assert.AreEqual(legacyStructure.Sum(item => item.NumTypes), coreResults.Sum(result => result.Snapshot!.Header.TypeCount));
        Assert.AreEqual(legacyStructure.Sum(item => item.NumFiles), coreResults.Sum(result => result.Snapshot!.Header.FileCount));
        Assert.AreEqual(legacyStructure.Sum(item => item.EntryIndexIssueCount), coreResults.Sum(result => result.Snapshot!.EntryIndexIssueCount));
        Assert.AreEqual(legacyStructure.Sum(item => item.TypeDistributionIssueCount), coreResults.Sum(result => result.Snapshot!.TypeDistributionIssueCount));
        Assert.AreEqual(legacyStructure.Sum(item => item.MainDataIssueCount), coreResults.Sum(result => result.Snapshot!.MainDataIssueCount));
        Assert.AreEqual(legacyStructure.Sum(item => item.GpuResourceIssueCount), coreResults.Sum(result => result.Snapshot!.GpuRangeIssueCount));
        Assert.AreEqual(legacyStructure.Sum(item => item.GpuAlignmentIssueCount), coreResults.Sum(result => result.Snapshot!.GpuAlignmentIssueCount));
        Assert.AreEqual(legacyStructure.Sum(item => item.StreamIssueCount), coreResults.Sum(result => result.Snapshot!.StreamRangeIssueCount));
        Assert.AreEqual(legacyStructure.Sum(item => item.StreamAlignmentIssueCount), coreResults.Sum(result => result.Snapshot!.StreamAlignmentIssueCount));
        Assert.IsFalse(coreResults.Any(result => result.Snapshot!.HasErrors));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "Test")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? string.Empty;
    }
}
