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
    public void IsOpacityMask_CutoutDistribution_UsesAlphaBlending()
    {
        // 70% 不透明 + 30% 全透：典型头发/面纱裁切遮罩。
        Span<byte> pixels = stackalloc byte[100 * 4];
        for (var i = 0; i < 100; i++)
        {
            pixels[i * 4] = 0x20;
            pixels[i * 4 + 1] = 0x40;
            pixels[i * 4 + 2] = 0x60;
            pixels[i * 4 + 3] = i < 70 ? (byte)255 : (byte)0;
        }

        Assert.IsTrue(ModelPreviewTextureAnalysis.IsOpacityMask(pixels));
    }

    [TestMethod]
    public void IsOpacityMask_PackedGradientAlpha_StaysOpaque()
    {
        // 平滑渐变（打包数据常见分布）不满足接近二值的判定。
        Span<byte> pixels = stackalloc byte[64 * 4];
        for (var i = 0; i < 64; i++)
            pixels[i * 4 + 3] = (byte)(i * 4);

        Assert.IsFalse(ModelPreviewTextureAnalysis.IsOpacityMask(pixels));
    }

    [TestMethod]
    public void IsOpacityMask_UniformAlpha_StaysOpaque()
    {
        // 全不透明：没有可混合内容；全透明：按透明渲染等于让模型消失。
        Span<byte> opaquePixels = stackalloc byte[8 * 4];
        opaquePixels.Fill(255);
        Assert.IsFalse(ModelPreviewTextureAnalysis.IsOpacityMask(opaquePixels));

        Span<byte> transparentPixels = stackalloc byte[8 * 4];
        Assert.IsFalse(ModelPreviewTextureAnalysis.IsOpacityMask(transparentPixels));
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
