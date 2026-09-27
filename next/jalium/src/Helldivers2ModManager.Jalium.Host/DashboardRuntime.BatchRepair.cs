using System.Text;
using Helldivers2ModManager.Models;
using Jalium.UI;
using Jalium.UI.Controls;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class DashboardRuntime
{
    private bool _batchRepairActive;

    private async Task BatchRepairAsync()
    {
        if (_batchRepairActive || _modOperationActive || !settings.EnableBatchRepair
            || workspace.Mods.Count == 0 || _tasks is null || _versionService is null)
            return;
        if (settings.IsReadonly)
            throw new InvalidOperationException(localization["VersionCheckDisclaimer.SettingsUnavailable"]);
        if (!settings.RepairDisclaimerAccepted)
        {
            if (!await ConfirmBatch(localization["VersionCheckDisclaimer.Title"],
                    localization["VersionCheckDisclaimer.Message"]))
                return;
            settings.RepairDisclaimerAccepted = true;
            try { await settings.SaveAsync(); }
            catch
            {
                settings.RepairDisclaimerAccepted = false;
                throw;
            }
        }

        _batchRepairActive = true;
        try
        {
            var mods = workspace.Mods.ToArray();
            var plan = await RunBatchStageAsync(
                localization["VersionCheckBatch.ScanTitle"],
                localization["VersionCheckBatch.ScanMessage"], mods.Length,
                (progress, token) => _versionService.CreateBatchRepairPlanAsync(mods, progress, token));
            if (plan.RepairableCount == 0)
            {
                ShowBatchReport(localization["VersionCheckBatch.ScanTitle"],
                    BuildBatchPlanSummary(plan));
                return;
            }

            var confirmation = localization["VersionCheckBatch.ConfirmMessage"]
                .Replace("{repairable}", plan.RepairableCount.ToString())
                .Replace("{unsupported}", plan.UnsupportedCount.ToString())
                .Replace("{blocked}", plan.BlockedCount.ToString())
                .Replace("{clean}", plan.NoActionCount.ToString());
            if (!await ConfirmBatch(localization["VersionCheckBatch.ConfirmTitle"], confirmation))
                return;

            CancelConflictScan();
            CancelVersionScan();
            await _conflictIdleTask;
            await _versionIdleTask;
            var result = await RunBatchStageAsync(
                localization["VersionCheckBatch.RepairTitle"],
                localization["VersionCheckBatch.RepairMessage"], plan.RepairableCount,
                (progress, token) => _versionService.RepairModsBatchAsync(plan, progress, token));
            ShowBatchReport(localization["VersionCheckBatch.RepairTitle"],
                BuildBatchResultSummary(result));
            await workspace.RefreshAsync();
            await ScanVersionsAsync(afterRepair: true);
        }
        finally
        {
            _batchRepairActive = false;
            RequestAutomaticConflictScan();
            RequestAutomaticVersionCheck();
        }
    }

    private async Task<T> RunBatchStageAsync<T>(string title, string description, int count,
        Func<IProgress<BatchModRepairItem>, CancellationToken, Task<T>> operation)
    {
        var messageBox = _messageBoxOverlay;
        if (messageBox is null)
            throw new InvalidOperationException("Message overlay is not ready.");
        messageBox.ShowProgress(title, description);
        var completed = 0;
        var progress = new Progress<BatchModRepairItem>(item =>
        {
            completed++;
            messageBox.UpdateProgress(item.ModName,
                count == 0 ? 1 : Math.Min(1, (double)completed / count));
        });
        try
        {
            return await _tasks!.RunAsync(
                localization["BackgroundTasksPage.TaskTypeBatchRepair"], title,
                (_, token) => operation(progress, token), title, isForeground: true);
        }
        finally
        {
            messageBox.CloseProgress();
        }
    }

    private Task<bool> ConfirmBatch(string title, string message)
        => _messageBoxOverlay!.ConfirmAsync(title, message);

    private void ShowBatchReport(string title, string message)
    {
        _messageBoxOverlay!.ShowInfo(title, message);
    }

    private string BuildBatchPlanSummary(BatchModRepairPlan plan)
    {
        var summary = localization["VersionCheckBatch.PlanSummary"]
            .Replace("{repairable}", plan.RepairableCount.ToString())
            .Replace("{unsupported}", plan.UnsupportedCount.ToString())
            .Replace("{blocked}", plan.BlockedCount.ToString())
            .Replace("{clean}", plan.NoActionCount.ToString());
        return AppendBatchIssues(summary, plan.Items);
    }

    private string BuildBatchResultSummary(BatchModRepairResult result)
    {
        var summary = localization["VersionCheckBatch.ResultSummary"]
            .Replace("{repaired}", result.RepairedCount.ToString())
            .Replace("{failed}", result.FailedCount.ToString())
            .Replace("{skipped}", result.SkippedCount.ToString());
        return AppendBatchIssues(summary, result.Items);
    }

    private static string AppendBatchIssues(string summary, IEnumerable<BatchModRepairItem> items)
    {
        var builder = new StringBuilder(summary);
        foreach (var item in items.Where(item => item.State is
                     BatchModRepairState.SkippedUnsupported or BatchModRepairState.Blocked
                         or BatchModRepairState.Failed).Take(20))
            builder.AppendLine().Append(item.ModName).Append(": ").Append(item.Message);
        return builder.ToString();
    }
}
