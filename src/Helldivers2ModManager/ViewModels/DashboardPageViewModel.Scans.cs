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
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MessageBox = Helldivers2ModManager.Components.MessageBox;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class DashboardPageViewModel
{
    [RelayCommand]
    void Remove(ModViewModel modVm)
    {
        var deleteMessage = _settingsService.DeleteToRecycleBin
            ? _localizationService["DashboardPage.RecycleBinConfirm"]
            : _localizationService["DashboardPage.PermanentDeleteConfirm"];
        
        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = _localizationService["DashboardPage.DeleteConfirmTitle"],
            Message = $"{_localizationService["DashboardPage.DeleteConfirmPrefix"]}{modVm.Name}{_localizationService["DashboardPage.DeleteConfirmSuffix"]}{deleteMessage}",
            Confirm = () =>
            {
                _ = DeleteModAsync(modVm);
            }
        });
    }

    private async Task DeleteModAsync(ModViewModel modVm)
    {
        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
        {
            Title = _localizationService["DashboardPage.DeleteModProgress"],
            Message = _localizationService["SettingsPage.PleaseWait"]
        });

        try
        {
            await _backgroundTaskService.RunAsync(
                _localizationService["DashboardPage.DeleteModHint"],
                modVm.Name,
                async (_, _) =>
                {
                    await _modService.RemoveAsync(modVm.Data);

                    // 删除后同步更新数据库：直接删除该模组对应的记录
                    if (!_settingsService.IsReadonly)
                    {
                        await _profileService.DeleteEnabledDataAsync(_settingsService.StorageDirectory, modVm.Guid);
                        await _modGroupService.RemoveModsFromAllGroupsAsync([modVm.Guid]);
                        // 同时删除该模组的版本检测记录
                        await _versionCheckRepository.DeleteByGuidAsync(_settingsService.StorageDirectory, modVm.Guid);
                    }

                    modVm.Dispose();
                },
                _localizationService["BackgroundTasksPage.DeleteComplete"].Replace("{name}", modVm.Name),
                isForeground: true);

            WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unknown mod removal error");
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = ex.Message
            });
        }
    }

    [RelayCommand]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is a command of a view model and should not be static.")]
    void Run()
    {
        Process.Start(s_gameStartInfo);
    }

    [RelayCommand]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is a command of a view model and should not be static.")]
    void Github()
    {
        Process.Start(s_githubStartInfo);
    }

    [RelayCommand]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is a command of a view model and should not be static.")]
    void GithubFork()
    {
        Process.Start(s_githubForkStartInfo);
    }

    [RelayCommand]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is a command of a view model and should not be static.")]
    void Discord()
    {
        Process.Start(s_discordStartInfo);
    }

    // ===== 版本兼容性检查命令（委托给 VersionCheckViewModel） =====

    /// <summary>
    /// 检查所有模组的版本兼容性
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task CheckVersionCompatibility()
    {
        await RunVersionCheckCompatibilityAsync(true);
    }

    private async Task RunVersionCheckCompatibilityAsync(bool forceFullScan)
    {
        await _versionCheckVm.CheckVersionCompatibilityAsync(_mods, forceFullScan);
    }

    /// <summary>
    /// 扫描当前启用模组在实际部署顺序下的 Unit 覆盖关系。
    /// 该操作只读，不会修改模组文件或部署目录。
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task ScanModConflicts()
    {
        await RunConflictScanAsync(showReport: true, allowCachedResult: false);
    }

    private async Task RunConflictScanAsync(bool showReport, bool allowCachedResult)
    {
        if (!Initialized || IsScanningConflicts)
            return;

        var deploymentMods = GetDeploymentMods(CaptureProfileSnapshot());
        var cacheKey = _modConflictService.BuildCacheKey(deploymentMods);

        if (allowCachedResult && TryGetCachedConflictResult(cacheKey, out var cachedResult))
        {
            ApplyConflictAnalysisResult(cacheKey, cachedResult, showReport);
            return;
        }

        ClearConflictStatuses();
        IsScanningConflicts = true;
        var scanningText = _localizationService["DashboardPage.ConflictScanning"];

        try
        {
            // 扫描在后台线程执行（BackgroundTaskService 统一管理状态），结果回 UI 线程应用
            var result = await _backgroundTaskService.RunAsync(
                _localizationService["BackgroundTasksPage.TaskTypeConflictScan"],
                scanningText,
                (_, _) => _modConflictService.AnalyzeAsync(deploymentMods),
                scanningText);

            _conflictCache[cacheKey] = result;
            if (!_settingsService.IsReadonly)
                await _modConflictRepository.SaveAsync(_settingsService.StorageDirectory, cacheKey, result);

            if (showReport || string.Equals(GetCurrentConflictCacheKey(), cacheKey, StringComparison.Ordinal))
                ApplyConflictAnalysisResult(cacheKey, result, showReport);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan mod conflicts");
            if (showReport)
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
                {
                    Message = $"{_localizationService["DashboardPage.ConflictScanFailed"]}\n{ex.Message}"
                });
            }
        }
        finally
        {
            IsScanningConflicts = false;
            if (_conflictScanPending)
            {
                _conflictScanPending = false;
                RequestAutomaticConflictScan();
            }
        }
    }

    private void VersionCheckVm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsCheckingVersion));
        OnPropertyChanged(nameof(CompatibleModCount));
        OnPropertyChanged(nameof(IncompatibleModCount));
        OnPropertyChanged(nameof(HasIncompatibleMods));
    }
}
