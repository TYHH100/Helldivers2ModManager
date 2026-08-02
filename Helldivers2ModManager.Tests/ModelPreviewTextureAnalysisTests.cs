using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewTextureAnalysisTests
{
    [TestMethod]
    public void Classify_RecognizesTangentSpaceNormalMap()
    {
        var preview = CreatePreview(180, 132, 124);

        var role = ModelPreviewTextureAnalysis.Classify(preview);

        Assert.AreEqual(TexturePreviewRole.LikelyNormalMap, role);
    }

    [TestMethod]
    public void Classify_LeavesColorTextureAsColorCandidate()
    {
        var preview = CreatePreview(75, 100, 190);

        var role = ModelPreviewTextureAnalysis.Classify(preview);

        Assert.AreEqual(TexturePreviewRole.ColorCandidate, role);
    }

    private static TexturePreviewData CreatePreview(byte blue, byte green, byte red) => new()
    {
        Width = 2,
        Height = 2,
        BgraPixels = [blue, green, red, 255, blue, green, red, 255, blue, green, red, 255, blue, green, red, 255],
        Description = "test"
    };
}
