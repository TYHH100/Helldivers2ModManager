namespace Helldivers2ModManager.Models;

/// <summary>
/// Texture IDs can appear in more than one patch inside a mod. The automatic preview
/// needs one readable source per ID, not a globally unique texture-resource table. A
/// later selected patch is the effective source, matching the deployment order.
/// </summary>
internal static class ModelPreviewTextureIndex
{
    public static IReadOnlyDictionary<ulong, TextureInspectionItem> Create(IReadOnlyList<TextureInspectionItem> textures)
    {
        ArgumentNullException.ThrowIfNull(textures);
        return textures
            .GroupBy(static texture => texture.TextureId)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static texture => texture.PatchOrder).Last());
    }
}
