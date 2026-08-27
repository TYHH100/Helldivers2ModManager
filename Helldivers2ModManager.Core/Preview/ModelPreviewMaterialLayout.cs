namespace Helldivers2ModManager.Core.Preview;

public static class UnitMaterialLayoutReader
{
    public static ModelPreviewMesh? CreateSectionMesh(ModelPreviewMesh source, ModelPreviewMaterialSection section)
    {
        if (section.VertexOffset > int.MaxValue || section.VertexCount > int.MaxValue ||
            section.IndexOffset > int.MaxValue || section.IndexCount > int.MaxValue)
            return null;

        var vertexStart = (int)section.VertexOffset;
        var vertexCount = (int)section.VertexCount;
        var start = (int)section.IndexOffset;
        var count = (int)section.IndexCount;
        count -= count % 3;
        if (vertexCount <= 0 || vertexStart > source.VertexCount || vertexCount > source.VertexCount - vertexStart ||
            start < 0 || start > source.TriangleIndices.Length || count < 3 ||
            count > source.TriangleIndices.Length - start)
            return null;

        var remap = new Dictionary<int, int>();
        var positions = new List<float>();
        var coordinates = source.TextureCoordinates is { Length: > 0 } ? new List<float>() : null;
        var indices = new int[count];
        for (var index = 0; index < count; index++)
        {
            var localVertex = source.TriangleIndices[start + index];
            if (localVertex < 0 || localVertex >= vertexCount)
                return null;

            var sourceVertex = vertexStart + localVertex;
            if (!remap.TryGetValue(sourceVertex, out var targetVertex))
            {
                targetVertex = remap.Count;
                remap.Add(sourceVertex, targetVertex);
                var positionOffset = sourceVertex * 3;
                var transformed = section.Transform.TransformPoint(
                    source.Positions[positionOffset],
                    source.Positions[positionOffset + 1],
                    source.Positions[positionOffset + 2]);
                if (!float.IsFinite(transformed.X) || !float.IsFinite(transformed.Y) || !float.IsFinite(transformed.Z))
                    return null;

                positions.Add(transformed.X);
                positions.Add(transformed.Y);
                positions.Add(transformed.Z);
                if (coordinates is not null && source.TextureCoordinates is { } sourceCoordinates)
                {
                    var coordinateOffset = sourceVertex * 2;
                    coordinates.Add(sourceCoordinates[coordinateOffset]);
                    coordinates.Add(sourceCoordinates[coordinateOffset + 1]);
                }
            }

            indices[index] = targetVertex;
        }

        var compactPositions = positions.ToArray();
        return new ModelPreviewMesh
        {
            PatchFile = source.PatchFile,
            UnitId = source.UnitId,
            StreamIndex = source.StreamIndex,
            MeshInfoIndex = section.MeshInfoIndex,
            SourceVertexOffset = section.VertexOffset,
            SourceVertexCount = section.VertexCount,
            SourceIndexOffset = section.IndexOffset,
            SourceIndexCount = section.IndexCount,
            BodyShape = source.BodyShape,
            CustomizationSlot = source.CustomizationSlot,
            Positions = compactPositions,
            Normals = ModelPreviewNormals.BuildSmoothedNormals(compactPositions, indices),
            TextureCoordinates = coordinates?.ToArray(),
            TriangleIndices = indices,
            TextureIds = section.TextureIds.Count > 0 ? section.TextureIds : source.TextureIds,
            ColorTextureId = section.ColorTextureId ?? source.ColorTextureId,
            MaterialId = section.MaterialId,
            MaterialTextures = section.MaterialTextures ?? source.MaterialTextures,
            IsCullingBody = section.IsCullingBody
        };
    }

    public static IReadOnlyDictionary<int, IReadOnlyList<ModelPreviewMaterialSection>> TryReadUnitMaterialSections(
        byte[] data,
        IReadOnlyDictionary<ulong, ModelPreviewMaterialTextures> materialTextures)
    {
        const int unitMeshInfoOffset = 0x64;
        const int unitMaterialsOffset = 0x70;
        const int meshInfoSize = 128;
        const int meshInfoTransformIndexOffset = 48;
        const int meshInfoLodIndexOffset = 56;
        const int meshInfoStreamIndexOffset = 60;
        const int meshInfoMaterialCountOffset = 104;
        const int meshInfoMaterialOffset = 108;
        const int meshInfoSectionCountOffset = 120;
        const int meshInfoSectionsOffset = 124;
        const int meshSectionSize = 24;
        var empty = new Dictionary<int, IReadOnlyList<ModelPreviewMaterialSection>>();
        if (data.Length < unitMaterialsOffset + sizeof(uint)) return empty;

        var meshInfoOffset = ReadInt32(data, unitMeshInfoOffset);
        var materialsOffset = ReadInt32(data, unitMaterialsOffset);
        if (!InRange(meshInfoOffset, sizeof(uint), data.Length) || !InRange(materialsOffset, sizeof(uint), data.Length)) return empty;
        var materialCount = ReadInt32(data, materialsOffset);
        if (materialCount < 0 || materialCount > 4096 ||
            !InRange(materialsOffset + sizeof(uint), materialCount * sizeof(uint), data.Length) ||
            !InRange(materialsOffset + sizeof(uint) + materialCount * sizeof(uint), materialCount * sizeof(ulong), data.Length))
            return empty;

        var materialBySlot = new Dictionary<uint, ulong>();
        for (var index = 0; index < materialCount; index++)
        {
            var slot = ReadUInt32(data, materialsOffset + sizeof(uint) + index * sizeof(uint));
            var materialId = ReadUInt64(data, materialsOffset + sizeof(uint) + materialCount * sizeof(uint) + index * sizeof(ulong));
            materialBySlot.TryAdd(slot, materialId);
        }

        var meshCount = ReadInt32(data, meshInfoOffset);
        var offsetsStart = meshInfoOffset + sizeof(uint);
        if (meshCount < 0 || meshCount > 4096 || !InRange(offsetsStart, meshCount * sizeof(uint) * 2L, data.Length)) return empty;
        var transforms = TryReadUnitTransforms(data);
        var preferredLod = int.MaxValue;
        for (var meshIndex = 0; meshIndex < meshCount; meshIndex++)
        {
            var relative = ReadInt32(data, offsetsStart + meshIndex * sizeof(uint));
            var meshOffset = meshInfoOffset + relative;
            if (relative < 0 || !InRange(meshOffset, meshInfoSize, data.Length)) continue;
            var lod = ReadInt32(data, meshOffset + meshInfoLodIndexOffset);
            if (lod >= 0) preferredLod = Math.Min(preferredLod, lod);
        }

        var sectionsByStream = new Dictionary<int, List<ModelPreviewMaterialSection>>();
        for (var meshIndex = 0; meshIndex < meshCount; meshIndex++)
        {
            var relative = ReadInt32(data, offsetsStart + meshIndex * sizeof(uint));
            var meshOffset = meshInfoOffset + relative;
            if (relative < 0 || !InRange(meshOffset, meshInfoSize, data.Length)) continue;
            var streamIndex = ReadInt32(data, meshOffset + meshInfoStreamIndexOffset);
            var lodIndex = ReadInt32(data, meshOffset + meshInfoLodIndexOffset);
            var transformIndex = ReadInt32(data, meshOffset + meshInfoTransformIndexOffset);
            var materialCountForMesh = ReadInt32(data, meshOffset + meshInfoMaterialCountOffset);
            var materialsRelative = ReadInt32(data, meshOffset + meshInfoMaterialOffset);
            var sectionCount = ReadInt32(data, meshOffset + meshInfoSectionCountOffset);
            var sectionsRelative = ReadInt32(data, meshOffset + meshInfoSectionsOffset);
            var materialTableOffset = (long)meshOffset + materialsRelative;
            var sectionTableOffset = (long)meshOffset + sectionsRelative;
            if (streamIndex < 0 || materialCountForMesh < 0 || materialCountForMesh > 4096 ||
                sectionCount < 0 || sectionCount > 4096 || materialsRelative < 0 || sectionsRelative < 0 ||
                !InRange(materialTableOffset, materialCountForMesh * sizeof(uint), data.Length) ||
                !InRange(sectionTableOffset, sectionCount * meshSectionSize, data.Length))
                continue;

            if (!sectionsByStream.TryGetValue(streamIndex, out var sections))
                sectionsByStream[streamIndex] = sections = [];
            if (lodIndex >= 0 && preferredLod != int.MaxValue && lodIndex != preferredLod) continue;
            var transform = transformIndex >= 0 && transformIndex < transforms.Count ? transforms[transformIndex] : ModelPreviewTransform.Identity;
            var rawSections = new List<(int SectionIndex, int MaterialIndex, uint Slot, uint VertexOffset, uint VertexCount, uint IndexOffset, uint IndexCount)>();
            for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
            {
                var offset = checked((int)(sectionTableOffset + sectionIndex * meshSectionSize));
                var materialIndex = ReadInt32(data, offset);
                if (materialIndex < 0 || materialIndex >= materialCountForMesh) continue;
                rawSections.Add((
                    sectionIndex,
                    materialIndex,
                    ReadUInt32(data, checked((int)(materialTableOffset + materialIndex * sizeof(uint)))),
                    ReadUInt32(data, offset + 4),
                    ReadUInt32(data, offset + 8),
                    ReadUInt32(data, offset + 12),
                    ReadUInt32(data, offset + 16)));
            }
            if (rawSections.Count == 0) continue;
            var isCullingBody = rawSections.All(section => !materialBySlot.ContainsKey(section.Slot));
            foreach (var raw in rawSections)
            {
                IReadOnlyList<ulong> textureIds = [];
                ulong? colorTextureId = null;
                ModelPreviewMaterialTextureSet? textureSet = null;
                ulong? materialIdForSection = null;
                if (materialBySlot.TryGetValue(raw.Slot, out var materialId) && materialTextures.TryGetValue(materialId, out var resolved))
                {
                    textureIds = resolved.TextureIds;
                    colorTextureId = resolved.ColorTextureId;
                    textureSet = resolved.ToTextureSet();
                    materialIdForSection = materialId;
                }
                sections.Add(new(meshIndex, raw.SectionIndex, raw.VertexOffset, raw.VertexCount, raw.IndexOffset,
                    raw.IndexCount, textureIds, colorTextureId, isCullingBody, transform, textureSet,
                    materialIdForSection, lodIndex, raw.MaterialIndex));
            }
        }

        return sectionsByStream.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ModelPreviewMaterialSection>)pair.Value);
    }

    private static IReadOnlyList<ModelPreviewTransform> TryReadUnitTransforms(byte[] data)
    {
        const int pointerOffset = 0x34;
        const int headerSize = 16;
        const int localTransformSize = 64;
        const int matrixSize = 64;
        if (data.Length < pointerOffset + sizeof(uint)) return [];
        var infoOffset = ReadInt32(data, pointerOffset);
        if (!InRange(infoOffset, headerSize, data.Length)) return [];
        var count = ReadInt32(data, infoOffset);
        if (count < 0 || count > 65_536) return [];
        var matricesOffset = (long)infoOffset + headerSize + (long)count * localTransformSize;
        if (!InRange(matricesOffset, (long)count * matrixSize, data.Length)) return [];
        var transforms = new ModelPreviewTransform[count];
        for (var index = 0; index < count; index++)
        {
            var offset = checked((int)(matricesOffset + index * matrixSize));
            var values = new float[16];
            var valid = true;
            for (var component = 0; component < values.Length; component++)
            {
                values[component] = BitConverter.ToSingle(data, offset + component * sizeof(float));
                valid &= float.IsFinite(values[component]);
            }
            transforms[index] = valid
                ? new(values[0], values[4], values[8], values[12], values[1], values[5], values[9], values[13], values[2], values[6], values[10], values[14])
                : ModelPreviewTransform.Identity;
        }
        return transforms;
    }

    private static bool InRange(long offset, long size, long total) => offset >= 0 && size >= 0 && offset <= total && size <= total - offset;
    private static int ReadInt32(byte[] data, long offset) => BitConverter.ToInt32(data, checked((int)offset));
    private static uint ReadUInt32(byte[] data, long offset) => BitConverter.ToUInt32(data, checked((int)offset));
    private static ulong ReadUInt64(byte[] data, long offset) => BitConverter.ToUInt64(data, checked((int)offset));
}

/// <summary>
/// The Unit main resource has an explicit MeshInfo table: every GPU stream can contain
/// multiple MeshInfo vertex windows and section index ranges. Keeping that relationship
/// prevents local indices from accidentally addressing the beginning of the whole stream
/// and preserves each mesh's material and Unit transform.
/// </summary>
public sealed record ModelPreviewMaterialLayout(
    IReadOnlyDictionary<int, IReadOnlyList<ModelPreviewMaterialSection>> SectionsByStream,
    IReadOnlyList<ulong> FallbackTextureIds,
    ulong? FallbackColorTextureId,
    ModelPreviewBodyShape BodyShape,
    ModelPreviewCustomizationSlot CustomizationSlot = ModelPreviewCustomizationSlot.Unknown,
    ModelPreviewUnitRig? Rig = null);

public sealed record ModelPreviewCustomizationInfo(
    ModelPreviewBodyShape BodyShape,
    ModelPreviewCustomizationSlot Slot);

public enum ModelPreviewBodyShape
{
    Unknown,
    Any,
    Slim,
    Stocky
}

public enum ModelPreviewCustomizationSlot
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

public static class ModelPreviewBodyShapeParser
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

public sealed record ModelPreviewMaterialTextures(
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
public sealed record ModelPreviewMaterialInput(
    uint SemanticId,
    ulong TextureId,
    ModelPreviewTextureRole Role);

/// <summary>
/// Semantic texture inputs for one material section. A Stingray material can reference
/// several textures; keeping the semantic grouping in the asset graph prevents the
/// renderer from selecting a random normal or mask texture as the visible color map.
/// </summary>
public sealed record ModelPreviewMaterialTextureSet(
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
            ModelPreviewTextureRole.Mask,
            ModelPreviewTextureRole.Normal
        })
        {
            foreach (var textureId in Get(role))
                yield return textureId;
        }
    }
}

public enum ModelPreviewTextureRole
{
    Unknown,
    BaseColor,
    Normal,
    Mask,
    Emissive
}

public sealed record ModelPreviewMaterialSection(
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

public readonly record struct ModelPreviewTransform(
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

