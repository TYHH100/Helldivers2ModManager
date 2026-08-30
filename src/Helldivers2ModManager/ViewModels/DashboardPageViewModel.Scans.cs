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
        ConflictSummary = _localizationService["DashboardPage.ConflictScanning"];

        try
        {
            // 扫描在后台线程执行（BackgroundTaskService 统一管理状态），结果回 UI 线程应用
            var result = await _backgroundTaskService.RunAsync(
                _localizationService["BackgroundTasksPage.TaskTypeConflictScan"],
                ConflictSummary,
                (_, _) => _modConflictService.AnalyzeAsync(deploymentMods),
                ConflictSummary);

            _conflictCache[cacheKey] = result;
            if (!_settingsService.IsReadonly)
                await _modConflictRepository.SaveAsync(_settingsService.StorageDirectory, cacheKey, result);

            if (showReport || string.Equals(GetCurrentConflictCacheKey(), cacheKey, StringComparison.Ordinal))
                ApplyConflictAnalysisResult(cacheKey, result, showReport);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan mod conflicts");
            ConflictSummary = _localizationService["DashboardPage.ConflictScanFailed"];
            if (showReport)
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
                {
                    Message = $"{ConflictSummary}\n{ex.Message}"
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

    private string FormatConflictReport(ModConflictAnalysisResult result, IReadOnlyList<ModConflictRecord> visibleConflicts)
    {
        var sb = new StringBuilder();
        sb.AppendLine(_localizationService["DashboardPage.ConflictReportTitle"]);
        sb.AppendLine(_localizationService["DashboardPage.ConflictReportScanned"]
            .Replace("{mods}", result.ScannedModCount.ToString())
            .Replace("{patches}", result.ScannedPatchCount.ToString())
            .Replace("{units}", result.ScannedUnitCount.ToString()));

        if (visibleConflicts.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine(_localizationService["DashboardPage.ConflictNone"]);
            return sb.ToString();
        }

        sb.AppendLine(_localizationService["DashboardPage.ConflictReportCount"]
            .Replace("{count}", visibleConflicts.Count.ToString())
            .Replace("{definite}", visibleConflicts.Count(static conflict => conflict.IsDefiniteConflict).ToString()));

        foreach (var conflict in visibleConflicts.Take(50))
        {
            var winner = conflict.Winner;
            var names = string.Join(", ", conflict.Participants
                .Select(static p => p.ModName)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            sb.AppendLine();
            sb.AppendLine(_localizationService["DashboardPage.ConflictReportItem"]
                .Replace("{resource}", conflict.FriendlyName)
                .Replace("{kind}", conflict.IsDefiniteConflict
                    ? _localizationService["ConflictDetail.Definite"]
                    : _localizationService["DashboardPage.ConflictPotential"])
                .Replace("{mods}", names)
                .Replace("{winner}", winner.ModName));
        }

        if (visibleConflicts.Count > 50)
            sb.AppendLine(_localizationService["DashboardPage.ConflictReportTruncated"]
                .Replace("{count}", (visibleConflicts.Count - 50).ToString()));

        return sb.ToString();
    }

    private void VersionCheckVm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsCheckingVersion));
        OnPropertyChanged(nameof(VersionCheckSummary));
        OnPropertyChanged(nameof(CompatibleModCount));
        OnPropertyChanged(nameof(IncompatibleModCount));
        OnPropertyChanged(nameof(HasIncompatibleMods));
        OnPropertyChanged(nameof(HasVersionCheckResult));
    }
}
