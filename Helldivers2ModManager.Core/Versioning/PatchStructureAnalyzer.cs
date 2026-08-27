using System.Collections.Concurrent;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.PatchKit;

namespace Helldivers2ModManager.Core.Versioning;

public sealed class PatchStructureAnalyzer
{
    private const int MaxCacheEntries = 512;
    private readonly PatchFileParser _parser;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public PatchStructureAnalyzer(PatchFileParser? parser = null) => _parser = parser ?? new();

    public void ClearCache() => _cache.Clear();

    public async Task<ModPatchAnalysis> AnalyzeAsync(
        DirectoryInfo directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var patchFiles = directory.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(file => PatchFileRules.IsMainPatchFile(file.Name))
            .ToArray();
        var analyses = new List<PatchFileAnalysis>(patchFiles.Length);
        foreach (var file in patchFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            analyses.Add(await AnalyzeFileAsync(file, cancellationToken).ConfigureAwait(false));
        }

        return CreateAnalysis(analyses);
    }

    public async Task<PatchFileAnalysis> AnalyzeFileAsync(
        FileInfo patchFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patchFile);
        patchFile.Refresh();
        if (!patchFile.Exists)
        {
            return PatchFileAnalysis.Failed(patchFile.FullName, 0);
        }

        var key = Path.GetFullPath(patchFile.FullName);
        var length = patchFile.Length;
        var modified = patchFile.LastWriteTimeUtc;
        if (_cache.TryGetValue(key, out var cached) &&
            cached.Length == length &&
            cached.LastWriteTimeUtc == modified)
        {
            return cached.Analysis;
        }

        var parse = await _parser.ParseFileAsync(patchFile, options: null, cancellationToken).ConfigureAwait(false);
        var analysis = ToAnalysis(parse, key, length);
        if (_cache.Count >= MaxCacheEntries)
        {
            _cache.Clear();
        }
        _cache[key] = new(length, modified, analysis);
        return analysis;
    }

    public async Task<PatchFileAnalysis> AnalyzeTemporaryFileAsync(
        FileInfo temporaryFile,
        FileInfo companionSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(temporaryFile);
        ArgumentNullException.ThrowIfNull(companionSource);
        await using var patchStream = OpenRead(temporaryFile);
        var gpuPath = companionSource.FullName + ".gpu_resources";
        var streamPath = companionSource.FullName + ".stream";
        await using var gpuStream = File.Exists(gpuPath) ? OpenRead(new FileInfo(gpuPath)) : Stream.Null;
        await using var streamResource = File.Exists(streamPath) ? OpenRead(new FileInfo(streamPath)) : Stream.Null;
        var parse = await _parser.ParseAsync(patchStream,
            gpuStream == Stream.Null ? null : gpuStream,
            streamResource == Stream.Null ? null : streamResource,
            temporaryFile.FullName,
            options: null,
            cancellationToken);


        return ToAnalysis(parse, temporaryFile.FullName, temporaryFile.Length);
    }

    private static FileStream OpenRead(FileInfo file) =>
        new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
    private static PatchFileAnalysis ToAnalysis(PatchParseResult parse, string path, long length)
    {
        if (parse.Snapshot is not { } snapshot)
        {
            return PatchFileAnalysis.Failed(path, length);
        }

        var health = snapshot.HasErrors ? PatchHealthStatus.Corrupted
            : snapshot.Issues.Count != 0 ? PatchHealthStatus.Warning
            : PatchHealthStatus.Healthy;
        var distributions = snapshot.Types
            .GroupBy(type => (long)type.TypeId)
            .Select(group => new PatchTypeDistribution(group.Key, group.Sum(type => (int)type.ResourceCount)))
            .OrderByDescending(item => item.ResourceCount)
            .ToArray();
        var units = snapshot.Units.Select(unit =>
        {
            var structure = unit.Structure!;
            var gpuIssues = unit.Streams.Count(stream =>
                stream.Components.Count == 0 ||
                stream.Components.Sum(static component => (int)component.Size) != stream.VertexStride ||
                !stream.VertexBufferInGpuRange ||
                !stream.IndexBufferInGpuRange);
            return new PatchUnitAnalysis(
                path,
                unit.TocEntryIndex,
                (long)unit.UnitId,
                unit.Version,
                unit.MainSize,
                unit.GpuSize,
                structure.EndingOffset,
                structure.ExpectedDataSize,
                structure.DeclaredSizeMatchesInternal,
                structure.IsTruncated,
                structure.LodGroupOffset,
                structure.JointListOffset,
                structure.JointListOffset - structure.LodGroupOffset,
                structure.LodGroupInBounds,
                unit.MainSize >= PatchUnitHeaderSize && unit.MainOffset <= ulong.MaxValue - unit.MainSize,
                structure.LayoutFormatChecked,
                structure.LayoutFormatValid,
                structure.LayoutFormatIssueCount,
                structure.GpuStructureChecked,
                structure.GpuStructureValid,
                gpuIssues + Math.Sign(unit.Streams.Count != unit.StreamCount ? 1 : 0),
                unit.StreamCount,
                structure.UnknownGpuComponentCount);
        }).ToArray();
        return new(
            path,
            length,
            health,
            true,
            snapshot.Header.TypeCount,
            snapshot.Header.FileCount,
            (int)snapshot.Types.Sum(static type => (long)type.ResourceCount),
            distributions,
            snapshot.TypeDistributionIssueCount == 0,
            snapshot.TypeDistributionIssueCount,
            snapshot.FileEntriesInBounds,
            snapshot.MainDataIssueCount == 0,
            snapshot.MainDataIssueCount,
            snapshot.EntryIndexIssueCount == 0,
            snapshot.EntryIndexIssueCount,
            snapshot.GpuResources?.Exists == true,
            snapshot.RequiresGpuResources,
            snapshot.Stream?.Exists == true,
            snapshot.RequiresStream,
            snapshot.GpuRangeIssueCount == 0,
            snapshot.GpuRangeIssueCount,
            snapshot.GpuAlignmentIssueCount,
            snapshot.StreamRangeIssueCount == 0,
            snapshot.StreamRangeIssueCount,
            snapshot.StreamAlignmentIssueCount,
            units);
    }

    private static ModPatchAnalysis CreateAnalysis(IReadOnlyList<PatchFileAnalysis> analyses)
    {
        var types = analyses.SelectMany(file => file.ResourceTypes)
            .GroupBy(item => item.TypeId)
            .Select(group => new PatchTypeDistribution(group.Key, group.Sum(item => item.ResourceCount)))
            .OrderByDescending(item => item.ResourceCount)
            .ToArray();
        return new(
            analyses,
            types,
            analyses.Any(file => !file.HeaderValid || !file.FileEntriesInBounds || !file.TypeDistributionValid ||
                                 !file.MainDataBoundsValid || !file.EntryIndicesValid || file.HealthStatus == PatchHealthStatus.Corrupted),
            analyses.Any(file => (file.RequiresGpuResources && !file.HasGpuResources) || (file.RequiresStream && !file.HasStream)),
            analyses.Any(file => file.UnitDetails.Any(unit => !unit.LodGroupInBounds || !unit.UnitDataInBounds ||
                             !unit.DeclaredSizeMatchesInternal || (unit.LayoutFormatChecked && !unit.LayoutFormatValid))),
            analyses.Any(file => !file.GpuResourceBoundsValid || file.GpuAlignmentIssueCount > 0 ||
                             file.UnitDetails.Any(unit => unit.GpuStructureChecked && !unit.GpuStructureValid)),
            analyses.Any(file => !file.StreamBoundsValid || file.StreamAlignmentIssueCount > 0),
            analyses.Count,
            analyses.Count(file => file.UnitDetails.Count > 0),
            analyses.Count(file => file.HealthStatus == PatchHealthStatus.Healthy),
            analyses.Count(file => file.HealthStatus == PatchHealthStatus.Warning),
            analyses.Count(file => file.HealthStatus == PatchHealthStatus.Corrupted));
    }

    private const int PatchUnitHeaderSize = 0x68;

    private sealed record CacheEntry(long Length, DateTime LastWriteTimeUtc, PatchFileAnalysis Analysis);
}







