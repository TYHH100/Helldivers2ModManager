using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
[TestCategory("Fixture")]
public sealed class PatchResourceInspectionLegacyUnitTests
{
    [TestMethod]
    public async Task InspectAsync_OriginalVersionOneUnit_ReadsLegacyGpuStreams()
    {
        var modDirectory = new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName,
            "Test", "Mods", "Mods", "8 嘉然 生日礼服 替换 DP-00 “战术”民主中甲_3e2f99ef"));
        var patch = new FileInfo(Path.Combine(modDirectory.FullName, "9ba626afa44a3aa3.patch_0"));

        var result = await new PatchResourceInspectionService().InspectAsync(modDirectory, [patch]);

        Assert.IsNull(result.Error);
        Assert.AreEqual(46, result.GpuStreams.Count);
        Assert.IsTrue(result.GpuStreams.All(stream => stream.UnitVersion == 1));
        Assert.IsTrue(result.GpuStreams.Any(stream => stream.Components.Contains("oct-normal (legacy)")));
        Assert.IsTrue(result.GpuStreams.All(stream => stream.VertexSample.StartsWith("Position (", StringComparison.Ordinal)));
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (DirectoryInfo? current = new(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return current;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root for the patch fixture.");
    }
}
