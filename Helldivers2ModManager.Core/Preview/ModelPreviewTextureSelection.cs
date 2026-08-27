namespace Helldivers2ModManager.Core.Preview;

public static class ModelPreviewTextureSelector
{
    public static IReadOnlyList<ulong> SelectAutomaticTextureIds(
        IReadOnlyList<ModelPreviewMesh> meshes,
        int maximumCount)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);

        var candidates = new List<ulong>();
        var priorities = new Dictionary<ulong, int>();
        foreach (var mesh in meshes)
        {
            var hasColorBinding = false;
            foreach (var textureId in mesh.MaterialTextures.Get(ModelPreviewTextureRole.BaseColor))
            {
                AddCandidate(candidates, priorities, textureId, 0);
                hasColorBinding = true;
            }

            if (mesh.ColorTextureId is ulong colorTextureId)
            {
                AddCandidate(candidates, priorities, colorTextureId, 0);
                hasColorBinding = true;
            }

            foreach (var textureId in mesh.MaterialTextures.Get(ModelPreviewTextureRole.Emissive))
                AddCandidate(candidates, priorities, textureId, 1);

            if (!hasColorBinding)
            {
                foreach (var textureId in mesh.TextureIds)
                    AddCandidate(candidates, priorities, textureId, 2);
            }
        }

        return candidates
            .OrderBy(textureId => priorities[textureId])
            .Take(maximumCount)
            .ToArray();
    }

    public static ulong? FindPreferredTextureId(
        ModelPreviewMesh mesh,
        IReadOnlyDictionary<ulong, TexturePreviewCandidate> texturePreviews)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(texturePreviews);

        var semanticColorId = mesh.MaterialTextures
            .Get(ModelPreviewTextureRole.BaseColor)
            .FirstOrDefault(texturePreviews.ContainsKey);
        if (semanticColorId != 0)
            return semanticColorId;

        if (mesh.ColorTextureId is ulong colorTextureId && texturePreviews.ContainsKey(colorTextureId))
            return colorTextureId;

        return mesh.TextureIds
            .Where(texturePreviews.ContainsKey)
            .OrderBy(id => texturePreviews[id].Role == TexturePreviewRole.ColorCandidate ? 0 :
                texturePreviews[id].Role == TexturePreviewRole.Unknown ? 1 : 2)
            .ThenByDescending(id => texturePreviews[id].SourcePixelCount)
            .Cast<ulong?>()
            .FirstOrDefault();
    }

    private static void AddCandidate(
        ICollection<ulong> target,
        IDictionary<ulong, int> priorities,
        ulong textureId,
        int priority)
    {
        if (textureId == 0)
            return;

        if (priorities.TryGetValue(textureId, out var existingPriority))
        {
            priorities[textureId] = Math.Min(existingPriority, priority);
            return;
        }

        priorities[textureId] = priority;
        target.Add(textureId);
    }
}

public sealed record TexturePreviewCandidate(
    TexturePreviewRole Role,
    long SourcePixelCount);
