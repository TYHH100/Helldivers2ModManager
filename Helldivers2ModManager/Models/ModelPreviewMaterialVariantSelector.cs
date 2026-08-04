namespace Helldivers2ModManager.Models;

/// <summary>
/// Selects one material variant for a shared section without conflating the Unit's
/// index-buffer storage offset with its triangle geometry.
/// </summary>
internal static class ModelPreviewMaterialVariantSelector
{
    public static IReadOnlyList<ModelPreviewMesh> SelectPreferredVariants(
        IReadOnlyList<ModelPreviewMesh> meshes,
        Func<ulong, long> getColorTexturePixels)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(getColorTexturePixels);

        var selected = new List<ModelPreviewMesh>(meshes.Count);
        foreach (var group in meshes
                     .Select(static (mesh, index) => (mesh, index))
                     .GroupBy(static item => new VariantGeometryKey(
                         item.mesh.PatchFile,
                         item.mesh.UnitId,
                         item.mesh.StreamIndex,
                         item.mesh.MeshInfoIndex,
                         item.mesh.SourceVertexOffset,
                         item.mesh.SourceVertexCount,
                         item.mesh.SourceIndexCount)))
        {
            if (group.Key.MeshInfoIndex < 0 || group.Count() == 1)
            {
                selected.AddRange(group.Select(static item => item.mesh));
                continue;
            }

            // Material variants share MeshInfo/vertex range/index count but frequently
            // point at different index-buffer offsets. IndexOffset is deliberately not
            // part of VariantGeometryKey: it identifies storage, not geometry.
            selected.Add(group
                .OrderByDescending(item => item.mesh.ColorTextureId is ulong textureId
                    ? getColorTexturePixels(textureId)
                    : 0)
                .ThenByDescending(static item => item.mesh.ColorTextureId.HasValue)
                .ThenBy(static item => item.index)
                .First()
                .mesh);
        }

        return selected;
    }

    public static IReadOnlyList<ModelPreviewMesh> FilterPureBlackPlaceholders(
        IReadOnlyList<ModelPreviewMesh> meshes,
        IReadOnlySet<ulong> pureBlackBaseColorTextureIds)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(pureBlackBaseColorTextureIds);
        if (pureBlackBaseColorTextureIds.Count == 0)
            return meshes;

        var selected = new List<ModelPreviewMesh>(meshes.Count);
        foreach (var stream in meshes.GroupBy(static mesh => (mesh.PatchFile, mesh.UnitId, mesh.StreamIndex)))
        {
            // A black material remains valid when it is this stream's only color source;
            // otherwise it is the known low-resolution placeholder companion.
            var hasNonBlackSection = stream.Any(mesh =>
                mesh.MaterialId.HasValue &&
                mesh.ColorTextureId is ulong colorTextureId &&
                !pureBlackBaseColorTextureIds.Contains(colorTextureId));
            foreach (var mesh in stream)
            {
                var isPlaceholder = mesh.MaterialId.HasValue &&
                    mesh.ColorTextureId is ulong colorTextureId &&
                    pureBlackBaseColorTextureIds.Contains(colorTextureId);
                if (!hasNonBlackSection || !isPlaceholder)
                    selected.Add(mesh);
            }
        }

        return selected;
    }

    internal static bool IsBc7PureBlackPlaceholder(ReadOnlySpan<byte> data)
    {
        const int blockSize = 16;
        const int requiredBytes = blockSize * 4;
        if (data.Length < requiredBytes)
            return false;

        var firstBlock = data[..blockSize];
        for (var blockIndex = 0; blockIndex < 4; blockIndex++)
        {
            var block = data.Slice(blockIndex * blockSize, blockSize);
            var nonZeroCount = 0;
            foreach (var value in block)
                nonZeroCount += value == 0 ? 0 : 1;
            if (!block.SequenceEqual(firstBlock) || nonZeroCount > 6)
                return false;
        }

        return true;
    }

    private readonly record struct VariantGeometryKey(
        string PatchFile,
        ulong UnitId,
        int StreamIndex,
        int MeshInfoIndex,
        uint VertexOffset,
        uint VertexCount,
        uint IndexCount);
}
