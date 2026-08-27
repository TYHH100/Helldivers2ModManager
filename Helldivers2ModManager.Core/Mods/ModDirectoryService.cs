using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Core.Mods;

public sealed record ImportProgress(int CopiedCount, int TotalCount, string CurrentFile);

public sealed class ModDirectoryService(
    FileHashService fileHashService,
    FileHashRepository fileHashRepository,
    ILogger<ModDirectoryService> logger,
    IRecycleBinAdapter? recycleBinAdapter = null)
{
    public async Task<Result<DiscoveredMod>> ImportDirectoryAsync(
        DirectoryInfo sourceDirectory,
        DirectoryInfo storageDirectory,
        bool replaceExisting = false,
        bool deleteExistingToRecycleBin = false,
        bool mutateSourceManifest = false,
        CancellationToken cancellationToken = default)
    {
        if (!sourceDirectory.Exists)
        {
            return Result.Fail<DiscoveredMod>(Error.Create(CoreErrorCode.PathNotFound, $"Source directory does not exist: {sourceDirectory.FullName}"));
        }

        var manifestFile = FindManifest(sourceDirectory);
        var inferredManifest = manifestFile is null;
        var manifest = inferredManifest
            ? ModManifest.InferFromDirectory(sourceDirectory, logger)
            : ModManifest.DeserializeFromFile(manifestFile!, logger);
        manifest = ModManifestSanitizer.SanitizeImagePaths(manifest, sourceDirectory, logger);

        var destination = GetDestination(storageDirectory, manifest.Name);
        if (destination is null)
        {
            return Result.Fail<DiscoveredMod>(Error.Create(CoreErrorCode.InvalidInput, "The manifest name resolves to an invalid mod path."));
        }

        if (mutateSourceManifest)
        {
            ModManifest.SaveToFile(manifest, sourceDirectory);
        }

        if (destination.Exists)
        {
            if (!await AreDirectoriesEqualAsync(sourceDirectory, destination, cancellationToken).ConfigureAwait(false))
            {
                if (!replaceExisting)
                {
                    return Result.Fail<DiscoveredMod>(Error.Create(CoreErrorCode.Conflict, $"A different mod already exists at \"{destination.FullName}\"."));
                }

                var deletion = await DeleteAsync(
                    destination,
                    storageDirectory,
                    manifest.Guid,
                    deleteExistingToRecycleBin,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (deletion.Failed)
                {
                    return Result.Fail<DiscoveredMod>(deletion.Error);
                }
            }
            else
            {
                return Result.Fail<DiscoveredMod>(Error.Create(CoreErrorCode.Conflict, $"The existing mod is identical: \"{destination.FullName}\"."));
            }
        }

        await CopyDirectoryAsync(sourceDirectory, destination, cancellationToken).ConfigureAwait(false);
        if (!mutateSourceManifest)
        {
            ModManifest.SaveToFile(manifest, destination);
        }

        return Result.Success(new DiscoveredMod(destination, manifest));
    }

    private async Task<bool> AreDirectoriesEqualAsync(
        DirectoryInfo left,
        DirectoryInfo right,
        CancellationToken cancellationToken)
    {
        var leftHashes = await fileHashService.ComputeDirectoryHashesAsync(left, cancellationToken: cancellationToken).ConfigureAwait(false);
        var rightHashes = await fileHashService.ComputeDirectoryHashesAsync(right, cancellationToken: cancellationToken).ConfigureAwait(false);
        var comparison = FileHashService.CompareHashes(leftHashes, rightHashes);
        return comparison.ChangedFiles.Count == 0 && comparison.DeletedFiles.Count == 0;
    }

    public ModDiscoveryResult DiscoverMods(DirectoryInfo storageDirectory)
    {
        var modsDirectory = new DirectoryInfo(Path.Combine(storageDirectory.FullName, "Mods"));
        if (!modsDirectory.Exists)
        {
            return new([], []);
        }

        var mods = new List<DiscoveredMod>();
        var problems = new List<Error>();
        foreach (var directory in modsDirectory.EnumerateDirectories().OrderBy(static directory => directory.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var manifestFile = FindManifest(directory);
                if (manifestFile is null)
                {
                    problems.Add(Error.Create(CoreErrorCode.ResourceNotFound, $"Missing manifest.json: {directory.FullName}"));
                    continue;
                }

                mods.Add(new(directory, ModManifest.DeserializeFromFile(manifestFile, logger)));
            }
            catch (Exception exception) when (
                exception is IOException or ManifestParseException or NotSupportedException or InvalidDataException or FormatException)
            {
                problems.Add(Error.Create(CoreErrorCode.InvalidFormat, $"{directory.FullName}: {exception.Message}"));
            }
        }

        return new(mods, problems);
    }

    public async Task<Result<ModUpdateResult>> UpdateFromDirectoryAsync(
        DirectoryInfo currentDirectory,
        DirectoryInfo sourceDirectory,
        IModManifest currentManifest,
        Guid cacheKey,
        bool saveCurrentHashes,
        CancellationToken cancellationToken = default,
        IProgress<ModUpdateProgress>? progress = null)
    {
        if (!currentDirectory.Exists)
        {
            return Result.Fail<ModUpdateResult>(Error.Create(CoreErrorCode.PathNotFound, $"Current mod directory does not exist: {currentDirectory.FullName}"));
        }

        if (!sourceDirectory.Exists)
        {
            return Result.Fail<ModUpdateResult>(Error.Create(CoreErrorCode.PathNotFound, $"Source directory does not exist: {sourceDirectory.FullName}"));
        }

        var sourceManifestFile = FindManifest(sourceDirectory);
        var updatedManifest = sourceManifestFile is null
            ? ModManifest.InferFromDirectory(sourceDirectory, logger)
            : ModManifest.DeserializeFromFile(sourceManifestFile, logger);
        updatedManifest = PreserveIdentity(updatedManifest, currentManifest);
        updatedManifest = ModManifestSanitizer.SanitizeImagePaths(updatedManifest, sourceDirectory, logger);

        var currentHashes = await fileHashService.ComputeDirectoryHashesWithCacheAsync(
            currentDirectory,
            cacheKey,
            saveToDatabase: saveCurrentHashes,
            progress: CreateHashProgress(progress, ModUpdateStage.HashingCurrent),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var newHashes = await fileHashService.ComputeDirectoryHashesAsync(
            sourceDirectory,
            CreatePlainHashProgress(progress),
            cancellationToken).ConfigureAwait(false);
        var comparison = FileHashService.CompareHashes(currentHashes, newHashes);
        progress?.Report(new(ModUpdateStage.Comparing, null, 0, comparison.ChangedFiles.Count, 0, comparison.ChangedFiles.Count));

        if (comparison.ChangedFiles.Count == 0 && comparison.DeletedFiles.Count == 0)
        {
            ModManifest.SaveToFile(updatedManifest, currentDirectory);
            return Result.Success(new ModUpdateResult(updatedManifest, comparison, 0, false));
        }

        progress?.Report(new(ModUpdateStage.Updating, null, 0, comparison.ChangedFiles.Count, 0, comparison.ChangedFiles.Count));
        await CopyChangedFilesAsync(
            sourceDirectory,
            currentDirectory,
            comparison.ChangedFiles,
            progress,
            cancellationToken).ConfigureAwait(false);
        var deletedFiles = await DeleteObsoleteFilesAsync(currentDirectory, newHashes.Keys, cancellationToken).ConfigureAwait(false);
        CleanEmptyDirectories(currentDirectory);
        ModManifest.SaveToFile(updatedManifest, currentDirectory);

        await fileHashService.ComputeDirectoryHashesWithCacheAsync(
            currentDirectory,
            cacheKey,
            saveToDatabase: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return Result.Success(new ModUpdateResult(updatedManifest, comparison, deletedFiles, true));
    }

    public async Task<Result> DeleteAsync(
        DirectoryInfo modDirectory,
        DirectoryInfo storageDirectory,
        Guid modGuid,
        bool sendToRecycleBin,
        Func<string, CancellationToken, Task>? recycleDirectoryAsync = null,
        CancellationToken cancellationToken = default)
    {
        if (!modDirectory.Exists)
        {
            return Result.Failure(Error.Create(CoreErrorCode.PathNotFound, $"Mod directory does not exist: {modDirectory.FullName}"));
        }

        var pathGuard = PathGuard.EnsureInside(Path.GetFullPath(storageDirectory.FullName), Path.GetFullPath(modDirectory.FullName));
        if (pathGuard.Failed)
        {
            return Result.Failure(pathGuard.Error);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (sendToRecycleBin)
        {
            if (recycleDirectoryAsync is not null)
            {
                await recycleDirectoryAsync(modDirectory.FullName, cancellationToken).ConfigureAwait(false);
            }
            else if (recycleBinAdapter is not null)
            {
                await recycleBinAdapter.SendDirectoryToRecycleBinAsync(modDirectory.FullName, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return Result.Failure(Error.Create(CoreErrorCode.InvalidInput, "No recycle-bin adapter was provided."));
            }
        }
        else
        {
            modDirectory.Delete(true);
        }

        await fileHashRepository.DeleteForModAsync(modGuid, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    private static FileInfo? FindManifest(DirectoryInfo directory) =>
        directory.GetFiles("manifest.json", SearchOption.TopDirectoryOnly).FirstOrDefault();

    private static DirectoryInfo? GetDestination(DirectoryInfo storageDirectory, string manifestName)
    {
        var safeName = Path.GetFileName(manifestName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            return null;
        }

        var storageRoot = Path.GetFullPath(storageDirectory.FullName);
        var candidate = Path.GetFullPath(Path.Combine(storageRoot, "Mods", safeName));
        var guarded = PathGuard.EnsureInside(storageRoot, candidate);
        return guarded.Succeeded && candidate != storageRoot ? new DirectoryInfo(candidate) : null;
    }

    private static IModManifest PreserveIdentity(IModManifest updated, IModManifest current) => updated switch
    {
        LegacyModManifest legacy => legacy with
        {
            Guid = current.Guid,
            Name = current.Name,
            Description = current.Description,
            IconPath = current.IconPath,
        },
        V1ModManifest v1 => v1 with
        {
            Guid = current.Guid,
            Name = current.Name,
            Description = current.Description,
            IconPath = current.IconPath,
        },
        _ => throw new NotSupportedException($"Unsupported manifest version: {updated.Version}"),
    };

    private static async Task CopyDirectoryAsync(
        DirectoryInfo source,
        DirectoryInfo destination,
        CancellationToken cancellationToken)
    {
        destination.Create();
        var files = source.EnumerateFiles("*", SearchOption.AllDirectories).ToArray();
        await Parallel.ForEachAsync(files, CreateOptions(cancellationToken), (file, token) =>
        {
            token.ThrowIfCancellationRequested();
            var target = Path.Combine(destination.FullName, Path.GetRelativePath(source.FullName, file.FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file.FullName, target, true);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
    }

    private static async Task CopyChangedFilesAsync(
        DirectoryInfo source,
        DirectoryInfo destination,
        IReadOnlyList<string> relativePaths,
        IProgress<ModUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var completedCount = 0;
        await Parallel.ForEachAsync(relativePaths, CreateOptions(cancellationToken), (relativePath, token) =>
        {
            token.ThrowIfCancellationRequested();
            var sourcePath = Path.Combine(source.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var targetPath = Path.Combine(destination.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var sourceGuard = PathGuard.EnsureInside(source.FullName, sourcePath);
            var targetGuard = PathGuard.EnsureInside(destination.FullName, targetPath);
            if (sourceGuard.Failed || targetGuard.Failed)
            {
                throw new IOException($"Relative path escaped its mod directory: {relativePath}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, true);
            var completed = Interlocked.Increment(ref completedCount);
            progress?.Report(new(ModUpdateStage.Updating, relativePath, completed, relativePaths.Count, 0, relativePaths.Count));
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
    }

    private static IProgress<CachedDirectoryHashProgress>? CreateHashProgress(
        IProgress<ModUpdateProgress>? progress,
        ModUpdateStage stage) => progress is null
        ? null
        : new Progress<CachedDirectoryHashProgress>(item => progress.Report(
            new(stage, item.CurrentFile, item.CheckedCount, item.TotalCount, item.CacheHits, 0)));

    private static IProgress<DirectoryHashProgress>? CreatePlainHashProgress(
        IProgress<ModUpdateProgress>? progress) => progress is null
        ? null
        : new Progress<DirectoryHashProgress>(item => progress.Report(
            new(ModUpdateStage.HashingNew, item.CurrentFile, item.CheckedCount, item.TotalCount, 0, 0)));

    private static async Task<int> DeleteObsoleteFilesAsync(
        DirectoryInfo directory,
        IEnumerable<string> retainedRelativePaths,
        CancellationToken cancellationToken)
    {
        var retained = retainedRelativePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deletedCount = 0;
        foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(directory.FullName, file.FullName)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            if (!retained.Contains(relativePath))
            {
                file.Delete();
                deletedCount++;
            }
        }

        return await Task.FromResult(deletedCount).ConfigureAwait(false);
    }

    private static void CleanEmptyDirectories(DirectoryInfo directory)
    {
        foreach (var subdirectory in directory.EnumerateDirectories("*", SearchOption.AllDirectories)
                     .OrderByDescending(static item => item.FullName.Length))
        {
            if (!subdirectory.EnumerateFileSystemInfos().Any())
            {
                subdirectory.Delete();
            }
        }
    }

    private static ParallelOptions CreateOptions(CancellationToken cancellationToken) => new()
    {
        MaxDegreeOfParallelism = ConcurrencyPolicy.GetIoParallelism(Environment.ProcessorCount),
        CancellationToken = cancellationToken,
    };
}
