using System.Security.Cryptography;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Persistence;

namespace Helldivers2ModManager.Core.Mods;

public sealed record DirectoryHashProgress(int CheckedCount, int TotalCount, string CurrentFile);

public sealed record CachedDirectoryHashProgress(
    int CheckedCount,
    int TotalCount,
    string CurrentFile,
    int CacheHits);

public sealed record HashComparison(
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> DeletedFiles,
    int UnchangedCount,
    int TotalNewFiles,
    int TotalCurrentFiles);

public sealed class FileHashService(IFileHashRepository repository)
{
    public const long LargeGpuFileThresholdBytes = 1L << 30;

    public async Task<string> ComputeFileHashAsync(FileInfo file, CancellationToken cancellationToken = default)
    {
        using var hashAlgorithm = SHA256.Create();
        await using var stream = file.OpenRead();
        var hash = await hashAlgorithm.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public async Task<IReadOnlyDictionary<string, string>> ComputeDirectoryHashesAsync(
        DirectoryInfo directory,
        IProgress<DirectoryHashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = EnumerateOrderedFiles(directory);
        var result = new Dictionary<string, string>(files.Length, StringComparer.OrdinalIgnoreCase);
        var pending = new List<HashWorkItem>();

        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var relativePath = GetRelativePath(directory, file);

            if (TryCreateLargeGpuFastHash(file, out var fastHash))
            {
                result[relativePath] = fastHash;
            }
            else
            {
                pending.Add(new HashWorkItem(index, file, relativePath));
            }
        }

        var hashed = new (string RelativePath, string Hash)?[files.Length];
        await ComputePendingAsync(pending, hashed, progress, cancellationToken).ConfigureAwait(false);
        MergeComputed(result, hashed);
        progress?.Report(new(files.Length, files.Length, string.Empty));
        return result;
    }

    public Task<IReadOnlyDictionary<string, string>> ComputeDirectoryHashesWithCacheAsync(
        DirectoryInfo directory,
        Guid modGuid,
        bool saveToDatabase,
        IProgress<CachedDirectoryHashProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ComputeCachedAsync(directory, modGuid, saveToDatabase, progress, cancellationToken);

    public static HashComparison CompareHashes(
        IReadOnlyDictionary<string, string> currentHashes,
        IReadOnlyDictionary<string, string> newHashes)
    {
        var changedFiles = new List<string>();
        var deletedFiles = new List<string>();
        var unchangedCount = 0;

        foreach (var (relativePath, newHash) in newHashes)
        {
            if (!currentHashes.TryGetValue(relativePath, out var currentHash))
            {
                changedFiles.Add(relativePath);
            }
            else if (!string.Equals(currentHash, newHash, StringComparison.OrdinalIgnoreCase))
            {
                changedFiles.Add(relativePath);
            }
            else
            {
                unchangedCount++;
            }
        }

        foreach (var relativePath in currentHashes.Keys)
        {
            if (!newHashes.ContainsKey(relativePath))
            {
                deletedFiles.Add(relativePath);
            }
        }

        return new(changedFiles, deletedFiles, unchangedCount, newHashes.Count, currentHashes.Count);
    }

    private async Task<IReadOnlyDictionary<string, string>> ComputeCachedAsync(
        DirectoryInfo directory,
        Guid modGuid,
        bool saveToDatabase,
        IProgress<CachedDirectoryHashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = EnumerateOrderedFiles(directory);
        var cachedRecords = await repository.LoadForModAsync(modGuid, cancellationToken).ConfigureAwait(false);
        var cachedHashes = cachedRecords.ToDictionary(record => record.FilePath, record => record, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(files.Length, StringComparer.OrdinalIgnoreCase);
        var pending = new List<CachedHashWorkItem>();
        var computedRecords = new List<FileHashRecord>();
        var retainedCachedRecords = new List<FileHashRecord>();
        var cacheHits = 0;

        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var relativePath = GetRelativePath(directory, file);
            var fileSize = file.Length;
            var lastModified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);

            if (cachedHashes.TryGetValue(relativePath, out var cached) &&
                cached.FileSize == fileSize &&
                cached.LastModifiedUtc == lastModified)
            {
                result[relativePath] = cached.FileHash;
                retainedCachedRecords.Add(cached);
                cacheHits++;
                continue;
            }

            if (TryCreateLargeGpuFastHash(file, out var fastHash))
            {
                result[relativePath] = fastHash;
                computedRecords.Add(new(modGuid, relativePath, fastHash, fileSize, lastModified));
                continue;
            }

            pending.Add(new CachedHashWorkItem(index, file, relativePath, fileSize, lastModified));
        }

        var initialCacheHits = cacheHits;
        var computedCount = 0;
        var hashed = new (string RelativePath, string Hash, long Size, DateTimeOffset LastModified)?[files.Length];
        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = ConcurrencyPolicy.GetIoParallelism(Environment.ProcessorCount),
                CancellationToken = cancellationToken,
            },
            async (item, token) =>
            {
                var hash = await ComputeFileHashAsync(item.File, token).ConfigureAwait(false);
                hashed[item.Index] = (item.RelativePath, hash, item.Size, item.LastModified);
                var completed = Interlocked.Increment(ref computedCount);
                progress?.Report(new(initialCacheHits + completed, files.Length, item.RelativePath, initialCacheHits));
            }).ConfigureAwait(false);

        foreach (var entry in hashed)
        {
            if (entry is not { } value)
            {
                continue;
            }

            result[value.RelativePath] = value.Hash;
            computedRecords.Add(new(modGuid, value.RelativePath, value.Hash, value.Size, value.LastModified));
        }

        progress?.Report(new(files.Length, files.Length, string.Empty, initialCacheHits));

        if (saveToDatabase && computedRecords.Count > 0)
        {
            var records = retainedCachedRecords
                .Concat(computedRecords)
                .ToList();
            await repository.ReplaceForModAsync(modGuid, records, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task ComputePendingAsync(
        IReadOnlyList<HashWorkItem> pending,
        (string RelativePath, string Hash)?[] hashed,
        IProgress<DirectoryHashProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            progress?.Report(new(0, 0, string.Empty));
            return;
        }

        var completedCount = 0;
        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = ConcurrencyPolicy.GetIoParallelism(Environment.ProcessorCount),
                CancellationToken = cancellationToken,
            },
            async (item, token) =>
            {
                string hash;
                try
                {
                    hash = await ComputeFileHashAsync(item.File, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not IOException and not OperationCanceledException)
                {
                    throw new IOException($"Unable to hash \"{item.RelativePath}\": {exception.Message}", exception);
                }

                hashed[item.Index] = (item.RelativePath, hash);
                var completed = Interlocked.Increment(ref completedCount);
                progress?.Report(new(completed, pending.Count, item.RelativePath));
            }).ConfigureAwait(false);
    }

    private static void MergeComputed(
        Dictionary<string, string> result,
        IEnumerable<(string RelativePath, string Hash)?> hashed)
    {
        foreach (var entry in hashed)
        {
            if (entry is { } value)
            {
                result[value.RelativePath] = value.Hash;
            }
        }
    }

    internal bool TryCreateLargeGpuFastHash(FileInfo file, out string fastHash)
    {
        if (string.Equals(file.Extension, ".gpu_resources", StringComparison.OrdinalIgnoreCase) &&
            file.Length > LargeGpuFileThresholdBytes)
        {
            fastHash = $"__gpu_{file.Length}_{file.LastWriteTimeUtc.Ticks}";
            return true;
        }

        fastHash = string.Empty;
        return false;
    }

    private static FileInfo[] EnumerateOrderedFiles(DirectoryInfo directory)
    {
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"Directory not found: {directory.FullName}");
        }

        return directory.EnumerateFiles("*", SearchOption.AllDirectories)
            .OrderBy(file => file.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    private static string GetRelativePath(DirectoryInfo directory, FileSystemInfo file)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.FullName));
        var relativePath = Path.GetRelativePath(root, file.FullName);
        return relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private sealed record HashWorkItem(int Index, FileInfo File, string RelativePath);

    private sealed record CachedHashWorkItem(
        int Index,
        FileInfo File,
        string RelativePath,
        long Size,
        DateTimeOffset LastModified);
}
