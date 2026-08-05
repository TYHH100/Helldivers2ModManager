using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewTextureResolutionStateTests
{
    [TestMethod]
    public void GetMaterialPreviews_AutomaticMaterialsWithOriginalSelectedTexture_OverlaysOnlyThatTexture()
    {
        var automaticPreviews = new Dictionary<ulong, string>
        {
            [1] = "regular-base",
            [2] = "regular-emissive"
        };

        var previews = ModelPreviewTextureResolutionState.GetMaterialPreviews(
            automaticPreviews,
            useOriginalResolution: true,
            selectedTextureId: 1,
            originalTextureId: 1,
            originalPreview: "native-base");

        Assert.AreEqual("native-base", previews[1]);
        Assert.AreEqual("regular-emissive", previews[2]);
        Assert.AreEqual(2, previews.Count);
    }

    [TestMethod]
    public void GetMaterialPreviews_OriginalResolutionDisabled_RestoresAllRegularAutomaticTextures()
    {
        var automaticPreviews = new Dictionary<ulong, string>
        {
            [1] = "regular-base",
            [2] = "regular-emissive"
        };

        var previews = ModelPreviewTextureResolutionState.GetMaterialPreviews(
            automaticPreviews,
            useOriginalResolution: false,
            selectedTextureId: 1,
            originalTextureId: 1,
            originalPreview: "native-base");

        Assert.AreSame(automaticPreviews, previews);
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
}
