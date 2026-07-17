using Helldivers2ModManager.Core.Operations;

namespace Helldivers2ModManager.Core.Archives;

public sealed record ArchiveSafetyLimits
{
    public static ArchiveSafetyLimits Default { get; } = new();

    public int MaximumEntries { get; init; } = 100_000;

    public long MaximumExpandedBytes { get; init; } = 50L * 1024 * 1024 * 1024;

    public long MaximumFileBytes { get; init; } = 20L * 1024 * 1024 * 1024;

    public int MaximumNestedDepth { get; init; } = 3;

    public int MaximumNestedArchives { get; init; } = 100;

    public long RequiredFreeSpaceReserveBytes { get; init; } = 2L * 1024 * 1024 * 1024;
}

public sealed record ArchiveEntryPlan(
    int Index,
    string RelativePath,
    string DestinationPath,
    long ExpandedBytes,
    bool IsDirectory);

public sealed record ArchiveExtractionPlan(
    string ArchivePath,
    string DestinationRoot,
    IReadOnlyList<ArchiveEntryPlan> Entries,
    long TotalExpandedBytes);

public interface IArchiveInspector
{
    Task<OperationResult<ArchiveExtractionPlan>> PlanExtractionAsync(
        string archivePath,
        string destinationRoot,
        ArchiveSafetyLimits limits,
        CancellationToken cancellationToken);

    Task<OperationResult> ExtractAsync(
        ArchiveExtractionPlan plan,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);
}
