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
    /// 大量角色模组把同一张贴图同时绑定到 Albedo 与 AlbedoIridescence 语义，
    /// 此时 Alpha 是镂空/覆盖遮罩（透明+不透明混合分布），不是流光强度——
    /// 按强度解释会让整模错误叠加高光，因此非均匀 Alpha（标准差过大）返回 0。
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

        if (sampled == 0)
            return 1.0;

        var mean = alphaSum / sampled;
        var varianceSum = 0.0;
        for (var pixel = 0; pixel < pixelCount; pixel += step)
        {
            var difference = pixels[pixel * 4 + 3] - mean;
            varianceSum += difference * difference;
        }

        // 非均匀 Alpha（镂空遮罩/打包数据）：不解释为流光强度。
        // 0.15 ≈ 真实油光遮罩（整体开启/关闭）与覆盖遮罩（约半数透明）的分界。
        if (sampled > 0 && Math.Sqrt(varianceSum / sampled) > 255.0 * 0.15)
            return 0.0;

        return mean / 255.0;
    }

}
