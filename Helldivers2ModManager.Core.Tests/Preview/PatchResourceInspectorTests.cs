using Helldivers2ModManager.Core.Preview;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Preview;

[TestClass]
public sealed class PatchResourceInspectorTests
{
    [TestMethod]
    public async Task InspectAsync_EmptyTemporaryDirectory_ReturnsBoundedEmptyResult()
    {
        var directory = Directory.CreateTempSubdirectory("hd2mm-core-preview-");
        try
        {
            var result = await new PatchResourceInspector().InspectAsync(directory);

            Assert.IsNull(result.Error, result.Error);
            Assert.AreEqual(0, result.PatchFileCount);
            Assert.AreEqual(0, result.TocEntries.Count);
            Assert.AreEqual(0, result.GpuStreams.Count);
            Assert.AreEqual(0, result.Textures.Count);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task InspectAsync_MissingDirectory_ReportsStableError()
    {
        var directory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "hd2mm-core-preview-missing", Guid.NewGuid().ToString("N")));

        var result = await new PatchResourceInspector().InspectAsync(directory);

        Assert.IsNotNull(result.Error);
        Assert.AreEqual(0, result.PatchFileCount);
    }
}
