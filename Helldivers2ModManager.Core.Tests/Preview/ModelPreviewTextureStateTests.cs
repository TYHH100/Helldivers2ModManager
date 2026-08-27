using Helldivers2ModManager.Core.Preview;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Preview;

[TestClass]
public sealed class ModelPreviewTextureStateTests
{
    [TestMethod]
    public void Classify_RecognizesTangentSpaceNormalMap()
    {
        Assert.AreEqual(TexturePreviewRole.LikelyNormalMap, ModelPreviewTextureAnalysis.Classify(CreatePreview(180, 132, 124)));
    }

    [TestMethod]
    public void Classify_LeavesColorTextureAsColorCandidate()
    {
        Assert.AreEqual(TexturePreviewRole.ColorCandidate, ModelPreviewTextureAnalysis.Classify(CreatePreview(75, 100, 190)));
    }

    [TestMethod]
    public void Create_UsesLatestSelectedPatch_WhenTextureIdAppearsInMultiplePatches()
    {
        var first = CreateTexture("Material/A.patch_0", 42, 2);
        var duplicate = CreateTexture("Material/B.patch_0", 42, 5);
        var index = ModelPreviewTextureIndex.Create([first, duplicate]);
        Assert.AreEqual(1, index.Count);
        Assert.AreSame(duplicate, index[42]);
    }

    [TestMethod]
    public void IsCurrent_ModelSwitchOrPageClose_PreventsLateOriginalTextureResultFromApplying()
    {
        Assert.IsTrue(ModelPreviewTextureRequestState.IsCurrent(4, 4, false));
        Assert.IsFalse(ModelPreviewTextureRequestState.IsCurrent(4, 5, false));
        Assert.IsFalse(ModelPreviewTextureRequestState.IsCurrent(4, 4, true));
    }

    [TestMethod]
    public void GetMaterialPreviews_AutomaticMaterialsWithOriginalSelectedTexture_OverlaysOnlyThatTexture()
    {
        var automatic = new Dictionary<ulong, string> { [1] = "regular-base", [2] = "regular-emissive" };
        var previews = ModelPreviewTextureResolutionState.GetMaterialPreviews(automatic, true, 1, 1, "native-base");
        Assert.AreEqual("native-base", previews[1]);
        Assert.AreEqual("regular-emissive", previews[2]);
        Assert.AreEqual(2, previews.Count);
    }

    [TestMethod]
    public void GetMaterialPreviews_OriginalResolutionDisabled_RestoresAllRegularAutomaticTextures()
    {
        var automatic = new Dictionary<ulong, string> { [1] = "regular-base", [2] = "regular-emissive" };
        var previews = ModelPreviewTextureResolutionState.GetMaterialPreviews(automatic, false, 1, 1, "native-base");
        Assert.AreSame(automatic, previews);
        Assert.AreEqual("regular-base", previews[1]);
        Assert.AreEqual("regular-emissive", previews[2]);
    }

    [TestMethod]
    public void IsCurrentOriginalPreview_OnlyMatchesTheSelectedOriginalTexture()
    {
        Assert.IsTrue(ModelPreviewTextureResolutionState.IsCurrentOriginalPreview(true, 7, 7));
        Assert.IsFalse(ModelPreviewTextureResolutionState.IsCurrentOriginalPreview(false, 7, 7));
        Assert.IsFalse(ModelPreviewTextureResolutionState.IsCurrentOriginalPreview(true, 7, 8));
    }

    private static TexturePreviewData CreatePreview(byte blue, byte green, byte red) => new()
    {
        Width = 2,
        Height = 2,
        BgraPixels = (byte[])[blue, green, red, 255, blue, green, red, 255, blue, green, red, 255, blue, green, red, 255],
        Description = "test",
    };

    private static TextureInspectionItem CreateTexture(string patchPath, ulong textureId, int patchOrder) => new()
    {
        PatchFile = Path.GetFileName(patchPath),
        PatchPath = patchPath,
        PatchOrder = patchOrder,
        TocEntryIndex = 1,
        TextureId = textureId,
        MainOffset = 0,
        MainSize = 1,
        GpuOffset = 0,
        GpuSize = 1,
        StreamOffset = 0,
        StreamSize = 0,
        Width = 1,
        Height = 1,
        MipCount = 1,
        DxgiFormat = 0,
        PayloadKind = "DDS",
        PayloadSource = "gpu_resources",
    };
}
