using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Helldivers2ModManager.Core.GameData;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.PatchKit;
using Helldivers2ModManager.Core.Preview;
using Helldivers2ModManager.Core.Versioning;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Core.Repair;

public class AssistedRepairService(
    MetadataRepairService safeRepair,
    PatchStructureAnalyzer analyzer,
    GameArchiveService? gameArchiveService = null,
    Func<DirectoryInfo?>? dataDirectoryProvider = null,
    ILogger<AssistedRepairService>? logger = null)
{
    private const ulong MaterialTypeId = 0xEAC0B497876ADEDFUL;
    private const int ParentIdOffset = 0x18;
    private const int VariableCountOffset = 0x68;
    private const int VariableDataSizeOffset = 0x78;
    private const int TextureCountOffset = 0x40;
    private const int TextureTableOffset = 0x88;
    private const int VariableDescriptorSize = 20;
    private const ulong LegacyCharacterParentId = 0x54AE9CE1A8FAFE8BUL;
    private const ulong CurrentCharacterParentId = 0x8F669F365F24594EUL;
    private const int LegacyEmissiveMaterialSize = 512;
    private const int CurrentEmissiveMaterialSize = 480;
    private const uint CurrentEmissiveMaterialVersion = 0x11F;
    private const uint CurrentEmissiveMaterialEndOffset = 0x1C8;
    private const uint CurrentEmissiveOpacityThresholdVariableId = 0x529A4AAF;
    private const uint CurrentEmissiveRangeVariableId = 0x32C02400;
    private const ulong LegacyEmissiveParentId = 0xD3701FC725106C09UL;
    private const ulong CurrentEmissiveParentId = 0xC6042E3403385D40UL;
    private const ulong CurrentEmissiveOpacityTextureId = 0x12D4692531C1FD35UL;
    private const uint LayoutThreshold = 0xA4CD36;
    private const int HeaderSize = 72;
    private const int TypeEntrySize = 32;
    private const int FileEntrySize = 80;
    private static readonly uint[] CharacterSemantics =
    [
        0x7CA0D044, 0xC985395A, 0xA72CB013, 0x479FB1EF, 0xDF3EE984,
        0xCAED6CD6, 0xD2F99D38, 0xE7BD9019, 0xD47DB28B, 0xFF2C91CC,
        0x736A0029, 0xF8E31D7B, 0xA59F5E11,
    ];
    private static readonly uint[] LegacyEmissiveSemantics = [0x1D57DCF3, 0xCA6F2CF1, 0x848BA63B];
    private static readonly uint[] CurrentEmissiveSemantics = [0x1D57DCF3, 0xCA6F2CF1, 0x848BA63B, 0xCBDE381B];
    private static readonly EmissiveVariable[] LegacyEmissiveVariables =
    [
        new(0xA3351311, 0), new(0x43695F7B, 4), new(0x64AAB07B, 8),
        new(0x6FD0B9E7, 12), new(0x60E7D2A1, 16), new(0x4A7CD0EF, 20),
        new(0x4A6796C6, 24), new(0xBD16A396, 28), new(0x32C02400, 56),
        new(0xC012EFE1, 36), new(0xA83F44CD, 40), new(0x6DDBAE8F, 44),
        new(0x4B564F57, 48), new(0x9ED04DA2, 52),
    ];
    private static readonly EmissiveVariable[] CurrentEmissiveVariables =
    [
        new(CurrentEmissiveOpacityThresholdVariableId, 48), new(0xA3351311, 4),
        new(0x43695F7B, 8), new(0x64AAB07B, 12), new(0x6FD0B9E7, 16),
        new(0x60E7D2A1, 20), new(0x4A7CD0EF, 24), new(0xBD16A396, 28),
        new(CurrentEmissiveRangeVariableId, 52), new(0xA83F44CD, 36),
        new(0x6DDBAE8F, 40), new(0x9ED04DA2, 44),
    ];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly PatchFileParser _parser = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public Task<AssistedModRepairPlan> CreatePlanAsync(DirectoryInfo directory, AssistedLodStrategy strategy = AssistedLodStrategy.PreserveMod, CancellationToken cancellationToken = default) =>
        CreatePlanCore(directory, _ => strategy, cancellationToken);

    public async Task<AssistedModRepairPlan> CreateMixedPlanAsync(DirectoryInfo directory, IReadOnlySet<long> preserveIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preserveIds);
        return await CreatePlanCore(directory, id => preserveIds.Contains(id) ? AssistedLodStrategy.PreserveMod : AssistedLodStrategy.UseGameReference, cancellationToken);
    }

    public async Task<AssistedModRepairPlan> CreateAutomaticPlanAsync(DirectoryInfo directory, CancellationToken cancellationToken = default)
    {
        var gamePlan = await CreatePlanAsync(directory, AssistedLodStrategy.UseGameReference, cancellationToken);
        if (!gamePlan.CanRepair) return gamePlan;
        var classified = AssistedRepairRules.ClassifyAutomaticLodActions(gamePlan.Actions);
        var mixed = await CreateMixedPlanAsync(directory, classified.Preserve, cancellationToken);
        return mixed with
        {
            IsAutomatic = true,
            AutomaticStrongCustomCount = classified.StrongCustom.Count,
            AutomaticPreserveUnitCount = mixed.Actions.Count(action => action.LodStrategy == AssistedLodStrategy.PreserveMod),
            AutomaticGameLodUnitCount = mixed.Actions.Count(action => action.LodStrategy == AssistedLodStrategy.UseGameReference),
        };
    }

    public async Task<ModRepairResult> RepairAsync(DirectoryInfo directory, AssistedLodStrategy strategy = AssistedLodStrategy.PreserveMod, bool automatic = false, IReadOnlySet<long>? preserveIds = null, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try { return await RepairCoreAsync(directory, strategy, automatic, preserveIds, cancellationToken); }
        finally { _lock.Release(); }
    }

    protected virtual async Task<GameUnitReferenceLookup> ResolveReferencesAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        var directory = dataDirectoryProvider?.Invoke() ?? FindGameData();
        if (gameArchiveService is null || directory is not { Exists: true }) return GameUnitReferenceLookup.Empty with { ErrorMessage = "GameDataUnavailable" };
        return await gameArchiveService.ResolveUnitsAsync(directory, ids, cancellationToken);
    }

    private async Task<AssistedModRepairPlan> CreatePlanCore(DirectoryInfo directory, Func<long, AssistedLodStrategy> selector, CancellationToken cancellationToken)
    {
        var safePlan = await safeRepair.CreatePlanAsync(directory, cancellationToken);
        if (safePlan.ActionCount > 0) return new([], [], ["RunSafeMetadataRepairFirst"]);
        var blockers = new List<string>();
        var plans = new List<(FileInfo File, PatchFileAnalysis Analysis, PatchFileSnapshot Snapshot)>();
        var materialActions = new List<AssistedMaterialRepairAction>();
        var ids = new HashSet<long>();
        foreach (var file in EnumeratePatches(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = await _parser.ParseFileAsync(file, options: null, cancellationToken);
            if (parsed.Snapshot is not { } snapshot) { blockers.Add($"{file.Name}: patch cannot be parsed"); continue; }
            var analysis = await analyzer.AnalyzeFileAsync(file, cancellationToken);
            if (!CanUse(file, analysis, blockers)) continue;
            materialActions.AddRange(await CreateMaterialActionsAsync(file, snapshot, blockers, cancellationToken));
            plans.Add((file, analysis, snapshot));
            foreach (var unit in analysis.UnitDetails) ids.Add(unit.FileId);
        }
        if (blockers.Count > 0 || ids.Count == 0) return new([], materialActions, blockers.Distinct(StringComparer.Ordinal).ToArray());

        var lookup = await ResolveReferencesAsync(ids, cancellationToken);
        if (!string.IsNullOrWhiteSpace(lookup.ErrorMessage)) blockers.Add(lookup.ErrorMessage);
        foreach (var ambiguous in lookup.AmbiguousUnitIds) blockers.Add($"Ambiguous game reference 0x{unchecked((ulong)ambiguous):X16}");
        var legacyIds = materialActions.Where(action => action.OldParentMaterialId == LegacyCharacterParentId && action.NewParentMaterialId == CurrentCharacterParentId).Select(action => action.FileId).ToHashSet();
        var actions = new List<AssistedUnitRepairAction>();
        foreach (var (file, _, snapshot) in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentAnalysis = plans.First(item => item.File.FullName == file.FullName).Analysis;
            var patchHasLegacyPack = materialActions.Any(action => action.PatchFilePath == file.FullName && action.FileId is > 0 && action.NewParentMaterialId == CurrentCharacterParentId && action.OldParentMaterialId == LegacyCharacterParentId);
            var entries = snapshot.Entries.ToDictionary(entry => (int)entry.EntryIndex);
            await using var stream = Open(file);
            foreach (var unit in currentAnalysis.UnitDetails)
            {
                if (!lookup.References.TryGetValue(unit.FileId, out var reference) || !entries.TryGetValue((int)unit.EntryIndex, out var entry)) continue;
                var data = new byte[entry.MainSize];
                if (!await ReadAtAsync(stream, (long)entry.MainOffset, data, cancellationToken)) continue;
                var lodStart = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x30));
                var lodEnd = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x34));
                if (lodStart < 0x68 || lodEnd <= lodStart || lodEnd > data.Length || unit.LodGroupSize != lodEnd - lodStart)
                {
                    blockers.Add($"{file.Name}: Unit #{unit.EntryIndex} has invalid LOD offsets");
                    continue;
                }

                var lodDiffers = !data.AsSpan((int)lodStart, unit.LodGroupSize).SequenceEqual(reference.LodGroupData);
                var meshIds = ReadMeshIds(data);
                var meshDiffers = meshIds.Length > 0 && reference.MeshIds.Length > 0 && !meshIds.AsSpan().SequenceEqual(reference.MeshIds);
                var strong = AssistedRepairRules.IsStrongCustomModel(meshDiffers, entry.GpuSize, (uint)reference.GpuSize);
                var customization = TryReadCustomization(data);
                var legacyRequired = AssistedRepairRules.RequiresCurrentGameLodForLegacyPack(patchHasLegacyPack, unit.Version, reference.Version) ||
                                     AssistedRepairRules.RequiresCurrentGameLodForLegacyMaterial(unit.Version, reference.Version, data, legacyIds);
                var selectedStrategy = legacyRequired ? AssistedLodStrategy.UseGameReference : selector(unit.FileId);
                var needsVersion = unit.Version != reference.Version || (unit.LayoutFormatChecked && !unit.LayoutFormatValid);
                var needsLod = selectedStrategy == AssistedLodStrategy.UseGameReference && lodDiffers;
                if (!needsVersion && !needsLod) continue;
                var updatedData = TryBuildUpdatedUnitData(data, reference, selectedStrategy, out var error);
                if (updatedData is null) { blockers.Add($"{file.Name}: Unit #{unit.EntryIndex} {error}"); continue; }
                actions.Add(new(file.FullName, unit.EntryIndex, unit.FileId, unit.Version, reference.Version,
                    (uint)unit.LodGroupSize, (uint)reference.LodGroupData.Length, entry.GpuSize, (uint)reference.GpuSize, meshDiffers,
                    string.Join(',', meshIds.Select(id => id.ToString("X8"))), strong, customization.BodyShape,
                    customization.Slot, selectedStrategy, lodDiffers));
            }
        }
        return new(actions, materialActions, blockers.Distinct(StringComparer.Ordinal).ToArray(), lookup.References.Count, lookup.MissingUnitIds.Count);
    }

    private async Task<ModRepairResult> RepairCoreAsync(DirectoryInfo directory, AssistedLodStrategy strategy, bool automatic, IReadOnlySet<long>? preserveIds, CancellationToken cancellationToken)
    {
        var plan = automatic ? await CreateAutomaticPlanAsync(directory, cancellationToken)
            : preserveIds is null ? await CreatePlanAsync(directory, strategy, cancellationToken)
            : await CreateMixedPlanAsync(directory, preserveIds, cancellationToken);
        if (!plan.CanRepair) return ModRepairResult.Failed(plan.BlockingReasons.Count > 0 ? string.Join(Environment.NewLine, plan.BlockingReasons) : "NothingToRepair");
        var prepared = new List<(string Original, string Temporary, string Backup)>();
        var committed = new List<(string Original, string Temporary, string Backup)>();
        try
        {
            var references = await ResolveReferencesAsync(plan.Actions.Select(action => action.FileId).Distinct().ToArray(), cancellationToken);
            if (!string.IsNullOrWhiteSpace(references.ErrorMessage)) throw new InvalidDataException(references.ErrorMessage);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var path in plan.Actions.Select(action => action.PatchFilePath).Concat(plan.MaterialActions.Select(action => action.PatchFilePath)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var original = new FileInfo(path);
                var temporary = Path.Combine(original.DirectoryName!, "." + original.Name + ".hd2mm-assisted-" + Guid.NewGuid().ToString("N") + ".tmp");
                var backup = CreateBackupPath(original, stamp);
                prepared.Add((original.FullName, temporary, backup));
                await RebuildAsync(original, temporary, plan.Actions, plan.MaterialActions, references, cancellationToken);
                var validated = await analyzer.AnalyzeTemporaryFileAsync(new FileInfo(temporary), original, cancellationToken);
                if (!IsValid(validated)) throw new InvalidDataException($"Assisted repair validation failed for {original.Name}.");
            }
            foreach (var item in prepared) { File.Replace(item.Temporary, item.Original, item.Backup, true); committed.Add(item); }
            foreach (var item in committed)
            {
                var fileActions = plan.Actions.Where(action => action.PatchFilePath == item.Original).ToArray();
                var count = fileActions.Length + plan.MaterialActions.Count(action => action.PatchFilePath == item.Original);
                var strategies = fileActions.Select(action => action.LodStrategy).Distinct().ToArray();
                var kind = automatic ? ModBackupRepairKind.AutomaticLod : strategies.Length > 1 ? ModBackupRepairKind.MixedLod :
                    strategies.SingleOrDefault() == AssistedLodStrategy.UseGameReference ? ModBackupRepairKind.UseGameLod : ModBackupRepairKind.PreserveModLod;
                await BackupService.TryWriteMetadataAsync(directory, item.Backup, item.Original, item.Original, kind, count, cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, ".hd2mm-backup-history.json.log"), $"{DateTimeOffset.UtcNow:o}|{item.Backup}|{count}{Environment.NewLine}", cancellationToken);
            }
            return new(true, plan.Actions.Count + plan.MaterialActions.Count, committed.Select(item => item.Backup).ToArray());
        }
        catch (Exception exception)
        {
            logger?.LogError(exception, "Assisted repair failed in {Directory}", directory.FullName);
            foreach (var item in committed.AsEnumerable().Reverse())
            {
                try { File.Copy(item.Backup, item.Original, true); }
                catch (Exception rollback) { logger?.LogCritical(rollback, "Assisted repair rollback failed"); }
            }
            return ModRepairResult.Failed(exception.Message);
        }
        finally
        {
            foreach (var item in prepared)
            {
                try { if (File.Exists(item.Temporary)) File.Delete(item.Temporary); }
                catch (Exception cleanup) { logger?.LogWarning(cleanup, "Could not remove assisted temporary"); }
            }
        }
    }

    private async Task RebuildAsync(FileInfo original, string temporary, IReadOnlyList<AssistedUnitRepairAction> actions, IReadOnlyList<AssistedMaterialRepairAction> materialActions, GameUnitReferenceLookup lookup, CancellationToken cancellationToken)
    {
        var source = await File.ReadAllBytesAsync(original.FullName, cancellationToken);
        var parsed = await _parser.ParseFileAsync(original, options: null, cancellationToken);
        if (parsed.Snapshot is not { } snapshot) throw new InvalidDataException("The assisted repair plan no longer matches the patch.");
        var unitByEntry = actions.ToDictionary(action => action.EntryIndex);
        var materialByEntry = materialActions.ToDictionary(action => action.EntryIndex);
        var replacements = new List<ResourceReplacement>();
        foreach (var entry in snapshot.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (unitByEntry.TryGetValue((int)entry.EntryIndex, out var action))
            {
                if (entry.FileId != unchecked((ulong)action.FileId) || !lookup.References.TryGetValue(action.FileId, out var reference)) throw new InvalidDataException("The assisted repair plan no longer matches the patch.");
                var data = source[(int)entry.MainOffset..(int)(entry.MainOffset + entry.MainSize)];
                replacements.Add(new(entry.MainOffset, entry.MainSize, TryBuildUpdatedUnitData(data, reference, action.LodStrategy, out var error) ?? throw new InvalidDataException(error), (int)entry.EntryIndex));
                continue;
            }
            if (materialByEntry.TryGetValue((int)entry.EntryIndex, out var material))
            {
                var materialData = source[(int)entry.MainOffset..(int)(entry.MainOffset + entry.MainSize)];
                byte[] updated;
                if (material.Kind == AssistedMaterialRepairKind.LegacyEmissiveSchema)
                {
                    if (!TryBuildLegacyEmissiveMaterialMigration(materialData, out var migrated))
                        throw new InvalidDataException("The emissive material migration no longer matches the patch.");
                    updated = migrated;
                }
                else
                {
                    if (BinaryPrimitives.ReadUInt64LittleEndian(materialData.AsSpan(ParentIdOffset)) != material.OldParentMaterialId)
                        throw new InvalidDataException("The material migration no longer matches the patch.");
                    updated = materialData.ToArray();
                    BinaryPrimitives.WriteUInt64LittleEndian(updated.AsSpan(ParentIdOffset), material.NewParentMaterialId);
                }
                replacements.Add(new(entry.MainOffset, entry.MainSize, updated, (int)entry.EntryIndex));
            }
        }
        replacements.Sort((left, right) => left.OriginalOffset.CompareTo(right.OriginalOffset));
        using var output = new MemoryStream();
        int cursor = 0;
        foreach (var replacement in replacements)
        {
            int start = checked((int)replacement.OriginalOffset);
            output.Write(source, cursor, start - cursor);
            output.Write(replacement.UpdatedData);
            cursor = checked(start + (int)replacement.OriginalSize);
        }
        output.Write(source, cursor, source.Length - cursor);
        var result = output.ToArray();
        foreach (var entry in snapshot.Entries)
        {
            var tableOffset = HeaderSize + snapshot.Header.TypeCount * TypeEntrySize + ((int)entry.Index - 1) * FileEntrySize;
            long adjustment = replacements.Where(item => item.OriginalOffset < entry.MainOffset).Sum(item => (long)item.UpdatedData.Length - item.OriginalSize);
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(tableOffset + 16), (ulong)((long)entry.MainOffset + adjustment));
            var replacement = replacements.FirstOrDefault(item => item.EntryIndex == entry.EntryIndex);
            if (replacement.UpdatedData is not null) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(tableOffset + 56), (uint)replacement.UpdatedData.Length);
        }
        await File.WriteAllBytesAsync(temporary, result, cancellationToken);
        await using var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(true);
    }

    private async Task<List<AssistedMaterialRepairAction>> CreateMaterialActionsAsync(FileInfo file, PatchFileSnapshot snapshot, List<string> blockers, CancellationToken cancellationToken)
    {
        var actions = new List<AssistedMaterialRepairAction>();
        await using var stream = Open(file);
        foreach (var entry in snapshot.Entries.Where(entry => entry.TypeId == MaterialTypeId))
        {
            if (entry.MainSize > 1024 * 1024) { blockers.Add($"{file.Name}: Material #{entry.EntryIndex} is too large for parent migration"); continue; }
            var data = new byte[entry.MainSize];
            if (!await ReadAtAsync(stream, (long)entry.MainOffset, data, cancellationToken)) { blockers.Add($"{file.Name}: Material #{entry.EntryIndex} cannot be read"); continue; }
            var parentId = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(ParentIdOffset));
            var textureCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(TextureCountOffset));
            if (parentId == LegacyCharacterParentId && textureCount == (uint)CharacterSemantics.Length && HasExactSemantics(data, CharacterSemantics))
            {
                actions.Add(new(file.FullName, (int)entry.EntryIndex, unchecked((long)entry.FileId), AssistedMaterialRepairKind.ParentReference, parentId, CurrentCharacterParentId));
                continue;
            }
            if (TryBuildLegacyEmissiveMaterialMigration(data, out _))
                actions.Add(new(file.FullName, (int)entry.EntryIndex, unchecked((long)entry.FileId), AssistedMaterialRepairKind.LegacyEmissiveSchema, LegacyEmissiveParentId, CurrentEmissiveParentId));
        }
        return actions;
    }

    private static bool HasExactSemantics(ReadOnlySpan<byte> data, ReadOnlySpan<uint> expected)
    {
        if (data.Length < TextureTableOffset || BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(TextureCountOffset, sizeof(uint))) != (uint)expected.Length) return false;
        for (var index = 0; index < expected.Length; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(TextureTableOffset + index * sizeof(uint), sizeof(uint))) != expected[index]) return false;
        }
        return true;
    }

    internal static bool TryBuildLegacyEmissiveMaterialMigration(ReadOnlySpan<byte> materialData, out byte[] updatedData)
    {
        updatedData = [];
        if (materialData.Length != LegacyEmissiveMaterialSize ||
            BinaryPrimitives.ReadUInt64LittleEndian(materialData.Slice(ParentIdOffset, sizeof(ulong))) != LegacyEmissiveParentId ||
            !HasExactSemantics(materialData, LegacyEmissiveSemantics) ||
            BinaryPrimitives.ReadUInt32LittleEndian(materialData.Slice(VariableCountOffset, sizeof(uint))) != (uint)LegacyEmissiveVariables.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(materialData.Slice(VariableDataSizeOffset, sizeof(uint))) != 60)
        {
            return false;
        }

        int sourceTextureIds = GetTextureIdOffset(checked((int)LegacyEmissiveSemantics.Length));
        int sourceDescriptors = checked(sourceTextureIds + (int)LegacyEmissiveSemantics.Length * sizeof(ulong));
        int sourceValues = checked(sourceDescriptors + LegacyEmissiveVariables.Length * VariableDescriptorSize);
        if (sourceValues + 60 != materialData.Length ||
            !HasExactScalarVariableLayout(materialData, sourceDescriptors, LegacyEmissiveVariables, 60))
        {
            return false;
        }

        updatedData = new byte[CurrentEmissiveMaterialSize];
        materialData[..TextureTableOffset].CopyTo(updatedData);
        BinaryPrimitives.WriteUInt32LittleEndian(updatedData.AsSpan(0), CurrentEmissiveMaterialVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(updatedData.AsSpan(12), CurrentEmissiveMaterialEndOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(updatedData.AsSpan(ParentIdOffset), CurrentEmissiveParentId);
        BinaryPrimitives.WriteUInt32LittleEndian(updatedData.AsSpan(TextureCountOffset), (uint)CurrentEmissiveSemantics.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(updatedData.AsSpan(VariableCountOffset), (uint)CurrentEmissiveVariables.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(updatedData.AsSpan(VariableDataSizeOffset), 56);
        for (var index = 0; index < CurrentEmissiveSemantics.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(updatedData.AsSpan(TextureTableOffset + index * sizeof(uint)), CurrentEmissiveSemantics[index]);

        int targetTextureIds = GetTextureIdOffset(CurrentEmissiveSemantics.Length);
        for (var index = 0; index < LegacyEmissiveSemantics.Length; index++)
        {
            var textureId = BinaryPrimitives.ReadUInt64LittleEndian(materialData.Slice(sourceTextureIds + index * sizeof(ulong), sizeof(ulong)));
            BinaryPrimitives.WriteUInt64LittleEndian(updatedData.AsSpan(targetTextureIds + index * sizeof(ulong)), textureId);
        }
        BinaryPrimitives.WriteUInt64LittleEndian(
            updatedData.AsSpan(targetTextureIds + LegacyEmissiveSemantics.Length * sizeof(ulong)),
            CurrentEmissiveOpacityTextureId);

        var sourceValuesById = new Dictionary<uint, byte[]>();
        foreach (var variable in LegacyEmissiveVariables)
            sourceValuesById.Add(variable.Id, materialData.Slice(checked(sourceValues + (int)variable.Offset), sizeof(uint)).ToArray());
        int targetDescriptors = targetTextureIds + checked((int)CurrentEmissiveSemantics.Length) * sizeof(ulong);
        int targetValues = targetDescriptors + CurrentEmissiveVariables.Length * VariableDescriptorSize;
        for (var index = 0; index < CurrentEmissiveVariables.Length; index++)
        {
            var variable = CurrentEmissiveVariables[index];
            var descriptor = targetDescriptors + index * VariableDescriptorSize;
            BinaryPrimitives.WriteUInt32LittleEndian(updatedData.AsSpan(descriptor + 8), variable.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(updatedData.AsSpan(descriptor + 12), variable.Offset);
            if (variable.Id != CurrentEmissiveRangeVariableId && sourceValuesById.TryGetValue(variable.Id, out var value))
            {
                value.CopyTo(updatedData, targetValues + variable.Offset);
                continue;
            }
            float defaultValue = variable.Id switch
            {
                CurrentEmissiveOpacityThresholdVariableId => 0.144f,
                CurrentEmissiveRangeVariableId => 1f,
                _ => throw new InvalidDataException("The current emissive material layout has an unmapped variable."),
            };
        BinaryPrimitives.WriteInt32LittleEndian(
            updatedData.AsSpan(targetValues + checked((int)variable.Offset)),
            BitConverter.SingleToInt32Bits(defaultValue));
        }
        return true;
    }

    private static int GetTextureIdOffset(int textureCount) => checked(TextureTableOffset + textureCount * sizeof(uint));

    private static bool HasExactScalarVariableLayout(ReadOnlySpan<byte> materialData, int descriptorOffset, EmissiveVariable[] expected, int variableDataSize)
    {
        int valuesOffset = checked(descriptorOffset + expected.Length * VariableDescriptorSize);
        if (descriptorOffset < 0 || valuesOffset + variableDataSize > materialData.Length) return false;
        for (var index = 0; index < expected.Length; index++)
        {
            var offset = descriptorOffset + index * VariableDescriptorSize;
            var item = expected[index];
            if (BinaryPrimitives.ReadUInt32LittleEndian(materialData.Slice(offset, sizeof(uint))) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(materialData.Slice(offset + 4, sizeof(uint))) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(materialData.Slice(offset + 8, sizeof(uint))) != item.Id ||
                BinaryPrimitives.ReadUInt32LittleEndian(materialData.Slice(offset + 12, sizeof(uint))) != item.Offset ||
                BinaryPrimitives.ReadUInt32LittleEndian(materialData.Slice(offset + 16, sizeof(uint))) != 0 ||
                item.Offset > variableDataSize - sizeof(uint))
            {
                return false;
            }
        }
        return true;
    }

    private static byte[]? TryBuildUpdatedUnitData(byte[] original, GameUnitReference reference, AssistedLodStrategy strategy, out string error)
    {
        error = string.Empty;
        if (original.Length < 0x78) { error = "header is too small"; return null; }
        uint lodStart = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(0x30));
        uint lodEnd = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(0x34));
        uint ending = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(0x60));
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(0x2C));
        if (lodStart < 0x68 || lodEnd <= lodStart || lodEnd > original.Length || ending + 8 != original.Length) { error = "has invalid LOD or ending offsets"; return null; }
        byte[] working = version < LayoutThreshold ? UpgradeLegacyLayouts(original, out error) : original.ToArray();
        if (working is null) return null;
        if (strategy == AssistedLodStrategy.PreserveMod)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(working.AsSpan(0x2C), reference.Version);
            return working;
        }

        int difference = reference.LodGroupData.Length - checked((int)(lodEnd - lodStart));
        if (difference % 16 != 0) { error = "game LOD size change would break 16-byte alignment"; return null; }
        byte[] updated = new byte[working.Length + difference];
        Buffer.BlockCopy(working, 0, updated, 0, (int)lodStart);
        Buffer.BlockCopy(reference.LodGroupData, 0, updated, (int)lodStart, reference.LodGroupData.Length);
        Buffer.BlockCopy(working, (int)lodEnd, updated, (int)lodStart + reference.LodGroupData.Length, working.Length - (int)lodEnd);
        BinaryPrimitives.WriteUInt32LittleEndian(updated.AsSpan(0x2C), reference.Version);
        for (int offset = 0x34; offset <= 0x70; offset += 4)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(updated.AsSpan(offset));
            if (value != 0 && value > lodStart) BinaryPrimitives.WriteUInt32LittleEndian(updated.AsSpan(offset), checked(value + (uint)difference));
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(updated.AsSpan(0x34)) - lodStart != reference.LodGroupData.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(updated.AsSpan(0x60)) + 8 != updated.Length ||
            !updated.AsSpan((int)lodStart, reference.LodGroupData.Length).SequenceEqual(reference.LodGroupData))
        {
            error = "failed its game LOD post-transform checks"; return null;
        }
        return updated;
    }

    private static byte[] UpgradeLegacyLayouts(byte[] source, out string error)
    {
        error = string.Empty;
        byte[] data = source.ToArray();
        uint listOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x5C));
        if (listOffset > data.Length - 4) { error = "legacy Layout list is out of bounds"; return []; }
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((int)listOffset));
        if (count < 0 || count > 100 || (long)listOffset + 4L + count * sizeof(uint) > data.Length) { error = "legacy Layout table is invalid"; return []; }
        for (int index = 0; index < count; index++)
        {
            uint relative = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)listOffset + 4 + index * sizeof(uint)));
            long layoutStart = listOffset + relative;
            if (layoutStart + 8 + 16 * 20 > data.Length) { error = "legacy Layout entry is out of bounds"; return []; }
            for (int item = 0; item < 16; item++)
            {
                int formatOffset = checked((int)layoutStart + 12 + item * 20);
                uint format = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(formatOffset));
                if (format > 16) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(formatOffset), format + 4);
            }
        }
        return data;
    }

    private static uint[] ReadMeshIds(byte[] data)
    {
        if (data.Length < 0x68) return [];
        uint infoOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x64));
        if (infoOffset > data.Length - 4) return [];
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((int)infoOffset));
        long idOffset = infoOffset + 4L + count * 4L;
        if (count < 0 || count > 4096 || idOffset + count * sizeof(uint) > data.Length) return [];
        uint[] result = new uint[count];
        for (int index = 0; index < count; index++) result[index] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)idOffset + index * sizeof(uint)));
        return result;
    }

    private static ModelPreviewCustomizationInfo TryReadCustomization(byte[] data)
    {
        if (data.Length < 0x50) return new(ModelPreviewBodyShape.Unknown, ModelPreviewCustomizationSlot.Unknown);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x4C));
        if (offset == 0 || offset > data.Length - 28) return new(ModelPreviewBodyShape.Unknown, ModelPreviewCustomizationSlot.Unknown);
        int cursor = (int)offset + 24;
        if (cursor + 4 > data.Length) return new(ModelPreviewBodyShape.Unknown, ModelPreviewCustomizationSlot.Unknown);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor));
        cursor += 4;
        if (length > 1024 || cursor + length > data.Length) return new(ModelPreviewBodyShape.Unknown, ModelPreviewCustomizationSlot.Unknown);
        string bodyType = Encoding.UTF8.GetString(data, cursor, (int)length).TrimEnd('\0');
        string tail = Encoding.UTF8.GetString(data, cursor + (int)length, Math.Min(4096, data.Length - cursor - (int)length));
        int prefix = tail.IndexOf("HelldiverCustomizationSlot_", StringComparison.OrdinalIgnoreCase);
        ModelPreviewCustomizationSlot slot = prefix < 0 ? ModelPreviewCustomizationSlot.Unknown : ModelPreviewBodyShapeParser.ParseSlot(tail[prefix..].Split('\0')[0]);
        return new(ModelPreviewBodyShapeParser.Parse(bodyType), slot);
    }

    private static bool CanUse(FileInfo file, PatchFileAnalysis analysis, List<string> blockers)
    {
        bool valid = analysis.HeaderValid && analysis.FileEntriesInBounds && analysis.MainDataBoundsValid && analysis.EntryIndicesValid &&
            (!analysis.RequiresGpuResources || analysis.HasGpuResources) && (!analysis.RequiresStream || analysis.HasStream) &&
            analysis.GpuResourceBoundsValid && analysis.StreamBoundsValid && analysis.UnitDetails.All(unit =>
                unit.UnitDataInBounds && unit.DeclaredSizeMatchesInternal && unit.LodGroupInBounds);
        if (!valid) blockers.Add($"{file.Name}: structural metadata must pass safe repair before assisted repair");
        return valid;
    }

    private static bool IsValid(PatchFileAnalysis analysis) => analysis.HealthStatus != PatchHealthStatus.Corrupted &&
        analysis.HeaderValid && analysis.FileEntriesInBounds && analysis.TypeDistributionValid && analysis.MainDataBoundsValid &&
        analysis.EntryIndicesValid && (!analysis.RequiresGpuResources || analysis.HasGpuResources) &&
        (!analysis.RequiresStream || analysis.HasStream) && analysis.GpuResourceBoundsValid && analysis.StreamBoundsValid &&
        analysis.UnitDetails.All(unit => unit.UnitDataInBounds && unit.DeclaredSizeMatchesInternal && unit.LodGroupInBounds);

    private static FileInfo[] EnumeratePatches(DirectoryInfo directory) => directory.EnumerateFiles("*", SearchOption.AllDirectories).Where(file => PatchFileRules.IsMainPatchFile(file.Name)).ToArray();

    private static DirectoryInfo? FindGameData()
    {
        string path = Environment.GetEnvironmentVariable("HELLDIVERS2_DATA_PATH") ?? Path.Combine("C:", "Program Files (x86)", "Steam", "steamapps", "common", "Helldivers 2", "data");
        return new DirectoryInfo(path);
    }

    private static FileStream Open(FileInfo file) => new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess);

    private static async Task<bool> ReadAtAsync(Stream stream, long offset, Memory<byte> target, CancellationToken token)
    {
        try { stream.Seek(offset, SeekOrigin.Begin); await stream.ReadExactlyAsync(target, token); return true; }
        catch (EndOfStreamException) { return false; }
    }

    private static string CreateBackupPath(FileInfo file, string stamp)
    {
        string name = file.Name.Replace(".patch_", ".patch-backup_", StringComparison.OrdinalIgnoreCase);
        string candidate = Path.Combine(file.DirectoryName!, $"{name}.{stamp}.hd2mm-backup");
        for (int suffix = 1; File.Exists(candidate); suffix++) candidate = Path.Combine(file.DirectoryName!, $"{name}.{stamp}-{suffix}.hd2mm-backup");
        return candidate;
    }

    private static uint Crc32(FileInfo file)
    {
        uint crc = 0xFFFFFFFFu;
        using FileStream stream = file.OpenRead();
        Span<byte> buffer = stackalloc byte[81920];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            foreach (byte value in buffer[..read])
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u * (crc & 1));
            }
        }
        return ~crc;
    }

    private readonly record struct ResourceReplacement(ulong OriginalOffset, uint OriginalSize, byte[] UpdatedData, int EntryIndex);
    private readonly record struct EmissiveVariable(uint Id, uint Offset);
}






