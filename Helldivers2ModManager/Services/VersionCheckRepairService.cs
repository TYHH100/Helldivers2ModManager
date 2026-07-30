using Helldivers2ModManager.Models;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 补丁安全修复实现。
/// Unit/TOC 校验与重建流程参考 hd2-repatcher 的结构研究，格式常量参考 HD2SDK-CommunityEdition。
/// 来源：https://github.com/RaidingForPants/hd2-repatcher、
/// https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition。
/// </summary>
internal sealed partial class VersionCheckService
{
    private readonly SemaphoreSlim _repairSemaphore = new(1, 1);

    private readonly record struct RepairTocEntry(
        PatchTocEntry Toc,
        long TableOffset);

    private readonly record struct RepairTypeRecord(
        long TypeId,
        ulong Count,
        uint MainAlignment,
        uint GpuAlignment,
        long TableOffset);

    private readonly record struct ActualTypeGroup(
        long TypeId,
        int Count);

    private sealed class PreparedRepair
    {
        public required string OriginalPath { get; init; }
        public required string TemporaryPath { get; init; }
        public required string BackupPath { get; init; }
    }

    public async Task<ModRepairPlan> CreateRepairPlanAsync(DirectoryInfo modDirectory)
    {
        var actions = new List<PatchRepairAction>();
        var blockers = new List<string>();
        var patchFiles = modDirectory.GetFiles("*", SearchOption.AllDirectories)
            .Where(f => IsMainPatchFile(f.Name))
            .ToArray();

        foreach (var patchFile in patchFiles)
            await InspectPatchForRepairsAsync(patchFile, actions, blockers);

        return new ModRepairPlan
        {
            Actions = actions,
            BlockingReasons = blockers.Distinct(StringComparer.Ordinal).ToList()
        };
    }

    public async Task<ModRepairResult> RepairModAsync(DirectoryInfo modDirectory)
    {
        await _repairSemaphore.WaitAsync();
        var prepared = new List<PreparedRepair>();
        var committed = new List<PreparedRepair>();
        try
        {
            // Rebuild the plan under the repair lock so UI data cannot become a stale write plan.
            var plan = await CreateRepairPlanAsync(modDirectory);
            if (!plan.CanRepair)
            {
                return new ModRepairResult
                {
                    ErrorMessage = plan.BlockingReasons.Count > 0
                        ? string.Join(Environment.NewLine, plan.BlockingReasons)
                        : _localizationService["VersionCheckRepair.NothingToRepair"]
                };
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var fileGroup in plan.Actions.GroupBy(a => a.PatchFilePath, StringComparer.OrdinalIgnoreCase))
            {
                var originalPath = fileGroup.Key;
                var originalFile = new FileInfo(originalPath);
                var temporaryPath = Path.Combine(
                    originalFile.DirectoryName!,
                    "." + originalFile.Name + ".hd2mm-repair-" + Guid.NewGuid().ToString("N") + ".tmp");
                var backupPath = CreateBackupPath(originalFile, stamp);

                prepared.Add(new PreparedRepair
                {
                    OriginalPath = originalPath,
                    TemporaryPath = temporaryPath,
                    BackupPath = backupPath
                });

                File.Copy(originalPath, temporaryPath, false);
                await ApplyRepairActionsAsync(temporaryPath, fileGroup.OrderBy(a => a.Offset).ToList());

                var validation = await AnalyzeSinglePatchFileStructureAsync(
                    new FileInfo(temporaryPath),
                    originalFile);
                if (!IsRepairValidationSuccessful(validation, allowLegacyLayoutIssues: true))
                {
                    throw new InvalidDataException(_localizationService["VersionCheckRepair.ValidationFailed"]
                        .Replace("{file}", originalFile.Name));
                }

            }


            foreach (var item in prepared)
            {
                File.Replace(item.TemporaryPath, item.OriginalPath, item.BackupPath, true);
                committed.Add(item);
            }

            foreach (var item in prepared)
            {
                var actionCount = plan.Actions.Count(action => string.Equals(
                    action.PatchFilePath,
                    item.OriginalPath,
                    StringComparison.OrdinalIgnoreCase));
                await TryWriteBackupMetadataAsync(
                    item.BackupPath,
                    item.OriginalPath,
                    ModBackupRepairKind.SafeMetadata,
                    actionCount);
            }

            return new ModRepairResult
            {
                Success = true,
                AppliedActionCount = plan.ActionCount,
                BackupPaths = prepared.Select(p => p.BackupPath).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to repair mod patch metadata in {Directory}", modDirectory.FullName);
            foreach (var item in committed.AsEnumerable().Reverse())
            {
                try
                {
                    File.Copy(item.BackupPath, item.OriginalPath, true);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogCritical(rollbackException, "Failed to roll back repaired patch {Patch}", item.OriginalPath);
                }
            }

            return new ModRepairResult { ErrorMessage = ex.Message };
        }
        finally
        {
            foreach (var item in prepared)
            {
                try
                {
                    if (File.Exists(item.TemporaryPath))
                        File.Delete(item.TemporaryPath);
                }
                catch (Exception cleanupException)
                {
                    _logger.LogWarning(cleanupException, "Failed to remove repair temp file {File}", item.TemporaryPath);
                }
            }
            _repairSemaphore.Release();
        }
    }

    private async Task InspectPatchForRepairsAsync(
        FileInfo patchFile,
        List<PatchRepairAction> actions,
        List<string> blockers,
        FileInfo? companionSource = null)
    {
        try
        {
            await using var stream = OpenPatchReadStream(patchFile);
            var header = new byte[HeaderSize];
            if (!await ReadAtAsync(stream, 0, header) ||
                MemoryMarshal.Read<int>(header.AsSpan(0, 4)) != PatchHeaderMagic)
            {
                AddRepairBlocker(blockers, patchFile, "invalid patch header");
                return;
            }

            var numTypes = MemoryMarshal.Read<int>(header.AsSpan(4, 4));
            var numFiles = MemoryMarshal.Read<int>(header.AsSpan(8, 4));
            if (numTypes < 0 || numFiles < 0 || numTypes > 1000 || numFiles > 100000)
            {
                AddRepairBlocker(blockers, patchFile, "suspicious TOC counts");
                return;
            }

            var fileEntriesOffset = HeaderSize + (long)numTypes * TypeEntrySize;
            var tableEnd = fileEntriesOffset + (long)numFiles * FileEntrySize;
            if (tableEnd > stream.Length)
            {
                AddRepairBlocker(blockers, patchFile, "TOC exceeds file bounds");
                return;
            }

            var typeRecords = new List<RepairTypeRecord>(numTypes);
            var typeBuffer = new byte[TypeEntrySize];
            for (var i = 0; i < numTypes; i++)
            {
                var typeOffset = HeaderSize + (long)i * TypeEntrySize;
                if (!await ReadAtAsync(stream, typeOffset, typeBuffer))
                {
                    AddRepairBlocker(blockers, patchFile, "type table cannot be read");
                    return;
                }

                typeRecords.Add(new RepairTypeRecord(
                    MemoryMarshal.Read<long>(typeBuffer.AsSpan(8, 8)),
                    MemoryMarshal.Read<ulong>(typeBuffer.AsSpan(16, 8)),
                    MemoryMarshal.Read<uint>(typeBuffer.AsSpan(24, 4)),
                    MemoryMarshal.Read<uint>(typeBuffer.AsSpan(28, 4)),
                    typeOffset));
            }

            var entries = new List<RepairTocEntry>(numFiles);
            var actualTypes = new Dictionary<long, int>();
            var actualGroups = new List<ActualTypeGroup>();
            var entryBuffer = new byte[FileEntrySize];
            for (var i = 0; i < numFiles; i++)
            {
                var tableOffset = fileEntriesOffset + (long)i * FileEntrySize;
                if (!await ReadAtAsync(stream, tableOffset, entryBuffer))
                {
                    AddRepairBlocker(blockers, patchFile, "file table cannot be read");
                    return;
                }

                var toc = new PatchTocEntry(
                    MemoryMarshal.Read<long>(entryBuffer.AsSpan(0, 8)),
                    MemoryMarshal.Read<long>(entryBuffer.AsSpan(8, 8)),
                    MemoryMarshal.Read<ulong>(entryBuffer.AsSpan(16, 8)),
                    MemoryMarshal.Read<ulong>(entryBuffer.AsSpan(24, 8)),
                    MemoryMarshal.Read<ulong>(entryBuffer.AsSpan(32, 8)),
                    MemoryMarshal.Read<uint>(entryBuffer.AsSpan(56, 4)),
                    MemoryMarshal.Read<uint>(entryBuffer.AsSpan(60, 4)),
                    MemoryMarshal.Read<uint>(entryBuffer.AsSpan(64, 4)),
                    MemoryMarshal.Read<uint>(entryBuffer.AsSpan(76, 4)));
                entries.Add(new RepairTocEntry(toc, tableOffset));
                actualTypes.TryGetValue(toc.TypeId, out var actualCount);
                actualTypes[toc.TypeId] = actualCount + 1;

                if (actualGroups.Count == 0 || actualGroups[^1].TypeId != toc.TypeId)
                    actualGroups.Add(new ActualTypeGroup(toc.TypeId, 1));
                else
                    actualGroups[^1] = actualGroups[^1] with { Count = actualGroups[^1].Count + 1 };

                if (toc.EntryIndex != (uint)(i + 1))
                {
                    actions.Add(new PatchRepairAction
                    {
                        Kind = PatchRepairKind.EntryIndex,
                        PatchFilePath = patchFile.FullName,
                        Offset = tableOffset + 76,
                        Width = 4,
                        OldValue = toc.EntryIndex,
                        NewValue = (uint)(i + 1),
                        EntryIndex = i + 1,
                        FileId = toc.FileId
                    });
                }
            }

            if (actualGroups.Count != numTypes ||
                actualGroups.Select(g => g.TypeId).Distinct().Count() != actualGroups.Count)
            {
                AddRepairBlocker(blockers, patchFile, "file entries do not form one unique contiguous group per resource type");
                return;
            }

            var declaredTypeIds = typeRecords.Select(t => t.TypeId).ToHashSet();
            var actualTypeIds = actualTypes.Keys.ToHashSet();
            var rebuildTypeIds = declaredTypeIds.Count != typeRecords.Count ||
                                 !declaredTypeIds.SetEquals(actualTypeIds);
            if (rebuildTypeIds)
            {
                for (var i = 0; i < typeRecords.Count; i++)
                {
                    var record = typeRecords[i];
                    var group = actualGroups[i];
                    if (record.TypeId != group.TypeId)
                    {
                        actions.Add(new PatchRepairAction
                        {
                            Kind = PatchRepairKind.ResourceTypeId,
                            PatchFilePath = patchFile.FullName,
                            Offset = record.TableOffset + 8,
                            Width = 8,
                            OldValue = unchecked((ulong)record.TypeId),
                            NewValue = unchecked((ulong)group.TypeId)
                        });
                    }
                    if (record.Count != (ulong)group.Count)
                    {
                        actions.Add(new PatchRepairAction
                        {
                            Kind = PatchRepairKind.TypeResourceCount,
                            PatchFilePath = patchFile.FullName,
                            Offset = record.TableOffset + 16,
                            Width = 8,
                            OldValue = record.Count,
                            NewValue = (ulong)group.Count
                        });
                    }
                }
            }
            else
            {
                foreach (var record in typeRecords)
                {
                    var actualCount = (ulong)actualTypes[record.TypeId];
                    if (record.Count != actualCount)
                    {
                        actions.Add(new PatchRepairAction
                        {
                            Kind = PatchRepairKind.TypeResourceCount,
                            PatchFilePath = patchFile.FullName,
                            Offset = record.TableOffset + 16,
                            Width = 8,
                            OldValue = record.Count,
                            NewValue = actualCount
                        });
                    }
                }
            }

            AddAlignmentRepairs(
                patchFile,
                typeRecords,
                t => t.MainAlignment,
                t => t.TableOffset + 24,
                actions,
                blockers,
                "main");
            AddAlignmentRepairs(
                patchFile,
                typeRecords,
                t => t.GpuAlignment,
                t => t.TableOffset + 28,
                actions,
                blockers,
                "GPU");
            if (blockers.Count > 0)
                return;

            var virtualEntries = entries.Select(e => e.Toc).ToArray();

            // An oversized Unit TOC range can overlap the following payload even when both
            // offsets are correct. Repair those self-describing sizes before asking the
            // generic offset recovery code to reject overlapping "valid-looking" ranges.
            // Invalid Unit offsets are deferred until after offset inference below.
            await AddUnitSizeRepairsAsync(
                patchFile,
                entries,
                virtualEntries,
                stream,
                actions,
                blockers,
                deferInvalidUnitHeaders: true);
            if (blockers.Count > 0)
                return;

            if (!TryInferInvalidMainOffsets(
                    patchFile,
                    entries,
                    virtualEntries,
                    stream.Length,
                    tableEnd,
                    actions,
                    blockers))
            {
                return;
            }

            // Re-run after offset reconstruction so a Unit whose original offset was invalid
            // can now be checked and, when physically proven, have its TOC size repaired.
            await AddUnitSizeRepairsAsync(
                patchFile,
                entries,
                virtualEntries,
                stream,
                actions,
                blockers,
                deferInvalidUnitHeaders: false);
            if (blockers.Count > 0)
                return;

            var companionAnalysis = new PatchFileAnalysis();
            ValidateCompanionFiles(companionSource ?? patchFile, virtualEntries, companionAnalysis);
            if ((companionAnalysis.RequiresGpuResources && !companionAnalysis.HasGpuResources) ||
                (companionAnalysis.RequiresStream && !companionAnalysis.HasStream) ||
                !companionAnalysis.GpuResourceBoundsValid ||
                !companionAnalysis.StreamBoundsValid)
            {
                AddRepairBlocker(blockers, patchFile, "required GPU or stream payload data is unavailable");
                return;
            }

            var repairedRangeAnalysis = new PatchFileAnalysis();
            ValidateMainDataRanges(virtualEntries, stream.Length, tableEnd, repairedRangeAnalysis);
            if (!repairedRangeAnalysis.MainDataBoundsValid)
                AddRepairBlocker(blockers, patchFile, "proposed metadata would overlap resource payloads");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build repair plan for {Patch}", patchFile.FullName);
            AddRepairBlocker(blockers, patchFile, ex.Message);
        }
    }

    private async Task AddUnitSizeRepairsAsync(
        FileInfo patchFile,
        IReadOnlyList<RepairTocEntry> entries,
        PatchTocEntry[] virtualEntries,
        FileStream stream,
        List<PatchRepairAction> actions,
        List<string> blockers,
        bool deferInvalidUnitHeaders)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var toc = virtualEntries[i];
            var entry = entries[i];
            if (toc.TypeId != UnitTypeId)
                continue;

            var detail = await AnalyzeUnitResourceDeepAsync(
                patchFile.Name, i + 1, toc, stream, _localizationService);
            if (!detail.UnitDataInBounds)
            {
                if (deferInvalidUnitHeaders)
                    continue;

                AddRepairBlocker(blockers, patchFile, $"Unit #{i + 1} header is outside its declared payload");
                continue;
            }

            if (detail.DeclaredSizeMatchesInternal)
            {
                if (!detail.LODGroupInBounds)
                    AddRepairBlocker(blockers, patchFile, $"Unit #{i + 1} has an unsupported LOD boundary problem");
                continue;
            }

            if (detail.ExpectedDataSize <= 0)
            {
                AddRepairBlocker(blockers, patchFile, $"Unit #{i + 1} does not contain a usable internal size");
                continue;
            }

            var expectedSize = (uint)detail.ExpectedDataSize;
            var expectedEnd = toc.TocOffset + expectedSize;
            var nextPayloadOffset = virtualEntries
                .Where(e => e.TocSize > 0 && e.TocOffset > toc.TocOffset)
                .Select(e => e.TocOffset)
                .DefaultIfEmpty((ulong)stream.Length)
                .Min();

            // The internal end marker is accepted only when it lands exactly on the next
            // payload (or EOF). This proves both safe expansion of a truncated declaration
            // and safe shrinking of an oversized declaration without moving any bytes.
            if (expectedEnd != nextPayloadOffset || expectedEnd > (ulong)stream.Length)
            {
                AddRepairBlocker(blockers, patchFile, $"Unit #{i + 1} physical boundary does not prove the expected size");
                continue;
            }

            var repairedEntry = toc with { TocSize = expectedSize };
            var repairedDetail = await AnalyzeUnitResourceDeepAsync(
                patchFile.Name, i + 1, repairedEntry, stream, _localizationService);
            if (!repairedDetail.UnitDataInBounds ||
                !repairedDetail.DeclaredSizeMatchesInternal ||
                !repairedDetail.LODGroupInBounds)
            {
                AddRepairBlocker(blockers, patchFile, $"Unit #{i + 1} still fails validation with the proposed size");
                continue;
            }

            virtualEntries[i] = repairedEntry;
            actions.Add(new PatchRepairAction
            {
                Kind = PatchRepairKind.UnitTocSize,
                PatchFilePath = patchFile.FullName,
                Offset = entry.TableOffset + 56,
                Width = 4,
                OldValue = toc.TocSize,
                NewValue = expectedSize,
                EntryIndex = i + 1,
                FileId = toc.FileId
            });
        }
    }

    private static void AddAlignmentRepairs(
        FileInfo patchFile,
        IReadOnlyList<RepairTypeRecord> typeRecords,
        Func<RepairTypeRecord, uint> getValue,
        Func<RepairTypeRecord, long> getOffset,
        List<PatchRepairAction> actions,
        List<string> blockers,
        string alignmentName)
    {
        var zeroRecords = typeRecords.Where(r => getValue(r) == 0).ToList();
        if (zeroRecords.Count == 0)
            return;

        var candidates = typeRecords
            .Select(getValue)
            .Where(value => value > 0 && value <= 4096 && (value & (value - 1)) == 0)
            .Distinct()
            .ToList();
        if (candidates.Count != 1)
        {
            AddRepairBlocker(blockers, patchFile, $"{alignmentName} alignment cannot be inferred uniquely");
            return;
        }

        foreach (var record in zeroRecords)
        {
            actions.Add(new PatchRepairAction
            {
                Kind = PatchRepairKind.TypeAlignment,
                PatchFilePath = patchFile.FullName,
                Offset = getOffset(record),
                Width = 4,
                OldValue = 0,
                NewValue = candidates[0]
            });
        }
    }

    private static bool TryInferInvalidMainOffsets(
        FileInfo patchFile,
        IReadOnlyList<RepairTocEntry> entries,
        PatchTocEntry[] virtualEntries,
        long fileLength,
        long tableEnd,
        List<PatchRepairAction> actions,
        List<string> blockers)
    {
        var invalidIndices = new List<int>();
        var validRanges = new List<(ulong Start, ulong End)>();
        for (var i = 0; i < virtualEntries.Length; i++)
        {
            var entry = virtualEntries[i];
            if (!IsRangeInBounds(entry.TocOffset, entry.TocSize, fileLength) ||
                (entry.TocSize > 0 && entry.TocOffset < (ulong)tableEnd))
            {
                invalidIndices.Add(i);
            }
            else if (entry.TocSize > 0)
            {
                validRanges.Add((entry.TocOffset, entry.TocOffset + entry.TocSize));
            }
        }

        validRanges.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (var i = 1; i < validRanges.Count; i++)
        {
            if (validRanges[i].Start < validRanges[i - 1].End)
            {
                AddRepairBlocker(blockers, patchFile, "valid-looking main payload ranges overlap");
                return false;
            }
        }

        if (invalidIndices.Count == 0)
            return true;

        var gaps = new List<(ulong Start, ulong Length)>();
        var cursor = (ulong)tableEnd;
        foreach (var range in validRanges)
        {
            if (range.Start > cursor)
                gaps.Add((cursor, range.Start - cursor));
            if (range.End > cursor)
                cursor = range.End;
        }
        if (cursor < (ulong)fileLength)
            gaps.Add((cursor, (ulong)fileLength - cursor));

        var assignments = new Dictionary<int, (ulong Start, ulong Length)>();
        foreach (var index in invalidIndices)
        {
            var entry = virtualEntries[index];
            if (entry.TocSize == 0)
            {
                AddRepairBlocker(blockers, patchFile, $"entry #{index + 1} has an invalid zero-sized payload offset");
                return false;
            }

            var candidates = gaps
                .Where(g => g.Length == entry.TocSize)
                .ToList();
            if (candidates.Count != 1)
            {
                AddRepairBlocker(blockers, patchFile, $"entry #{index + 1} does not have one unique equal-sized physical gap");
                return false;
            }
            assignments[index] = candidates[0];
        }

        if (assignments.Values.Select(g => g.Start).Distinct().Count() != assignments.Count)
        {
            AddRepairBlocker(blockers, patchFile, "multiple invalid entries map to the same physical gap");
            return false;
        }

        foreach (var (index, gap) in assignments)
        {
            var entry = entries[index];
            virtualEntries[index] = entry.Toc with { TocOffset = gap.Start };
            actions.Add(new PatchRepairAction
            {
                Kind = PatchRepairKind.MainDataOffset,
                PatchFilePath = patchFile.FullName,
                Offset = entry.TableOffset + 16,
                Width = 8,
                OldValue = entry.Toc.TocOffset,
                NewValue = gap.Start,
                EntryIndex = index + 1,
                FileId = entry.Toc.FileId
            });
        }

        return true;
    }

    private static async Task ApplyRepairActionsAsync(
        string temporaryPath,
        IReadOnlyList<PatchRepairAction> actions)
    {
        await using var stream = new FileStream(
            temporaryPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        foreach (var action in actions)
        {
            var buffer = new byte[action.Width];
            stream.Seek(action.Offset, SeekOrigin.Begin);
            var read = await stream.ReadAsync(buffer);
            if (read != buffer.Length)
                throw new EndOfStreamException($"Cannot read repair target at 0x{action.Offset:X}.");

            var current = action.Width switch
            {
                4 => BinaryPrimitives.ReadUInt32LittleEndian(buffer),
                8 => BinaryPrimitives.ReadUInt64LittleEndian(buffer),
                _ => throw new InvalidDataException($"Unsupported repair width {action.Width}.")
            };
            if (current != action.OldValue)
                throw new InvalidDataException($"Repair target changed at 0x{action.Offset:X}.");

            if (action.Width == 4)
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, checked((uint)action.NewValue));
            else
                BinaryPrimitives.WriteUInt64LittleEndian(buffer, action.NewValue);

            stream.Seek(action.Offset, SeekOrigin.Begin);
            await stream.WriteAsync(buffer);
        }

        await stream.FlushAsync();
        stream.Flush(true);
    }

    private static bool IsRepairValidationSuccessful(
        PatchFileAnalysis analysis,
        bool allowLegacyLayoutIssues = false)
    {
        return analysis.HealthStatus != PatchHealthStatus.Corrupted &&
               analysis.HeaderValid &&
               analysis.FileEntriesInBounds &&
               analysis.TypeDistributionValid &&
               analysis.MainDataBoundsValid &&
               analysis.EntryIndicesValid &&
               (!analysis.RequiresGpuResources || analysis.HasGpuResources) &&
               (!analysis.RequiresStream || analysis.HasStream) &&
               analysis.GpuResourceBoundsValid &&
               analysis.StreamBoundsValid &&
               analysis.UnitDetails.All(u =>
                   u.UnitDataInBounds &&
                   u.DeclaredSizeMatchesInternal &&
                   u.LODGroupInBounds &&
                   (allowLegacyLayoutIssues || !u.LayoutFormatChecked || u.LayoutFormatValid));
    }

    private static string CreateBackupPath(FileInfo patchFile, string stamp)
    {
        var backupName = patchFile.Name.Replace(
            ".patch_",
            ".patch-backup_",
            StringComparison.OrdinalIgnoreCase);
        var candidate = Path.Combine(
            patchFile.DirectoryName!,
            $"{backupName}.{stamp}.hd2mm-backup");
        var suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(
                patchFile.DirectoryName!,
                $"{backupName}.{stamp}-{suffix++}.hd2mm-backup");
        }
        return candidate;
    }

    private static void AddRepairBlocker(List<string> blockers, FileInfo patchFile, string reason)
    {
        blockers.Add($"{patchFile.Name}: {reason}");
    }
}
