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
    private async Task LoadRepairPlanAsync(VersionCheckDetailMessage message)
    {
        if (_versionCheckService is null)
            return;

        ResetRepairPlans();
        SetRepairControlsBusy(true);
        repairProgress.Text = L("VersionCheckDetail.CheckingRepair", "Checking repair options...");
        repairProgress.Visibility = Visibility.Visible;
        try
        {
            var recoveryPlan = await _versionCheckService.CreateCompanionRecoveryPlanAsync(
                message.ModDirectory);
            if (!ReferenceEquals(_currentMessage, message))
                return;
            _companionRecoveryPlan = recoveryPlan;
            if (recoveryPlan.MissingCount > 0)
            {
                recoveryButtonText.Text = recoveryPlan.CanRecover
                    ? L("VersionCheckRecovery.Button", "Recover {count} companion file(s)")
                        .Replace("{count}", recoveryPlan.RecoverableCount.ToString())
                    : L("VersionCheckRecovery.Unavailable", "No reliable companion source");
                return;
            }

            var plan = await _versionCheckService.CreateRepairPlanAsync(message.ModDirectory);
            if (!ReferenceEquals(_currentMessage, message))
                return;

            _repairPlan = plan;
            if (plan.CanRepair)
            {
                _useGameReferences = false;
                repairButtonText.Text = L("VersionCheckDetail.RepairButton", "Repair {count} issue(s)")
                    .Replace("{count}", plan.ActionCount.ToString());
                return;
            }

            repairProgress.Text = L("VersionCheckDetail.IndexingGameUnits", "Checking current game Unit references...");
            var automaticLodPlan = await _versionCheckService.CreateAutomaticAssistedRepairPlanAsync(
                message.ModDirectory);
            if (!ReferenceEquals(_currentMessage, message))
                return;

            _automaticLodPlan = automaticLodPlan;
            if (automaticLodPlan.CanRepair)
            {
                _useGameReferences = true;
                repairButtonText.Text = L("VersionCheckDetail.AutomaticRepairButton", "Automatically repair {count} Unit(s)")
                    .Replace("{count}", automaticLodPlan.ActionCount.ToString());
            }
        }
        catch (Exception ex)
        {
            repairStatus.Text = L(
                    "VersionCheckDetail.RepairPlanFailed",
                    "Failed to load repair options: {message}")
                .Replace("{message}", ex.Message);
            repairStatus.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_currentMessage, message))
            {
                repairProgress.Visibility = Visibility.Collapsed;
                SetRepairControlsBusy(false);
            }
        }
    }

    private bool EnsureRepairDisclaimerAccepted(Action continuation)
    {
        if (_repairDisclaimerService is not null)
            return _repairDisclaimerService.ContinueOrRequest(continuation);

        WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
        {
            Message = L(
                "VersionCheckDisclaimer.SettingsUnavailable",
                "Repair settings are unavailable. Restart HD2MM and try again.")
        });
        return false;
    }

    private void ResetRepairPlans()
    {
        _repairPlan = null;
        _automaticLodPlan = null;
        _preserveLodPlan = null;
        _gameLodPlan = null;
        _companionRecoveryPlan = null;
        _useGameReferences = false;
    }

    private void SetRepairControlsBusy(bool busy)
    {
        _repairControlsBusy = busy;
        RefreshRepairControls();
    }

    private void RefreshRepairControls()
    {
        var recoveryVisible = _companionRecoveryPlan?.MissingCount > 0;
        var safeRepairVisible = !recoveryVisible && _repairPlan?.CanRepair == true;
        var automaticRepairVisible = !recoveryVisible && !safeRepairVisible &&
            _automaticLodPlan?.CanRepair == true;
        var repairVisible = safeRepairVisible || automaticRepairVisible;

        recoveryButton.Visibility = recoveryVisible ? Visibility.Visible : Visibility.Collapsed;
        recoveryButton.IsEnabled = !_repairControlsBusy &&
            _companionRecoveryPlan?.CanRecover == true;
        repairButton.Visibility = repairVisible ? Visibility.Visible : Visibility.Collapsed;
        repairButton.IsEnabled = !_repairControlsBusy && repairVisible;
        advancedRepairButton.Visibility = automaticRepairVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        advancedRepairButton.IsEnabled = !_repairControlsBusy && automaticRepairVisible;
        backupHistoryExpander.IsEnabled = !_repairControlsBusy;
    }

    private static Brush GetBrush(string resourceKey, Color fallback)
    {
        return Application.Current.TryFindResource(resourceKey) as Brush ?? new SolidColorBrush(fallback);
    }


    private void RecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_companionRecoveryPlan?.CanRecover != true)
            return;

        if (!EnsureRepairDisclaimerAccepted(() => RecoveryButton_Click(sender, e)))
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = L("VersionCheckRecovery.ConfirmTitle", "Recover missing companion files"),
            Message = L(
                    "VersionCheckRecovery.ConfirmMessage",
                    "HD2MM will recover {count} companion file(s) from byte-verified sources, stage them in temporary files, and commit only after bounds validation. Continue?")
                .Replace("{count}", _companionRecoveryPlan.RecoverableCount.ToString()),
            Confirm = () => _ = ExecuteCompanionRecoveryAsync()
        });
    }

    private async Task ExecuteCompanionRecoveryAsync()
    {
        if (_versionCheckService is null || _currentMessage is null ||
            _companionRecoveryPlan?.CanRecover != true)
        {
            return;
        }

        var message = _currentMessage;
        SetRepairControlsBusy(true);
        repairProgress.Text = L(
            "VersionCheckRecovery.Recovering",
            "Recovering and validating companion files...");
        repairProgress.Visibility = Visibility.Visible;
        try
        {
            var result = await _versionCheckService.RecoverCompanionFilesAsync(message.ModDirectory);
            if (!result.Success)
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
                {
                    Message = L("VersionCheckRecovery.Failed", "Companion recovery failed: {message}")
                        .Replace("{message}", result.ErrorMessage ?? L("Converters.Unknown", "Unknown"))
                });
                return;
            }

            repairStatus.Text = L(
                    "VersionCheckRecovery.Success",
                    "Recovered {count} companion file(s).")
                .Replace("{count}", result.RecoveredCount.ToString());
            repairStatus.Visibility = Visibility.Visible;
            await message.RefreshAsync();
            if (ReferenceEquals(_currentMessage, message))
                await LoadRepairPlanAsync(message);
        }
        catch (Exception ex)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = L("VersionCheckRecovery.Failed", "Companion recovery failed: {message}")
                    .Replace("{message}", ex.Message)
            });
        }
        finally
        {
            if (ReferenceEquals(_currentMessage, message))
            {
                repairProgress.Visibility = Visibility.Collapsed;
                SetRepairControlsBusy(false);
            }
        }
    }
    private void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMessage is null)
            return;

        if (!EnsureRepairDisclaimerAccepted(() => RepairButton_Click(sender, e)))
            return;

        if (_useGameReferences)
        {
            ShowAutomaticRepairConfirmation();
            return;
        }

        var fileCount = _repairPlan?.FileCount ?? 0;
        var actionCount = _repairPlan?.ActionCount ?? 0;
        if (actionCount == 0)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = L("VersionCheckDetail.RepairConfirmTitle", "Repair mod"),
            Message = L(
                    "VersionCheckDetail.RepairConfirmMessage",
                    "HD2MM will back up {files} patch file(s), apply {count} verified metadata repair(s), and validate the result before replacing the originals.")
                .Replace("{files}", fileCount.ToString())
                .Replace("{count}", actionCount.ToString()),
            Confirm = () => _ = ExecuteRepairAsync(false, null, null)
        });
    }

    private void ShowAutomaticRepairConfirmation()
    {
        if (_automaticLodPlan?.CanRepair != true)
            return;

        var plan = _automaticLodPlan;
        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = L("VersionCheckDetail.AutomaticRepairTitle", "Automatically repair mod"),
            Message = L(
                    "VersionCheckDetail.AutomaticRepairConfirm",
                    "HD2MM analyzed the Unit mesh and GPU structure. It will preserve mod LOD for {preserve} Unit(s), use current game LOD for {game} Unit(s), back up the patch files, and validate the rebuilt result before replacement.")
                .Replace("{preserve}", plan.AutomaticPreserveUnitCount.ToString())
                .Replace("{game}", plan.AutomaticGameLodUnitCount.ToString()),
            Confirm = () => _ = ExecuteRepairAsync(true, null, null)
        });
    }

    private async void AdvancedRepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (_versionCheckService is null || _currentMessage is null)
            return;

        if (!EnsureRepairDisclaimerAccepted(() => AdvancedRepairButton_Click(sender, e)))
            return;

        var message = _currentMessage;
        SetRepairControlsBusy(true);
        repairProgress.Text = L("VersionCheckDetail.LoadingAdvancedRepair", "Loading advanced LOD strategies...");
        repairProgress.Visibility = Visibility.Visible;
        try
        {
            _preserveLodPlan ??= await _versionCheckService.CreateAssistedRepairPlanAsync(
                message.ModDirectory,
                AssistedLodStrategy.PreserveMod);
            _gameLodPlan ??= await _versionCheckService.CreateAssistedRepairPlanAsync(
                message.ModDirectory,
                AssistedLodStrategy.UseGameReference);
            if (!ReferenceEquals(_currentMessage, message))
                return;

            ShowLodStrategySelection();
        }
        catch (Exception ex)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = L("VersionCheckDetail.AdvancedRepairFailed", "Failed to load advanced repair strategies: {message}")
                    .Replace("{message}", ex.Message)
            });
        }
        finally
        {
            if (ReferenceEquals(_currentMessage, message))
            {
                repairProgress.Visibility = Visibility.Collapsed;
                SetRepairControlsBusy(false);
            }
        }
    }

    private void ShowLodStrategySelection()
    {
        var options = new List<LodStrategyOption>();
        var selectableUnitCount = _gameLodPlan?.Actions
            .Where(action => action.LodDataDiffers)
            .Select(action => action.FileId)
            .Distinct()
            .Count() ?? 0;
        if (selectableUnitCount > 0)
        {
            options.Add(new LodStrategyOption(
                null,
                L("VersionCheckDetail.LodStrategyPerUnit", "Choose per Unit manually ({count} selectable)")
                    .Replace("{count}", selectableUnitCount.ToString())));
        }
        if (_gameLodPlan?.CanRepair == true)
        {
            options.Add(new LodStrategyOption(
                AssistedLodStrategy.UseGameReference,
                L("VersionCheckDetail.LodStrategyGameReference", "Use current game LOD (standard, {count} Unit(s))")
                    .Replace("{count}", _gameLodPlan.ActionCount.ToString())));
        }
        if (_preserveLodPlan?.CanRepair == true)
        {
            options.Add(new LodStrategyOption(
                AssistedLodStrategy.PreserveMod,
                L("VersionCheckDetail.LodStrategyPreserveMod", "Preserve mod LOD (custom models, {count} Unit(s))")
                    .Replace("{count}", _preserveLodPlan.ActionCount.ToString())));
        }
        if (options.Count == 0)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxSelectionMessage
        {
            Title = L("VersionCheckDetail.LodStrategyTitle", "Choose Unit LOD strategy"),
            Message = L(
                "VersionCheckDetail.LodStrategyMessage",
                "LOD compatibility can differ between Units in the same patch. Per-Unit selection is the safest option. Game LOD can prevent preview crashes but may hide custom models; preserving mod LOD keeps custom models but an obsolete group can crash the preview. Every repair creates a backup."),
            Options = options,
            Confirm = selected =>
            {
                if (selected is not LodStrategyOption option)
                    return;
                if (option.Strategy is AssistedLodStrategy strategy)
                    _ = ExecuteRepairAsync(true, strategy, null);
                else
                    ShowUnitLodSelection();
            }
        });
    }

    private void ShowUnitLodSelection()
    {
        var candidates = _gameLodPlan?.Actions
            .Where(action => action.LodDataDiffers)
            .GroupBy(action => action.FileId)
            .Select(group =>
            {
                var first = group.First();
                var title = string.IsNullOrWhiteSpace(first.FriendlyName)
                    ? L("VersionCheckDetail.UnitSelectionUnnamed", "Unit {id}")
                        .Replace("{id}", $"0x{(ulong)first.FileId:X16}")
                    : first.FriendlyName;
                var lodSizes = string.Join(", ", group
                    .Select(action => $"{action.CurrentLodSize}->{action.ReferenceLodSize}")
                    .Distinct(StringComparer.Ordinal));
                var description = L(
                        "VersionCheckDetail.UnitSelectionDescription",
                        "{occurrences} patch entry(s) | LOD {lodSizes} | ID {id}")
                    .Replace("{occurrences}", group.Count().ToString())
                    .Replace("{lodSizes}", lodSizes)
                    .Replace("{id}", $"0x{(ulong)first.FileId:X16}");
                return new ChecklistSelectionItem
                {
                    Value = first.FileId,
                    Title = title,
                    Description = description
                };
            })
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Value)
            .ToList() ?? [];
        if (candidates.Count == 0)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxChecklistMessage
        {
            Title = L("VersionCheckDetail.UnitSelectionTitle", "Choose Units that keep mod LOD"),
            Message = L(
                "VersionCheckDetail.UnitSelectionMessage",
                "Checked Units preserve the mod LOD; unchecked Units use current game LOD. Equal LOD sizes can still contain incompatible data. If the mod was already converted entirely to game LOD, restore its HD2MM backup before using this screen."),
            Items = candidates,
            Confirm = selected =>
            {
                var preserveIds = selected.Select(item => item.Value).ToHashSet();
                _ = ExecuteRepairAsync(true, null, preserveIds);
            }
        });
    }
    private async Task ExecuteRepairAsync(
        bool useGameReferences,
        AssistedLodStrategy? lodStrategy,
        IReadOnlySet<long>? preserveModLodUnitIds)
    {
        if (_versionCheckService is null || _currentMessage is null)
            return;

        var message = _currentMessage;
        SetRepairControlsBusy(true);
        var usesMixedLod = preserveModLodUnitIds is not null;
        var usesGameLod = lodStrategy == AssistedLodStrategy.UseGameReference;
        var usesAutomaticLod = useGameReferences &&
            lodStrategy is null &&
            preserveModLodUnitIds is null;
        repairProgress.Text = useGameReferences
            ? L(
                usesAutomaticLod
                    ? "VersionCheckDetail.AutomaticRepairing"
                    : usesMixedLod
                        ? "VersionCheckDetail.AssistedRepairingMixedLod"
                        : usesGameLod
                            ? "VersionCheckDetail.AssistedRepairingGameLod"
                            : "VersionCheckDetail.AssistedRepairingPreserveLod",
                usesAutomaticLod
                    ? "Automatically classifying each Unit, rebuilding the patch, and validating..."
                    : usesMixedLod
                        ? "Applying per-Unit LOD choices, then validating..."
                        : usesGameLod
                            ? "Replacing Unit LOD data with current game references, then validating..."
                            : "Upgrading Units while preserving mod LOD data, then validating...")
            : L("VersionCheckDetail.Repairing", "Backing up, repairing, and validating...");
        repairProgress.Visibility = Visibility.Visible;
        repairStatus.Visibility = Visibility.Collapsed;

        try
        {
            ModRepairResult result;
            if (!useGameReferences)
            {
                result = await _versionCheckService.RepairModAsync(message.ModDirectory);
            }
            else if (usesAutomaticLod)
            {
                result = await _versionCheckService.RepairModAutomaticallyAsync(message.ModDirectory);
            }
            else if (preserveModLodUnitIds is not null)
            {
                result = await _versionCheckService.RepairModWithMixedGameReferencesAsync(
                    message.ModDirectory,
                    preserveModLodUnitIds);
            }
            else
            {
                result = await _versionCheckService.RepairModWithGameReferencesAsync(
                    message.ModDirectory,
                    lodStrategy ?? AssistedLodStrategy.PreserveMod);
            }

            if (!result.Success)
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
                {
                    Message = L("VersionCheckDetail.RepairFailed", "Repair failed: {message}")
                        .Replace("{message}", result.ErrorMessage ?? L("Converters.Unknown", "Unknown"))
                });
                return;
            }

            await message.RefreshAsync();
            repairStatus.Text = useGameReferences
                ? usesAutomaticLod
                    ? L(
                        "VersionCheckDetail.AutomaticRepairSuccess",
                        "Automatically updated {count} Unit(s): preserved mod LOD for {preserve}, used game LOD for {game}. Original files were kept as backups.")
                    .Replace("{count}", result.AppliedActionCount.ToString())
                    .Replace("{preserve}", (_automaticLodPlan?.AutomaticPreserveUnitCount ?? 0).ToString())
                    .Replace("{game}", (_automaticLodPlan?.AutomaticGameLodUnitCount ?? 0).ToString())
                    : L(
                        usesMixedLod
                            ? "VersionCheckDetail.AssistedRepairSuccessMixedLod"
                            : usesGameLod
                                ? "VersionCheckDetail.AssistedRepairSuccessGameLod"
                                : "VersionCheckDetail.AssistedRepairSuccessPreserveLod",
                        usesMixedLod
                            ? "Updated {count} Unit(s) with per-Unit LOD choices. Original files were kept as backups."
                            : usesGameLod
                                ? "Updated {count} Unit(s) with current game LOD data. Original files were kept as backups."
                                : "Updated {count} Unit(s) while preserving mod LOD data. Original files were kept as backups.")
                        .Replace("{count}", result.AppliedActionCount.ToString())
                : L("VersionCheckDetail.RepairSuccess", "Repaired {count} issue(s). Original files were kept as backups.")
                    .Replace("{count}", result.AppliedActionCount.ToString());
            repairStatus.Visibility = Visibility.Visible;
            ResetRepairPlans();
            if (ReferenceEquals(_currentMessage, message))
                await LoadBackupHistoryAsync(message);
        }
        catch (Exception ex)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = L("VersionCheckDetail.RepairFailed", "Repair failed: {message}")
                    .Replace("{message}", ex.Message)
            });
        }
        finally
        {
            if (ReferenceEquals(_currentMessage, message))
            {
                repairProgress.Visibility = Visibility.Collapsed;
                SetRepairControlsBusy(false);
            }
        }
    }
}
