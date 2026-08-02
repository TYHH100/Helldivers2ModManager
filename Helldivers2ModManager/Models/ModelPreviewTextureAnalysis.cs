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
}
