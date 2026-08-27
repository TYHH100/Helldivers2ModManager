using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Mods;

namespace Helldivers2ModManager.Core.Deployment;

public sealed class DeploymentService
{
    private const long LargeFileCopyThreshold = 32L * 1024 * 1024;

    public DeploymentPlan CreatePlan(
        IReadOnlyList<ModDeploymentInput> mods,
        DeploymentOptions options)
    {
        var groupedByBaseName = new Dictionary<string, List<PatchFileTriplet>>(StringComparer.OrdinalIgnoreCase);
        var rangesByBaseName = new Dictionary<string, List<(Guid ModGuid, List<PatchFileTriplet> Triplets)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            foreach (var directory in ExpandSelectedDirectories(mod))
            {
                var patchFiles = directory.GetFiles().Where(file => PatchFileRules.IsPatchFile(file.Name)).ToArray();
                var grouped = GroupPatchFiles(patchFiles);
                foreach (var (baseName, triplets) in grouped)
                {
                    if (!groupedByBaseName.TryGetValue(baseName, out var allTriplets))
                    {
                        allTriplets = [];
                        groupedByBaseName[baseName] = allTriplets;
                    }

                    allTriplets.AddRange(triplets);
                    if (!rangesByBaseName.TryGetValue(baseName, out var ranges))
                    {
                        ranges = [];
                        rangesByBaseName[baseName] = ranges;
                    }

                    ranges.Add((mod.Guid, triplets));
                }
            }
        }

        var files = new List<PatchDeploymentItem>();
        var placeholderCount = 0;
        foreach (var (baseName, ranges) in rangesByBaseName)
        {
            var offset = options.SkipList.Contains(baseName) ? 1 : 0;
            var position = 0;
            foreach (var (modGuid, rangeTriplets) in ranges)
            {
                for (var rangeIndex = 0; rangeIndex < rangeTriplets.Count; rangeIndex++)
                {
                    var destinationIndex = position + rangeIndex + offset;
                    var destinationPrefix = Path.Combine(options.GameDataDirectory.FullName, $"{baseName}.patch_{destinationIndex}");
                    var triplet = rangeTriplets[rangeIndex];
                    foreach (var (source, suffix) in new[]
                             {
                                 (triplet.Patch, string.Empty),
                                 (triplet.GpuResources, ".gpu_resources"),
                                 (triplet.Stream, ".stream"),
                             })
                    {
                        var destinationPath = destinationPrefix + suffix;
                        if (source is null)
                        {
                            placeholderCount++;
                            files.Add(new(modGuid, null, destinationPath, 0));
                            continue;
                        }

                        files.Add(new(modGuid, source.FullName, destinationPath, source.Length));
                    }
                }

                position += rangeTriplets.Count;
            }
        }

        return new(mods, files, placeholderCount);
    }

    public async Task DeployAsync(
        IReadOnlyList<ModDeploymentInput> mods,
        DeploymentOptions options,
        IProgress<DeploymentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (mods.Count == 0)
        {
            return;
        }

        await PurgeAsync(options.GameDataDirectory, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(options.GameDataDirectory.FullName);
        var plan = CreatePlan(mods, options);
        await DeployPlanAsync(
            plan,
            options,
            DeploymentStepCallbacks.FromGlobalProgress(progress),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DeployPlanAsync(
        DeploymentPlan plan,
        DeploymentOptions options,
        DeploymentStepCallbacks? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Directory.CreateDirectory(options.GameDataDirectory.FullName);
        var filesByMod = plan.Files.ToLookup(static item => item.ModGuid);
        Guid currentModGuid = plan.Mods.Count > 0 ? plan.Mods[0].Guid : Guid.Empty;

        try
        {
            foreach (var modGuid in plan.Mods.Select(static mod => mod.Guid))
            {
                currentModGuid = modGuid;
                callbacks?.ModStarted?.Invoke(modGuid);
                cancellationToken.ThrowIfCancellationRequested();

                var modFiles = filesByMod[modGuid].ToArray();
                foreach (var placeholder in modFiles.Where(static item => item.SourcePath is null))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await using var stream = new FileStream(placeholder.DestinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                }

                var copyItems = modFiles.Where(static item => item.SourcePath is not null).ToArray();
                if (copyItems.Length == 0)
                {
                    callbacks?.ModCompleted?.Invoke(modGuid);
                    continue;
                }

                var completedCount = 0;
                var smallItems = copyItems.Where(static item => item.Size < LargeFileCopyThreshold).ToArray();
                await Parallel.ForEachAsync(smallItems, CreateParallelOptions(cancellationToken), async (item, token) =>
                {
                    await CopyItemAsync(item.SourcePath!, item.DestinationPath, options.UseSymbolicLinks, token).ConfigureAwait(false);
                    var completed = Interlocked.Increment(ref completedCount);
                    callbacks?.FileCopied?.Invoke(new(modGuid, item, item.Size, item.Size, completed, copyItems.Length));
                }).ConfigureAwait(false);

                foreach (var item in copyItems.Where(static item => item.Size >= LargeFileCopyThreshold).OrderByDescending(static item => item.Size))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    completedCount = await CopyLargeFileAsync(
                        item.SourcePath!,
                        item.DestinationPath,
                        null,
                        copyItems.Length,
                        completedCount,
                        cancellationToken,
                        completedBytes => callbacks?.FileCopied?.Invoke(new(
                            modGuid,
                            item,
                            completedBytes,
                            item.Size,
                            completedCount,
                            copyItems.Length))).ConfigureAwait(false);
                    callbacks?.FileCopied?.Invoke(new(modGuid, item, item.Size, item.Size, completedCount, copyItems.Length));
                }

                callbacks?.ModCompleted?.Invoke(modGuid);
            }
        }
        catch (Exception exception)
        {
            callbacks?.ModFailed?.Invoke(currentModGuid, exception);
            throw;
        }
    }

    public Task PurgeAsync(DirectoryInfo gameDataDirectory, CancellationToken cancellationToken = default)
    {
        if (!gameDataDirectory.Exists)
        {
            return Task.CompletedTask;
        }

        return Parallel.ForEachAsync(
            gameDataDirectory.EnumerateFiles("*.patch_*", SearchOption.TopDirectoryOnly),
            CreateParallelOptions(cancellationToken),
            (file, token) =>
            {
                token.ThrowIfCancellationRequested();
                file.Delete();
                return ValueTask.CompletedTask;
            });
    }

    public async Task<IReadOnlyList<string>> CleanupDeployedFilesAsync(
        ModDeploymentInput removedMod,
        IReadOnlyList<ModDeploymentInput> remainingMods,
        DeploymentOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(removedMod);
        ArgumentNullException.ThrowIfNull(remainingMods);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.GameDataDirectory.Exists)
        {
            return [];
        }

        var removedPlan = CreatePlan([removedMod], options);
        var occupiedBases = CreatePlan(remainingMods, options).Files
            .Select(static item => GetDeploymentBasePath(item.DestinationPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var deletedFiles = new List<string>();
        foreach (var baseGroup in removedPlan.Files.GroupBy(static item => GetDeploymentBasePath(item.DestinationPath), StringComparer.OrdinalIgnoreCase))
        {
            if (occupiedBases.Contains(baseGroup.Key))
            {
                continue;
            }

            foreach (var path in new[]
                     {
                         baseGroup.Key,
                         baseGroup.Key + ".gpu_resources",
                         baseGroup.Key + ".stream",
                     })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(path))
                {
                    continue;
                }

                await Task.Run(() => File.Delete(path), cancellationToken).ConfigureAwait(false);
                deletedFiles.Add(path);
            }
        }

        return deletedFiles;
    }

    private static string GetDeploymentBasePath(string destinationPath)
    {
        if (destinationPath.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase))
        {
            return destinationPath[..^".gpu_resources".Length];
        }

        if (destinationPath.EndsWith(".stream", StringComparison.OrdinalIgnoreCase))
        {
            return destinationPath[..^".stream".Length];
        }

        return destinationPath;
    }

    public static Dictionary<string, List<PatchFileTriplet>> GroupPatchFiles(IReadOnlyList<FileInfo> files)
    {
        var byBaseName = new Dictionary<string, Dictionary<int, PatchFileTriplet>>(StringComparer.OrdinalIgnoreCase);
        var baseNames = new List<string>();
        var indexes = new List<int>();
        foreach (var file in files)
        {
            if (!PatchFileRules.TryParse(file.Name, out var parsed) || parsed.Kind == PatchFileKind.Unknown)
            {
                continue;
            }

            var baseName = parsed.BaseName;
            if (!byBaseName.TryGetValue(baseName, out var byIndex))
            {
                byIndex = [];
                byBaseName[baseName] = byIndex;
                baseNames.Add(baseName);
            }

            if (!byIndex.TryGetValue(parsed.Index, out var triplet))
            {
                triplet = default;
                byIndex[parsed.Index] = triplet;
                indexes.Add(parsed.Index);
            }

            triplet = parsed.Kind switch
            {
                PatchFileKind.GpuResources => triplet with { GpuResources = file },
                PatchFileKind.Stream => triplet with { Stream = file },
                _ => triplet with { Patch = file },
            };
            byIndex[parsed.Index] = triplet;
        }

        var result = new Dictionary<string, List<PatchFileTriplet>>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseName in baseNames)
        {
            var byIndex = byBaseName[baseName];
            var triplets = new List<PatchFileTriplet>(indexes.Count);
            foreach (var index in indexes)
            {
                triplets.Add(byIndex.TryGetValue(index, out var triplet) ? triplet : default);
            }

            result[baseName] = triplets;
        }

        return result;
    }

    private static IEnumerable<DirectoryInfo> ExpandSelectedDirectories(ModDeploymentInput input)
    {
        var directories = new List<DirectoryInfo>();
        void Add(string relativePath)
        {
            var directory = new DirectoryInfo(Path.Combine(input.Directory.FullName, relativePath));
            if (directory.Exists)
            {
                directories.Add(directory);
            }
        }

        switch (input.Manifest)
        {
            case LegacyModManifest legacy when legacy.Options is { } legacyOptions:
            {
                var selected = input.SelectedOptions.Count > 0 ? input.SelectedOptions[0] : 0;
                if (selected >= 0 && selected < legacyOptions.Count)
                {
                    Add(legacyOptions[selected]);
                }

                break;
            }
            case LegacyModManifest:
                directories.Add(input.Directory);
                break;
            case V1ModManifest { Options: null }:
                directories.Add(input.Directory);
                break;
            case V1ModManifest { Options: { } options }:
            {
                if (input.EnabledOptions.Count != options.Count || input.SelectedOptions.Count != options.Count)
                {
                    break;
                }

                for (var optionIndex = 0; optionIndex < options.Count; optionIndex++)
                {
                    if (!input.EnabledOptions[optionIndex])
                    {
                        continue;
                    }

                    var option = options[optionIndex];
                    foreach (var include in option.Include ?? [])
                    {
                        Add(include);
                    }

                    if (option.SubOptions is not { Count: > 0 } subOptions)
                    {
                        continue;
                    }

                    var selectedSubOption = input.SelectedOptions[optionIndex];
                    if (selectedSubOption >= 0 && selectedSubOption < subOptions.Count)
                    {
                        foreach (var include in subOptions[selectedSubOption].Include)
                        {
                            Add(include);
                        }
                    }
                }

                break;
            }
        }

        return directories;
    }

    private static async Task CopyItemAsync(string sourcePath, string destinationPath, bool useSymbolicLinks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (useSymbolicLinks)
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            File.CreateSymbolicLink(destinationPath, sourcePath);
        }
        else
        {
            File.Copy(sourcePath, destinationPath, true);
        }

        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
    }

    private static async Task<int> CopyLargeFileAsync(
        string sourcePath,
        string destinationPath,
        IProgress<DeploymentProgress>? progress,
        int totalFiles,
        int completedCount,
        CancellationToken cancellationToken,
        Action<long>? fileProgress = null)
    {
        const int bufferSize = 8 * 1024 * 1024;
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = GC.AllocateUninitializedArray<byte>(bufferSize);
        long totalRead = 0;
        long lastReported = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            totalRead += read;
            if (totalRead - lastReported >= bufferSize)
            {
                lastReported = totalRead;
                progress?.Report(new(totalRead / (double)source.Length, completedCount, totalFiles, Path.GetFileName(sourcePath)));
                fileProgress?.Invoke(totalRead);
            }
        }

        completedCount++;
        progress?.Report(new(completedCount / (double)totalFiles, completedCount, totalFiles, Path.GetFileName(sourcePath)));
        return completedCount;
    }

    private static ParallelOptions CreateParallelOptions(CancellationToken cancellationToken) => new()
    {
        MaxDegreeOfParallelism = ConcurrencyPolicy.GetIoParallelism(Environment.ProcessorCount),
        CancellationToken = cancellationToken,
    };
}

public readonly record struct PatchFileTriplet(FileInfo? Patch, FileInfo? GpuResources, FileInfo? Stream);
