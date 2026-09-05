using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GongSolutions.Wpf.DragDrop;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Nexus;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SharpSevenZip;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MessageBox = Helldivers2ModManager.Components.MessageBox;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class DashboardPageViewModel
{
    [RelayCommand]
    void Create()
    {
        _navStore.Value.Navigate<CreatePageViewModel>();
    }

    [RelayCommand]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is a command of a view model and should not be static.")]
    void ReportBug()
    {
        Process.Start(s_reportStartInfo);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task TagManagement()
    {
        await SaveProfileNowAsync();

        _navStore.Value.Navigate<TagManagementPageViewModel>();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task Settings()
    {
        await SaveProfileNowAsync();

        _navStore.Value.Navigate<SettingsPageViewModel>();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task DeploymentOrder()
    {
        await SaveProfileNowAsync();

        _navStore.Value.Navigate<DeploymentOrderPageViewModel>();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task ArmorReuse()
    {
        await SaveProfileNowAsync();

        _navStore.Value.Navigate<ArmorReusePageViewModel>();
    }

    [RelayCommand]
    void PatchResourceViewer()
    {
        _navStore.Value.Navigate<PatchResourceViewerPageViewModel>();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task Bisect()
    {
        await SaveProfileNowAsync();

        _navStore.Value.Navigate<BisectPageViewModel>();
    }

    [RelayCommand]
    void PreviewModel(ModViewModel modVm)
    {
        _navStore.Value.Navigate<ModelPreviewPageViewModel>(page => page.SetInitialMod(modVm.Data));
        WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
        {
            Message = _localizationService["ModelPreviewPage.Disclaimer"]
        });
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task Purge()
    {
        if (!_settingsService.Initialized || string.IsNullOrEmpty(_settingsService.GameDirectory))
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = _localizationService["DashboardPage.PurgeNoGameDir"]
            });
            return;
        }

        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
        {
            Title = _localizationService["DashboardPage.PurgeProgress"],
            Message = _localizationService["SettingsPage.PleaseWait"]
        });

        try
        {
            await _backgroundTaskService.RunAsync(
                _localizationService["BackgroundTasksPage.TaskTypePurge"],
                _localizationService["SettingsPage.PleaseWait"],
                (_, _) => _modService.PurgeAsync(),
                _localizationService["BackgroundTasksPage.PurgeComplete"],
                isForeground: true);

            // 成功：隐藏进度弹窗，改为气泡提示并自动消失（失败仍用错误弹窗保留给用户）
            WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
            WeakReferenceMessenger.Default.Send(new ToastMessage(
                _localizationService["BackgroundTasksPage.TaskTypePurge"],
                _localizationService["BackgroundTasksPage.PurgeComplete"]));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Purge failed");
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = ex.Message
            });
        }
    }

    /// <summary>
    /// 根据当前设置获取按部署顺序排列的主页快照模组（排序逻辑与二分排查共用 DeploymentOrderHelper）
    /// </summary>
    private ModData[] GetDeploymentMods(ProfileSnapshot snapshot)
    {
        return DeploymentOrderHelper.BuildDeploymentMods(
            snapshot,
            _settingsService.UseDeploymentOrder,
            _settingsService.DeploymentOrderGuids,
            _settingsService.DeployBottomToTop);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task Deploy()
    {
        if (!_settingsService.Initialized || string.IsNullOrEmpty(_settingsService.GameDirectory))
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = _localizationService["DashboardPage.DeployNoGameDir"]
            });
            return;
        }

        var snapshot = CaptureProfileSnapshot();
        var deploymentMods = GetDeploymentMods(snapshot);
        BackgroundTaskItem? deployTask = null;

        try
        {
            await _backgroundTaskService.RunAsync(
                _localizationService["DashboardPage.DeployMods"],
                _localizationService["SettingsPage.PleaseWait"],
                async (ctx, _) =>
                {
                    await SaveProfileNowAsync(false, snapshot);
                    await _modService.DeployAsync(deploymentMods, ctx.ReportStep, ctx.ReportStepDetail, ctx.CompleteStep, ctx.FailStep);
                },
                _localizationService["DashboardPage.DeploySuccess"],
                cancellationToken: default,
                // 任务创建后立即弹出进度窗，并把任务的步骤集合挂上：
                // 部署过程中每处理一个模组追加一行（自动滚动列表），显示正在部署哪个模组
                task =>
                {
                    deployTask = task;
                    WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
                    {
                        Title = _localizationService["DashboardPage.DeployProgress"],
                        Message = _localizationService["SettingsPage.PleaseWait"],
                        Steps = task.Steps
                    });
                },
                isForeground: true);

            // 部署成功：弹窗保留步骤列表（全部 ✓），可确认每个模组都已部署
            WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage()
            {
                Message = _localizationService["DashboardPage.DeploySuccess"],
                Steps = deployTask?.Steps
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unknown deployment error");
            // 部署失败：弹窗显示错误并保留步骤列表（出问题的模组标 ✗），
            // 便于定位是哪个模组复制失败
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = ex.Message,
                Steps = deployTask?.Steps
            });
        }
    }

    [RelayCommand]
    async Task RescanMods()
    {
        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
        {
            Title = _localizationService["DashboardPage.RescanMods"],
            Message = _localizationService["SettingsPage.PleaseWait"]
        });

        try
        {
            // 完整刷新（对账目录：新增/更新清单/移除丢失，CPU/IO 密集）：后台线程执行 + 任务状态统一管理。
            // 有加载弹窗，属前台任务，任务页不显示。
            var result = await _backgroundTaskService.RunAsync(
                _localizationService["DashboardPage.RescanMods"],
                _localizationService["SettingsPage.PleaseWait"],
                (_, ct) => _modService.RefreshModsAsync(ct),
                _localizationService["DashboardPage.RescanComplete"],
                isForeground: true);

            WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());

            if (result.Problems.Length > 0)
                ShowProblems(result.Problems, _localizationService["DashboardPage.RescanProblemsPrefix"], false, true);

            if (result.HasChanges)
            {
                RebuildOrderedItems();
                UpdateView();

                if (!_settingsService.IsReadonly)
                {
                    await SaveProfileNowAsync(false);
                }
            }

            // 完成提示改为气泡（自动消失）；有问题时仍用弹窗展示详情
            WeakReferenceMessenger.Default.Send(new ToastMessage(
                _localizationService["DashboardPage.RescanMods"],
                _localizationService["DashboardPage.RescanSummary"]
                    .Replace("{added}", result.AddedCount.ToString())
                    .Replace("{updated}", result.UpdatedCount.ToString())
                    .Replace("{removed}", result.RemovedCount.ToString())));
        }
        catch (Exception ex)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
            _logger.LogError(ex, "Refresh mods failed");
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = ex.Message
            });
        }
    }

    [RelayCommand]
    void MoveUp(ModViewModel modVm)
    {
        var index = _mods.IndexOf(modVm);
        if (index <= 0)
            return;
        _mods.Move(index, index - 1);
    }

    [RelayCommand]
    void MoveDown(ModViewModel modVm)
    {
        var index = _mods.IndexOf(modVm);
        if (index >= _mods.Count - 1)
            return;
        _mods.Move(index, index + 1);
    }

    /// <summary>
    /// 将模组（或全部选中模组）移动到列表顶部
    /// </summary>
    [RelayCommand]
    void MoveToTop(ModViewModel modVm)
    {
        var mods = GetModsForReorder(modVm);
        if (mods.Count == 0)
            return;

        foreach (var mod in mods)
            _orderedItems.Remove(mod);

        for (int i = 0; i < mods.Count; i++)
            _orderedItems.Insert(i, mods[i]);

        AfterModsReordered();
    }

    /// <summary>
    /// 将模组（或全部选中模组）移动到列表底部
    /// </summary>
    [RelayCommand]
    void MoveToBottom(ModViewModel modVm)
    {
        var mods = GetModsForReorder(modVm);
        if (mods.Count == 0)
            return;

        foreach (var mod in mods)
            _orderedItems.Remove(mod);

        foreach (var mod in mods)
            _orderedItems.Add(mod);

        AfterModsReordered();
    }

    /// <summary>
    /// 将模组（或全部选中模组）移动到指定位置（1 到列表模组总数）
    /// </summary>
    [RelayCommand]
    void MoveToPosition(ModViewModel modVm)
    {
        if (!_orderedItems.Contains(modVm))
            return;

        var totalCount = _orderedItems.OfType<ModViewModel>().Count();
        if (totalCount < 2)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
        {
            Title = _localizationService["DashboardPage.MoveToPositionTitle"],
            Message = _localizationService["DashboardPage.MoveToPositionMsg"].Replace("{count}", totalCount.ToString()),
            MaxLength = 6,
            InitialText = (_orderedItems.OfType<ModViewModel>().ToList().IndexOf(modVm) + 1).ToString(),
            Confirm = input =>
            {
                if (!int.TryParse(input, out var position) || position < 1 || position > totalCount)
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
                    {
                        Message = _localizationService["DashboardPage.MoveToPositionInvalid"].Replace("{count}", totalCount.ToString())
                    });
                    return;
                }

                MoveModsToTargetPosition(modVm, position);
            }
        });
    }

    /// <summary>
    /// 获取参与重排序的模组列表：右键的模组已选中时移动全部选中项，否则只移动该模组
    /// </summary>
    private List<ModViewModel> GetModsForReorder(ModViewModel source)
    {
        if (!_orderedItems.Contains(source))
            return [];

        var ordered = _orderedItems.OfType<ModViewModel>().ToList();
        return source.IsSelected
            ? ordered.Where(static vm => vm.IsSelected).ToList()
            : [source];
    }

    /// <summary>
    /// 将模组集合移动到目标位置（1 基，基于只含模组的显示序列），并保留分隔符的相对位置
    /// </summary>
    private void MoveModsToTargetPosition(ModViewModel source, int targetPosition)
    {
        var mods = GetModsForReorder(source);
        if (mods.Count == 0)
            return;

        foreach (var mod in mods)
            _orderedItems.Remove(mod);

        var displayMods = _orderedItems.OfType<ModViewModel>().ToList();
        var targetIndex = Math.Clamp(targetPosition - 1, 0, displayMods.Count);
        var insertIndex = targetIndex >= displayMods.Count
            ? _orderedItems.Count
            : _orderedItems.IndexOf(displayMods[targetIndex]);

        for (int i = 0; i < mods.Count; i++)
            _orderedItems.Insert(insertIndex + i, mods[i]);

        AfterModsReordered();
    }

    /// <summary>
    /// 重排序后同步顺序、保存配置并刷新冲突扫描
    /// </summary>
    private void AfterModsReordered()
    {
        SyncModsOrderFromDisplay();
        RequestProfileSave();
        RequestAutomaticConflictScan();
    }
}
