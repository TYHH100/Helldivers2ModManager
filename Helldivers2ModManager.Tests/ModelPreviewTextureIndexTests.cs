using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewTextureIndexTests
{
    [TestMethod]
    public void Create_UsesLatestSelectedPatch_WhenTextureIdAppearsInMultiplePatches()
    {
        var first = CreateTexture("Material/A.patch_0", 42, patchOrder: 2);
        var duplicate = CreateTexture("Material/B.patch_0", 42, patchOrder: 5);

        var index = ModelPreviewTextureIndex.Create([first, duplicate]);

        Assert.AreEqual(1, index.Count);
        Assert.AreSame(duplicate, index[42]);
    }

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
        PayloadSource = "gpu_resources"
    };
}
