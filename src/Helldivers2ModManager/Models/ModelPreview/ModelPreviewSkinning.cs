using System.Numerics;

namespace Helldivers2ModManager.Models;

internal sealed class ModelPreviewSkeleton
{
    public required ulong BonesId { get; init; }
    public required ulong StateMachineId { get; init; }
    public required IReadOnlyList<ModelPreviewSkeletonBone> Bones { get; init; }
}

internal readonly record struct ModelPreviewSkeletonBone(
    int ParentIndex,
    uint NameHash,
    Matrix4x4 BindTransform);

internal sealed class ModelPreviewBonePalette
{
    public required IReadOnlyList<int> TransformIndices { get; init; }
    public required IReadOnlyList<IReadOnlyList<int>> Remaps { get; init; }

    public bool TryResolve(int materialIndex, byte paletteIndex, out int transformIndex)
    {
        transformIndex = -1;
        if (materialIndex < 0 || materialIndex >= Remaps.Count)
            return false;

        var remap = Remaps[materialIndex];
        if (paletteIndex >= remap.Count)
            return false;

        var realIndex = remap[paletteIndex];
        if (realIndex < 0 || realIndex >= TransformIndices.Count)
            return false;

        transformIndex = TransformIndices[realIndex];
        return transformIndex >= 0;
    }
}

internal sealed class ModelPreviewUnitRig
{
    public required ModelPreviewSkeleton Skeleton { get; init; }
    public required IReadOnlyList<ModelPreviewBonePalette> Palettes { get; init; }
}

internal sealed class ModelPreviewSkinningData
{
    public const int InfluencesPerVertex = 4;

    public required ModelPreviewSkeleton Skeleton { get; init; }
    public required int[] TransformIndices { get; init; }
    public required float[] Weights { get; init; }

    public bool IsValidForVertexCount(int vertexCount) =>
        TransformIndices.Length == vertexCount * InfluencesPerVertex &&
        Weights.Length == vertexCount * InfluencesPerVertex;
}

internal readonly record struct ModelPreviewAnimationResourceReference(
    ulong BonesId,
    ulong StateMachineId)
{
    /// <summary>承载该动画引用的 Unit 自带变换层级（哈希/父索引/绑定矩阵）。
    /// 动画重定向以它为源参考系：clip 局部 TRS 沿此层级摆出游戏姿态后，
    /// 以世界差量转移到目标（模组）骨架；为 null 时退回局部差量路径。</summary>
    public ModelPreviewSkeleton? SourceSkeleton { get; init; }
}
