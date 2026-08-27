using System.Buffers.Binary;
using System.Security.Cryptography;
using Helldivers2ModManager.Core.PatchKit;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Core.GameData;

public sealed partial class GameArchiveService
{
    private const int MaxGameCompanionSegmentBytes = 256 * 1024 * 1024;
    private const long MaxCompanionPatchBytes = 256L * 1024 * 1024;

    public async Task<GameCompanionRecipeResult> BuildCompanionRecipeAsync(
        DirectoryInfo dataDirectory,
        FileInfo patchFile,
        GameCompanionKind companionKind,
        bool includePayloads,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patchFile);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!patchFile.Exists || patchFile.Length > MaxCompanionPatchBytes)
            return GameCompanionRecipeResult.Failure("The patch is unavailable or too large for game recovery.");

        var parsed = await new PatchFileParser().ParseFileAsync(patchFile, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var snapshot = parsed.Snapshot;
        var parseIssues = snapshot?.Issues ?? [];
        var mainIssueCount = snapshot?.MainDataIssueCount ?? 0;
        if (snapshot is null || !snapshot.FileEntriesInBounds || mainIssueCount > 0)
            return GameCompanionRecipeResult.Failure("The patch structure cannot be used for game recovery: " + string.Join("; ", parseIssues.Select(issue => $"{issue.Code}: {issue.Detail}")) + "; mainIssues=" + mainIssueCount + ".");

        if (!dataDirectory.Exists || !File.Exists(Path.Combine(dataDirectory.FullName, "bundles.nxa")))
            return GameCompanionRecipeResult.Failure("The game data directory or bundles.nxa is unavailable.");

        var suffix = companionKind == GameCompanionKind.GpuResources ? ".gpu_resources" : ".stream";
        var needed = snapshot.Entries.Where(entry => GetCompanionSize(entry, companionKind) > 0).ToArray();
        if (needed.Length == 0)
            return GameCompanionRecipeResult.Failure("The patch has no companion resources to recover.");

        var keys = needed
            .Select(entry => (unchecked((long)entry.FileId), unchecked((long)entry.TypeId)))
            .ToHashSet();

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => BuildCompanionRecipe(dataDirectory, patchFile, snapshot, needed, keys, suffix, companionKind, includePayloads, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to build a game companion recovery recipe");
            return GameCompanionRecipeResult.Failure(exception.Message);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private GameCompanionRecipeResult BuildCompanionRecipe(
        DirectoryInfo dataDirectory,
        FileInfo patchFile,
        PatchFileSnapshot snapshot,
        IReadOnlyList<PatchTocEntry> needed,
        HashSet<(long FileId, long TypeId)> keys,
        string suffix,
        GameCompanionKind companionKind,
        bool includePayloads,
        CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(dataDirectory.FullName, "bundles.nxa");
        if (!dataDirectory.Exists || !File.Exists(indexPath))
            return GameCompanionRecipeResult.Failure("The game data directory or bundles.nxa is unavailable.");

        var lookup = BuildCompanionLookup(dataDirectory, suffix, companionKind, keys, cancellationToken);
        using var disposableLookup = lookup;
        if (lookup.Resources.Count == 0)
            return GameCompanionRecipeResult.Failure("No matching companion packages were found.");

        var segments = new List<GameCompanionSegment>();
        using var patchStream = new FileStream(patchFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        foreach (var toc in needed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (toc.MainSize > MaxBundleResourceBytes ||
                !lookup.Resources.TryGetValue((unchecked((long)toc.FileId), unchecked((long)toc.TypeId)), out var candidates))
            {
                return GameCompanionRecipeResult.Failure("A companion source is incomplete.");
            }

            var modMainData = new byte[toc.MainSize];
            patchStream.Position = checked((long)toc.MainOffset);
            patchStream.ReadExactly(modMainData);

            var exactCandidates = new List<CompanionLocator>();
            foreach (var candidate in candidates)
            {
                if (candidate.Entry.MainSize != toc.MainSize ||
                    GetCompanionSize(candidate.Entry, companionKind) != GetCompanionSize(toc, companionKind))
                {
                    continue;
                }

                var gameMainData = TryReadPackageRange(
                    lookup.Bundles,
                    candidate.Main.Items,
                    candidate.Entry.MainOffset,
                    candidate.Entry.MainSize,
                    MaxBundleResourceBytes);
                if (gameMainData is not null && gameMainData.AsSpan().SequenceEqual(modMainData))
                    exactCandidates.Add(candidate);
            }

            if (exactCandidates.Count == 0)
                return GameCompanionRecipeResult.Failure("No exact main-data match was found.");

            byte[]? payload = null;
            var selected = exactCandidates[0];
            if (includePayloads || exactCandidates.Count > 1)
            {
                var payloadCandidates = new List<(CompanionLocator Locator, byte[] Payload)>();
                foreach (var candidate in exactCandidates)
                {
                    var candidatePayload = TryReadPackageRange(
                        lookup.Bundles,
                        candidate.Companion.Items,
                        GetCompanionOffset(candidate.Entry, companionKind),
                        GetCompanionSize(candidate.Entry, companionKind),
                        MaxGameCompanionSegmentBytes);
                    if (candidatePayload is not null)
                        payloadCandidates.Add((candidate, candidatePayload));
                }

                var distinctPayloads = payloadCandidates
                    .GroupBy(item => Convert.ToHexString(SHA256.HashData(item.Payload)), StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToArray();
                if (distinctPayloads.Length != 1)
                    return GameCompanionRecipeResult.Failure("Game companion payloads are ambiguous.");

                selected = distinctPayloads[0].Locator;
                if (includePayloads)
                    payload = distinctPayloads[0].Payload;
            }

            segments.Add(new(
                GetCompanionOffset(toc, companionKind),
                GetCompanionSize(toc, companionKind),
                selected.Companion.Name,
                payload));
        }

        var normalized = NormalizeCompanionSegments(segments, includePayloads);
        if (normalized is null)
            return GameCompanionRecipeResult.Failure("Game companion segments are ambiguous or overlapping.");
        var maximumEnd = normalized.Max(segment => checked(segment.TargetOffset + segment.Size));
        if (maximumEnd > long.MaxValue)
            return GameCompanionRecipeResult.Failure("The recovered companion would be too large.");

        return new(new GameCompanionRecipe(
            $"Current game bundles ({normalized.Count} exact segment(s))",
            (long)maximumEnd,
            normalized), null);
    }

    private CompanionLookup BuildCompanionLookup(
        DirectoryInfo dataDirectory,
        string suffix,
        GameCompanionKind companionKind,
        HashSet<(long FileId, long TypeId)> keys,
        CancellationToken cancellationToken)
    {
        var bundleIndexData = DecodeDsar(Path.Combine(dataDirectory.FullName, "bundles.nxa"));
        if (bundleIndexData.Length < 0x18)
            return new([], []);

        var bundleCount = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(0x0C, 4));
        var packageCount = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(0x10, 4));
        if (bundleCount is 0 or > 256 || packageCount > 1_000_000)
            return new([], []);

        var bundles = new BundleInfo[bundleCount];
        try
        {
            for (var index = 0; index < bundles.Length; index++)
                bundles[index] = LoadBundle(Path.Combine(dataDirectory.FullName, $"bundles.{index:00}.nxa"));

            var packages = new Dictionary<string, PackageRecord>(StringComparer.OrdinalIgnoreCase);
            for (var packageIndex = 0u; packageIndex < packageCount; packageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recordOffset = checked(0x18 + (int)packageIndex * 0x18);
                if (recordOffset + 0x18 > bundleIndexData.Length)
                    break;

                var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(recordOffset + 8, 4));
                var itemCount = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(recordOffset + 12, 4));
                var itemsOffset = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(recordOffset + 16, 4));
                if (itemCount == 0 ||
                    itemCount > 100_000 ||
                    (ulong)itemsOffset + (ulong)itemCount * 0x10UL > (ulong)bundleIndexData.Length)
                {
                    continue;
                }

                var name = ReadNullTerminatedString(bundleIndexData, nameOffset);
                if (name.Length == 0 || !TryReadItems(bundleIndexData, itemsOffset, itemCount, bundles.Length, out var items))
                    continue;

                packages[name] = new(name, items);
            }

            var resources = new Dictionary<(long FileId, long TypeId), List<CompanionLocator>>();
            foreach (var package in packages.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (package.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                    package.Name.Contains(".patch", StringComparison.OrdinalIgnoreCase) ||
                    !packages.TryGetValue(package.Name + suffix, out var companionPackage))
                {
                    continue;
                }

                var tocData = TryReadWholePackageRange(
                    bundles,
                    package.Items[0].BundleIndex,
                    package.Items[0].BundleOffset,
                    MaxBundleResourceBytes);
                if (tocData is null)
                    continue;

                IndexCompanionPackage(package, companionPackage, tocData, companionKind, keys, resources);
            }

            return new(bundles, resources);
        }
        catch
        {
            foreach (var bundle in bundles)
                bundle.Dispose();
            throw;
        }
    }

    private static bool TryReadItems(byte[] data, uint itemsOffset, uint itemCount, int bundleCount, out ArchiveItem[] items)
    {
        if ((ulong)(ulong)itemsOffset + (ulong)itemCount * 0x10UL > (ulong)data.Length)
        {
            items = [];
            return false;
        }

        items = new ArchiveItem[itemCount];
        for (var index = 0; index < items.Length; index++)
        {
            var offset = checked((int)itemsOffset + index * 0x10);
            var bundleIndex = data[offset + 15];
            if (bundleIndex >= bundleCount)
                return false;

            items[index] = new(
                BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8, 4)),
                bundleIndex);
        }

        return true;
    }

    private void IndexCompanionPackage(
        PackageRecord mainPackage,
        PackageRecord companionPackage,
        byte[] tocData,
        GameCompanionKind companionKind,
        HashSet<(long FileId, long TypeId)> keys,
        Dictionary<(long FileId, long TypeId), List<CompanionLocator>> result)
    {
        if (tocData.Length < HeaderSize ||
            BinaryPrimitives.ReadInt32LittleEndian(tocData.AsSpan(0, 4)) != unchecked((int)0xF0000011))
        {
            return;
        }

        var typeCount = BinaryPrimitives.ReadInt32LittleEndian(tocData.AsSpan(4, 4));
        var fileCount = BinaryPrimitives.ReadInt32LittleEndian(tocData.AsSpan(8, 4));
        if (typeCount is < 0 or > 1000 || fileCount is < 0 or > 100_000)
            return;
        var entryStart = HeaderSize + typeCount * TypeEntrySize;
        if (entryStart + fileCount * FileEntrySize > tocData.Length)
            return;

        for (var index = 0; index < fileCount; index++)
        {
            var offset = entryStart + index * FileEntrySize;
            var entry = new PatchTocEntry(
                index,
                BinaryPrimitives.ReadUInt64LittleEndian(tocData.AsSpan(offset, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(tocData.AsSpan(offset + 8, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(tocData.AsSpan(offset + 16, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(tocData.AsSpan(offset + 24, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(tocData.AsSpan(offset + 32, 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(tocData.AsSpan(offset + 56, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(tocData.AsSpan(offset + 60, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(tocData.AsSpan(offset + 64, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(tocData.AsSpan(offset + 76, 4)));
            var key = (unchecked((long)entry.FileId), unchecked((long)entry.TypeId));
            if (!keys.Contains(key) || GetCompanionSize(entry, companionKind) == 0)
                continue;

            if (!result.TryGetValue(key, out var locators))
            {
                locators = [];
                result[key] = locators;
            }

            locators.Add(new(mainPackage, companionPackage, entry));
        }
    }

    private static byte[]? TryReadPackageRange(
        BundleInfo[] bundles,
        ArchiveItem[] items,
        ulong archiveOffset,
        uint size,
        int maxBytes,
        bool readWholeResource = false)
    {
        var item = items.LastOrDefault(candidate => candidate.ArchiveOffset <= archiveOffset);
        if (item is null) return null;
        try
        {
            var data = ReadResource(bundles[item.BundleIndex], checked(item.BundleOffset + archiveOffset - item.ArchiveOffset), maxBytes);
            if (readWholeResource) return data;
            if (data.Length < size) return null;
            return data.AsSpan(0, checked((int)size)).ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryReadWholePackageRange(BundleInfo[] bundles, byte bundleIndex, uint bundleOffset, int maxBytes) =>
        bundleIndex < bundles.Length ? SafeReadResource(bundles[bundleIndex], bundleOffset, maxBytes) : null;

    private static byte[]? SafeReadResource(BundleInfo bundle, ulong offset, int maxBytes)
    {
        try
        {
            return ReadResource(bundle, offset, maxBytes);
        }
        catch
        {
            return null;
        }
    }

    private static List<GameCompanionSegment>? NormalizeCompanionSegments(List<GameCompanionSegment> segments, bool includePayloads)
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
            var previousEnd = checked(normalized[index - 1].TargetOffset + normalized[index - 1].Size);
            if (normalized[index].TargetOffset < previousEnd)
                return null;
        }

        return normalized;
    }

    private static ulong GetCompanionOffset(PatchTocEntry entry, GameCompanionKind kind) =>
        kind == GameCompanionKind.GpuResources ? entry.GpuOffset : entry.StreamOffset;

    private static uint GetCompanionSize(PatchTocEntry entry, GameCompanionKind kind) =>
        kind == GameCompanionKind.GpuResources ? entry.GpuSize : entry.StreamSize;

    private sealed record PackageRecord(string Name, ArchiveItem[] Items);

    private sealed record CompanionLocator(PackageRecord Main, PackageRecord Companion, PatchTocEntry Entry);

    private sealed class CompanionLookup(
        BundleInfo[] bundles,
        Dictionary<(long FileId, long TypeId), List<CompanionLocator>> resources) : IDisposable
    {
        public BundleInfo[] Bundles { get; } = bundles;
        public Dictionary<(long FileId, long TypeId), List<CompanionLocator>> Resources { get; } = resources;

        public void Dispose()
        {
            foreach (var bundle in Bundles)
                bundle.Dispose();
        }
    }
}
