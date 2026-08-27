namespace Helldivers2ModManager.Core.PatchKit;

public sealed record PatchHeader(int TypeCount, int FileCount);

public sealed record PatchTypeEntry(
    int Index,
    ulong Reserved,
    ulong TypeId,
    ulong ResourceCount);

public sealed record PatchTocEntry(
    int Index,
    ulong FileId,
    ulong TypeId,
    ulong MainOffset,
    ulong StreamOffset,
    ulong GpuOffset,
    uint MainSize,
    uint StreamSize,
    uint GpuSize,
    uint EntryIndex)
{
    public bool MainInRange(long length) =>
        length >= 0 && MainOffset <= (ulong)length && MainSize <= (ulong)length - MainOffset;

    public bool GpuInRange(long length) =>
        length >= 0 && GpuOffset <= (ulong)length && GpuSize <= (ulong)length - GpuOffset;

    public bool StreamInRange(long length) =>
        length >= 0 && StreamOffset <= (ulong)length && StreamSize <= (ulong)length - StreamOffset;
}

public sealed record PatchCompanionInfo(bool Exists, long Length);

public sealed record PatchFileSnapshot(
    string Path,
    long FileLength,
    PatchHeader Header,
    IReadOnlyList<PatchTypeEntry> Types,
    IReadOnlyList<PatchTocEntry> Entries,
    bool FileEntriesInBounds,
    int EntryIndexIssueCount,
    int TypeDistributionIssueCount,
    int MainDataIssueCount,
    PatchCompanionInfo? GpuResources,
    PatchCompanionInfo? Stream,
    bool RequiresGpuResources,
    bool RequiresStream,
    int GpuRangeIssueCount,
    int GpuAlignmentIssueCount,
    int StreamRangeIssueCount,
    int StreamAlignmentIssueCount,
    IReadOnlyList<PatchUnitSnapshot> Units,
    IReadOnlyList<PatchParseIssue> Issues)
{
    public bool HasErrors => Issues.Any(issue => issue.Severity == PatchParseSeverity.Error);
}

public sealed record PatchTypeDistribution(ulong TypeId, int DeclaredCount, int ActualCount);

public sealed record PatchUnitStructureSnapshot(
    int LodGroupOffset,
    int JointListOffset,
    int EndingOffset,
    int ExpectedDataSize,
    bool DeclaredSizeMatchesInternal,
    bool IsTruncated,
    bool LodGroupInBounds,
    bool LayoutFormatChecked,
    bool LayoutFormatValid,
    int LayoutFormatIssueCount,
    bool GpuStructureChecked,
    bool GpuStructureValid,
    int UnknownGpuComponentCount);

