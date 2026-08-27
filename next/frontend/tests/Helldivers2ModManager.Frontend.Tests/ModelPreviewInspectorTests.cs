using System.IO;
using Helldivers2ModManager.Core.Preview;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Frontend.Tests;

[TestClass]
public sealed class ModelPreviewInspectorTests
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
    public async Task PreviewModelAsync_EmptyMod_ReturnsNoGeometry()
    {
        _root = Path.Combine(Path.GetTempPath(), "Helldivers2ModManagerFrontendTests", Guid.NewGuid().ToString("N"));
        var directory = Directory.CreateDirectory(Path.Combine(_root, "Empty Mod"));

        var result = await new PatchResourceInspector().PreviewModelAsync(directory);

        Assert.AreEqual(0, result.Meshes.Count);
        Assert.AreEqual(0, result.SkippedStreams);
        Assert.IsNull(result.Error);
    }
}
