using Helldivers2ModManager.Models;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Buffers.Binary;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 使用游戏参考资源协助修复 Unit LOD 的实现。
/// Unit 结构、旧版 Layout 判断及重建思路参考 hd2-repatcher，格式与归档资料参考
/// HD2SDK-CommunityEdition。来源：https://github.com/RaidingForPants/hd2-repatcher、
/// https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition。
/// </summary>
internal sealed partial class VersionCheckService
{
    private const long MaxAssistedRepairFileBytes = 256L * 1024 * 1024;
    private const double AutomaticMeshGpuExpansionRatio = 6.0;
    private const uint AutomaticLargeCustomGpuBytes = 5U * 1024U * 1024U;
    private const double AutomaticLargeCustomGpuExpansionRatio = 8.0;
    private const double AutomaticWholePatchCustomDensity = 0.5;
    private const long MaterialTypeId = unchecked((long)0xEAC0B497876ADEDFUL);
    private const int MaterialParentIdOffset = 0x18;
    private const int MaterialTextureCountOffset = 0x40;
    private const int MaterialVariableCountOffset = 0x68;
    private const int MaterialVariableDataSizeOffset = 0x78;
    private const int MaterialTextureTableOffset = 0x88;
    private const int MaterialVariableDescriptorSize = 20;
    private const int LegacyEmissiveMaterialSize = 512;
    private const int CurrentEmissiveMaterialSize = 480;
    private const uint CurrentEmissiveMaterialVersion = 0x11F;
    private const uint CurrentEmissiveMaterialEndOffset = 0x1C8;
    private const uint CurrentEmissiveOpacityThresholdVariableId = 0x529A4AAF;
    private const uint CurrentEmissiveRangeVariableId = 0x32C02400;
    private const ulong LegacyEmissiveMaterialParentId = 0xD3701FC725106C09UL;
    private const ulong CurrentEmissiveMaterialParentId = 0xC6042E3403385D40UL;
    private const ulong CurrentEmissiveOpacityTextureId = 0x12D4692531C1FD35UL;
    private static readonly uint[] s_legacyCharacterMaterialTextureSemantics =
    [
        0x7CA0D044, 0xC985395A, 0xA72CB013, 0x479FB1EF, 0xDF3EE984,
        0xCAED6CD6, 0xD2F99D38, 0xE7BD9019, 0xD47DB28B, 0xFF2C91CC,
        0x736A0029, 0xF8E31D7B, 0xA59F5E11
    ];
    private static readonly uint[] s_legacyEmissiveMaterialTextureSemantics =
    [
        0x1D57DCF3, 0xCA6F2CF1, 0x848BA63B
    ];
    private static readonly uint[] s_currentEmissiveMaterialTextureSemantics =
    [
        0x1D57DCF3, 0xCA6F2CF1, 0x848BA63B, 0xCBDE381B
    ];
    private static readonly MaterialScalarVariable[] s_legacyEmissiveMaterialVariables =
    [
        new(0xA3351311, 0), new(0x43695F7B, 4), new(0x64AAB07B, 8),
        new(0x6FD0B9E7, 12), new(0x60E7D2A1, 16), new(0x4A7CD0EF, 20),
        new(0x4A6796C6, 24), new(0xBD16A396, 28), new(0x32C02400, 56),
        new(0xC012EFE1, 36), new(0xA83F44CD, 40), new(0x6DDBAE8F, 44),
        new(0x4B564F57, 48), new(0x9ED04DA2, 52)
    ];
    private static readonly MaterialScalarVariable[] s_currentEmissiveMaterialVariables =
    [
        new(CurrentEmissiveOpacityThresholdVariableId, 48), new(0xA3351311, 4),
        new(0x43695F7B, 8), new(0x64AAB07B, 12), new(0x6FD0B9E7, 16),
        new(0x60E7D2A1, 20), new(0x4A7CD0EF, 24), new(0xBD16A396, 28),
        new(CurrentEmissiveRangeVariableId, 52), new(0xA83F44CD, 36),
        new(0x6DDBAE8F, 40), new(0x9ED04DA2, 44)
    ];
    private static readonly Lazy<IReadOnlyDictionary<long, string>> s_unitFriendlyNames =
        new(LoadUnitFriendlyNames);

    private sealed record AssistedPatchEntry(
        PatchTocEntry Toc,
        long TableOffset);

    private sealed record ResourceReplacement(
        ulong OriginalOffset,
        uint OriginalSize,
        byte[] UpdatedData,
        int EntryIndex);

    private readonly record struct MaterialScalarVariable(uint Id, uint Offset);

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

        var classification = ClassifyAutomaticLodActions(gamePlan.Actions);
        var mixedPlan = await CreateMixedAssistedRepairPlanAsync(
            modDirectory,
            classification.PreserveUnitIds);
        return new AssistedModRepairPlan
        {
            Actions = mixedPlan.Actions,
            MaterialActions = mixedPlan.MaterialActions,
            BlockingReasons = mixedPlan.BlockingReasons,
            MatchedReferenceCount = mixedPlan.MatchedReferenceCount,
            MissingReferenceCount = mixedPlan.MissingReferenceCount,
            IsAutomatic = true,
            AutomaticStrongCustomCount = classification.StrongCustomUnitIds.Count,
            AutomaticPreserveUnitCount = classification.PreserveUnitIds.Count,
            AutomaticGameLodUnitCount = Math.Max(
                0,
                classification.AutomaticUnitIds.Count - classification.PreserveUnitIds.Count)
        };
    }

    internal static (
        IReadOnlySet<long> PreserveUnitIds,
        IReadOnlySet<long> StrongCustomUnitIds,
        IReadOnlySet<long> AutomaticUnitIds) ClassifyAutomaticLodActions(
            IEnumerable<AssistedUnitRepairAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        var preserveIds = new HashSet<long>();
        var strongCustomIds = new HashSet<long>();
        var automaticUnitIds = new HashSet<long>();
        foreach (var patchGroup in actions
                     .Where(action => action.LodDataDiffers)
                     .GroupBy(action => action.PatchFilePath, StringComparer.OrdinalIgnoreCase))
        {
            var units = patchGroup
                .GroupBy(action => action.FileId)
                .Select(group => new
                {
                    FileId = group.Key,
                    MeshDiffers = group.Any(action => action.MeshIdsDiffer),
                    StrongCustom = group.Any(action => action.StrongCustomModelSignal),
                    MeshSignature = group
                        .Select(action => action.CurrentMeshSignature)
                        .FirstOrDefault(signature => !string.IsNullOrEmpty(signature))
                        ?? string.Empty,
                    BodyShape = group
                        .Select(action => action.BodyShape)
                        .FirstOrDefault(shape => shape != ModelPreviewBodyShape.Unknown),
                    CustomizationSlot = group
                        .Select(action => action.CustomizationSlot)
                        .FirstOrDefault(slot => slot != ModelPreviewCustomizationSlot.Unknown)
                })
                .ToList();
            foreach (var unit in units)
            {
                automaticUnitIds.Add(unit.FileId);
                if (unit.StrongCustom)
                    strongCustomIds.Add(unit.FileId);
            }

            var strongCount = units.Count(unit => unit.StrongCustom);
            var meshDiffCount = units.Count(unit => unit.MeshDiffers);
            var wholePatchIsCustom = strongCount > 0 &&
                strongCount / (double)units.Count >= AutomaticWholePatchCustomDensity;
            var wholePatchHasCustomMeshes = meshDiffCount > 0 &&
                meshDiffCount / (double)units.Count >= AutomaticWholePatchCustomDensity;
            var strongCustomMeshSignatures = units
                .Where(unit => (unit.StrongCustom || unit.MeshDiffers) &&
                               !string.IsNullOrEmpty(unit.MeshSignature))
                .Select(unit => unit.MeshSignature)
                .ToHashSet(StringComparer.Ordinal);
            var strongCustomSlots = units
                .Where(unit => unit.StrongCustom &&
                               unit.CustomizationSlot != ModelPreviewCustomizationSlot.Unknown)
                .Select(unit => unit.CustomizationSlot)
                .ToHashSet();
            foreach (var unit in units.Where(unit =>
                         unit.StrongCustom ||
                         unit.MeshDiffers ||
                         wholePatchIsCustom ||
                         wholePatchHasCustomMeshes ||
                         strongCustomSlots.Contains(unit.CustomizationSlot) ||
                         strongCustomMeshSignatures.Contains(unit.MeshSignature)))
                preserveIds.Add(unit.FileId);
        }

        return (preserveIds, strongCustomIds, automaticUnitIds);
    }

    internal static bool IsStrongAutomaticLodCustomModel(
        bool meshIdsDiffer,
        uint currentGpuSize,
        uint referenceGpuSize)
    {
        var gpuExpansionRatio = referenceGpuSize > 0
            ? currentGpuSize / (double)referenceGpuSize
            : currentGpuSize > 0 ? double.PositiveInfinity : 1.0;
        return (meshIdsDiffer && gpuExpansionRatio >= AutomaticMeshGpuExpansionRatio) ||
               (currentGpuSize >= AutomaticLargeCustomGpuBytes &&
                gpuExpansionRatio >= AutomaticLargeCustomGpuExpansionRatio);
    }

    private async Task<AssistedModRepairPlan> CreateAssistedRepairPlanInternalAsync(
        DirectoryInfo modDirectory,
        Func<long, AssistedLodStrategy> lodStrategySelector)
    {
        var actions = new List<AssistedUnitRepairAction>();
        var materialActions = new List<AssistedMaterialRepairAction>();
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

        if (blockers.Count > 0)
        {
            return new AssistedModRepairPlan
            {
                BlockingReasons = blockers.Distinct(StringComparer.Ordinal).ToList(),
                MaterialActions = materialActions
            };
        }

        foreach (var (patchFile, _, entries) in patchData)
        {
            materialActions.AddRange(await CreateMaterialParentMigrationActionsAsync(
                patchFile,
                entries,
                blockers));
        }

        if (blockers.Count > 0 || unitIds.Count == 0)
        {
            return new AssistedModRepairPlan
            {
                Actions = actions,
                MaterialActions = materialActions,
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
                var strongCustomModelSignal = IsStrongAutomaticLodCustomModel(
                    meshIdsDiffer,
                    entry.Toc.GpuSize,
                    reference.GpuSize);
                var customizationInfo = PatchResourceInspectionService.TryReadUnitCustomizationInfo(unitData);
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
                    BodyShape = customizationInfo.BodyShape,
                    CustomizationSlot = customizationInfo.Slot,
                    LodStrategy = lodStrategy,
                    LodDataDiffers = lodDataDiffers,
                    FriendlyName = GetUnitFriendlyName(unit.FileId)
                });
            }
        }

        return new AssistedModRepairPlan
        {
            Actions = actions,
            MaterialActions = materialActions,
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

            var references = new Dictionary<long, GameUnitReferenceData>();
            if (plan.Actions.Count > 0)
            {
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

                references = referenceLookup.References;
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var patchPaths = plan.Actions
                .Select(action => action.PatchFilePath)
                .Concat(plan.MaterialActions.Select(action => action.PatchFilePath))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var patchPath in patchPaths)
            {
                var originalFile = new FileInfo(patchPath);
                var unitActions = plan.Actions
                    .Where(action => string.Equals(
                        action.PatchFilePath,
                        originalFile.FullName,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var materialActions = plan.MaterialActions
                    .Where(action => string.Equals(
                        action.PatchFilePath,
                        originalFile.FullName,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
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
                    unitActions,
                    materialActions,
                    references);

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
                var materialActionCount = plan.MaterialActions.Count(action => string.Equals(
                    action.PatchFilePath,
                    item.OriginalPath,
                    StringComparison.OrdinalIgnoreCase));
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
                    modDirectory,
                    item.BackupPath,
                    item.OriginalPath,
                    repairKind,
                    fileActions.Count + materialActionCount);
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

    private async Task<List<AssistedMaterialRepairAction>> CreateMaterialParentMigrationActionsAsync(
        FileInfo patchFile,
        IReadOnlyList<AssistedPatchEntry> entries,
        List<string> blockers)
    {
        var actions = new List<AssistedMaterialRepairAction>();
        await using var stream = OpenPatchReadStream(patchFile);
        foreach (var entry in entries.Where(entry => entry.Toc.TypeId == MaterialTypeId))
        {
            if (entry.Toc.TocSize > 1024 * 1024)
            {
                blockers.Add($"{patchFile.Name}: Material #{entry.Toc.EntryIndex} is too large for parent migration");
                continue;
            }

            var materialData = new byte[entry.Toc.TocSize];
            if (!await ReadAtAsync(stream, checked((long)entry.Toc.TocOffset), materialData))
            {
                blockers.Add($"{patchFile.Name}: Material #{entry.Toc.EntryIndex} cannot be read");
                continue;
            }

            if (!TryGetLegacyMaterialParentMigration(
                    materialData,
                    out var oldParentMaterialId,
                    out var newParentMaterialId))
            {
                if (!TryBuildLegacyEmissiveMaterialMigration(
                        materialData,
                        out _))
                {
                    continue;
                }

                oldParentMaterialId = LegacyEmissiveMaterialParentId;
                newParentMaterialId = CurrentEmissiveMaterialParentId;
                actions.Add(new AssistedMaterialRepairAction
                {
                    PatchFilePath = patchFile.FullName,
                    EntryIndex = checked((int)entry.Toc.EntryIndex),
                    FileId = entry.Toc.FileId,
                    Kind = AssistedMaterialRepairKind.LegacyEmissiveSchema,
                    OldParentMaterialId = oldParentMaterialId,
                    NewParentMaterialId = newParentMaterialId
                });
                continue;
            }

            actions.Add(new AssistedMaterialRepairAction
            {
                PatchFilePath = patchFile.FullName,
                EntryIndex = checked((int)entry.Toc.EntryIndex),
                FileId = entry.Toc.FileId,
                Kind = AssistedMaterialRepairKind.ParentReference,
                OldParentMaterialId = oldParentMaterialId,
                NewParentMaterialId = newParentMaterialId
            });
        }

        return actions;
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
        IReadOnlyList<AssistedMaterialRepairAction> materialActions,
        IReadOnlyDictionary<long, GameUnitReferenceData> references)
    {
        var originalData = await File.ReadAllBytesAsync(originalFile.FullName);
        var planBlockers = new List<string>();
        var entries = await ReadAssistedPatchEntriesAsync(originalFile, planBlockers)
            ?? throw new InvalidDataException(string.Join(Environment.NewLine, planBlockers));
        var actionByEntry = actions.ToDictionary(a => a.EntryIndex);
        var materialActionByEntry = materialActions.ToDictionary(a => a.EntryIndex);
        var replacements = new List<ResourceReplacement>();

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

            replacements.Add(new ResourceReplacement(
                entry.Toc.TocOffset,
                entry.Toc.TocSize,
                updatedData,
                action.EntryIndex));
        }

        foreach (var entry in entries)
        {
            if (!materialActionByEntry.TryGetValue((int)entry.Toc.EntryIndex, out var action))
                continue;
            if (entry.Toc.FileId != action.FileId || entry.Toc.TypeId != MaterialTypeId)
                throw new InvalidDataException("The material migration plan no longer matches the patch.");

            var materialData = originalData.AsSpan(
                checked((int)entry.Toc.TocOffset),
                checked((int)entry.Toc.TocSize)).ToArray();
            byte[] updatedData;
            switch (action.Kind)
            {
                case AssistedMaterialRepairKind.ParentReference:
                    updatedData = materialData.ToArray();
                    if (BinaryPrimitives.ReadUInt64LittleEndian(
                            updatedData.AsSpan(MaterialParentIdOffset, sizeof(ulong))) !=
                        action.OldParentMaterialId)
                    {
                        throw new InvalidDataException("The material parent changed after the assisted repair plan was created.");
                    }

                    BinaryPrimitives.WriteUInt64LittleEndian(
                        updatedData.AsSpan(MaterialParentIdOffset, sizeof(ulong)),
                        action.NewParentMaterialId);
                    break;
                case AssistedMaterialRepairKind.LegacyEmissiveSchema:
                    if (!TryBuildLegacyEmissiveMaterialMigration(materialData, out updatedData) ||
                        BinaryPrimitives.ReadUInt64LittleEndian(
                            updatedData.AsSpan(MaterialParentIdOffset, sizeof(ulong))) !=
                        action.NewParentMaterialId)
                    {
                        throw new InvalidDataException("The legacy emissive material changed after the assisted repair plan was created.");
                    }
                    break;
                default:
                    throw new InvalidDataException("The material migration kind is unsupported.");
            }

            replacements.Add(new ResourceReplacement(
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

    internal static bool TryGetLegacyMaterialParentMigration(
        ReadOnlySpan<byte> materialData,
        out ulong oldParentMaterialId,
        out ulong newParentMaterialId)
    {
        oldParentMaterialId = 0;
        newParentMaterialId = 0;
        if (materialData.Length < MaterialTextureTableOffset ||
            materialData.Length < MaterialParentIdOffset + sizeof(ulong))
        {
            return false;
        }

        var parentMaterialId = BinaryPrimitives.ReadUInt64LittleEndian(
            materialData.Slice(MaterialParentIdOffset, sizeof(ulong)));
        var textureCount = BinaryPrimitives.ReadUInt32LittleEndian(
            materialData.Slice(MaterialTextureCountOffset, sizeof(uint)));
        var semanticsLength = checked((long)textureCount * sizeof(uint));
        if (textureCount > 4096 ||
            MaterialTextureTableOffset + semanticsLength > materialData.Length)
        {
            return false;
        }

        if (parentMaterialId == 0x54AE9CE1A8FAFE8BUL &&
            HasExactTextureSemantics(materialData, s_legacyCharacterMaterialTextureSemantics))
        {
            oldParentMaterialId = parentMaterialId;
            newParentMaterialId = 0x8F669F365F24594EUL;
            return true;
        }

        // The current replacement for the legacy three-input emissive template adds
        // an opacity-clip input, so it cannot be migrated as a fixed-width parent edit.
        return false;
    }

    internal static bool TryBuildLegacyEmissiveMaterialMigration(
        ReadOnlySpan<byte> materialData,
        out byte[] updatedData)
    {
        updatedData = [];
        if (materialData.Length != LegacyEmissiveMaterialSize ||
            BinaryPrimitives.ReadUInt64LittleEndian(
                materialData.Slice(MaterialParentIdOffset, sizeof(ulong))) !=
            LegacyEmissiveMaterialParentId ||
            BinaryPrimitives.ReadUInt32LittleEndian(
                materialData.Slice(MaterialTextureCountOffset, sizeof(uint))) !=
            s_legacyEmissiveMaterialTextureSemantics.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(
                materialData.Slice(MaterialVariableCountOffset, sizeof(uint))) !=
            s_legacyEmissiveMaterialVariables.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(
                materialData.Slice(MaterialVariableDataSizeOffset, sizeof(uint))) != 60 ||
            !HasExactTextureSemantics(materialData, s_legacyEmissiveMaterialTextureSemantics))
        {
            return false;
        }

        var sourceTextureIdOffset = GetMaterialTextureIdOffset(
            s_legacyEmissiveMaterialTextureSemantics.Length);
        var sourceVariableDescriptorOffset = sourceTextureIdOffset +
            s_legacyEmissiveMaterialTextureSemantics.Length * sizeof(ulong);
        var sourceVariableDataOffset = sourceVariableDescriptorOffset +
            s_legacyEmissiveMaterialVariables.Length * MaterialVariableDescriptorSize;
        if (sourceVariableDataOffset + 60 != materialData.Length ||
            !HasExactScalarVariableLayout(
                materialData,
                sourceVariableDescriptorOffset,
                s_legacyEmissiveMaterialVariables,
                60))
        {
            return false;
        }

        updatedData = new byte[CurrentEmissiveMaterialSize];
        materialData.Slice(0, MaterialTextureTableOffset).CopyTo(updatedData);
        BinaryPrimitives.WriteUInt32LittleEndian(updatedData.AsSpan(0, sizeof(uint)),
            CurrentEmissiveMaterialVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(
            updatedData.AsSpan(12, sizeof(uint)),
            CurrentEmissiveMaterialEndOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(
            updatedData.AsSpan(MaterialParentIdOffset, sizeof(ulong)),
            CurrentEmissiveMaterialParentId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            updatedData.AsSpan(MaterialTextureCountOffset, sizeof(uint)),
            checked((uint)s_currentEmissiveMaterialTextureSemantics.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            updatedData.AsSpan(MaterialVariableCountOffset, sizeof(uint)),
            checked((uint)s_currentEmissiveMaterialVariables.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            updatedData.AsSpan(MaterialVariableDataSizeOffset, sizeof(uint)), 56);

        var targetTextureIdOffset = GetMaterialTextureIdOffset(
            s_currentEmissiveMaterialTextureSemantics.Length);
        for (var index = 0; index < s_currentEmissiveMaterialTextureSemantics.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                updatedData.AsSpan(MaterialTextureTableOffset + index * sizeof(uint), sizeof(uint)),
                s_currentEmissiveMaterialTextureSemantics[index]);
        }

        for (var index = 0; index < s_legacyEmissiveMaterialTextureSemantics.Length; index++)
        {
            var textureId = BinaryPrimitives.ReadUInt64LittleEndian(materialData.Slice(
                sourceTextureIdOffset + index * sizeof(ulong),
                sizeof(ulong)));
            BinaryPrimitives.WriteUInt64LittleEndian(updatedData.AsSpan(
                targetTextureIdOffset + index * sizeof(ulong),
                sizeof(ulong)), textureId);
        }
        BinaryPrimitives.WriteUInt64LittleEndian(updatedData.AsSpan(
            targetTextureIdOffset + s_legacyEmissiveMaterialTextureSemantics.Length * sizeof(ulong),
            sizeof(ulong)), CurrentEmissiveOpacityTextureId);

        var sourceValuesById = new Dictionary<uint, byte[]>();
        foreach (var variable in s_legacyEmissiveMaterialVariables)
        {
            sourceValuesById[variable.Id] = materialData.Slice(
                sourceVariableDataOffset + checked((int)variable.Offset),
                sizeof(uint)).ToArray();
        }
        var targetVariableDescriptorOffset = targetTextureIdOffset +
            s_currentEmissiveMaterialTextureSemantics.Length * sizeof(ulong);
        var targetVariableDataOffset = targetVariableDescriptorOffset +
            s_currentEmissiveMaterialVariables.Length * MaterialVariableDescriptorSize;
        for (var index = 0; index < s_currentEmissiveMaterialVariables.Length; index++)
        {
            var variable = s_currentEmissiveMaterialVariables[index];
            var descriptorOffset = targetVariableDescriptorOffset + index * MaterialVariableDescriptorSize;
            BinaryPrimitives.WriteUInt32LittleEndian(
                updatedData.AsSpan(descriptorOffset + 8, sizeof(uint)), variable.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(
                updatedData.AsSpan(descriptorOffset + 12, sizeof(uint)), variable.Offset);

            if (variable.Id != CurrentEmissiveRangeVariableId &&
                sourceValuesById.TryGetValue(variable.Id, out var sourceValue))
            {
                sourceValue.CopyTo(updatedData, targetVariableDataOffset + checked((int)variable.Offset));
                continue;
            }

            var defaultValue = variable.Id switch
            {
                CurrentEmissiveOpacityThresholdVariableId => 0.144f,
                CurrentEmissiveRangeVariableId => 1.0f,
                _ => throw new InvalidDataException("The current emissive material layout has an unmapped variable.")
            };
            BinaryPrimitives.WriteInt32LittleEndian(
                updatedData.AsSpan(targetVariableDataOffset + checked((int)variable.Offset), sizeof(uint)),
                BitConverter.SingleToInt32Bits(defaultValue));
        }

        return true;
    }

    private static bool HasExactTextureSemantics(
        ReadOnlySpan<byte> materialData,
        ReadOnlySpan<uint> expectedSemantics)
    {
        var textureCount = BinaryPrimitives.ReadUInt32LittleEndian(
            materialData.Slice(MaterialTextureCountOffset, sizeof(uint)));
        if (textureCount != expectedSemantics.Length)
            return false;

        for (var index = 0; index < expectedSemantics.Length; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(
                    materialData.Slice(
                        MaterialTextureTableOffset + index * sizeof(uint),
                        sizeof(uint))) != expectedSemantics[index])
            {
                return false;
            }
        }

        return true;
    }

    private static int GetMaterialTextureIdOffset(int textureCount) =>
        checked(MaterialTextureTableOffset + textureCount * sizeof(uint));

    private static bool HasExactScalarVariableLayout(
        ReadOnlySpan<byte> materialData,
        int descriptorOffset,
        IReadOnlyList<MaterialScalarVariable> expectedVariables,
        int variableDataSize)
    {
        var variableDataOffset = checked(
            descriptorOffset + expectedVariables.Count * MaterialVariableDescriptorSize);
        if (descriptorOffset < 0 || variableDataOffset + variableDataSize > materialData.Length)
            return false;

        for (var index = 0; index < expectedVariables.Count; index++)
        {
            var offset = checked(descriptorOffset + index * MaterialVariableDescriptorSize);
            var expected = expectedVariables[index];
            if (BinaryPrimitives.ReadUInt32LittleEndian(materialData.Slice(offset, sizeof(uint))) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(materialData.Slice(offset + 4, sizeof(uint))) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(materialData.Slice(offset + 8, sizeof(uint))) != expected.Id ||
                BinaryPrimitives.ReadUInt32LittleEndian(materialData.Slice(offset + 12, sizeof(uint))) != expected.Offset ||
                BinaryPrimitives.ReadUInt32LittleEndian(materialData.Slice(offset + 16, sizeof(uint))) != 0 ||
                expected.Offset > variableDataSize - sizeof(uint))
            {
                return false;
            }
        }

        return true;
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
        return new Dictionary<long, string>();
    }
}
