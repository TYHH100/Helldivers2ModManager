using Helldivers2ModManager.Models;
using Microsoft.Extensions.Logging;
using System.IO;

namespace Helldivers2ModManager.Services;

internal sealed partial class VersionCheckService
{
    public async Task<BatchModRepairPlan> CreateBatchRepairPlanAsync(
        IEnumerable<ModData> mods,
        IProgress<BatchModRepairItem>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_coreBatchRepairService is not null)
        {
            var grouped = mods
                .GroupBy(item => item.Directory.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToArray();
            var coreItems = await _coreBatchRepairService.CreatePlanAsync(
                grouped.Select(static mod => (mod.Manifest.Guid, mod.Directory)),
                cancellationToken).ConfigureAwait(false);

            var mappedItems = new List<BatchModRepairItem>(grouped.Length);
            foreach (var mod in grouped)
            {
                var coreItem = coreItems.FirstOrDefault(item => item.ModId == mod.Manifest.Guid);
                if (coreItem is null)
                    continue;
                var mapped = ToLegacyItem(mod.Manifest.Name, coreItem, _localizationService);
                mappedItems.Add(mapped);
                progress?.Report(mapped);
            }

            return new BatchModRepairPlan { Items = mappedItems };
        }

        var items = new List<BatchModRepairItem>();
        foreach (var mod in mods
                     .GroupBy(item => item.Directory.FullName, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = new BatchModRepairItem
            {
                ModName = mod.Manifest.Name,
                ModDirectory = mod.Directory.FullName,
                State = BatchModRepairState.NoAction
            };
            try
            {
                await PopulateBatchPlanItemAsync(item, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                item.State = BatchModRepairState.Blocked;
                item.Message = ex.Message;
                _logger.LogWarning(ex, "Failed to create batch repair plan for {Mod}", item.ModName);
            }
            items.Add(item);
            progress?.Report(item);
        }

        return new BatchModRepairPlan { Items = items };
    }

    public async Task<BatchModRepairResult> RepairModsBatchAsync(
        BatchModRepairPlan plan,
        IProgress<BatchModRepairItem>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_coreBatchRepairService is not null)
        {
            await _repairSemaphore.WaitAsync(cancellationToken);
            try
            {
                var candidates = plan.Items
                    .Where(candidate => candidate.State == BatchModRepairState.Repairable)
                    .ToArray();
                var maxDegree = Math.Max(1, Math.Min(4, Environment.ProcessorCount));
                await Parallel.ForEachAsync(
                    candidates,
                    new ParallelOptions { MaxDegreeOfParallelism = maxDegree, CancellationToken = cancellationToken },
                    async (item, ct) =>
                    {
                        try
                        {
                            var directory = new DirectoryInfo(item.ModDirectory);
                            var coreItems = await _coreBatchRepairService.CreatePlanAsync(
                                [(item.ModGuid, directory)],
                                ct).ConfigureAwait(false);
                            if (coreItems.Count == 0)
                                return;

                            var results = await _coreBatchRepairService.RepairAsync(coreItems, ct).ConfigureAwait(false);
                            var result = results.Items[0];
                            item.MetadataActionCount = result.MetadataActionCount;
                            item.AssistedActionCount = result.AssistedActionCount;
                            item.CompanionRecoveryCount = result.CompanionRecoveryCount;
                            item.Message = result.Message;
                            item.State = ToLegacyState(result.State);
                            if (result.State == Core.Repair.BatchRepairState.NoAction && result.Message != "No repair was required after the plan was refreshed.")
                                item.State = BatchModRepairState.Repaired;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            item.State = BatchModRepairState.Failed;
                            item.Message = ex.Message;
                            _logger.LogWarning(ex, "Batch repair failed for {Mod}", item.ModName);
                        }

                        progress?.Report(item);
                    });
            }
            finally
            {
                _repairSemaphore.Release();
            }

            return new BatchModRepairResult { Items = plan.Items };
        }

        // 批量修复整体持锁：与外部单个修复互斥，避免并发写同一模组；
        // 内部不同模组之间有限并发（仅操作各自目录，使用不加锁的 Core 变体）。
        await _repairSemaphore.WaitAsync(cancellationToken);
        try
        {
            var candidates = plan.Items
                .Where(candidate => candidate.State == BatchModRepairState.Repairable)
                .ToArray();
            var maxDegree = Math.Max(1, Math.Min(4, Environment.ProcessorCount));
            await Parallel.ForEachAsync(
                candidates,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxDegree,
                    CancellationToken = cancellationToken
                },
                async (item, ct) => await RepairSingleBatchItemAsync(item, ct, progress));
        }
        finally
        {
            _repairSemaphore.Release();
        }

        return new BatchModRepairResult { Items = plan.Items };
    }

    /// <summary>
    /// 执行单个模组的批量修复（companion 恢复 → 安全元数据修复 → 自动修复）。
    /// 调用方必须已持有 _repairSemaphore；内部使用不加锁的 Core 变体，仅操作该模组目录。
    /// </summary>
    private async Task RepairSingleBatchItemAsync(
        BatchModRepairItem item,
        CancellationToken cancellationToken,
        IProgress<BatchModRepairItem>? progress)
    {
        var directory = new DirectoryInfo(item.ModDirectory);
        var changed = false;
        try
        {
            if (!await SupportsAutomaticUnitRepairAsync(directory))
            {
                item.State = BatchModRepairState.SkippedUnsupported;
                item.Message = _localizationService["VersionCheckBatch.UnsupportedResourceType"];
                progress?.Report(item);
                return;
            }

            var companionPlan = await CreateCompanionRecoveryPlanAsync(directory, cancellationToken);
            if (companionPlan.MissingCount > 0)
            {
                if (!companionPlan.CanRecover)
                    throw new InvalidDataException(BuildCompanionBlockMessage(companionPlan));
                var companionResult = await RecoverCompanionFilesCoreAsync(directory, cancellationToken);
                if (!companionResult.Success)
                    throw new InvalidDataException(companionResult.ErrorMessage);
                item.CompanionRecoveryCount = companionResult.RecoveredCount;
                changed |= companionResult.RecoveredCount > 0;
            }

            var metadataPlan = await CreateRepairPlanAsync(directory);
            if (metadataPlan.ActionCount > 0 || metadataPlan.BlockingReasons.Count > 0)
            {
                if (!metadataPlan.CanRepair)
                    throw new InvalidDataException(string.Join(Environment.NewLine, metadataPlan.BlockingReasons));
                var metadataResult = await RepairModCoreAsync(directory);
                if (!metadataResult.Success)
                    throw new InvalidDataException(metadataResult.ErrorMessage);
                item.MetadataActionCount = metadataResult.AppliedActionCount;
                changed |= metadataResult.AppliedActionCount > 0;
            }

            var assistedPlan = await CreateAutomaticAssistedRepairPlanAsync(directory);
            if (assistedPlan.CanRepair)
            {
                var assistedResult = await RepairModWithGameReferencesCoreAsync(
                    directory,
                    () => CreateAutomaticAssistedRepairPlanAsync(directory));
                if (!assistedResult.Success)
                    throw new InvalidDataException(assistedResult.ErrorMessage);
                item.AssistedActionCount = assistedResult.AppliedActionCount;
                changed |= assistedResult.AppliedActionCount > 0;
            }
            else if (assistedPlan.BlockingReasons.Count > 0)
            {
                throw new InvalidDataException(string.Join(Environment.NewLine, assistedPlan.BlockingReasons));
            }

            item.State = changed ? BatchModRepairState.Repaired : BatchModRepairState.NoAction;
            item.Message = changed
                ? $"Recovered {item.CompanionRecoveryCount} companion file(s), applied {item.MetadataActionCount} metadata repair(s) and {item.AssistedActionCount} Unit repair(s)."
                : "No repair was required after the plan was refreshed.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            item.State = BatchModRepairState.Failed;
            item.Message = ex.Message;
            _logger.LogError(ex, "Batch repair failed for {Mod}", item.ModName);
        }
        progress?.Report(item);
    }

    private static BatchModRepairState ToLegacyState(Core.Repair.BatchRepairState state)
    {
        return state switch
        {
            Core.Repair.BatchRepairState.Repairable => BatchModRepairState.Repairable,
            Core.Repair.BatchRepairState.Blocked => BatchModRepairState.Blocked,
            Core.Repair.BatchRepairState.SkippedUnsupported => BatchModRepairState.SkippedUnsupported,
            _ => BatchModRepairState.NoAction
        };
    }

    private static BatchModRepairItem ToLegacyItem(
        string modName,
        Core.Repair.BatchRepairItem item,
        LocalizationService localization)
    {
        var message = item.State == Core.Repair.BatchRepairState.SkippedUnsupported
            ? localization["VersionCheckBatch.UnsupportedResourceType"]
            : item.Message;
        return new BatchModRepairItem
        {
            ModGuid = item.ModId,
            ModName = modName,
            ModDirectory = item.Directory.FullName,
            State = ToLegacyState(item.State),
            Message = message,
            MetadataActionCount = item.MetadataActionCount,
            AssistedActionCount = item.AssistedActionCount,
            CompanionRecoveryCount = item.CompanionRecoveryCount
        };
    }

    private async Task PopulateBatchPlanItemAsync(
        BatchModRepairItem item,
        CancellationToken cancellationToken)
    {
        var directory = new DirectoryInfo(item.ModDirectory);
        if (!directory.Exists)
        {
            item.State = BatchModRepairState.Blocked;
            item.Message = "The mod directory no longer exists.";
            return;
        }

        if (!await SupportsAutomaticUnitRepairAsync(directory))
        {
            item.State = BatchModRepairState.SkippedUnsupported;
            item.Message = _localizationService["VersionCheckBatch.UnsupportedResourceType"];
            return;
        }

        var companionPlan = await CreateCompanionRecoveryPlanAsync(directory, cancellationToken);
        if (companionPlan.MissingCount > 0)
        {
            item.CompanionRecoveryCount = companionPlan.RecoverableCount;
            item.State = companionPlan.CanRecover
                ? BatchModRepairState.Repairable
                : BatchModRepairState.Blocked;
            item.Message = companionPlan.CanRecover
                ? $"{companionPlan.RecoverableCount} missing companion file(s) can be recovered before patch repair."
                : BuildCompanionBlockMessage(companionPlan);
            return;
        }

        var metadataPlan = await CreateRepairPlanAsync(directory);
        item.MetadataActionCount = metadataPlan.ActionCount;
        if (metadataPlan.ActionCount > 0 || metadataPlan.BlockingReasons.Count > 0)
        {
            item.State = metadataPlan.CanRepair
                ? BatchModRepairState.Repairable
                : BatchModRepairState.Blocked;
            item.Message = metadataPlan.CanRepair
                ? $"{metadataPlan.ActionCount} safe metadata repair(s) are available."
                : string.Join(Environment.NewLine, metadataPlan.BlockingReasons);
            return;
        }

        var assistedPlan = await CreateAutomaticAssistedRepairPlanAsync(directory);
        item.AssistedActionCount = assistedPlan.ActionCount;
        if (assistedPlan.CanRepair)
        {
            item.State = BatchModRepairState.Repairable;
            item.Message = $"{assistedPlan.ActionCount} Unit repair(s) are available.";
        }
        else if (assistedPlan.BlockingReasons.Count > 0)
        {
            item.State = BatchModRepairState.Blocked;
            item.Message = string.Join(Environment.NewLine, assistedPlan.BlockingReasons);
        }
        else
        {
            item.State = BatchModRepairState.NoAction;
            item.Message = "No repair is required.";
        }
    }

    /// <summary>
    /// 自动修复当前只验证了 Unit（模型）资源的结构和 LOD 路径。
    /// 音频、材质及其他非 Unit 资源即使拥有合法的 .stream companion，也不能进入
    /// 通用元数据或 companion 恢复流程，以免在支持其语义修复前改写原始模组。
    /// </summary>
    private async Task<bool> SupportsAutomaticUnitRepairAsync(DirectoryInfo directory)
    {
        var analysis = await AnalyzeModPatchFilesAsync(directory);
        return analysis.PatchFiles.Any(patch => patch.UnitDetails.Count > 0);
    }

    private static string BuildCompanionBlockMessage(CompanionRecoveryPlan plan)
    {
        var reasons = plan.Items
            .Where(item => item.IsMissing && !item.CanRecover)
            .Select(item => $"{Path.GetFileName(item.CompanionPath)}: {item.Reason}")
            .ToList();
        return reasons.Count > 0
            ? string.Join(Environment.NewLine, reasons)
            : "Required companion data cannot be recovered.";
    }
}
