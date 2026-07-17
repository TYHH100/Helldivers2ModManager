using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Core.UI;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using System.Text;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class DashboardPageViewModel
{
    public bool IsBatchRepairEnabled =>
        _settingsService.Initialized &&
        _settingsService.EnableExperimentalRepair &&
        _settingsService.EnableBatchRepair;

    [ObservableProperty]
    private bool _isBatchRepairing;

    partial void OnInitializedChanged(bool value) =>
        OnPropertyChanged(nameof(IsBatchRepairEnabled));

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task BatchRepair(CancellationToken cancellationToken)
    {
        if (!IsBatchRepairEnabled || IsBatchRepairing || _mods.Count == 0)
        {
            return;
        }

        if (!await _repairDisclaimerService.EnsureAcceptedAsync(cancellationToken))
            return;

        IsBatchRepairing = true;
        await using var progressDialog = await _dialogService.OpenProgressAsync(
            new ProgressDialogRequest(
                _localizationService["VersionCheckBatch.ScanTitle"],
                _localizationService["VersionCheckBatch.ScanMessage"]),
            cancellationToken);
        try
        {
            var scanned = 0;
            var progress = new Progress<BatchModRepairItem>(item =>
            {
                scanned++;
                progressDialog.Report(new ProgressDialogRequest(
                    _localizationService["VersionCheckBatch.ScanTitle"],
                    _localizationService.Format("VersionCheckBatch.ScanProgress", new { current = scanned, total = _mods.Count, name = item.ModName })));
            });
            var plan = await _batchRepairCoordinator.CreatePlanAsync(
                _mods.Select(static viewModel => viewModel.Data),
                progress,
                cancellationToken);
            await progressDialog.CloseAsync(cancellationToken);
            if (plan.RepairableCount == 0)
            {
                await ShowDashboardMessageAsync(
                    BuildBatchPlanSummary(plan),
                    MessageDialogSeverity.Information,
                    cancellationToken);
                return;
            }

            if (await _dialogService.ShowAsync(
                new Helldivers2ModManager.Core.UI.DialogRequest(
                    _localizationService["VersionCheckBatch.ConfirmTitle"],
                    _localizationService.Format("VersionCheckBatch.ConfirmMessage", new { repairable = plan.RepairableCount, blocked = plan.BlockedCount, clean = plan.NoActionCount })),
                cancellationToken))
                await ExecuteBatchRepairAsync(plan, cancellationToken);
        }
        catch (Exception ex)
        {
            await progressDialog.CloseAsync(CancellationToken.None);
            await ShowDashboardMessageAsync(
                _localizationService.Format("VersionCheckBatch.ScanFailed", new { message = ex.Message }),
                MessageDialogSeverity.Error,
                CancellationToken.None);
        }
        finally
        {
            IsBatchRepairing = false;
        }
    }

    private async Task ExecuteBatchRepairAsync(
        BatchModRepairPlan plan,
        CancellationToken cancellationToken)
    {
        var processed = 0;
        await using var progressDialog = await _dialogService.OpenProgressAsync(
            new ProgressDialogRequest(
                _localizationService["VersionCheckBatch.RepairTitle"],
                _localizationService["VersionCheckBatch.RepairMessage"]),
            cancellationToken);
        try
        {
            var progress = new Progress<BatchModRepairItem>(item =>
            {
                processed++;
                progressDialog.Report(new ProgressDialogRequest(
                    _localizationService["VersionCheckBatch.RepairTitle"],
                    _localizationService.Format("VersionCheckBatch.RepairProgress", new { current = processed, total = plan.RepairableCount, name = item.ModName })));
            });
            var result = await _batchRepairCoordinator.ExecuteAsync(plan, progress, cancellationToken);
            await progressDialog.CloseAsync(cancellationToken);
            await ShowDashboardMessageAsync(
                BuildBatchResultSummary(result),
                MessageDialogSeverity.Information,
                cancellationToken);
            await CheckVersionCompatibility();
        }
        catch (Exception ex)
        {
            await progressDialog.CloseAsync(CancellationToken.None);
            await ShowDashboardMessageAsync(
                _localizationService.Format("VersionCheckBatch.RepairFailed", new { message = ex.Message }),
                MessageDialogSeverity.Error,
                CancellationToken.None);
        }
    }

    private Task ShowDashboardMessageAsync(
        string message,
        MessageDialogSeverity severity,
        CancellationToken cancellationToken)
    {
        var titleKey = severity == MessageDialogSeverity.Error ? "MessageBox.Error" : "MessageBox.Info";
        return _dialogService.ShowMessageAsync(
            new MessageDialogRequest(_localizationService[titleKey], message, severity),
            cancellationToken);
    }

    private string BuildBatchPlanSummary(BatchModRepairPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine(_localizationService.Format("VersionCheckBatch.PlanSummary", new { repairable = plan.RepairableCount, blocked = plan.BlockedCount, clean = plan.NoActionCount }));
        AppendBatchIssues(builder, plan.Items);
        return builder.ToString().TrimEnd();
    }

    private string BuildBatchResultSummary(BatchModRepairResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(_localizationService.Format("VersionCheckBatch.ResultSummary", new { repaired = result.RepairedCount, failed = result.FailedCount, skipped = result.SkippedCount }));
        AppendBatchIssues(builder, result.Items);
        return builder.ToString().TrimEnd();
    }

    private static void AppendBatchIssues(
        StringBuilder builder,
        IEnumerable<BatchModRepairItem> items)
    {
        var issues = items
            .Where(item => item.State is BatchModRepairState.Blocked or BatchModRepairState.Failed)
            .Take(20)
            .ToList();
        if (issues.Count == 0)
            return;
        builder.AppendLine();
        foreach (var item in issues)
            builder.AppendLine($"{item.ModName}: {item.Message}");
    }
}
