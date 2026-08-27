namespace Helldivers2ModManager.Core.PatchKit;

public sealed record PatchVertexComponent(
    int Index,
    uint Semantic,
    uint Format,
    int Offset,
    int Size,
    bool KnownFormat);

public sealed record PatchUnitSnapshot(
    int TocEntryIndex,
    ulong UnitId,
    ulong TypeId,
    ulong MainOffset,
    uint MainSize,
    ulong GpuOffset,
    uint GpuSize,
    uint Version,
    bool SupportedVersion,
    int ListOffset,
    int StreamCount,
    IReadOnlyList<PatchGpuStreamSnapshot> Streams,
    PatchUnitStructureSnapshot? Structure);

public sealed record PatchGpuStreamSnapshot(
    int StreamIndex,
    int RelativeOffset,
    int AbsoluteOffset,
    IReadOnlyList<PatchVertexComponent> Components,
    ulong ComponentCountRaw,
    uint VertexCount,
    uint VertexStride,
    uint IndexCount,
    uint IndexType,
    uint VertexBufferOffset,
    uint VertexBufferSize,
    uint IndexBufferOffset,
    uint IndexBufferSize,
    bool VertexBufferInGpuRange,
    bool IndexBufferInGpuRange);

public sealed record PatchParseResult(
    PatchFileSnapshot? Snapshot,
    IReadOnlyList<PatchParseIssue> Issues)
{
    public static PatchParseResult Failed(params PatchParseIssue[] issues) => new(null, issues);
}

