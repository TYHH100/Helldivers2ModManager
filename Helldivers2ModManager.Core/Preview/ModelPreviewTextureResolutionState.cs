namespace Helldivers2ModManager.Core.Preview;

/// <summary>
/// Keeps a source-resolution texture separate from the normal automatic-material
/// cache, so switching its display resolution never disables or discards automatic
/// material matching for the rest of the model.
/// </summary>
public static class ModelPreviewTextureResolutionState
{
    public static bool IsCurrentOriginalPreview(
        bool useOriginalResolution,
        ulong textureId,
        ulong? originalTextureId) =>
        useOriginalResolution && originalTextureId == textureId;

    public static IReadOnlyDictionary<ulong, TPreview> GetMaterialPreviews<TPreview>(
        IReadOnlyDictionary<ulong, TPreview> automaticPreviews,
        bool useOriginalResolution,
        ulong? selectedTextureId,
        ulong? originalTextureId,
        TPreview? originalPreview)
        where TPreview : class
    {
        ArgumentNullException.ThrowIfNull(automaticPreviews);

        if (!useOriginalResolution || selectedTextureId is not ulong textureId ||
            originalTextureId != textureId || originalPreview is null)
            return automaticPreviews;

        var previews = new Dictionary<ulong, TPreview>(automaticPreviews)
        {
            [textureId] = originalPreview
        };
        return previews;
    }
}

