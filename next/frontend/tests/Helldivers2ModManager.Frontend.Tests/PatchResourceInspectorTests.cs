using System.IO;
using Helldivers2ModManager.Core.Preview;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Frontend.Tests;

[TestClass]
public sealed class PatchResourceInspectorTests
{
    private string? _root;

    [TestCleanup]
    public void Cleanup()
    {
        if (_root is not null && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InspectAsync_EmptyMod_ReturnsBoundedEmptyResult()
    {
        _root = Path.Combine(Path.GetTempPath(), "Helldivers2ModManagerFrontendTests", Guid.NewGuid().ToString("N"));
        var directory = Directory.CreateDirectory(Path.Combine(_root, "Empty Mod"));

        var result = await new PatchResourceInspector().InspectAsync(directory);

        Assert.AreEqual(0, result.PatchFileCount);
        Assert.AreEqual(0, result.TocEntries.Count);
        Assert.AreEqual(0, result.GpuStreams.Count);
        Assert.AreEqual(0, result.Textures.Count);
    }
}
