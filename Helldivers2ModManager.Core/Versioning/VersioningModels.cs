namespace Helldivers2ModManager.Core.Versioning;

public enum ModVersionStatus
{
    Unknown,
    Compatible,
    Incompatible,
}

public enum PatchHealthStatus
{
    Healthy,
    Warning,
    Corrupted,
}

public sealed record PatchUnitAnalysis(
    string FileName,
    int EntryIndex,
    long FileId,
    uint Version,
    uint DataSize,
    uint GpuSize,
    int EndingOffset,
    int ExpectedDataSize,
    bool DeclaredSizeMatchesInternal,
    bool IsTruncated,
    int LodGroupOffset,
    int JointListOffset,
    int LodGroupSize,
    bool LodGroupInBounds,
    bool UnitDataInBounds,
    bool LayoutFormatChecked,
    bool LayoutFormatValid,
    int LayoutFormatIssueCount,
    bool GpuStructureChecked,
    bool GpuStructureValid,
    int GpuStructureIssueCount,
    int GpuStreamCount,
    int UnknownGpuComponentCount);

public sealed record PatchTypeDistribution(long TypeId, int ResourceCount);

public sealed record PatchFileAnalysis(
    string FileName,
    long FileSize,
    PatchHealthStatus HealthStatus,
    bool HeaderValid,
    int NumTypes,
    int NumFiles,
    int TotalResources,
    IReadOnlyList<PatchTypeDistribution> ResourceTypes,
    bool TypeDistributionValid,
    int TypeDistributionIssueCount,
    bool FileEntriesInBounds,
    bool MainDataBoundsValid,
    int MainDataIssueCount,
    bool EntryIndicesValid,
    int EntryIndexIssueCount,
    bool HasGpuResources,
    bool RequiresGpuResources,
    bool HasStream,
    bool RequiresStream,
    bool GpuResourceBoundsValid,
    int GpuResourceIssueCount,
    int GpuAlignmentIssueCount,
    bool StreamBoundsValid,
    int StreamIssueCount,
    int StreamAlignmentIssueCount,
    IReadOnlyList<PatchUnitAnalysis> UnitDetails)
{
    public static PatchFileAnalysis Failed(string fileName, long length) => new(
        fileName,
        length,
        PatchHealthStatus.Corrupted,
        false,
        0,
        0,
        0,
        [],
        false,
        0,
        false,
        false,
        1,
        false,
        0,
        false,
        false,
        false,
        false,
        true,
        0,
        0,
        true,
        0,
        0,
        []);
}

public sealed record ModPatchAnalysis(
    IReadOnlyList<PatchFileAnalysis> PatchFiles,
    IReadOnlyList<PatchTypeDistribution> ResourceTypes,
    bool HasStructuralIssues,
    bool HasCompanionFileIssues,
    bool HasUnitStructuralIssues,
    bool HasGpuResourceIssues,
    bool HasStreamResourceIssues,
    int TotalPatchFiles,
    int FilesWithUnits,
    int HealthyFileCount,
    int WarningFileCount,
    int CorruptedFileCount)
{
    public bool HasBlockingStructuralIssues =>
        CorruptedFileCount > 0 ||
        HasCompanionFileIssues ||
        PatchFiles.Any(file =>
            !file.GpuResourceBoundsValid ||
            !file.StreamBoundsValid ||
            file.UnitDetails.Any(unit =>
                !unit.UnitDataInBounds ||
                !unit.LodGroupInBounds ||
                unit.IsTruncated ||
                (unit.LayoutFormatChecked && !unit.LayoutFormatValid)));
}

public sealed record ModVersionCheckResult(
    Guid ModId,
    ModVersionStatus Status,
    uint GameVersion,
    DateTimeOffset LastChecked,
    IReadOnlyList<PatchUnitAnalysis> Units,
    ModPatchAnalysis? DetailedAnalysis,
    IReadOnlySet<long> UnitsMissingGameReference);

public sealed record VersionReferenceResult(uint? ReferenceVersion, bool GameDataAvailable);
