using Helldivers2ModManager.Models;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Buffers.Binary;

namespace Helldivers2ModManager.Services;

internal sealed partial class VersionCheckService
{
    private const long MaxAssistedRepairFileBytes = 256L * 1024 * 1024;
    private const double AutomaticMeshGpuExpansionRatio = 6.0;
    private const uint AutomaticLargeCustomGpuBytes = 6U * 1024U * 1024U;
    private const double AutomaticLargeCustomGpuExpansionRatio = 8.0;
    private const double AutomaticWholePatchCustomDensity = 0.5;
    private static readonly Lazy<IReadOnlyDictionary<long, string>> s_unitFriendlyNames =
        new(LoadUnitFriendlyNames);

    private sealed record AssistedPatchEntry(
        PatchTocEntry Toc,
        long TableOffset);

    private sealed record UnitReplacement(
        ulong OriginalOffset,
        uint OriginalSize,
        byte[] UpdatedData,
        int EntryIndex);

    public Task<AssistedModRepairPlan> CreateAssistedRepairPlanAsync(
        DirectoryInfo modDirectory) =>
        CreateAssistedRepairPlanAsync(modDirectory, AssistedLodStrategy.PreserveMod);

    public Task<AssistedModRepairPlan> CreateAssistedRepairPlanAsync(
        DirectoryInfo modDirectory,
        AssistedLodStrategy lodStrategy) =>
        CreateAssistedRepairPlanInternalAsync(modDirectory, _ => lodStrategy);

    public Task<AssistedModRepairPlan> CreateMixedAssistedRepairPlanAsync(
        DirectoryInfo modDirectory,
        IReadOnlySet<long> preserveModLodUnitIds) =>
        CreateAssistedRepairPlanInternalAsync(
            modDirectory,
            fileId => preserveModLodUnitIds.Contains(fileId)
                ? AssistedLodStrategy.PreserveMod
                : AssistedLodStrategy.UseGameReference);

    public async Task<AssistedModRepairPlan> CreateAutomaticAssistedRepairPlanAsync(
        DirectoryInfo modDirectory)
    {
        var gamePlan = await CreateAssistedRepairPlanAsync(
            modDirectory,
            AssistedLodStrategy.UseGameReference);
        if (!gamePlan.CanRepair)
        {
            return new AssistedModRepairPlan
            {
                Actions = gamePlan.Actions,
                BlockingReasons = gamePlan.BlockingReasons,
                MatchedReferenceCount = gamePlan.MatchedReferenceCount,
                MissingReferenceCount = gamePlan.MissingReferenceCount,
                IsAutomatic = true
            };
        }

        var preserveIds = new HashSet<long>();
        var strongCustomIds = new HashSet<long>();
        var automaticUnitIds = new HashSet<long>();
        foreach (var patchGroup in gamePlan.Actions
                     .Where(action => action.LodDataDiffers)
                     .GroupBy(action => action.PatchFilePath, StringComparer.OrdinalIgnoreCase))
        {
            var units = patchGroup
                .GroupBy(action => action.FileId)
                .Select(group => new
                {
                    FileId = group.Key,
                    StrongCustom = group.Any(action => action.StrongCustomModelSignal),
                    StrongCustomMesh = group.Any(action =>
                        action.StrongCustomModelSignal && action.MeshIdsDiffer),
                    MeshSignature = group
                        .Select(action => action.CurrentMeshSignature)
                        .FirstOrDefault(signature => !string.IsNullOrEmpty(signature))
                        ?? string.Empty
                })
                .ToList();
            foreach (var unit in units)
            {
                automaticUnitIds.Add(unit.FileId);
                if (unit.StrongCustom)
                    strongCustomIds.Add(unit.FileId);
            }

            var strongCount = units.Count(unit => unit.StrongCustom);
            var wholePatchIsCustom = strongCount > 0 &&
                strongCount / (double)units.Count >= AutomaticWholePatchCustomDensity;
            var strongCustomMeshSignatures = units
                .Where(unit => unit.StrongCustomMesh && !string.IsNullOrEmpty(unit.MeshSignature))
                .Select(unit => unit.MeshSignature)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var unit in units.Where(unit =>
                         wholePatchIsCustom ||
                         (unit.StrongCustom && strongCustomMeshSignatures.Count > 0) ||
                         strongCustomMeshSignatures.Contains(unit.MeshSignature)))
                preserveIds.Add(unit.FileId);
        }

        var mixedPlan = await CreateMixedAssistedRepairPlanAsync(modDirectory, preserveIds);
        return new AssistedModRepairPlan
        {
            Actions = mixedPlan.Actions,
            BlockingReasons = mixedPlan.BlockingReasons,
            MatchedReferenceCount = mixedPlan.MatchedReferenceCount,
            MissingReferenceCount = mixedPlan.MissingReferenceCount,
            IsAutomatic = true,
            AutomaticStrongCustomCount = strongCustomIds.Count,
            AutomaticPreserveUnitCount = preserveIds.Count,
            AutomaticGameLodUnitCount = Math.Max(0, automaticUnitIds.Count - preserveIds.Count)
        };
    }

    private async Task<AssistedModRepairPlan> CreateAssistedRepairPlanInternalAsync(
        DirectoryInfo modDirectory,
        Func<long, AssistedLodStrategy> lodStrategySelector)
    {
        var actions = new List<AssistedUnitRepairAction>();
        var blockers = new List<string>();
        var patchFiles = modDirectory.GetFiles("*", SearchOption.AllDirectories)
            .Where(f => IsMainPatchFile(f.Name))
            .ToArray();

        var safePlan = await CreateRepairPlanAsync(modDirectory);
        if (safePlan.ActionCount > 0)
        {
            blockers.Add(_localizationService["VersionCheckRepair.RunSafeRepairFirst"]);
            return new AssistedModRepairPlan
            {
                BlockingReasons = blockers
            };
        }

        var patchData = new List<(FileInfo File, PatchFileAnalysis Analysis, List<AssistedPatchEntry> Entries)>();
        var unitIds = new HashSet<long>();
        foreach (var patchFile in patchFiles)
        {
            var analysis = await AnalyzeSinglePatchFileStructureAsync(patchFile);
            var entries = await ReadAssistedPatchEntriesAsync(patchFile, blockers);
            if (entries is null)
                continue;

            if (!CanUsePatchForAssistedRepair(patchFile, analysis, blockers))
                continue;

            patchData.Add((patchFile, analysis, entries));
            foreach (var unit in analysis.UnitDetails)
                unitIds.Add(unit.FileId);
        }

        if (blockers.Count > 0 || unitIds.Count == 0)
        {
            return new AssistedModRepairPlan
            {
                BlockingReasons = blockers.Distinct(StringComparer.Ordinal).ToList()
            };
        }

        var referenceLookup = await GetGameUnitReferencesAsync(unitIds);
        if (!string.IsNullOrWhiteSpace(referenceLookup.ErrorMessage))
            blockers.Add(referenceLookup.ErrorMessage);
        foreach (var ambiguousId in referenceLookup.AmbiguousUnitIds)
        {
            blockers.Add(_localizationService["VersionCheckRepair.AmbiguousReference"]
                .Replace("{id}", $"0x{(ulong)ambiguousId:X16}"));
        }

        foreach (var (patchFile, analysis, entries) in patchData)
        {
            var entryByIndex = entries.ToDictionary(e => (int)e.Toc.EntryIndex);
            await using var stream = OpenPatchReadStream(patchFile);
            foreach (var unit in analysis.UnitDetails)
            {
                if (!referenceLookup.References.TryGetValue(unit.FileId, out var reference))
                    continue;

                if (!entryByIndex.TryGetValue(unit.EntryIndex, out var entry))
                {
                    blockers.Add($"{patchFile.Name}: Unit #{unit.EntryIndex} TOC entry is unavailable");
                    continue;
                }

                var unitData = new byte[entry.Toc.TocSize];
                if (!await ReadAtAsync(stream, checked((long)entry.Toc.TocOffset), unitData))
                {
                    blockers.Add($"{patchFile.Name}: Unit #{unit.EntryIndex} cannot be read");
                    continue;
                }

                var lodDataDiffers = unit.LODGroupSize != reference.LodGroupData.Length ||
                    !unitData.AsSpan(unit.LODGroupOffset, unit.LODGroupSize)
                        .SequenceEqual(reference.LodGroupData);
                var currentMeshIds = ReadUnitMeshIds(unitData, unitData.Length);
                var currentMeshSignature = currentMeshIds.Length == 0
                    ? string.Empty
                    : string.Join(',', currentMeshIds.Select(id => id.ToString("X8")));
                var meshIdsDiffer = currentMeshIds.Length > 0 &&
                    reference.MeshIds.Length > 0 &&
                    !currentMeshIds.AsSpan().SequenceEqual(reference.MeshIds);
                var gpuExpansionRatio = reference.GpuSize > 0
                    ? entry.Toc.GpuSize / (double)reference.GpuSize
                    : entry.Toc.GpuSize > 0 ? double.PositiveInfinity : 1.0;
                var strongCustomModelSignal =
                    (meshIdsDiffer && gpuExpansionRatio >= AutomaticMeshGpuExpansionRatio) ||
                    (entry.Toc.GpuSize >= AutomaticLargeCustomGpuBytes &&
                     gpuExpansionRatio >= AutomaticLargeCustomGpuExpansionRatio);
                var lodStrategy = lodStrategySelector(unit.FileId);
                var requiresUnitUpdate = unit.Version != reference.Version ||
                    (unit.LayoutFormatChecked && !unit.LayoutFormatValid);
                var requiresLodReplacement =
                    lodStrategy == AssistedLodStrategy.UseGameReference && lodDataDiffers;
                if (!requiresUnitUpdate && !requiresLodReplacement)
                    continue;

                if (!TryBuildUpdatedUnitData(
                        unitData,
                        reference,
                        lodStrategy,
                        out _,
                        out var error))
                {
                    blockers.Add($"{patchFile.Name}: Unit #{unit.EntryIndex} {error}");
                    continue;
                }

                actions.Add(new AssistedUnitRepairAction
                {
                    PatchFilePath = patchFile.FullName,
                    EntryIndex = unit.EntryIndex,
                    FileId = unit.FileId,
                    CurrentVersion = unit.Version,
                    ReferenceVersion = reference.Version,
                    CurrentLodSize = unit.LODGroupSize,
                    ReferenceLodSize = reference.LodGroupData.Length,
                    CurrentGpuSize = entry.Toc.GpuSize,
                    ReferenceGpuSize = reference.GpuSize,
                    MeshIdsDiffer = meshIdsDiffer,
                    CurrentMeshSignature = currentMeshSignature,
                    StrongCustomModelSignal = strongCustomModelSignal,
                    LodStrategy = lodStrategy,
                    LodDataDiffers = lodDataDiffers,
                    FriendlyName = GetUnitFriendlyName(unit.FileId)
                });
            }
        }

        return new AssistedModRepairPlan
        {
            Actions = actions,
            BlockingReasons = blockers.Distinct(StringComparer.Ordinal).ToList(),
            MatchedReferenceCount = referenceLookup.References.Count,
            MissingReferenceCount = referenceLookup.MissingUnitIds.Count
        };
    }

    public Task<ModRepairResult> RepairModWithGameReferencesAsync(
        DirectoryInfo modDirectory) =>
        RepairModWithGameReferencesAsync(modDirectory, AssistedLodStrategy.PreserveMod);

    public Task<ModRepairResult> RepairModWithGameReferencesAsync(
        DirectoryInfo modDirectory,
        AssistedLodStrategy lodStrategy) =>
        RepairModWithGameReferencesCoreAsync(
            modDirectory,
            () => CreateAssistedRepairPlanAsync(modDirectory, lodStrategy));

    public Task<ModRepairResult> RepairModWithMixedGameReferencesAsync(
        DirectoryInfo modDirectory,
        IReadOnlySet<long> preserveModLodUnitIds) =>
        RepairModWithGameReferencesCoreAsync(
            modDirectory,
            () => CreateMixedAssistedRepairPlanAsync(modDirectory, preserveModLodUnitIds));

    public Task<ModRepairResult> RepairModAutomaticallyAsync(
        DirectoryInfo modDirectory) =>
        RepairModWithGameReferencesCoreAsync(
            modDirectory,
            () => CreateAutomaticAssistedRepairPlanAsync(modDirectory));

    private async Task<ModRepairResult> RepairModWithGameReferencesCoreAsync(
        DirectoryInfo modDirectory,
        Func<Task<AssistedModRepairPlan>> createPlanAsync)
    {
        await _repairSemaphore.WaitAsync();
        var prepared = new List<PreparedRepair>();
        var committed = new List<PreparedRepair>();
        try
        {
            var plan = await createPlanAsync();
            if (!plan.CanRepair)
            {
                return new ModRepairResult
                {
                    ErrorMessage = plan.BlockingReasons.Count > 0
                        ? string.Join(Environment.NewLine, plan.BlockingReasons)
                        : _localizationService["VersionCheckRepair.NothingToRepair"]
                };
            }

            var referenceLookup = await GetGameUnitReferencesAsync(
                plan.Actions.Select(a => a.FileId).Distinct().ToArray());
            if (!string.IsNullOrWhiteSpace(referenceLookup.ErrorMessage) ||
                referenceLookup.AmbiguousUnitIds.Count > 0 ||
                plan.Actions.Any(a => !referenceLookup.References.ContainsKey(a.FileId)))
            {
                return new ModRepairResult
                {
                    ErrorMessage = referenceLookup.ErrorMessage ??
                                   _localizationService["VersionCheckRepair.ReferenceChanged"]
                };
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var fileGroup in plan.Actions.GroupBy(
                         a => a.PatchFilePath,
                         StringComparer.OrdinalIgnoreCase))
            {
                var originalFile = new FileInfo(fileGroup.Key);
                var temporaryPath = Path.Combine(
                    originalFile.DirectoryName!,
                    "." + originalFile.Name + ".hd2mm-repair-" + Guid.NewGuid().ToString("N") + ".tmp");
                var backupPath = CreateBackupPath(originalFile, stamp);

                prepared.Add(new PreparedRepair
                {
                    OriginalPath = originalFile.FullName,
                    TemporaryPath = temporaryPath,
                    BackupPath = backupPath
                });

                await RebuildPatchWithGameReferencesAsync(
                    originalFile,
                    temporaryPath,
                    fileGroup.ToList(),
                    referenceLookup.References);

                var validation = await AnalyzeSinglePatchFileStructureAsync(
                    new FileInfo(temporaryPath),
                    originalFile);
                if (!IsRepairValidationSuccessful(validation))
                {
                    throw new InvalidDataException(
                        _localizationService["VersionCheckRepair.ValidationFailed"]
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
                var fileActions = plan.Actions
                    .Where(action => string.Equals(
                        action.PatchFilePath,
                        item.OriginalPath,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var strategies = fileActions
                    .Select(action => action.LodStrategy)
                    .Distinct()
                    .ToList();
                var repairKind = plan.IsAutomatic
                    ? ModBackupRepairKind.AutomaticLod
                    : strategies.Count > 1
                        ? ModBackupRepairKind.MixedLod
                        : strategies.SingleOrDefault() == AssistedLodStrategy.UseGameReference
                            ? ModBackupRepairKind.UseGameLod
                            : ModBackupRepairKind.PreserveModLod;
                await TryWriteBackupMetadataAsync(
                    item.BackupPath,
                    item.OriginalPath,
                    repairKind,
                    fileActions.Count);
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
            _logger.LogError(ex, "Failed to update mod Units from current game references in {Directory}", modDirectory.FullName);
            foreach (var item in committed.AsEnumerable().Reverse())
            {
                try
                {
                    File.Copy(item.BackupPath, item.OriginalPath, true);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogCritical(
                        rollbackException,
                        "Failed to roll back assisted Unit repair for {Patch}",
                        item.OriginalPath);
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
                    _logger.LogWarning(
                        cleanupException,
                        "Failed to remove assisted repair temp file {File}",
                        item.TemporaryPath);
                }
            }
            _repairSemaphore.Release();
        }
    }

    private async Task<List<AssistedPatchEntry>?> ReadAssistedPatchEntriesAsync(
        FileInfo patchFile,
        List<string> blockers)
    {
        if (patchFile.Length > MaxAssistedRepairFileBytes)
        {
            blockers.Add($"{patchFile.Name}: patch is too large for assisted repair");
            return null;
        }

        await using var stream = OpenPatchReadStream(patchFile);
        var header = new byte[HeaderSize];
        if (!await ReadAtAsync(stream, 0, header))
            return null;

        var numTypes = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        var numFiles = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
        if (numTypes < 0 || numFiles < 0 || numTypes > 1000 || numFiles > 100000)
            return null;

        var entryStart = HeaderSize + (long)numTypes * TypeEntrySize;
        if (entryStart + (long)numFiles * FileEntrySize > stream.Length)
            return null;

        var entries = new List<AssistedPatchEntry>(numFiles);
        var buffer = new byte[FileEntrySize];
        for (var i = 0; i < numFiles; i++)
        {
            var tableOffset = entryStart + (long)i * FileEntrySize;
            if (!await ReadAtAsync(stream, tableOffset, buffer))
                return null;

            entries.Add(new AssistedPatchEntry(
                new PatchTocEntry(
                    BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(0, 8)),
                    BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8, 8)),
                    BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(16, 8)),
                    BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(24, 8)),
                    BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(32, 8)),
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(56, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(60, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(64, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(76, 4))),
                tableOffset));
        }

        return entries;
    }

    private bool CanUsePatchForAssistedRepair(
        FileInfo patchFile,
        PatchFileAnalysis analysis,
        List<string> blockers)
    {
        var valid =
            analysis.HeaderValid &&
            analysis.FileEntriesInBounds &&
            analysis.MainDataBoundsValid &&
            analysis.EntryIndicesValid &&
            (!analysis.RequiresGpuResources || analysis.HasGpuResources) &&
            (!analysis.RequiresStream || analysis.HasStream) &&
            analysis.GpuResourceBoundsValid &&
            analysis.StreamBoundsValid &&
            analysis.UnitDetails.All(u =>
                u.UnitDataInBounds &&
                u.DeclaredSizeMatchesInternal &&
                u.LODGroupInBounds);

        if (!valid)
        {
            blockers.Add($"{patchFile.Name}: structural metadata must pass safe repair before assisted repair");
        }
        return valid;
    }

    private async Task RebuildPatchWithGameReferencesAsync(
        FileInfo originalFile,
        string temporaryPath,
        IReadOnlyList<AssistedUnitRepairAction> actions,
        IReadOnlyDictionary<long, GameUnitReferenceData> references)
    {
        var originalData = await File.ReadAllBytesAsync(originalFile.FullName);
        var planBlockers = new List<string>();
        var entries = await ReadAssistedPatchEntriesAsync(originalFile, planBlockers)
            ?? throw new InvalidDataException(string.Join(Environment.NewLine, planBlockers));
        var actionByEntry = actions.ToDictionary(a => a.EntryIndex);
        var replacements = new List<UnitReplacement>();

        foreach (var entry in entries)
        {
            if (!actionByEntry.TryGetValue((int)entry.Toc.EntryIndex, out var action))
                continue;
            if (entry.Toc.FileId != action.FileId ||
                !references.TryGetValue(action.FileId, out var reference))
            {
                throw new InvalidDataException("The assisted repair plan no longer matches the patch.");
            }

            var unitData = originalData.AsSpan(
                checked((int)entry.Toc.TocOffset),
                checked((int)entry.Toc.TocSize)).ToArray();
            var currentVersion = BinaryPrimitives.ReadUInt32LittleEndian(unitData.AsSpan(0x2C, 4));
            var currentLodStart = BinaryPrimitives.ReadUInt32LittleEndian(unitData.AsSpan(0x30, 4));
            var currentLodEnd = BinaryPrimitives.ReadUInt32LittleEndian(unitData.AsSpan(0x34, 4));
            if (currentVersion != action.CurrentVersion ||
                currentLodEnd <= currentLodStart ||
                currentLodEnd - currentLodStart != action.CurrentLodSize)
            {
                throw new InvalidDataException("The target Unit changed after the assisted repair plan was created.");
            }

            if (!TryBuildUpdatedUnitData(unitData, reference, action.LodStrategy, out var updatedData, out var error))
                throw new InvalidDataException($"Unit #{action.EntryIndex} {error}");

            replacements.Add(new UnitReplacement(
                entry.Toc.TocOffset,
                entry.Toc.TocSize,
                updatedData,
                action.EntryIndex));
        }

        replacements.Sort((left, right) => left.OriginalOffset.CompareTo(right.OriginalOffset));
        var newLength = checked(originalData.LongLength +
            replacements.Sum(r => (long)r.UpdatedData.Length - r.OriginalSize));
        if (newLength > int.MaxValue)
            throw new InvalidDataException("The rebuilt patch is too large.");

        using var output = new MemoryStream(checked((int)newLength));
        var cursor = 0;
        foreach (var replacement in replacements)
        {
            var start = checked((int)replacement.OriginalOffset);
            output.Write(originalData, cursor, start - cursor);
            output.Write(replacement.UpdatedData);
            cursor = checked(start + (int)replacement.OriginalSize);
        }
        output.Write(originalData, cursor, originalData.Length - cursor);
        var updatedPatch = output.ToArray();

        foreach (var entry in entries)
        {
            var offsetAdjustment = replacements
                .Where(r => r.OriginalOffset < entry.Toc.TocOffset)
                .Sum(r => (long)r.UpdatedData.Length - r.OriginalSize);
            var updatedOffset = checked((ulong)((long)entry.Toc.TocOffset + offsetAdjustment));
            BinaryPrimitives.WriteUInt64LittleEndian(
                updatedPatch.AsSpan(checked((int)entry.TableOffset + 16), 8),
                updatedOffset);

            var replacement = replacements.FirstOrDefault(r =>
                r.EntryIndex == entry.Toc.EntryIndex);
            if (replacement is not null)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    updatedPatch.AsSpan(checked((int)entry.TableOffset + 56), 4),
                    checked((uint)replacement.UpdatedData.Length));
            }
        }

        await File.WriteAllBytesAsync(temporaryPath, updatedPatch);
        await using var stream = new FileStream(
            temporaryPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        await stream.FlushAsync();
        stream.Flush(true);
    }

    private static bool TryBuildUpdatedUnitData(
        byte[] originalData,
        GameUnitReferenceData reference,
        AssistedLodStrategy lodStrategy,
        out byte[] updatedData,
        out string error)
    {
        updatedData = [];
        error = string.Empty;
        if (originalData.Length < 0x78)
        {
            error = "header is too small";
            return false;
        }

        var originalVersion = BinaryPrimitives.ReadUInt32LittleEndian(originalData.AsSpan(0x2C, 4));
        var lodGroupOffset = BinaryPrimitives.ReadUInt32LittleEndian(originalData.AsSpan(0x30, 4));
        var nextSectionOffset = BinaryPrimitives.ReadUInt32LittleEndian(originalData.AsSpan(0x34, 4));
        var endingOffset = BinaryPrimitives.ReadUInt32LittleEndian(originalData.AsSpan(0x60, 4));
        if (lodGroupOffset < 0x68 ||
            nextSectionOffset <= lodGroupOffset ||
            nextSectionOffset > originalData.Length ||
            endingOffset + 8 != originalData.Length)
        {
            error = "has invalid LOD or ending offsets";
            return false;
        }

        var lodStart = checked((int)lodGroupOffset);
        var lodSize = checked((int)(nextSectionOffset - lodGroupOffset));
        var originalLodData = originalData.AsSpan(lodStart, lodSize).ToArray();
        var workingData = originalData.ToArray();
        if (originalVersion < VersionThresholdForLayoutCheck &&
            !TryUpgradeLegacyLayoutFormats(workingData, out error))
        {
            return false;
        }

        return lodStrategy switch
        {
            AssistedLodStrategy.UseGameReference => TryUseGameReferenceLod(
                workingData,
                reference,
                lodGroupOffset,
                nextSectionOffset,
                out updatedData,
                out error),
            AssistedLodStrategy.PreserveMod => TryPreserveModLod(
                originalData,
                workingData,
                reference,
                lodGroupOffset,
                nextSectionOffset,
                endingOffset,
                originalLodData,
                out updatedData,
                out error),
            _ => throw new ArgumentOutOfRangeException(nameof(lodStrategy))
        };
    }

    private static bool TryPreserveModLod(
        byte[] originalData,
        byte[] workingData,
        GameUnitReferenceData reference,
        uint lodGroupOffset,
        uint nextSectionOffset,
        uint endingOffset,
        byte[] originalLodData,
        out byte[] updatedData,
        out string error)
    {
        var lodStart = checked((int)lodGroupOffset);
        var lodSize = checked((int)(nextSectionOffset - lodGroupOffset));
        if (!workingData.AsSpan(lodStart, lodSize).SequenceEqual(originalLodData))
        {
            error = "legacy Layout data overlaps the custom LOD group";
            updatedData = [];
            return false;
        }

        // Some custom models depend on their own unreversed LOD group list.
        // For this explicit strategy, the game reference supplies only the target Unit version.
        updatedData = workingData;
        BinaryPrimitives.WriteUInt32LittleEndian(
            updatedData.AsSpan(0x2C, 4),
            reference.Version);

        var updatedLodStart = BinaryPrimitives.ReadUInt32LittleEndian(updatedData.AsSpan(0x30, 4));
        var updatedLodEnd = BinaryPrimitives.ReadUInt32LittleEndian(updatedData.AsSpan(0x34, 4));
        var updatedEnding = BinaryPrimitives.ReadUInt32LittleEndian(updatedData.AsSpan(0x60, 4));
        if (updatedData.Length != originalData.Length ||
            updatedLodStart != lodGroupOffset ||
            updatedLodEnd != nextSectionOffset ||
            updatedEnding != endingOffset ||
            !updatedData.AsSpan(lodStart, lodSize).SequenceEqual(originalLodData))
        {
            error = "failed to preserve custom LOD and Unit offsets";
            updatedData = [];
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryUseGameReferenceLod(
        byte[] workingData,
        GameUnitReferenceData reference,
        uint lodGroupOffset,
        uint nextSectionOffset,
        out byte[] updatedData,
        out string error)
    {
        var oldLodSize = checked((int)(nextSectionOffset - lodGroupOffset));
        var sizeDifference = reference.LodGroupData.Length - oldLodSize;
        if (sizeDifference % 16 != 0)
        {
            error = "game LOD size change would break 16-byte alignment";
            updatedData = [];
            return false;
        }

        var newLength = checked(workingData.Length + sizeDifference);
        if (newLength < 0x78)
        {
            error = "game LOD would make the Unit too small";
            updatedData = [];
            return false;
        }

        updatedData = new byte[newLength];
        var lodStart = checked((int)lodGroupOffset);
        Buffer.BlockCopy(workingData, 0, updatedData, 0, lodStart);
        Buffer.BlockCopy(
            reference.LodGroupData,
            0,
            updatedData,
            lodStart,
            reference.LodGroupData.Length);
        Buffer.BlockCopy(
            workingData,
            checked((int)nextSectionOffset),
            updatedData,
            checked(lodStart + reference.LodGroupData.Length),
            workingData.Length - checked((int)nextSectionOffset));

        BinaryPrimitives.WriteUInt32LittleEndian(
            updatedData.AsSpan(0x2C, 4),
            reference.Version);
        for (var offset = 0x34; offset <= 0x70; offset += 4)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(updatedData.AsSpan(offset, 4));
            if (value == 0 || value <= lodGroupOffset)
                continue;

            var adjusted = checked((long)value + sizeDifference);
            if (adjusted < 0 || adjusted > uint.MaxValue)
            {
                error = "contains an offset that cannot be adjusted safely";
                updatedData = [];
                return false;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(
                updatedData.AsSpan(offset, 4),
                (uint)adjusted);
        }

        var updatedLodEnd = BinaryPrimitives.ReadUInt32LittleEndian(updatedData.AsSpan(0x34, 4));
        var updatedEnding = BinaryPrimitives.ReadUInt32LittleEndian(updatedData.AsSpan(0x60, 4));
        if (updatedLodEnd - lodGroupOffset != reference.LodGroupData.Length ||
            updatedEnding + 8 != updatedData.Length ||
            !updatedData.AsSpan(lodStart, reference.LodGroupData.Length)
                .SequenceEqual(reference.LodGroupData))
        {
            error = "failed its game LOD post-transform checks";
            updatedData = [];
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryUpgradeLegacyLayoutFormats(
        byte[] unitData,
        out string error)
    {
        error = string.Empty;
        var layoutListOffset = BinaryPrimitives.ReadUInt32LittleEndian(unitData.AsSpan(0x5C, 4));
        if (layoutListOffset > unitData.Length - 4)
        {
            error = "legacy Layout list is out of bounds";
            return false;
        }

        var layoutBase = checked((int)layoutListOffset);
        var layoutCount = BinaryPrimitives.ReadInt32LittleEndian(unitData.AsSpan(layoutBase, 4));
        if (layoutCount < 0 ||
            layoutCount > 100 ||
            (long)layoutBase + 4L + layoutCount * 4L > unitData.Length)
        {
            error = "legacy Layout table is invalid";
            return false;
        }

        for (var i = 0; i < layoutCount; i++)
        {
            var relativeOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                unitData.AsSpan(layoutBase + 4 + i * 4, 4));
            var layoutStart = (long)layoutBase + relativeOffset;
            if (layoutStart < 0 ||
                layoutStart + 8 + 16 * 20 > unitData.Length)
            {
                error = "legacy Layout entry is out of bounds";
                return false;
            }

            for (var itemIndex = 0; itemIndex < 16; itemIndex++)
            {
                var formatOffset = checked((int)layoutStart + 8 + itemIndex * 20 + 4);
                var itemFormat = BinaryPrimitives.ReadUInt32LittleEndian(
                    unitData.AsSpan(formatOffset, 4));
                if (itemFormat > 16)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        unitData.AsSpan(formatOffset, 4),
                        checked(itemFormat + 4));
                }
            }
        }

        return true;
    }

    private static string GetUnitFriendlyName(long fileId)
    {
        return s_unitFriendlyNames.Value.TryGetValue(fileId, out var name)
            ? name
            : string.Empty;
    }

    private static IReadOnlyDictionary<long, string> LoadUnitFriendlyNames()
    {
        var result = new Dictionary<long, string>();
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Resources",
            "Hashlists",
            "friendlynames.txt");
        if (!File.Exists(path))
            return result;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var separator = line.IndexOf(' ');
                if (separator <= 0 ||
                    !ulong.TryParse(line.AsSpan(0, separator), out var unsignedId))
                {
                    continue;
                }

                var name = line[(separator + 1)..].Trim();
                if (name.Length == 0)
                    continue;

                var fileId = unchecked((long)unsignedId);
                if (!result.TryGetValue(fileId, out var existing) ||
                    IsBetterFriendlyName(name, existing))
                {
                    result[fileId] = name;
                }
            }
        }
        catch
        {
            return new Dictionary<long, string>();
        }

        return result;
    }

    private static bool IsBetterFriendlyName(string candidate, string existing)
    {
        var candidateLooksLikePath = candidate.Contains('/');
        var existingLooksLikePath = existing.Contains('/');
        return existingLooksLikePath && !candidateLooksLikePath;
    }
}
