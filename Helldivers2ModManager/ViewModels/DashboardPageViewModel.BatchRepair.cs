using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using System.Text;
using System.Windows;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class DashboardPageViewModel
{
    public bool IsBatchRepairEnabled =>
        _settingsService.Initialized && _settingsService.EnableBatchRepair;

    [ObservableProperty]
    private bool _isBatchRepairing;

    partial void OnInitializedChanged(bool value) =>
        OnPropertyChanged(nameof(IsBatchRepairEnabled));

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task BatchRepair()
    {
        if (!IsBatchRepairEnabled || IsBatchRepairing || _mods.Count == 0 ||
            Application.Current is not App app ||
            app.Host?.Services?.GetService(typeof(VersionCheckService)) is not VersionCheckService service ||
            app.Host.Services.GetService(typeof(RepairDisclaimerService)) is not RepairDisclaimerService disclaimerService)
        {
            return;
        }

        if (!disclaimerService.ContinueOrRequest(() => _ = BatchRepair()))
            return;

        IsBatchRepairing = true;
        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
        {
            Title = _localizationService["VersionCheckBatch.ScanTitle"],
            Message = _localizationService["VersionCheckBatch.ScanMessage"]
        });
        try
        {
            var scanned = 0;
            var modDatas = _mods.Select(static viewModel => viewModel.Data).ToList();
            var progress = new Progress<BatchModRepairItem>(item =>
            {
                scanned++;
                WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
                {
                    Title = _localizationService["VersionCheckBatch.ScanTitle"],
                    Message = _localizationService["VersionCheckBatch.ScanProgress"]
                        .Replace("{current}", scanned.ToString())
                        .Replace("{total}", modDatas.Count.ToString())
                        .Replace("{name}", item.ModName)
                });
            });
            // 扫描是 CPU/IO 密集操作，由 BackgroundTaskService 统一在后台线程执行并管理任务状态。
            var plan = await _backgroundTaskService.RunAsync(
                _localizationService["BackgroundTasksPage.TaskTypeBatchRepair"],
                _localizationService["VersionCheckBatch.ScanTitle"],
                (_, _) => service.CreateBatchRepairPlanAsync(modDatas, progress),
                _localizationService["VersionCheckBatch.ScanTitle"]);
            WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
            if (plan.RepairableCount == 0)
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
                {
                    Message = BuildBatchPlanSummary(plan)
                });
                return;
            }

            WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
            {
                Title = _localizationService["VersionCheckBatch.ConfirmTitle"],
                Message = _localizationService["VersionCheckBatch.ConfirmMessage"]
                    .Replace("{repairable}", plan.RepairableCount.ToString())
                    .Replace("{unsupported}", plan.UnsupportedCount.ToString())
                    .Replace("{blocked}", plan.BlockedCount.ToString())
                    .Replace("{clean}", plan.NoActionCount.ToString()),
                Confirm = () => _ = ExecuteBatchRepairAsync(service, plan)
            });
        }
        catch (Exception ex)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = _localizationService["VersionCheckBatch.ScanFailed"]
                    .Replace("{message}", ex.Message)
            });
        }
        finally
        {
            IsBatchRepairing = false;
        }
    }

    private async Task ExecuteBatchRepairAsync(
        VersionCheckService service,
        BatchModRepairPlan plan)
    {
        if (IsBatchRepairing)
            return;

        IsBatchRepairing = true;
        var processed = 0;
        var repairableCount = plan.RepairableCount;
        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
        {
            Title = _localizationService["VersionCheckBatch.RepairTitle"],
            Message = _localizationService["VersionCheckBatch.RepairMessage"]
        });
        try
        {
            var progress = new Progress<BatchModRepairItem>(item =>
            {
                processed++;
                WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
                {
                    Title = _localizationService["VersionCheckBatch.RepairTitle"],
                    Message = _localizationService["VersionCheckBatch.RepairProgress"]
                        .Replace("{current}", processed.ToString())
                        .Replace("{total}", repairableCount.ToString())
                        .Replace("{name}", item.ModName)
                });
            });
            // 修复（备份/写文件/哈希/游戏参考解析）由 BackgroundTaskService 统一在后台线程执行。
            var result = await _backgroundTaskService.RunAsync(
                _localizationService["BackgroundTasksPage.TaskTypeBatchRepair"],
                _localizationService["VersionCheckBatch.RepairTitle"],
                (_, _) => service.RepairModsBatchAsync(plan, progress),
                _localizationService["VersionCheckBatch.RepairTitle"]);
            WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
            WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
            {
                Message = BuildBatchResultSummary(result)
            });
            await CheckVersionCompatibility();
        }
        catch (Exception ex)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = _localizationService["VersionCheckBatch.RepairFailed"]
                    .Replace("{message}", ex.Message)
            });
        }
        finally
        {
            IsBatchRepairing = false;
        }
    }

    private string BuildBatchPlanSummary(BatchModRepairPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine(_localizationService["VersionCheckBatch.PlanSummary"]
            .Replace("{repairable}", plan.RepairableCount.ToString())
            .Replace("{unsupported}", plan.UnsupportedCount.ToString())
            .Replace("{blocked}", plan.BlockedCount.ToString())
            .Replace("{clean}", plan.NoActionCount.ToString()));
        AppendBatchIssues(builder, plan.Items);
        return builder.ToString().TrimEnd();
    }

    private string BuildBatchResultSummary(BatchModRepairResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(_localizationService["VersionCheckBatch.ResultSummary"]
            .Replace("{repaired}", result.RepairedCount.ToString())
            .Replace("{failed}", result.FailedCount.ToString())
            .Replace("{skipped}", result.SkippedCount.ToString()));
        AppendBatchIssues(builder, result.Items);
        return builder.ToString().TrimEnd();
    }

    private static void AppendBatchIssues(
        StringBuilder builder,
        IEnumerable<BatchModRepairItem> items)
    {
        var issues = items
            .Where(item => item.State is BatchModRepairState.SkippedUnsupported or BatchModRepairState.Blocked or BatchModRepairState.Failed)
            .Take(20)
            .ToList();
        if (issues.Count == 0)
            return;
        builder.AppendLine();
        foreach (var item in issues)
            builder.AppendLine($"{item.ModName}: {item.Message}");
    }
}
