namespace Helldivers2ModManager.Models;

/// <summary>
/// The Unit main resource has an explicit MeshInfo table: every GPU stream can contain
/// multiple MeshInfo vertex windows and section index ranges. Keeping that relationship
/// prevents local indices from accidentally addressing the beginning of the whole stream
/// and preserves each mesh's material and Unit transform.
/// </summary>
internal sealed record ModelPreviewMaterialLayout(
    IReadOnlyDictionary<int, IReadOnlyList<ModelPreviewMaterialSection>> SectionsByStream,
    IReadOnlyList<ulong> FallbackTextureIds,
    ulong? FallbackColorTextureId,
    ModelPreviewBodyShape BodyShape,
    ModelPreviewCustomizationSlot CustomizationSlot = ModelPreviewCustomizationSlot.Unknown,
    ModelPreviewUnitRig? Rig = null);

internal sealed record ModelPreviewCustomizationInfo(
    ModelPreviewBodyShape BodyShape,
    ModelPreviewCustomizationSlot Slot);

internal enum ModelPreviewBodyShape
{
    Unknown,
    Any,
    Slim,
    Stocky
}

internal enum ModelPreviewCustomizationSlot
{
    Unknown,
    Torso,
    Hip,
    LeftArm,
    RightArm,
    LeftLeg,
    RightLeg,
    LeftShoulder,
    RightShoulder
}

internal static class ModelPreviewBodyShapeParser
{
    public static ModelPreviewBodyShape Parse(string? bodyType)
    {
        if (string.IsNullOrWhiteSpace(bodyType))
            return ModelPreviewBodyShape.Unknown;

        var value = bodyType.Trim();
        const string prefix = "HelldiverCustomizationBodyType_";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            value = value[prefix.Length..];

        return value switch
        {
            "Any" => ModelPreviewBodyShape.Any,
            "Slim" => ModelPreviewBodyShape.Slim,
            "Stocky" => ModelPreviewBodyShape.Stocky,
            _ => ModelPreviewBodyShape.Unknown
        };
    }

    public static ModelPreviewCustomizationSlot ParseSlot(string? slot)
    {
        if (string.IsNullOrWhiteSpace(slot))
            return ModelPreviewCustomizationSlot.Unknown;

        var value = slot.Trim();
        const string prefix = "HelldiverCustomizationSlot_";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            value = value[prefix.Length..];

        return value switch
        {
            "Torso" => ModelPreviewCustomizationSlot.Torso,
            "Hip" => ModelPreviewCustomizationSlot.Hip,
            "LeftArm" => ModelPreviewCustomizationSlot.LeftArm,
            "RightArm" => ModelPreviewCustomizationSlot.RightArm,
            "LeftLeg" => ModelPreviewCustomizationSlot.LeftLeg,
            "RightLeg" => ModelPreviewCustomizationSlot.RightLeg,
            "LeftShoulder" => ModelPreviewCustomizationSlot.LeftShoulder,
            "RightShoulder" => ModelPreviewCustomizationSlot.RightShoulder,
            _ => ModelPreviewCustomizationSlot.Unknown
        };
    }
}

internal sealed record ModelPreviewMaterialTextures(
    IReadOnlyList<ulong> TextureIds,
    ulong? ColorTextureId,
    IReadOnlyDictionary<ModelPreviewTextureRole, IReadOnlyList<ulong>>? TexturesByRole = null,
    IReadOnlyList<ModelPreviewMaterialInput>? Inputs = null)
{
    public ModelPreviewMaterialTextureSet ToTextureSet() => new(
        TexturesByRole ?? new Dictionary<ModelPreviewTextureRole, IReadOnlyList<ulong>>(),
        TextureIds,
        ColorTextureId,
        Inputs);
}

/// <summary>
/// One entry in Stingray's parallel material semantic and texture-ID tables.
/// Retaining the semantic hash preserves evidence for material types that the preview
/// does not yet classify into a render role.
/// </summary>
internal sealed record ModelPreviewMaterialInput(
    uint SemanticId,
    ulong TextureId,
    ModelPreviewTextureRole Role);

/// <summary>
/// Semantic texture inputs for one material section. A Stingray material can reference
/// several textures; keeping the semantic grouping in the asset graph prevents the
/// renderer from selecting a random normal or mask texture as the visible color map.
/// </summary>
internal sealed record ModelPreviewMaterialTextureSet(
    IReadOnlyDictionary<ModelPreviewTextureRole, IReadOnlyList<ulong>> ByRole,
    IReadOnlyList<ulong> AllTextureIds,
    ulong? ColorTextureId,
    IReadOnlyList<ModelPreviewMaterialInput>? Inputs = null)
{
    public static ModelPreviewMaterialTextureSet Empty { get; } = new(
        new Dictionary<ModelPreviewTextureRole, IReadOnlyList<ulong>>(),
        [],
        null);

    public IReadOnlyList<ulong> Get(ModelPreviewTextureRole role) =>
        ByRole.TryGetValue(role, out var textures) ? textures : [];

    public IEnumerable<ulong> EnumerateRenderableInputs()
    {
        foreach (var role in new[]
        {
            ModelPreviewTextureRole.BaseColor,
            ModelPreviewTextureRole.Emissive,
            ModelPreviewTextureRole.Iridescence,
            ModelPreviewTextureRole.Mask,
            ModelPreviewTextureRole.Normal
        })
        {
            foreach (var textureId in Get(role))
                yield return textureId;
        }
    }
}

internal enum ModelPreviewTextureRole
{
    Unknown,
    BaseColor,
    Normal,
    Mask,
    Emissive,
    Iridescence
}

internal sealed record ModelPreviewMaterialSection(
    int MeshInfoIndex,
    int SectionIndex,
    uint VertexOffset,
    uint VertexCount,
    uint IndexOffset,
    uint IndexCount,
    IReadOnlyList<ulong> TextureIds,
    ulong? ColorTextureId,
    bool IsCullingBody,
    ModelPreviewTransform Transform,
    ModelPreviewMaterialTextureSet? MaterialTextures = null,
    ulong? MaterialId = null,
    int LodIndex = -1,
    int MaterialIndex = 0);

internal readonly record struct ModelPreviewTransform(
    float M11, float M12, float M13, float M14,
    float M21, float M22, float M23, float M24,
    float M31, float M32, float M33, float M34)
{
    public static ModelPreviewTransform Identity { get; } = new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0);

    public (float X, float Y, float Z) TransformPoint(float x, float y, float z) => (
        M11 * x + M12 * y + M13 * z + M14,
        M21 * x + M22 * y + M23 * z + M24,
        M31 * x + M32 * y + M33 * z + M34);
}
