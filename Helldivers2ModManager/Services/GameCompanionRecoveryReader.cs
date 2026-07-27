using Helldivers2ModManager.Models;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 从游戏归档恢复补丁伴生资源。
/// 归档、TOC 与资源定位字段参考 HD2SDK-CommunityEdition 和 hd2-repatcher 的研究成果；
/// 来源：https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition、
/// https://github.com/RaidingForPants/hd2-repatcher。
/// </summary>
internal sealed partial class VersionCheckService
{
    private const int MaxGameCompanionSegmentBytes = 256 * 1024 * 1024;

    private sealed record RecoveryPackage(
        string Name,
        PackageItem[] Items);

    private sealed record GameCompanionLocator(
        string PackageName,
        RecoveryPackage MainPackage,
        RecoveryPackage CompanionPackage,
        PatchTocEntry Entry);

    private sealed record GameCompanionSegment(
        ulong TargetOffset,
        uint Size,
        GameCompanionLocator Locator,
        byte[]? Payload);

    private sealed class GameCompanionRecipe
    {
        public required string Description { get; init; }
        public required long Length { get; init; }
        public required List<GameCompanionSegment> Segments { get; init; }
    }

    private async Task<GameCompanionRecipe?> TryBuildGameCompanionRecipeAsync(
        FileInfo patchFile,
        string suffix,
        bool includePayloads,
        CancellationToken cancellationToken)
    {
        var dataDirectory = GetConfiguredGameDataDirectory();
        if (dataDirectory is null)
            return null;

        var blockers = new List<string>();
        var entries = await ReadAssistedPatchEntriesAsync(patchFile, blockers);
        if (entries is null || blockers.Count > 0)
            return null;

        var needed = entries
            .Where(entry => GetCompanionSize(entry.Toc, suffix) > 0)
            .ToList();
        if (needed.Count == 0)
            return null;

        var keys = needed
            .Select(entry => (entry.Toc.FileId, entry.Toc.TypeId))
            .ToHashSet();
        var lookup = await Task.Run(
            () => BuildGameCompanionLookup(dataDirectory, suffix, keys, cancellationToken),
            cancellationToken);
        if (lookup.Count == 0)
            return null;

        var segments = new List<GameCompanionSegment>();
        await using var patchStream = OpenPatchReadStream(patchFile);
        foreach (var neededEntry in needed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toc = neededEntry.Toc;
            if (toc.TocSize > MaxBundleResourceBytes ||
                !lookup.TryGetValue((toc.FileId, toc.TypeId), out var candidates))
            {
                return null;
            }

            var modMainData = new byte[toc.TocSize];
            if (!await ReadAtAsync(patchStream, checked((long)toc.TocOffset), modMainData))
                return null;

            var exactCandidates = new List<GameCompanionLocator>();
            foreach (var candidate in candidates)
            {
                if (candidate.Entry.TocSize != toc.TocSize ||
                    GetCompanionSize(candidate.Entry, suffix) != GetCompanionSize(toc, suffix))
                {
                    continue;
                }

                var gameMainData = TryReadPackageRange(
                    lookup.Bundles,
                    candidate.MainPackage.Items,
                    candidate.Entry.TocOffset,
                    candidate.Entry.TocSize,
                    MaxBundleResourceBytes);
                if (gameMainData is not null && gameMainData.AsSpan().SequenceEqual(modMainData))
                    exactCandidates.Add(candidate);
            }
            if (exactCandidates.Count == 0)
                return null;

            byte[]? payload = null;
            var selected = exactCandidates[0];
            if (includePayloads || exactCandidates.Count > 1)
            {
                var payloadCandidates = new List<(GameCompanionLocator Locator, byte[] Payload)>();
                foreach (var candidate in exactCandidates)
                {
                    var candidatePayload = TryReadPackageRange(
                        lookup.Bundles,
                        candidate.CompanionPackage.Items,
                        GetCompanionOffset(candidate.Entry, suffix),
                        GetCompanionSize(candidate.Entry, suffix),
                        MaxGameCompanionSegmentBytes);
                    if (candidatePayload is null)
                        continue;
                    payloadCandidates.Add((candidate, candidatePayload));
                }
                if (payloadCandidates.Count == 0)
                    return null;

                var distinctPayloads = payloadCandidates
                    .GroupBy(item => Convert.ToHexString(SHA256.HashData(item.Payload)), StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList();
                if (distinctPayloads.Count != 1)
                    return null;
                selected = distinctPayloads[0].Locator;
                if (includePayloads)
                    payload = distinctPayloads[0].Payload;
            }

            segments.Add(new GameCompanionSegment(
                GetCompanionOffset(toc, suffix),
                GetCompanionSize(toc, suffix),
                selected,
                payload));
        }

        var normalized = NormalizeGameCompanionSegments(segments, includePayloads);
        if (normalized is null)
            return null;
        var maximumEnd = normalized.Max(segment =>
            checked(segment.TargetOffset + segment.Size));
        if (maximumEnd > long.MaxValue)
            return null;

        return new GameCompanionRecipe
        {
            Description = $"Current game bundles ({normalized.Count} exact segment(s))",
            Length = (long)maximumEnd,
            Segments = normalized
        };
    }

    private static async Task WriteGameCompanionRecipeAsync(
        GameCompanionRecipe recipe,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        output.SetLength(recipe.Length);
        foreach (var segment in recipe.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment.Payload is null || segment.Payload.Length != segment.Size)
                throw new InvalidDataException("A game companion recovery segment is missing its verified payload.");
            output.Position = checked((long)segment.TargetOffset);
            await output.WriteAsync(segment.Payload, cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
        output.Flush(true);
    }

    private GameCompanionLookup BuildGameCompanionLookup(
        DirectoryInfo dataDirectory,
        string suffix,
        HashSet<(long FileId, long TypeId)> neededKeys,
        CancellationToken cancellationToken)
    {
        var bundleIndexData = DecodeDsarFile(Path.Combine(dataDirectory.FullName, "bundles.nxa"));
        if (bundleIndexData.Length < 0x18)
            return new GameCompanionLookup([], []);

        var bundleCount = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(0x0C, 4));
        var packageCount = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(0x10, 4));
        if (bundleCount is 0 or > 256 || packageCount > 1_000_000)
            return new GameCompanionLookup([], []);

        var bundles = new BundleInfo[bundleCount];
        for (var i = 0; i < bundles.Length; i++)
            bundles[i] = LoadBundleInfo(Path.Combine(dataDirectory.FullName, $"bundles.{i:00}.nxa"));

        var packages = new Dictionary<string, RecoveryPackage>(StringComparer.OrdinalIgnoreCase);
        for (var packageIndex = 0; packageIndex < packageCount; packageIndex++)
        {
            if (packageIndex % 1000 == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var recordOffset = checked(0x18 + packageIndex * 0x18);
            if ((long)recordOffset + 0x18 > bundleIndexData.Length)
                break;
            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(recordOffset + 8, 4));
            var itemCount = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(recordOffset + 12, 4));
            var itemsOffset = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(recordOffset + 16, 4));
            if (itemCount == 0 || itemCount > 100_000 ||
                (ulong)itemsOffset + itemCount * 0x10UL > (ulong)bundleIndexData.Length)
            {
                continue;
            }

            var name = ReadNullTerminatedString(bundleIndexData, nameOffset);
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var items = ReadRecoveryPackageItems(bundleIndexData, itemsOffset, itemCount, bundles.Length);
            if (items is not null)
                packages[name] = new RecoveryPackage(name, items);
        }

        var result = new GameCompanionLookup(bundles, []);
        foreach (var package in packages.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (package.Name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase) ||
                package.Name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase) ||
                package.Name.Contains(".patch", StringComparison.OrdinalIgnoreCase) ||
                !packages.TryGetValue(package.Name + suffix, out var companionPackage))
            {
                continue;
            }

            var tocData = TryReadPackageRange(
                bundles,
                package.Items,
                0,
                0,
                MaxBundleResourceBytes,
                readWholeResource: true);
            if (tocData is null)
                continue;
            IndexGameCompanionPackage(
                package,
                companionPackage,
                tocData,
                suffix,
                neededKeys,
                result.Resources);
        }
        return result;
    }

    private sealed class GameCompanionLookup(
        BundleInfo[] bundles,
        Dictionary<(long FileId, long TypeId), List<GameCompanionLocator>> resources)
    {
        public BundleInfo[] Bundles { get; } = bundles;
        public Dictionary<(long FileId, long TypeId), List<GameCompanionLocator>> Resources { get; } = resources;
        public int Count => Resources.Count;
        public bool TryGetValue(
            (long FileId, long TypeId) key,
            out List<GameCompanionLocator> value) => Resources.TryGetValue(key, out value!);
    }

    private static PackageItem[]? ReadRecoveryPackageItems(
        byte[] bundleIndexData,
        uint itemsOffset,
        uint itemCount,
        int bundleCount)
    {
        var items = new PackageItem[itemCount];
        for (var i = 0; i < items.Length; i++)
        {
            var itemOffset = checked((int)itemsOffset + i * 0x10);
            var bundleIndex = bundleIndexData[itemOffset + 15];
            if (bundleIndex >= bundleCount)
                return null;
            items[i] = new PackageItem(
                BinaryPrimitives.ReadUInt64LittleEndian(bundleIndexData.AsSpan(itemOffset, 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(itemOffset + 8, 4)),
                bundleIndex);
        }
        return items;
    }

    private static void IndexGameCompanionPackage(
        RecoveryPackage mainPackage,
        RecoveryPackage companionPackage,
        byte[] tocData,
        string suffix,
        HashSet<(long FileId, long TypeId)> neededKeys,
        Dictionary<(long FileId, long TypeId), List<GameCompanionLocator>> result)
    {
        if (tocData.Length < HeaderSize ||
            BinaryPrimitives.ReadInt32LittleEndian(tocData.AsSpan(0, 4)) != PatchHeaderMagic)
        {
            return;
        }

        var numTypes = BinaryPrimitives.ReadInt32LittleEndian(tocData.AsSpan(4, 4));
        var numFiles = BinaryPrimitives.ReadInt32LittleEndian(tocData.AsSpan(8, 4));
        if (numTypes < 0 || numFiles < 0 || numTypes > 1000 || numFiles > 100_000)
            return;
        var entryStart = HeaderSize + (long)numTypes * TypeEntrySize;
        if (entryStart + (long)numFiles * FileEntrySize > tocData.Length)
            return;

        for (var index = 0; index < numFiles; index++)
        {
            var offset = checked((int)(entryStart + (long)index * FileEntrySize));
            var entry = new PatchTocEntry(
                BinaryPrimitives.ReadInt64LittleEndian(tocData.AsSpan(offset, 8)),
                BinaryPrimitives.ReadInt64LittleEndian(tocData.AsSpan(offset + 8, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(tocData.AsSpan(offset + 16, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(tocData.AsSpan(offset + 24, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(tocData.AsSpan(offset + 32, 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(tocData.AsSpan(offset + 56, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(tocData.AsSpan(offset + 60, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(tocData.AsSpan(offset + 64, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(tocData.AsSpan(offset + 76, 4)));
            var key = (entry.FileId, entry.TypeId);
            if (!neededKeys.Contains(key) || GetCompanionSize(entry, suffix) == 0)
                continue;
            if (!result.TryGetValue(key, out var locators))
            {
                locators = [];
                result[key] = locators;
            }
            locators.Add(new GameCompanionLocator(
                mainPackage.Name,
                mainPackage,
                companionPackage,
                entry));
        }
    }

    private static byte[]? TryReadPackageRange(
        BundleInfo[] bundles,
        PackageItem[] items,
        ulong archiveOffset,
        uint size,
        int maxBytes,
        bool readWholeResource = false)
    {
        var item = items.LastOrDefault(candidate => candidate.ArchiveOffset <= archiveOffset);
        if (item is null)
            return null;
        try
        {
            var bundleOffset = checked(item.BundleOffset + archiveOffset - item.ArchiveOffset);
            var data = ReadBundleResource(bundles[item.BundleIndex], bundleOffset, maxBytes);
            if (readWholeResource)
                return data;
            if (data.Length < size)
                return null;
            return data.AsSpan(0, checked((int)size)).ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static List<GameCompanionSegment>? NormalizeGameCompanionSegments(
        List<GameCompanionSegment> segments,
        bool includePayloads)
    {
        var normalized = new List<GameCompanionSegment>();
        foreach (var group in segments.GroupBy(segment => (segment.TargetOffset, segment.Size)))
        {
            var entries = group.ToList();
            if (includePayloads && entries
                    .Select(entry => entry.Payload is null
                        ? string.Empty
                        : Convert.ToHexString(SHA256.HashData(entry.Payload)))
                    .Distinct(StringComparer.Ordinal)
                    .Count() != 1)
            {
                return null;
            }
            normalized.Add(entries[0]);
        }
        normalized.Sort((left, right) => left.TargetOffset.CompareTo(right.TargetOffset));
        for (var index = 1; index < normalized.Count; index++)
        {
            var previousEnd = normalized[index - 1].TargetOffset + normalized[index - 1].Size;
            if (normalized[index].TargetOffset < previousEnd)
                return null;
        }
        return normalized;
    }

    private static ulong GetCompanionOffset(PatchTocEntry entry, string suffix) =>
        suffix.Equals(".gpu_resources", StringComparison.OrdinalIgnoreCase)
            ? entry.GpuOffset
            : entry.StreamOffset;

    private static uint GetCompanionSize(PatchTocEntry entry, string suffix) =>
        suffix.Equals(".gpu_resources", StringComparison.OrdinalIgnoreCase)
            ? entry.GpuSize
            : entry.StreamSize;
}
