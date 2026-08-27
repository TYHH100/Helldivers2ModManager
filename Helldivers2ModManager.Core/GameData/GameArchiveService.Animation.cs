using System.Buffers.Binary;
using Helldivers2ModManager.Core.Preview;

namespace Helldivers2ModManager.Core.GameData;

public sealed partial class GameArchiveService
{
    public async Task<ModelPreviewAnimationLibrary?> ResolveCompatibleAnimationLibraryAsync(
        DirectoryInfo dataDirectory,
        IReadOnlyCollection<uint> transformNameHashes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(transformNameHashes);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (transformNameHashes.Count == 0)
            return null;

        var index = await EnsureIndexAsync(dataDirectory, cancellationToken).ConfigureAwait(false);
        if (index is null)
            return null;

        return await Task.Run(
            () => FindCompatibleAnimationLibrary(index, transformNameHashes, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArchiveIndex?> EnsureIndexAsync(DirectoryInfo dataDirectory, CancellationToken cancellationToken)
    {
        if (!dataDirectory.Exists || !File.Exists(Path.Combine(dataDirectory.FullName, "bundles.nxa")))
            return null;

        var bundleFiles = dataDirectory.GetFiles("bundles*.nxa", SearchOption.TopDirectoryOnly)
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var cacheKey = dataDirectory.FullName + "|" + string.Join("|", bundleFiles.Select(
            file => $"{file.Name}:{file.Length}:{file.LastWriteTimeUtc.Ticks}"));

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_index is null || !string.Equals(_index.CacheKey, cacheKey, StringComparison.Ordinal))
            {
                var newIndex = await Task.Run(() => BuildIndex(dataDirectory, cacheKey), cancellationToken)
                    .ConfigureAwait(false);
                var oldIndex = _index;
                _index = newIndex;
                oldIndex?.Dispose();
            }

            return _index;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static ModelPreviewAnimationLibrary? FindCompatibleAnimationLibrary(
        ArchiveIndex index,
        IReadOnlyCollection<uint> transformNameHashes,
        CancellationToken cancellationToken)
    {
        var requestedBones = transformNameHashes.Where(static hash => hash != 0).ToHashSet();
        if (requestedBones.Count == 0)
            return null;

        if (index.HelldiverAnimationReference is null &&
            index.UnitLocators.TryGetValue(HelldiverAvatarUnitId, out var locators))
        {
            foreach (var locator in locators)
            {
                var unitData = TryReadAnimationResource(index.Bundles, locator);
                if (unitData is null || unitData.Length < 0x28)
                    continue;

                var bonesId = BinaryPrimitives.ReadUInt64LittleEndian(unitData.AsSpan(0x08, sizeof(ulong)));
                var stateMachineId = BinaryPrimitives.ReadUInt64LittleEndian(unitData.AsSpan(0x20, sizeof(ulong)));
                if (bonesId == 0 || stateMachineId == 0)
                    continue;

                index.HelldiverAnimationReference = new(bonesId, stateMachineId);
                break;
            }
        }

        var reference = index.HelldiverAnimationReference;
        if (reference is null)
            return null;

        index.HelldiverAnimationLibrary ??= ReadAnimationLibrary(
            index,
            reference.Value.BonesId,
            reference.Value.StateMachineId,
            cancellationToken);
        var library = index.HelldiverAnimationLibrary;
        if (library is null)
            return null;

        var libraryBones = library.BoneHashes.Where(static hash => hash != 0).ToHashSet();
        var matchingBones = libraryBones.Count(requestedBones.Contains);
        return matchingBones >= ModelPreviewAnimationCompatibility.MinimumMatchingBones &&
               matchingBones >= libraryBones.Count * ModelPreviewAnimationCompatibility.MinimumBoneCoverage &&
               matchingBones >= requestedBones.Count * ModelPreviewAnimationCompatibility.MinimumBoneCoverage
            ? library
            : null;
    }

    private static ModelPreviewAnimationLibrary? ReadAnimationLibrary(
        ArchiveIndex index,
        ulong bonesId,
        ulong stateMachineId,
        CancellationToken cancellationToken)
    {
        var bonesData = TryReadIndexedAnimationResource(index, unchecked((long)bonesId), BonesTypeId);
        var stateMachineData = TryReadIndexedAnimationResource(index, unchecked((long)stateMachineId), StateMachineTypeId);
        if (bonesData is null || stateMachineData is null)
            return null;

        var boneHashes = ModelPreviewAnimationLibraryParser.ParseBoneHashes(bonesData);
        var references = ModelPreviewAnimationLibraryParser.ParseStateMachineAnimations(stateMachineData);
        var animations = new List<ModelPreviewAnimationOption>(Math.Min(references.Count, MaxAnimationsPerPreview));
        foreach (var reference in references.Take(MaxAnimationsPerPreview))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var animationData = TryReadIndexedAnimationResource(index, unchecked((long)reference.AnimationId), AnimationTypeId);
            if (animationData is null ||
                !ModelPreviewAnimationParser.TryParse(animationData, reference.AnimationId, out var clip, out _) ||
                clip is null ||
                clip.BoneCount > boneHashes.Count)
            {
                continue;
            }

            animations.Add(new ModelPreviewAnimationOption
            {
                AnimationId = reference.AnimationId,
                StateNameHash = reference.StateNameHash,
                LayerIndex = reference.LayerIndex,
                Clip = clip
            });
        }

        return animations.Count == 0
            ? null
            : new ModelPreviewAnimationLibrary
            {
                BonesId = bonesId,
                StateMachineId = stateMachineId,
                BoneHashes = boneHashes,
                Animations = animations
            };
    }

    private static byte[]? TryReadIndexedAnimationResource(ArchiveIndex index, long fileId, long typeId)
    {
        if (!index.AnimationResourceLocators.TryGetValue((fileId, typeId), out var locators))
            return null;

        foreach (var locator in locators)
        {
            var data = TryReadAnimationResource(index.Bundles, locator);
            if (data is not null)
                return data;
        }

        return null;
    }

    private static byte[]? TryReadAnimationResource(BundleInfo[] bundles, UnitLocator locator)
    {
        var item = locator.Items.LastOrDefault(candidate => candidate.ArchiveOffset <= locator.ResourceOffset);
        if (item is null || locator.ResourceSize == 0 || locator.ResourceSize > MaxBundleResourceBytes)
            return null;

        try
        {
            var bundleOffset = checked(item.BundleOffset + locator.ResourceOffset - item.ArchiveOffset);
            var data = ReadResource(bundles[item.BundleIndex], bundleOffset, MaxBundleResourceBytes);
            if (data.Length < locator.ResourceSize)
                return null;

            return data.Length == locator.ResourceSize
                ? data
                : data.AsSpan(0, checked((int)locator.ResourceSize)).ToArray();
        }
        catch
        {
            return null;
        }
    }
}
