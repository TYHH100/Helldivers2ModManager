using System.IO;
using Helldivers2ModManager.Core.Compatibility;
using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class BatchRepairCoordinator(
    ILogger<BatchRepairCoordinator> logger,
    LocalizationService localizationService,
    VersionCheckService legacyAnalyzer,
    IRepairPlanner repairPlanner,
    IRepairExecutor repairExecutor,
    ICompanionRecoveryService companionRecoveryService)
{
    public async Task<BatchModRepairPlan> CreatePlanAsync(
        IEnumerable<ModData> mods,
        IProgress<BatchModRepairItem>? progress,
        CancellationToken cancellationToken)
    {
        var items = new List<BatchModRepairItem>();
        foreach (var mod in mods
                     .GroupBy(item => item.Directory.FullName, StringComparer.OrdinalIgnoreCase)
                     .Select(static group => group.First()))
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
                await PopulatePlanItemAsync(item, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                item.State = BatchModRepairState.Blocked;
                item.Message = ex.Message;
                logger.LogWarning(ex, "Failed to create batch repair plan for {Mod}", item.ModName);
            }
            items.Add(item);
            progress?.Report(item);
        }

        return new BatchModRepairPlan { Items = items };
    }

    public async Task<BatchModRepairResult> ExecuteAsync(
        BatchModRepairPlan plan,
        IProgress<BatchModRepairItem>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var item in plan.Items.Where(static candidate => candidate.State == BatchModRepairState.Repairable))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = new DirectoryInfo(item.ModDirectory);
            var changed = false;
            try
            {
                var companionPlan = await legacyAnalyzer.CreateCompanionRecoveryPlanAsync(directory, cancellationToken);
                if (companionPlan.MissingCount > 0)
                {
                    if (!companionPlan.CanRecover)
                        throw new InvalidDataException(BuildCompanionBlockMessage(companionPlan));
                    var recovery = await companionRecoveryService.RecoverAsync(directory.FullName, cancellationToken);
                    if (!recovery.IsSuccess)
                        throw new InvalidDataException(recovery.ErrorMessage ?? recovery.ErrorCode);
                    item.CompanionRecoveryCount = recovery.Value;
                    changed |= item.CompanionRecoveryCount > 0;
                }

                var metadataPlan = await legacyAnalyzer.CreateRepairPlanAsync(directory, cancellationToken);
                if (metadataPlan.ActionCount > 0 || metadataPlan.BlockingReasons.Count > 0)
                {
                    if (!metadataPlan.CanRepair)
                        throw new InvalidDataException(string.Join(Environment.NewLine, metadataPlan.BlockingReasons));
                    var plans = new List<RepairPlan>();
                    foreach (var patchPath in metadataPlan.Actions
                                 .Select(static action => action.PatchFilePath)
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var planning = await repairPlanner.PlanAsync(patchPath, cancellationToken);
                        if (!planning.IsSuccess || planning.Value is null)
                            throw new InvalidDataException(planning.ErrorMessage ?? planning.ErrorCode);
                        plans.Add(planning.Value);
                    }

                    var repair = await repairExecutor.ExecuteBatchAsync(plans, null, cancellationToken);
                    if (!repair.IsSuccess)
                        throw new InvalidDataException(repair.ErrorMessage ?? repair.ErrorCode);
                    item.MetadataActionCount = plans.Sum(static candidate => candidate.Actions.Count);
                    changed |= item.MetadataActionCount > 0;
                }

                var assistedPlan = await legacyAnalyzer.CreateAutomaticAssistedRepairPlanAsync(directory);
                if (assistedPlan.CanRepair)
                {
                    var assistedResult = await legacyAnalyzer.RepairModAutomaticallyAsync(directory);
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
                    ? localizationService.Format("VersionCheckBatch.ItemRepaired", new
                    {
                        companions = item.CompanionRecoveryCount,
                        metadata = item.MetadataActionCount,
                        units = item.AssistedActionCount
                    })
                    : localizationService["VersionCheckBatch.NoRepairAfterRefresh"];
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                item.State = BatchModRepairState.Failed;
                item.Message = ex.Message;
                logger.LogError(ex, "Batch repair failed for {Mod}", item.ModName);
            }
            progress?.Report(item);
        }

        return new BatchModRepairResult { Items = plan.Items };
    }

    private async Task PopulatePlanItemAsync(BatchModRepairItem item, CancellationToken cancellationToken)
    {
        var directory = new DirectoryInfo(item.ModDirectory);
        if (!directory.Exists)
        {
            item.State = BatchModRepairState.Blocked;
            item.Message = localizationService["VersionCheckBatch.ModDirectoryMissing"];
            return;
        }

        var companionPlan = await legacyAnalyzer.CreateCompanionRecoveryPlanAsync(directory, cancellationToken);
        if (companionPlan.MissingCount > 0)
        {
            item.CompanionRecoveryCount = companionPlan.RecoverableCount;
            item.State = companionPlan.CanRecover ? BatchModRepairState.Repairable : BatchModRepairState.Blocked;
            item.Message = companionPlan.CanRecover
                ? localizationService.Format("VersionCheckBatch.CompanionAvailable", new { count = companionPlan.RecoverableCount })
                : BuildCompanionBlockMessage(companionPlan);
            return;
        }

        var metadataPlan = await legacyAnalyzer.CreateRepairPlanAsync(directory, cancellationToken);
        item.MetadataActionCount = metadataPlan.ActionCount;
        if (metadataPlan.ActionCount > 0 || metadataPlan.BlockingReasons.Count > 0)
        {
            item.State = metadataPlan.CanRepair ? BatchModRepairState.Repairable : BatchModRepairState.Blocked;
            item.Message = metadataPlan.CanRepair
                ? localizationService.Format("VersionCheckBatch.MetadataAvailable", new { count = metadataPlan.ActionCount })
                : string.Join(Environment.NewLine, metadataPlan.BlockingReasons);
            return;
        }

        var assistedPlan = await legacyAnalyzer.CreateAutomaticAssistedRepairPlanAsync(directory);
        item.AssistedActionCount = assistedPlan.ActionCount;
        if (assistedPlan.CanRepair)
        {
            item.State = BatchModRepairState.Repairable;
            item.Message = localizationService.Format("VersionCheckBatch.UnitAvailable", new { count = assistedPlan.ActionCount });
        }
        else if (assistedPlan.BlockingReasons.Count > 0)
        {
            item.State = BatchModRepairState.Blocked;
            item.Message = string.Join(Environment.NewLine, assistedPlan.BlockingReasons);
        }
        else
        {
            item.State = BatchModRepairState.NoAction;
            item.Message = localizationService["VersionCheckBatch.NoRepair"];
        }
    }

    private static string BuildCompanionBlockMessage(CompanionRecoveryPlan plan)
    {
        var reasons = plan.Items
            .Where(static item => item.IsMissing && !item.CanRecover)
            .Select(static item => $"{Path.GetFileName(item.CompanionPath)}: {item.Reason}")
            .ToList();
        return reasons.Count > 0
            ? string.Join(Environment.NewLine, reasons)
            : "Required companion data cannot be recovered.";
    }
}
