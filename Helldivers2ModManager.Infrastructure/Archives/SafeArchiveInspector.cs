using Helldivers2ModManager.Core.Archives;
using Helldivers2ModManager.Core.Operations;
using Helldivers2ModManager.Core.Security;
using SharpSevenZip;
using CoreOperationResult = Helldivers2ModManager.Core.Operations.OperationResult;

namespace Helldivers2ModManager.Infrastructure.Archives;

public sealed class SafeArchiveInspector : IArchiveInspector
{
    private readonly ISafePathPolicy _safePathPolicy;

    public SafeArchiveInspector(ISafePathPolicy safePathPolicy)
    {
        _safePathPolicy = safePathPolicy;
    }

    public Task<OperationResult<ArchiveExtractionPlan>> PlanExtractionAsync(
        string archivePath,
        string destinationRoot,
        ArchiveSafetyLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return Task.Run(() => PlanExtraction(archivePath, destinationRoot, limits, cancellationToken), cancellationToken);
    }

    public Task<CoreOperationResult> ExtractAsync(
        ArchiveExtractionPlan plan,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Task.Run(() => Extract(plan, progress, cancellationToken), cancellationToken);
    }

    private OperationResult<ArchiveExtractionPlan> PlanExtraction(
        string archivePath,
        string destinationRoot,
        ArchiveSafetyLimits limits,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(archivePath))
            return CoreOperationResult.Failure<ArchiveExtractionPlan>("Archive.NotFound");

        try
        {
            Directory.CreateDirectory(destinationRoot);
            using var extractor = new SharpSevenZipExtractor(archivePath);
            if (extractor.ArchiveFileData.Count > limits.MaximumEntries)
                return CoreOperationResult.Failure<ArchiveExtractionPlan>("Archive.EntryLimitExceeded");

            var entries = new List<ArchiveEntryPlan>(extractor.ArchiveFileData.Count);
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            foreach (var entry in extractor.ArchiveFileData)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Encrypted)
                    return CoreOperationResult.Failure<ArchiveExtractionPlan>("Archive.EncryptedEntry");
                if ((entry.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                    return CoreOperationResult.Failure<ArchiveExtractionPlan>("Archive.LinkEntry");

                var expandedBytes = checked((long)entry.Size);
                if (expandedBytes > limits.MaximumFileBytes)
                    return CoreOperationResult.Failure<ArchiveExtractionPlan>("Archive.FileLimitExceeded");
                totalBytes = checked(totalBytes + expandedBytes);
                if (totalBytes > limits.MaximumExpandedBytes)
                    return CoreOperationResult.Failure<ArchiveExtractionPlan>("Archive.ExpandedSizeLimitExceeded");

                var destination = _safePathPolicy.ResolveUnderRoot(destinationRoot, entry.FileName);
                if (!destinations.Add(destination))
                    return CoreOperationResult.Failure<ArchiveExtractionPlan>("Archive.DuplicatePath");
                entries.Add(new ArchiveEntryPlan(entry.Index, entry.FileName, destination, expandedBytes, entry.IsDirectory));
            }

            if (!HasRequiredFreeSpace(destinationRoot, totalBytes, limits.RequiredFreeSpaceReserveBytes))
                return CoreOperationResult.Failure<ArchiveExtractionPlan>("Archive.InsufficientDiskSpace");

            return CoreOperationResult.Success(
                new ArchiveExtractionPlan(
                    Path.GetFullPath(archivePath),
                    Path.GetFullPath(destinationRoot),
                    entries,
                    totalBytes));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or UnauthorizedAccessException)
        {
            return CoreOperationResult.Failure<ArchiveExtractionPlan>("Archive.Invalid", ex.Message);
        }
    }

    private CoreOperationResult Extract(
        ArchiveExtractionPlan plan,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var extractor = new SharpSevenZipExtractor(plan.ArchivePath);
            long completedBytes = 0;
            foreach (var entry in plan.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = _safePathPolicy.ResolveUnderRoot(plan.DestinationRoot, entry.RelativePath);
                if (!string.Equals(destination, entry.DestinationPath, StringComparison.OrdinalIgnoreCase))
                    return CoreOperationResult.Failure("Archive.PlanChanged");

                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(destination);
                }
                else
                {
                    var parent = Path.GetDirectoryName(destination)
                        ?? throw new InvalidDataException("An archive entry has no parent directory.");
                    Directory.CreateDirectory(parent);
                    using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    extractor.ExtractFile(entry.Index, stream);
                    if (stream.Length != entry.ExpandedBytes)
                        return CoreOperationResult.Failure("Archive.SizeMismatch");
                }

                completedBytes += entry.ExpandedBytes;
                progress?.Report(new OperationProgress(
                    "Extract",
                    completedBytes,
                    plan.TotalExpandedBytes,
                    entry.RelativePath));
            }
            return CoreOperationResult.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return CoreOperationResult.Failure("Archive.ExtractionFailed", ex.Message);
        }
    }

    private static bool HasRequiredFreeSpace(string destinationRoot, long expandedBytes, long reserveBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(destinationRoot));
        if (string.IsNullOrEmpty(root))
            return false;
        var drive = new DriveInfo(root);
        return drive.AvailableFreeSpace >= checked(expandedBytes + reserveBytes);
    }
}
