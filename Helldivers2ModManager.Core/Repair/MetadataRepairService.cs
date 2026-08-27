using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.PatchKit;
using Helldivers2ModManager.Core.Versioning;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Core.Repair;

public sealed class MetadataRepairService
{
    private const ulong UnitTypeId = 0xE0A48D0BE9A7453FUL;
    private const int HeaderSize = 72;
    private const int TypeEntrySize = 32;
    private const int FileEntrySize = 80;
    private const int UnitHeaderSize = 0x68;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly PatchStructureAnalyzer _analyzer;
    private readonly ILogger<MetadataRepairService> _logger;
    private readonly SemaphoreSlim _repairLock = new(1, 1);

    public MetadataRepairService(PatchStructureAnalyzer analyzer, ILogger<MetadataRepairService>? logger = null)
    {
        _analyzer = analyzer;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MetadataRepairService>.Instance;
    }

    public async Task<ModRepairPlan> CreatePlanAsync(DirectoryInfo modDirectory, CancellationToken cancellationToken = default)
    {
        var actions = new List<PatchRepairAction>();
        var blockers = new List<string>();
        foreach (var file in EnumeratePatchFiles(modDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await InspectAsync(file, actions, blockers, cancellationToken).ConfigureAwait(false);
        }
        return new(actions, blockers.Distinct(StringComparer.Ordinal).ToArray());
    }

    public async Task<ModRepairResult> RepairAsync(DirectoryInfo modDirectory, CancellationToken cancellationToken = default)
    {
        await _repairLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RepairCoreAsync(modDirectory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _repairLock.Release();
        }
    }

    private async Task<ModRepairResult> RepairCoreAsync(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        var plan = await CreatePlanAsync(directory, cancellationToken).ConfigureAwait(false);
        if (!plan.CanRepair)
        {
            return ModRepairResult.Failed(plan.BlockingReasons.Count > 0 ? string.Join(Environment.NewLine, plan.BlockingReasons) : "NothingToRepair");
        }

        var prepared = new List<(string Original, string Temporary, string Backup)>();
        var committed = new List<(string Original, string Backup)>();
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var group in plan.Actions.GroupBy(action => action.PatchFilePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var original = new FileInfo(group.Key);
                var temporaryPath = Path.Combine(original.DirectoryName!, "." + original.Name + ".hd2mm-repair-" + Guid.NewGuid().ToString("N") + ".tmp");
                var backupPath = CreateBackupPath(original, stamp);
                prepared.Add((original.FullName, temporaryPath, backupPath));
                File.Copy(original.FullName, temporaryPath, false);
                await ApplyActionsAsync(temporaryPath, group.OrderBy(action => action.Offset), cancellationToken).ConfigureAwait(false);


                var validated = await _analyzer.AnalyzeTemporaryFileAsync(new FileInfo(temporaryPath), original, cancellationToken).ConfigureAwait(false);
                if (!IsValid(validated, allowLegacyLayoutIssues: true))
                {
                    throw new InvalidDataException($"Repair validation failed for {original.Name}.");
                }
            }

            foreach (var item in prepared)
            {
                File.Replace(item.Temporary, item.Original, item.Backup, true);
                committed.Add((item.Original, item.Backup));
            }

            foreach (var item in committed)
            {
                var actionCount = plan.Actions.Count(action => string.Equals(action.PatchFilePath, item.Original, StringComparison.OrdinalIgnoreCase));
                await BackupService.TryWriteMetadataAsync(directory, item.Backup, item.Original, item.Original, ModBackupRepairKind.SafeMetadata, actionCount, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, ".hd2mm-backup-history.json.log"), $"{DateTimeOffset.UtcNow:o}|{item.Backup}|{actionCount}{Environment.NewLine}", cancellationToken).ConfigureAwait(false);
            }
            return new(true, plan.ActionCount, committed.Select(item => item.Backup).ToArray());
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Metadata repair failed in {Directory}", directory.FullName);
            foreach (var item in committed.AsEnumerable().Reverse())
            {
                try
                {
                    File.Copy(item.Backup, item.Original, true);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogCritical(rollbackException, "Rollback failed for {Patch}", item.Original);
                }
            }
            return ModRepairResult.Failed(exception.Message);
        }
        finally
        {
            foreach (var item in prepared)
            {
                try
                {
                    if (File.Exists(item.Temporary)) File.Delete(item.Temporary);
                }
                catch (Exception cleanupException)
                {
                    _logger.LogWarning(cleanupException, "Could not remove repair temporary file");
                }
            }
        }
    }

    private async Task InspectAsync(FileInfo patchFile, List<PatchRepairAction> actions, List<string> blockers, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = Open(patchFile);
            if (stream.Length < HeaderSize) throw new InvalidDataException("invalid patch header");
            var header = new byte[HeaderSize];
            if (!await ReadAtAsync(stream, 0, header, cancellationToken) || BinaryPrimitives.ReadInt32LittleEndian(header) != unchecked((int)0xF0000011))
                throw new InvalidDataException("invalid patch header");
            var typeCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            var fileCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            if (typeCount is < 0 or > 1000 || fileCount is < 0 or > 100000) throw new InvalidDataException("suspicious TOC counts");
            var tableStart = HeaderSize + typeCount * TypeEntrySize;
            var tableEnd = tableStart + fileCount * FileEntrySize;
            if (tableEnd > stream.Length) throw new InvalidDataException("TOC exceeds file bounds");

            var types = new List<TypeRecord>(typeCount);
            var typeBuffer = new byte[TypeEntrySize];
            for (var index = 0; index < typeCount; index++)
            {
                var offset = HeaderSize + index * TypeEntrySize;
                if (!await ReadAtAsync(stream, offset, typeBuffer, cancellationToken)) throw new InvalidDataException("type table cannot be read");
                types.Add(new(
                    BinaryPrimitives.ReadInt64LittleEndian(typeBuffer.AsSpan(8)),
                    BinaryPrimitives.ReadUInt64LittleEndian(typeBuffer.AsSpan(16)),
                    BinaryPrimitives.ReadUInt32LittleEndian(typeBuffer.AsSpan(24)),
                    BinaryPrimitives.ReadUInt32LittleEndian(typeBuffer.AsSpan(28)), offset));
            }

            var entries = new List<EntryRecord>(fileCount);
            var actualCounts = new Dictionary<ulong, int>();
            var contiguousGroups = new List<GroupRecord>();
            var entryBuffer = new byte[FileEntrySize];
            for (var index = 0; index < fileCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tableOffset = tableStart + index * FileEntrySize;
                if (!await ReadAtAsync(stream, tableOffset, entryBuffer, cancellationToken)) throw new InvalidDataException("file table cannot be read");
                var toc = ReadToc(entryBuffer);
                entries.Add(new(toc, tableOffset));
                actualCounts[toc.TypeId] = actualCounts.GetValueOrDefault(toc.TypeId) + 1;
                if (contiguousGroups.Count == 0 || contiguousGroups[^1].TypeId != toc.TypeId)
                    contiguousGroups.Add(new(toc.TypeId, 1));
                else contiguousGroups[^1] = contiguousGroups[^1] with { Count = contiguousGroups[^1].Count + 1 };
                if (toc.EntryIndex != index + 1u)
                    actions.Add(new(PatchRepairKind.EntryIndex, patchFile.FullName, tableOffset + 76, 4, toc.EntryIndex, (ulong)(index + 1), index + 1, unchecked((long)toc.FileId)));
            }

            if (contiguousGroups.Count != typeCount || contiguousGroups.Select(g => g.TypeId).Distinct().Count() != contiguousGroups.Count)
                throw new InvalidDataException("file entries do not form one unique contiguous group per resource type");

            var declaredIds = types.Select(record => unchecked((ulong)record.TypeId)).ToHashSet();
            var actualIds = actualCounts.Keys.ToHashSet();
            var rebuildTypes = declaredIds.Count != types.Count || !declaredIds.SetEquals(actualIds);
            if (rebuildTypes)
            {
                for (var index = 0; index < types.Count && index < contiguousGroups.Count; index++)
                {
                    var record = types[index];
                    var group = contiguousGroups[index];
                    if (unchecked((ulong)record.TypeId) != group.TypeId)
                        actions.Add(new(PatchRepairKind.ResourceTypeId, patchFile.FullName, record.TableOffset + 8, 8, unchecked((ulong)record.TypeId), group.TypeId));
                    if (record.ResourceCount != (ulong)group.Count)
                        actions.Add(new(PatchRepairKind.TypeResourceCount, patchFile.FullName, record.TableOffset + 16, 8, record.ResourceCount, (ulong)group.Count));
                }
            }
            else
            {
                foreach (var record in types)
                {
                    var count = actualCounts.GetValueOrDefault(unchecked((ulong)record.TypeId));
                    if (record.ResourceCount != (ulong)count)
                        actions.Add(new(PatchRepairKind.TypeResourceCount, patchFile.FullName, record.TableOffset + 16, 8, record.ResourceCount, (ulong)count));
                }
            }

            AddAlignmentRepairs(patchFile.FullName, types, record => record.MainAlignment, record => record.TableOffset + 24, actions, blockers, "main");
            AddAlignmentRepairs(patchFile.FullName, types, record => record.GpuAlignment, record => record.TableOffset + 28, actions, blockers, "GPU");
            if (blockers.Count > 0) return;

            var virtualEntries = entries.Select(entry => entry.Toc).ToArray();
            await AddUnitSizeRepairsAsync(patchFile, entries, virtualEntries, stream, actions, blockers, true, cancellationToken);
            if (blockers.Count > 0) return;
            if (!TryInferMainOffsets(patchFile.FullName, entries, virtualEntries, stream.Length, tableEnd, actions, blockers)) return;
            await AddUnitSizeRepairsAsync(patchFile, entries, virtualEntries, stream, actions, blockers, false, cancellationToken);
            if (blockers.Count > 0) return;

            var gpuPath = patchFile.FullName + ".gpu_resources";
            var streamPath = patchFile.FullName + ".stream";
            if ((virtualEntries.Any(e => e.GpuSize > 0) && !File.Exists(gpuPath)) ||
                (virtualEntries.Any(e => e.StreamSize > 0) && !File.Exists(streamPath)))
                blockers.Add($"{patchFile.Name}: required GPU or stream payload data is unavailable");
            if (virtualEntries.Any(e => !e.MainInRange(stream.Length) || e.MainOffset < (ulong)tableEnd)) blockers.Add($"{patchFile.Name}: proposed metadata would overlap resource payloads");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to build repair plan for {Patch}", patchFile.FullName);
            blockers.Add($"{patchFile.Name}: {exception.Message}");
        }
    }

    private async Task AddUnitSizeRepairsAsync(FileInfo patchFile, IReadOnlyList<EntryRecord> entries, PatchTocEntry[] virtualEntries, FileStream stream, List<PatchRepairAction> actions, List<string> blockers, bool deferInvalidHeaders, CancellationToken cancellationToken)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var toc = virtualEntries[index];
            var entry = entries[index];
            if ((ulong)toc.TypeId != UnitTypeId) continue;
            if (toc.MainOffset > (ulong)stream.Length - UnitHeaderSize || toc.MainSize < UnitHeaderSize)
            {
                if (!deferInvalidHeaders) blockers.Add($"{patchFile.Name}: Unit #{index + 1} header is outside its declared payload");
                continue;
            }

            var buffer = new byte[UnitHeaderSize];
            if (!await ReadAtAsync(stream, (long)toc.MainOffset, buffer, cancellationToken))
            {
                if (!deferInvalidHeaders) blockers.Add($"{patchFile.Name}: Unit #{index + 1} header is outside its declared payload");
                continue;
            }
            var endingOffset = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(0x60));
            var lodOffset = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(0x30));
            var jointOffset = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(0x34));
            var expectedSize = endingOffset > 0 && endingOffset <= int.MaxValue - 8 ? endingOffset + 8 : 0;
            if (expectedSize == toc.MainSize)
            {
                var size = jointOffset - lodOffset;
                if (!((lodOffset == 0 && jointOffset == 0) || (lodOffset >= 0 && size > 0 && lodOffset + size <= toc.MainSize)))
                    blockers.Add($"{patchFile.Name}: Unit #{index + 1} has an unsupported LOD boundary problem");
                continue;
            }
            if (expectedSize <= 0)
            {
                if (!deferInvalidHeaders) blockers.Add($"{patchFile.Name}: Unit #{index + 1} does not contain a usable internal size");
                continue;
            }

            var expectedEnd = toc.MainOffset + (uint)expectedSize;
            var nextPayload = virtualEntries.Where(e => e.MainSize > 0 && e.MainOffset > toc.MainOffset).Select(e => e.MainOffset).DefaultIfEmpty((ulong)stream.Length).Min();
            if (expectedEnd != nextPayload || expectedEnd > (ulong)stream.Length)
            {
                if (!deferInvalidHeaders) blockers.Add($"{patchFile.Name}: Unit #{index + 1} physical boundary does not prove the expected size");
                continue;
            }

            var repaired = toc with { MainSize = (uint)expectedSize };
            var lodSize = jointOffset - lodOffset;
            var repairedLodValid = (lodOffset == 0 && jointOffset == 0) || (lodOffset >= 0 && lodSize > 0 && lodOffset + lodSize <= repaired.MainSize);
            if (repaired.MainOffset > (ulong)stream.Length - repaired.MainSize || expectedSize != repaired.MainSize || !repairedLodValid)
            {
                if (!deferInvalidHeaders) blockers.Add($"{patchFile.Name}: Unit #{index + 1} still fails validation with the proposed size");
                continue;
            }

            virtualEntries[index] = repaired;
            actions.Add(new(PatchRepairKind.UnitTocSize, patchFile.FullName, entry.TableOffset + 56, 4, toc.MainSize, (ulong)expectedSize, index + 1, unchecked((long)toc.FileId)));
        }
    }

    private static void AddAlignmentRepairs(string path, IEnumerable<TypeRecord> records, Func<TypeRecord, uint> value, Func<TypeRecord, long> offset, List<PatchRepairAction> actions, List<string> blockers, string name)
    {
        var zeros = records.Where(record => value(record) == 0).ToArray();
        if (zeros.Length == 0) return;
        var candidates = records.Select(value).Where(candidate => candidate > 0 && candidate <= 4096 && (candidate & (candidate - 1)) == 0).Distinct().ToArray();
        if (candidates.Length != 1)
        {
            blockers.Add($"{name} alignment cannot be inferred uniquely");
            return;
        }
        foreach (var zero in zeros) actions.Add(new(PatchRepairKind.TypeAlignment, path, offset(zero), 4, 0, candidates[0]));
    }

    private static bool TryInferMainOffsets(string path, IReadOnlyList<EntryRecord> entries, PatchTocEntry[] virtualEntries, long length, long tableEnd, List<PatchRepairAction> actions, List<string> blockers)
    {
        var invalid = new List<int>();
        var validRanges = new List<(ulong Start, ulong End)>();
        for (var index = 0; index < virtualEntries.Length; index++)
        {
            var entry = virtualEntries[index];
            if (!entry.MainInRange(length) || entry.MainOffset < (ulong)tableEnd) invalid.Add(index);
            else if (entry.MainSize > 0) validRanges.Add((entry.MainOffset, entry.MainOffset + entry.MainSize));
        }
        validRanges.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (var index = 1; index < validRanges.Count; index++)
        {
            if (validRanges[index].Start < validRanges[index - 1].End)
            {
                blockers.Add("valid-looking main payload ranges overlap");
                return false;
            }
        }
        if (invalid.Count == 0) return true;
        var gaps = new List<(ulong Start, ulong Length)>();
        var cursor = (ulong)tableEnd;
        foreach (var range in validRanges)
        {
            if (range.Start > cursor) gaps.Add((cursor, range.Start - cursor));
            cursor = Math.Max(cursor, range.End);
        }
        if (cursor < (ulong)length) gaps.Add((cursor, (ulong)length - cursor));

        var assignments = new Dictionary<int, (ulong Start, ulong Length)>();
        foreach (var index in invalid)
        {
            var entry = virtualEntries[index];
            if (entry.MainSize == 0)
            {
                blockers.Add($"entry #{index + 1} has an invalid zero-sized payload offset");
                return false;
            }
            var candidates = gaps.Where(gap => gap.Length == entry.MainSize).ToArray();
            if (candidates.Length != 1)
            {
                blockers.Add($"entry #{index + 1} does not have one unique equal-sized physical gap");
                return false;
            }
            assignments[index] = candidates[0];
        }
        if (assignments.Values.Select(assignment => assignment.Start).Distinct().Count() != assignments.Count)
        {
            blockers.Add("multiple invalid entries map to the same physical gap");
            return false;
        }
        foreach (var (index, gap) in assignments)
        {
            var entry = entries[index];
            virtualEntries[index] = entry.Toc with { MainOffset = gap.Start };
            actions.Add(new(PatchRepairKind.MainDataOffset, path, entry.TableOffset + 16, 8, entry.Toc.MainOffset, gap.Start, index + 1, unchecked((long)entry.Toc.FileId)));
        }
        return true;
    }

    private static async Task ApplyActionsAsync(string temporaryPath, IEnumerable<PatchRepairAction> actions, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
        foreach (var action in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var buffer = new byte[action.Width];
            stream.Seek(action.Offset, SeekOrigin.Begin);
            if (await stream.ReadAsync(buffer, cancellationToken) != buffer.Length) throw new EndOfStreamException($"Cannot read repair target at 0x{action.Offset:X}.");
            var current = action.Width switch
            {
                4 => BinaryPrimitives.ReadUInt32LittleEndian(buffer),
                8 => BinaryPrimitives.ReadUInt64LittleEndian(buffer),
                _ => throw new InvalidDataException($"Unsupported repair width {action.Width}."),
            };
            if (current != action.OldValue) throw new InvalidDataException($"Repair target changed at 0x{action.Offset:X}.");
            if (action.Width == 4) BinaryPrimitives.WriteUInt32LittleEndian(buffer, checked((uint)action.NewValue));
            else BinaryPrimitives.WriteUInt64LittleEndian(buffer, action.NewValue);
            stream.Seek(action.Offset, SeekOrigin.Begin);
            await stream.WriteAsync(buffer, cancellationToken);
        }
        await stream.FlushAsync(cancellationToken);
        stream.Flush(true);
    }

    private static bool IsValid(PatchFileAnalysis analysis, bool allowLegacyLayoutIssues)
    {
        return analysis.HealthStatus != PatchHealthStatus.Corrupted &&
               analysis.HeaderValid && analysis.FileEntriesInBounds && analysis.TypeDistributionValid &&
               analysis.MainDataBoundsValid && analysis.EntryIndicesValid &&
               (!analysis.RequiresGpuResources || analysis.HasGpuResources) &&
               (!analysis.RequiresStream || analysis.HasStream) &&
               analysis.GpuResourceBoundsValid && analysis.StreamBoundsValid &&
               analysis.UnitDetails.All(unit => unit.UnitDataInBounds && unit.DeclaredSizeMatchesInternal &&
                   unit.LodGroupInBounds && (allowLegacyLayoutIssues || !unit.LayoutFormatChecked || unit.LayoutFormatValid));
    }

    private static uint Crc32(FileInfo file)
    {
        var crc = 0xFFFFFFFFu;
        using var stream = file.OpenRead();
        Span<byte> buffer = stackalloc byte[81920];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            foreach (var value in buffer[..read])
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u * (crc & 1));
            }
        }
        return ~crc;
    }

    private static PatchTocEntry ReadToc(byte[] buffer)
    {
        return new(
            1,
            unchecked((ulong)BinaryPrimitives.ReadInt64LittleEndian(buffer)),
            unchecked((ulong)BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8))),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(16)),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(24)),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(32)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(56)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(60)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(64)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(76)));
    }

    private static FileInfo[] EnumeratePatchFiles(DirectoryInfo directory) =>
        directory.EnumerateFiles("*", SearchOption.AllDirectories).Where(file => PatchFileRules.IsMainPatchFile(file.Name)).ToArray();

    private static string CreateBackupPath(FileInfo file, string stamp)
    {
        var name = file.Name.Replace(".patch_", ".patch-backup_", StringComparison.OrdinalIgnoreCase);
        var candidate = Path.Combine(file.DirectoryName!, $"{name}.{stamp}.hd2mm-backup");
        var suffix = 1;
        while (File.Exists(candidate)) candidate = Path.Combine(file.DirectoryName!, $"{name}.{stamp}-{suffix++}.hd2mm-backup");
        return candidate;
    }

    private static FileStream Open(FileInfo file) => new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess);

    private static async Task<bool> ReadAtAsync(Stream stream, long offset, Memory<byte> target, CancellationToken cancellationToken)
    {
        if (offset < 0) return false;
        try
        {
            stream.Seek(offset, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(target, cancellationToken);
            return true;
        }
        catch (EndOfStreamException) { return false; }
    }

    private readonly record struct TypeRecord(long TypeId, ulong ResourceCount, uint MainAlignment, uint GpuAlignment, long TableOffset);
    private readonly record struct EntryRecord(PatchTocEntry Toc, long TableOffset);
    private record struct GroupRecord(ulong TypeId, int Count);
}















