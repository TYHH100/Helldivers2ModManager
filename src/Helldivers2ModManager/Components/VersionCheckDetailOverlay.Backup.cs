using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Helldivers2ModManager.Components;

internal partial class VersionCheckDetailOverlay : UserControl, IRecipient<VersionCheckDetailMessage>
{
    private async Task LoadBackupHistoryAsync(VersionCheckDetailMessage message)
    {
        if (_versionCheckService is null)
            return;

        backupHistoryLoading.Text = L("VersionCheckBackup.Loading", "Loading backup history...");
        backupHistoryLoading.Visibility = Visibility.Visible;
        try
        {
            var history = await _versionCheckService.GetBackupHistoryAsync(message.ModDirectory);
            if (!ReferenceEquals(_currentMessage, message))
                return;

            _backupHistory = history;
            var groups = history.Entries
                .GroupBy(entry => entry.OriginalPath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new BackupHistoryGroupViewData
                {
                    Title = _currentMessage is { } message
                        ? Path.GetRelativePath(message.ModDirectory.FullName, group.Key)
                        : group.Key,
                    EntryCountText = group.Count().ToString(),
                    Entries = group
                        .OrderByDescending(entry => entry.CreatedLocal)
                        .Select(BuildBackupHistoryItem)
                        .ToList(),
                })
                .ToList();
            backupHistoryItems.ItemsSource = groups;
            backupHistorySummary.Text = history.Entries.Count == 0
                ? L("VersionCheckBackup.None", "No HD2MM repair backups were found for this mod.")
                : L(
                        "VersionCheckBackup.Summary",
                        "{count} backup(s), {restorable} restorable, {invalid} invalid.")
                    .Replace("{count}", history.Entries.Count.ToString())
                    .Replace("{restorable}", history.RestorableCount.ToString())
                    .Replace("{invalid}", history.InvalidCount.ToString());
            cleanBackupsButton.IsEnabled = history.Entries
                .GroupBy(entry => entry.OriginalPath, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() > 3);

            PopulateRollbackPoints(history);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_currentMessage, message))
            {
                backupHistorySummary.Text = L(
                        "VersionCheckBackup.LoadFailed",
                        "Failed to load backup history: {message}")
                    .Replace("{message}", ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_currentMessage, message))
                backupHistoryLoading.Visibility = Visibility.Collapsed;
        }
    }

    private BackupHistoryItemViewData BuildBackupHistoryItem(ModBackupEntry entry)
    {
        var status = !entry.CanRestore
            ? L("VersionCheckBackup.Invalid", "Cannot restore: {message}")
                .Replace("{message}", entry.ValidationMessage)
            : entry.CurrentMatchesBackup
                ? L("VersionCheckBackup.MatchesCurrent", "Current patch already matches this backup.")
                : !entry.CurrentExists
                    ? L("VersionCheckBackup.CurrentMissing", "Current patch is missing; restore will recreate it.")
                    : L("VersionCheckBackup.Ready", "Ready to restore. The current patch will be backed up first.");
        var statusBrush = !entry.CanRestore
            ? GetBrush("DangerBrush", Colors.IndianRed)
            : entry.CurrentMatchesBackup
                ? GetBrush("WarningBrush", Colors.Goldenrod)
                : GetBrush("SuccessBrush", Colors.ForestGreen);
        var detail = L(
                "VersionCheckBackup.ItemDetail",
                "{time} | {kind} | {actions} action(s) | {size} | SHA-256 {hash}")
            .Replace("{time}", entry.CreatedLocal.ToString("yyyy-MM-dd HH:mm:ss"))
            .Replace("{kind}", GetBackupKindLabel(entry.RepairKind))
            .Replace("{actions}", entry.ActionCount.ToString())
            .Replace("{size}", FormatFileSize(entry.BackupSize))
            .Replace("{hash}", entry.BackupSha256.Length >= 12 ? entry.BackupSha256[..12] : "-");
        return new BackupHistoryItemViewData
        {
            Entry = entry,
            Title = _currentMessage is { } message
                ? Path.GetRelativePath(message.ModDirectory.FullName, entry.OriginalPath)
                : entry.OriginalPath,
            Detail = detail,
            Status = status,
            StatusBrush = statusBrush
        };
    }

    /// <summary>
    /// 用备份历史填充整模组回滚的时间点下拉（去重到分钟，新到旧），
    /// 并决定回滚按钮是否可用。
    /// </summary>
    private void PopulateRollbackPoints(ModBackupHistory history)
    {
        var points = history.Entries
            .Select(entry => new DateTime(
                entry.CreatedLocal.Year, entry.CreatedLocal.Month, entry.CreatedLocal.Day,
                entry.CreatedLocal.Hour, entry.CreatedLocal.Minute, 0))
            .Distinct()
            .OrderByDescending(time => time)
            .Select(time => new RollbackPointViewData
            {
                Time = time,
                DisplayText = time.ToString("yyyy-MM-dd HH:mm"),
            })
            .ToList();

        rollbackPointCombo.ItemsSource = points;
        rollbackPointCombo.SelectedIndex = points.Count > 0 ? 0 : -1;
        rollbackButton.IsEnabled = points.Count > 0;
        rollbackPointCombo.IsEnabled = points.Count > 0;
        rollbackPanel.Visibility = points.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RollbackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMessage is null ||
            rollbackPointCombo.SelectedItem is not RollbackPointViewData point)
            return;

        var pendingCount = _backupHistory.Entries
            .GroupBy(entry => entry.OriginalPath, StringComparer.OrdinalIgnoreCase)
            .Count(group =>
            {
                var target = group
                    .Where(entry => entry.CreatedLocal <= point.Time)
                    .OrderByDescending(entry => entry.CreatedLocal)
                    .FirstOrDefault();
                return target is not null && target.CanRestore && !target.CurrentMatchesBackup;
            });

        if (pendingCount == 0)
        {
            backupOperationStatus.Text = L("VersionCheckBackup.RollbackNothing", "Nothing to roll back at this time point.");
            backupOperationStatus.Foreground = GetBrush("WarningBrush", Colors.Goldenrod);
            return;
        }

        var confirmText = L(
                "VersionCheckBackup.RollbackConfirm",
                "Roll the whole mod (including all option folders) back to {time}? {count} file(s) will be restored, and the current files will be backed up first.")
            .Replace("{time}", point.DisplayText)
            .Replace("{count}", pendingCount.ToString());
        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = L("VersionCheckBackup.RollbackButton", "Roll back whole mod"),
            Message = confirmText,
            Confirm = () => _ = ExecuteRollbackAsync(point)
        });
    }

    private async Task ExecuteRollbackAsync(RollbackPointViewData point)
    {
        if (_versionCheckService is null || _currentMessage is null)
            return;

        backupOperationStatus.Text = L("VersionCheckBackup.RollingBack", "Rolling back to {time}...")
            .Replace("{time}", point.DisplayText);
        backupOperationStatus.Foreground = GetBrush("SystemAccentBrush", Colors.DodgerBlue);
        SetRepairControlsBusy(true);

        try
        {
            var result = await _versionCheckService.RollbackModToAsync(
                _currentMessage.ModDirectory,
                point.Time);

            if (!result.Success && string.IsNullOrEmpty(result.RestoredPath) && result.FailedItems.Count == 0)
            {
                backupOperationStatus.Text = L("VersionCheckBackup.RestoreFailed", "Backup restore failed: {message}")
                    .Replace("{message}", result.ErrorMessage);
                backupOperationStatus.Foreground = GetBrush("DangerBrush", Colors.IndianRed);
                return;
            }

            backupOperationStatus.Text = L(
                    "VersionCheckBackup.RollbackResult",
                    "Rolled back to {time}: {restored} restored, {skipped} skipped, {failed} failed.")
                .Replace("{time}", point.DisplayText)
                .Replace("{restored}", result.RestoredCount.ToString())
                .Replace("{skipped}", result.SkippedCount.ToString())
                .Replace("{failed}", result.FailedItems.Count.ToString());
            backupOperationStatus.Foreground = result.FailedItems.Count == 0
                ? GetBrush("SuccessBrush", Colors.ForestGreen)
                : GetBrush("WarningBrush", Colors.Goldenrod);

            if (_currentMessage.RefreshAsync is { } refresh)
                await refresh();
            await LoadBackupHistoryAsync(_currentMessage);
        }
        catch (Exception ex)
        {
            backupOperationStatus.Text = L("VersionCheckBackup.RestoreFailed", "Backup restore failed: {message}")
                .Replace("{message}", ex.Message);
            backupOperationStatus.Foreground = GetBrush("DangerBrush", Colors.IndianRed);
        }
        finally
        {
            SetRepairControlsBusy(false);
        }
    }

    private string GetBackupKindLabel(ModBackupRepairKind kind)
    {
        return kind switch
        {
            ModBackupRepairKind.SafeMetadata => L("VersionCheckBackup.KindMetadata", "Safe metadata repair"),
            ModBackupRepairKind.AutomaticLod => L("VersionCheckBackup.KindAutomatic", "Automatic LOD repair"),
            ModBackupRepairKind.PreserveModLod => L("VersionCheckBackup.KindPreserve", "Preserve mod LOD"),
            ModBackupRepairKind.UseGameLod => L("VersionCheckBackup.KindGame", "Use game LOD"),
            ModBackupRepairKind.MixedLod => L("VersionCheckBackup.KindMixed", "Mixed per-Unit LOD"),
            ModBackupRepairKind.PreRestore => L("VersionCheckBackup.KindPreRestore", "Snapshot before restore"),
            _ => L("VersionCheckBackup.KindLegacy", "Legacy backup")
        };
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024d:F1} KiB";
        return $"{bytes / (1024d * 1024d):F1} MiB";
    }

    private void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMessage is null ||
            (sender as FrameworkElement)?.Tag is not ModBackupEntry entry ||
            !entry.CanRestore)
        {
            return;
        }

        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = L("VersionCheckBackup.RestoreTitle", "Restore patch backup"),
            Message = L(
                    "VersionCheckBackup.RestoreConfirm",
                    "Restore {file} from {time}? HD2MM will snapshot the current patch first, replace it atomically, and verify the restored SHA-256.")
                .Replace("{file}", entry.OriginalFileName)
                .Replace("{time}", entry.CreatedLocal.ToString("yyyy-MM-dd HH:mm:ss")),
            Confirm = () => _ = ExecuteBackupRestoreAsync(entry)
        });
    }

    private async Task ExecuteBackupRestoreAsync(ModBackupEntry entry)
    {
        if (_versionCheckService is null || _currentMessage is null)
            return;

        var message = _currentMessage;
        SetRepairControlsBusy(true);
        backupOperationStatus.Text = L("VersionCheckBackup.Restoring", "Restoring and validating backup...");
        backupOperationStatus.Foreground = GetBrush("SystemAccentBrush", Colors.DodgerBlue);
        try
        {
            var result = await _versionCheckService.RestoreBackupAsync(
                message.ModDirectory,
                entry.BackupPath);
            if (!result.Success)
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
                {
                    Message = L("VersionCheckBackup.RestoreFailed", "Backup restore failed: {message}")
                        .Replace("{message}", result.ErrorMessage ?? L("Converters.Unknown", "Unknown"))
                });
                return;
            }

            backupOperationStatus.Text = L(
                "VersionCheckBackup.RestoreSuccess",
                "Backup restored. A snapshot of the replaced patch was retained.");
            backupOperationStatus.Foreground = GetBrush("SuccessBrush", Colors.ForestGreen);
            await message.RefreshAsync();
            if (ReferenceEquals(_currentMessage, message))
            {
                await LoadBackupHistoryAsync(message);
                await LoadRepairPlanAsync(message);
            }
        }
        catch (Exception ex)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = L("VersionCheckBackup.RestoreFailed", "Backup restore failed: {message}")
                    .Replace("{message}", ex.Message)
            });
        }
        finally
        {
            if (ReferenceEquals(_currentMessage, message))
                SetRepairControlsBusy(false);
        }
    }

    private void DeleteBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMessage is null || (sender as FrameworkElement)?.Tag is not ModBackupEntry entry)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = L("VersionCheckBackup.DeleteTitle", "Delete backup"),
            Message = L(
                    "VersionCheckBackup.DeleteConfirm",
                    "Delete the backup of {file} from {time}? The final restorable backup for a patch is always protected.")
                .Replace("{file}", entry.OriginalFileName)
                .Replace("{time}", entry.CreatedLocal.ToString("yyyy-MM-dd HH:mm:ss")),
            Confirm = () => _ = ExecuteBackupDeleteAsync(entry)
        });
    }

    private async Task ExecuteBackupDeleteAsync(ModBackupEntry entry)
    {
        if (_versionCheckService is null || _currentMessage is null)
            return;

        var message = _currentMessage;
        backupHistoryExpander.IsEnabled = false;
        try
        {
            var result = await _versionCheckService.DeleteBackupAsync(
                message.ModDirectory,
                entry.BackupPath);
            if (!result.Success)
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
                {
                    Message = L("VersionCheckBackup.DeleteFailed", "Backup deletion failed: {message}")
                        .Replace("{message}", result.ErrorMessage ?? L("Converters.Unknown", "Unknown"))
                });
                return;
            }

            backupOperationStatus.Text = L("VersionCheckBackup.DeleteSuccess", "Backup deleted.");
            backupOperationStatus.Foreground = GetBrush("SuccessBrush", Colors.ForestGreen);
            await LoadBackupHistoryAsync(message);
        }
        finally
        {
            if (ReferenceEquals(_currentMessage, message))
                backupHistoryExpander.IsEnabled = true;
        }
    }

    private void CleanBackupsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMessage is null)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = L("VersionCheckBackup.CleanOld", "Clean old backups"),
            Message = L(
                "VersionCheckBackup.CleanConfirm",
                "Keep the newest three backups for each patch and delete older entries? At least one restorable backup is always retained."),
            Confirm = () => _ = ExecuteBackupCleanupAsync()
        });
    }

    private async Task ExecuteBackupCleanupAsync()
    {
        if (_versionCheckService is null || _currentMessage is null)
            return;

        var message = _currentMessage;
        backupHistoryExpander.IsEnabled = false;
        try
        {
            var result = await _versionCheckService.CleanOldBackupsAsync(message.ModDirectory, 3);
            if (!result.Success)
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
                {
                    Message = L("VersionCheckBackup.CleanFailed", "Backup cleanup failed: {message}")
                        .Replace("{message}", result.ErrorMessage ?? L("Converters.Unknown", "Unknown"))
                });
                return;
            }

            backupOperationStatus.Text = L(
                    "VersionCheckBackup.CleanSuccess",
                    "Deleted {count} old backup(s).")
                .Replace("{count}", result.DeletedCount.ToString());
            backupOperationStatus.Foreground = GetBrush("SuccessBrush", Colors.ForestGreen);
            await LoadBackupHistoryAsync(message);
        }
        finally
        {
            if (ReferenceEquals(_currentMessage, message))
                backupHistoryExpander.IsEnabled = true;
        }
    }
}
