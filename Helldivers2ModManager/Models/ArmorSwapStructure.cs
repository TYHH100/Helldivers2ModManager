namespace Helldivers2ModManager.Models;

/// <summary>
/// 一键换甲使用的 Unit 结构信息（来源模组与游戏目标共用同一结构）。
/// 保留原始偏移与计数，供"B 的 Unit 主数据 + A 的网格窗口值/GPU 数据"的
/// 字节级移植（等价 Blender 换甲流程：保留目标 Unit 属性数据，替换网格）。
/// </summary>
internal sealed class ArmorSwapUnitStructure
{
    public required long FileId { get; init; }

    /// <summary>Unit 主数据原始字节（来源侧 = 模组 patch；目标侧 = 游戏 bundle）。</summary>
    public required byte[] MainData { get; init; }

    /// <summary>来源侧：该 Unit 所在 patch 的绝对路径（目标侧为 null）。</summary>
    public string? SourcePatchPath { get; init; }

    /// <summary>来源侧：该 Unit 在 .gpu_resources 中的窗口（目标侧为 0）。</summary>
    public ulong GpuOffset { get; init; }

    /// <summary>来源侧：该 Unit 的 GPU 窗口大小。</summary>
    public uint GpuSize { get; init; }

    /// <summary>
    /// 该 Unit 是否来自头盔包（SDK Helmet 表关联的包）。目标侧在解析时标记；
    /// 来源侧在分析时按游戏索引反查标记——老 SDK 模组的头盔无槽位元数据
    /// （如白银之城的空气头盔），只能靠包归属识别。
    /// </summary>
    public bool IsFromHelmetPackage { get; set; }

    /// <summary>
    /// 目标侧：该 Unit 所属游戏包 ID（16 位 hex）。同名护甲可能有多个变体包
    /// （如 B-01 的 4 个 Variation，游戏随机选用），配对按包逐组分配以保证
    /// 每个变体包都拿到完整的层覆盖。来源侧为空字符串。
    /// </summary>
    public string PackageId { get; init; } = string.Empty;

    public required uint Version { get; init; }
    public required ulong BonesId { get; init; }
    public required ulong StateMachineId { get; init; }

    /// <summary>0x4C 处是否存在可解析的 CustomizationInfo（旧结构护甲为 0）。</summary>
    public required bool HasCustomizationInfo { get; init; }
    public required ModelPreviewBodyShape BodyShape { get; init; }
    public required ModelPreviewCustomizationSlot Slot { get; init; }

    /// <summary>GPU 流描述（StreamInfo 记录），按流索引。</summary>
    public required IReadOnlyList<ArmorSwapStreamStructure> Streams { get; init; }

    /// <summary>MeshInfo 表（含 LOD/Section 的网格窗口引用）。</summary>
    public required IReadOnlyList<ArmorSwapMeshInfoStructure> MeshInfos { get; init; }

    /// <summary>材质表 slot 数组（与 <see cref="MaterialIds"/> 并行）。</summary>
    public required IReadOnlyList<uint> MaterialSlots { get; init; }

    /// <summary>材质表引用的材质资源 FileId 数组（与 <see cref="MaterialSlots"/> 并行）。</summary>
    public required IReadOnlyList<ulong> MaterialIds { get; init; }

    /// <summary>是否有任何可识别的身体/头盔槽位（含未分类部件时不含本体）。</summary>
    public bool IsArmorPart =>
        Slot != ModelPreviewCustomizationSlot.Unknown || !HasCustomizationInfo;
}

/// <summary>一条 GPU 流的组件布局与窗口（vertex/index 窗口相对该 Unit 的 GPU 区域）。</summary>
internal sealed record ArmorSwapStreamStructure(
    int StreamIndex,
    IReadOnlyList<ArmorSwapStreamComponent> Components,
    uint VertexCount,
    uint VertexStride,
    uint IndexCount,
    uint IndexType,
    uint VertexOffset,
    uint VertexSize,
    uint IndexOffset,
    uint IndexSize);

internal readonly record struct ArmorSwapStreamComponent(uint Type, uint Format, int Offset);

/// <summary>一个 MeshInfo：流/LOD/变换引用 + 材质索引表 + Section 网格窗口。</summary>
internal sealed record ArmorSwapMeshInfoStructure(
    int MeshInfoIndex,
    int StreamIndex,
    int LodIndex,
    int TransformIndex,
    IReadOnlyList<uint> MaterialIndices,
    IReadOnlyList<ArmorSwapSectionStructure> Sections);

internal sealed record ArmorSwapSectionStructure(
    int SectionIndex,
    int MaterialIndex,
    uint VertexOffset,
    uint VertexCount,
    uint IndexOffset,
    uint IndexCount);
