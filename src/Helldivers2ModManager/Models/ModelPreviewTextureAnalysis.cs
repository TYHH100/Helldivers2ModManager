namespace Helldivers2ModManager.Models;

internal static class ModelPreviewTextureAnalysis
{
    public static TexturePreviewRole Classify(TexturePreviewData preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.BgraPixels is not { Length: >= 4 } pixels)
            return TexturePreviewRole.Unknown;

        var sampleCount = Math.Min(pixels.Length / 4, 16_384);
        var step = Math.Max(1, (pixels.Length / 4) / sampleCount);
        double red = 0;
        double green = 0;
        double blue = 0;
        var count = 0;
        for (var pixel = 0; pixel + 3 < pixels.Length && count < sampleCount; pixel += step * 4)
        {
            blue += pixels[pixel];
            green += pixels[pixel + 1];
            red += pixels[pixel + 2];
            count++;
        }

        if (count == 0)
            return TexturePreviewRole.Unknown;

        red /= count;
        green /= count;
        blue /= count;
        // Tangent-space normal maps concentrate around R/G 128 with a clearly dominant blue
        // channel. This is intentionally a hint, not a claim about the engine material slot.
        return blue >= 145 && blue >= red + 18 && blue >= green + 18 && red is >= 70 and <= 190 && green is >= 70 and <= 190
            ? TexturePreviewRole.LikelyNormalMap
            : TexturePreviewRole.ColorCandidate;
    }

    /// <summary>
    /// 估算 AlbedoIridescence（流光材质颜色贴图）的 Alpha 通道强度（0..1）。
    /// 实测该输入的 Alpha 承载流光强度遮罩：同一材质未开启流光时 Alpha≈0，
    /// 开启油光后为 255。取有界采样的平均 Alpha，使高光层强度跟随真实贴图，
    /// 而不是对所有引用流光输入的材质一律叠加同强度高光。
    /// 无像素数据时返回 1（未知按"流光开启"处理，避免静默隐藏该材质的流光显示）。
    /// </summary>
    public static double MeasureIridescenceStrength(TexturePreviewData? preview)
    {
        if (preview?.BgraPixels is not { Length: >= 4 } pixels)
            return 1.0;

        var pixelCount = pixels.Length / 4;
        if (pixelCount == 0)
            return 1.0;

        var sampleCount = Math.Min(pixelCount, 16_384);
        var step = Math.Max(1, pixelCount / sampleCount);
        long alphaSum = 0;
        var sampled = 0;
        for (var pixel = 0; pixel < pixelCount && sampled < sampleCount; pixel += step, sampled++)
            alphaSum += pixels[pixel * 4 + 3];

        return sampled == 0 ? 1.0 : alphaSum / (sampled * 255.0);
    }

    /// <summary>
    /// 判定 BGRA 像素的 Alpha 通道是否是真实的透明遮罩（头发/面纱等裁切几何），而不是
    /// Albedo 里打包的无关数据。只有"接近二值"的分布——几乎全不透与几乎全透的像素合计
    /// 占绝对多数、且两侧都真实存在——才启用 Alpha 混合：HD2 的 Albedo Alpha 常打包平滑
    /// 渐变或噪声类数据，按透明渲染会让模型整片消失；反之真正的裁切遮罩不会被误判。
    /// </summary>
    public static bool IsOpacityMask(ReadOnlySpan<byte> bgraPixels)
    {
        const int bytesPerPixel = 4;
        const byte transparentThreshold = 64;
        const byte opaqueThreshold = 224;
        // 采样上限与 Classify 一致：4K 纹理全量扫描也只是线性统计，但预览热路径
        // 保持与角色分析相同的成本级别。
        const int maxSamples = 16_384;
        const double minimumMaskedFraction = 0.02;
        const double minimumBinaryFraction = 0.85;

        var pixelCount = bgraPixels.Length / bytesPerPixel;
        if (pixelCount == 0)
            return false;

        var sampleCount = Math.Min(pixelCount, maxSamples);
        var step = Math.Max(1, pixelCount / sampleCount);
        var transparent = 0;
        var opaque = 0;
        var sampled = 0;
        for (var pixel = 0; pixel < pixelCount && sampled < sampleCount; pixel += step, sampled++)
        {
            var alpha = bgraPixels[pixel * bytesPerPixel + 3];
            if (alpha <= transparentThreshold)
                transparent++;
            else if (alpha >= opaqueThreshold)
                opaque++;
        }

        if (sampled == 0)
            return false;

        var transparentFraction = transparent / (double)sampled;
        var opaqueFraction = opaque / (double)sampled;
        // 全透明（无任何不透锚点）与全不透明（无变化）都不算遮罩：
        // 前者按透明渲染等于让模型消失，后者没有可混合的内容。
        return transparentFraction >= minimumMaskedFraction &&
               opaqueFraction >= minimumMaskedFraction &&
               transparentFraction + opaqueFraction >= minimumBinaryFraction;
    }
}
