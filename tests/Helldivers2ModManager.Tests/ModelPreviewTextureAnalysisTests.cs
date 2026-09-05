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

    [TestMethod]
    public void MeasureIridescenceStrength_OpaqueAlpha_IsFullStrength()
    {
        // 油光材质实测：AlbedoIridescence 的 Alpha 全为 255。
        var preview = new TexturePreviewData
        {
            Width = 2,
            Height = 2,
            BgraPixels = [0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255],
            Description = "test"
        };

        Assert.AreEqual(1.0, ModelPreviewTextureAnalysis.MeasureIridescenceStrength(preview), 0.001);
    }

    [TestMethod]
    public void MeasureIridescenceStrength_EmptyAlpha_IsZero()
    {
        // 同一材质未开启流光时 Alpha≈0：不叠加高光层。
        var preview = new TexturePreviewData
        {
            Width = 2,
            Height = 2,
            BgraPixels = [10, 20, 30, 0, 10, 20, 30, 1, 10, 20, 30, 0, 10, 20, 30, 1],
            Description = "test"
        };

        Assert.AreEqual(0.0, ModelPreviewTextureAnalysis.MeasureIridescenceStrength(preview), 0.01);
    }

    [TestMethod]
    public void MeasureIridescenceStrength_MixedCutoutAlpha_IsZero()
    {
        // 大量角色模组把同一张贴图同时绑定为 Albedo 与 AlbedoIridescence，
        // 此时 Alpha 是镂空遮罩（透明+不透明混合分布），不是流光强度：
        // 按强度解释会让整模错误叠加高光，非均匀 Alpha 必须返回 0。
        var pixels = new byte[64 * 4];
        for (var i = 0; i < 64; i++)
        {
            pixels[i * 4] = 30;
            pixels[i * 4 + 1] = 40;
            pixels[i * 4 + 2] = 50;
            pixels[i * 4 + 3] = i < 32 ? (byte)255 : (byte)0;
        }

        var preview = new TexturePreviewData { Width = 8, Height = 8, BgraPixels = pixels, Description = "test" };

        Assert.AreEqual(0.0, ModelPreviewTextureAnalysis.MeasureIridescenceStrength(preview), 0.001);
    }

    [TestMethod]
    public void MeasureIridescenceStrength_UniformMidAlpha_IsMeanStrength()
    {
        // 均匀的部分强度 Alpha（如半开油光）仍按均值解释。
        var pixels = new byte[16 * 4];
        for (var i = 0; i < 16; i++)
        {
            pixels[i * 4] = 10;
            pixels[i * 4 + 1] = 20;
            pixels[i * 4 + 2] = 30;
            pixels[i * 4 + 3] = 128;
        }

        var preview = new TexturePreviewData { Width = 4, Height = 4, BgraPixels = pixels, Description = "test" };

        Assert.AreEqual(128 / 255.0, ModelPreviewTextureAnalysis.MeasureIridescenceStrength(preview), 0.001);
    }

    [TestMethod]
    public void MeasureIridescenceStrength_MissingPixels_DefaultsToFullStrength()
    {
        // 无像素数据（如 PNG 编码回退）按"流光开启"处理，避免静默隐藏流光显示。
        var preview = new TexturePreviewData
        {
            Width = 2,
            Height = 2,
            EncodedImageBytes = [1, 2, 3],
            Description = "test"
        };

        Assert.AreEqual(1.0, ModelPreviewTextureAnalysis.MeasureIridescenceStrength(preview), 0.001);
    }

    private static TexturePreviewData CreatePreview(byte blue, byte green, byte red) => new()
    {
        Width = 2,
        Height = 2,
        BgraPixels = [blue, green, red, 255, blue, green, red, 255, blue, green, red, 255, blue, green, red, 255],
        Description = "test"
    };
}
