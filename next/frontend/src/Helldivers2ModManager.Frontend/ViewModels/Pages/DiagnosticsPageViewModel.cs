using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Repair;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Services;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed record DiagnosticLogItem(string Time, string Message);

public sealed class DiagnosticsPageViewModel : FrontendPageViewModel
{
    private readonly ModLibraryService _library;
    private readonly VersionCheckFacade _versionCheck;
    private readonly ConflictAnalysisFacade _conflicts;
    private readonly ArmorReuseFacade _armorReuse;
    private readonly DiagnosticsFacade _diagnostics;
    private readonly LocalizationCatalog _localization;
    private IReadOnlyList<RepairPlanItem> _repairPlan = [];

    public ObservableCollection<DiagnosticLogItem> Logs { get; } = [];
    public ObservableCollection<RepairPlanItem> Repairs { get; } = [];

    private bool _isBusy;
    private string _status = string.Empty;
    private DiagnosticsStatus? _currentStatus;
    private int _versionCount;
    private int _conflictCount;
    private int _definiteConflictCount;
    private int _armorReuseCount;

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public DiagnosticsStatus? CurrentStatus
    {
        get => _currentStatus;
        private set
        {
            if (SetProperty(ref _currentStatus, value))
            {
                OnPropertyChanged(nameof(StatusSummary));
            }
        }
    }

    public int VersionCount
    {
        get => _versionCount;
        private set
        {
            if (SetProperty(ref _versionCount, value))
            {
                OnPropertyChanged(nameof(CountersSummary));
            }
        }
    }

    public int ConflictCount
    {
        get => _conflictCount;
        private set
        {
            if (SetProperty(ref _conflictCount, value))
            {
                OnPropertyChanged(nameof(CountersSummary));
            }
        }
    }

    public int DefiniteConflictCount
    {
        get => _definiteConflictCount;
        private set
        {
            if (SetProperty(ref _definiteConflictCount, value))
            {
                OnPropertyChanged(nameof(CountersSummary));
            }
        }
    }

    public int ArmorReuseCount
    {
        get => _armorReuseCount;
        private set
        {
            if (SetProperty(ref _armorReuseCount, value))
            {
                OnPropertyChanged(nameof(CountersSummary));
            }
        }
    }

    public string StatusSummary => CurrentStatus is null
        ? string.Empty
        : string.Format(
            _localization.GetString("Next.Diagnostics.SummaryFormat"),
            CurrentStatus.ModCount,
            CurrentStatus.EnabledCount,
            CurrentStatus.UseSymbolicLinks);

    public string CountersSummary => string.Format(
        _localization.GetString("Next.Diagnostics.CountersFormat"),
        VersionCount,
        ConflictCount,
        DefiniteConflictCount,
        ArmorReuseCount);
    public bool HasRepairPlan => _repairPlan.Count > 0;

    public ICommand RefreshCommand { get; }
    public ICommand CheckVersionsCommand { get; }
    public ICommand ScanConflictsCommand { get; }
    public ICommand ScanArmorReuseCommand { get; }
    public ICommand CreateRepairPlanCommand { get; }
    public ICommand ExecuteRepairsCommand { get; }

    public override string Title => _localization.GetString("Nav.BackendTestCenter");

    public DiagnosticsPageViewModel(
        ModLibraryService library,
        VersionCheckFacade versionCheck,
        ConflictAnalysisFacade conflicts,
        ArmorReuseFacade armorReuse,
        DiagnosticsFacade diagnostics,
        LocalizationCatalog localization)
    {
        _library = library;
        _versionCheck = versionCheck;
        _conflicts = conflicts;
        _armorReuse = armorReuse;
        _diagnostics = diagnostics;
        _localization = localization;
        RefreshCommand = new DelegateCommand(async _ => await RefreshAsync(), _ => !IsBusy);
        CheckVersionsCommand = new DelegateCommand(async _ => await CheckVersionsAsync(), _ => !IsBusy);
        ScanConflictsCommand = new DelegateCommand(async _ => await ScanConflictsAsync(), _ => !IsBusy);
        ScanArmorReuseCommand = new DelegateCommand(async _ => await ScanArmorReuseAsync(), _ => !IsBusy);
        CreateRepairPlanCommand = new DelegateCommand(async _ => await CreateRepairPlanAsync(), _ => !IsBusy);
        ExecuteRepairsCommand = new DelegateCommand(async _ => await ExecuteRepairsAsync(), _ => !IsBusy && HasRepairPlan);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentStatus is null)
        {
            await RefreshCoreAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task RefreshAsync() => await RefreshCoreAsync(CancellationToken.None).ConfigureAwait(true);

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            CurrentStatus = await _diagnostics.GetStatusAsync(cancellationToken).ConfigureAwait(true);
            Status = _localization.GetString("Next.Diagnostics.StatusRefreshed");
            Log(Status);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.Message;
            Log(exception.Message);
        }
    }

    private async Task CheckVersionsAsync()
    {
        SetBusy(true, _localization.GetString("Next.Diagnostics.CheckingVersions"));
        try
        {
            var mods = (await _library.LoadAsync().ConfigureAwait(true)).Mods;
            var results = await _versionCheck.CheckAllAsync(mods).ConfigureAwait(true);
            VersionCount = results.Count;
            Status = string.Format(_localization.GetString("Next.Diagnostics.VersionCheckDoneFormat"), results.Count);
            Log(Status);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.Message;
            Log(exception.Message);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async Task ScanConflictsAsync()
    {
        SetBusy(true, _localization.GetString("Next.Diagnostics.ScanningConflicts"));
        try
        {
            var mods = (await _library.LoadAsync().ConfigureAwait(true)).Mods;
            var result = await _conflicts.ScanEnabledAsync(mods).ConfigureAwait(true);
            ConflictCount = result.Conflicts.Count;
            DefiniteConflictCount = result.DefiniteConflictCount;
            Status = string.Format(
                _localization.GetString("Next.Diagnostics.ConflictScanDoneFormat"),
                result.ScannedUnitCount,
                result.Conflicts.Count);
            Log(Status);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.Message;
            Log(exception.Message);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async Task ScanArmorReuseAsync()
    {
        SetBusy(true, _localization.GetString("Next.Diagnostics.ScanningArmorReuse"));
        try
        {
            var mods = (await _library.LoadAsync().ConfigureAwait(true)).Mods;
            var result = await _armorReuse.ScanEnabledAsync(mods).ConfigureAwait(true);
            ArmorReuseCount = result.Records.Count;
            Status = string.Format(_localization.GetString("Next.Diagnostics.ArmorReuseDoneFormat"), result.Records.Count);
            Log(Status);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.Message;
            Log(exception.Message);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async Task CreateRepairPlanAsync()
    {
        SetBusy(true, _localization.GetString("Next.Diagnostics.GeneratingPlan"));
        try
        {
            var plan = await _diagnostics.CreateRepairPlanAsync().ConfigureAwait(true);
            ApplyRepairPlan(plan);
            Status = string.Format(
                _localization.GetString("Next.Diagnostics.PlanDoneFormat"),
                plan.Count(item => item.State == BatchRepairState.Repairable));
            foreach (var item in plan.Take(20))
            {
                Log($"{item.ModName}: {item.StateText} - {item.Message}");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.Message;
            Log(exception.Message);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async Task ExecuteRepairsAsync()
    {
        if (!HasRepairPlan)
        {
            return;
        }

        if (MessageBox.Show(
                _localization.GetString("Next.Diagnostics.ExecuteConfirm"),
                Title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true, _localization.GetString("Next.Diagnostics.ExecutingRepairs"));
        try
        {
            var results = await _diagnostics.ExecuteRepairsAsync([.. _repairPlan.Select(item => item.Source)]).ConfigureAwait(true);
            var changed = results.Count(result =>
                result.MetadataActionCount > 0 ||
                result.AssistedActionCount > 0 ||
                result.CompanionRecoveryCount > 0);
            ApplyRepairResults(results);
            Status = string.Format(
                _localization.GetString("Next.Diagnostics.RepairDoneFormat"),
                changed,
                results.Count(item => item.State == BatchRepairState.Blocked));
            foreach (var item in results.Take(30))
            {
                Log($"{item.ModId}: {item.Message}");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.Message;
            Log(exception.Message);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private void ApplyRepairPlan(IReadOnlyList<RepairPlanItem> plan)
    {
        _repairPlan = plan;
        Repairs.Clear();
        foreach (var item in plan)
        {
            Repairs.Add(item);
        }

        OnPropertyChanged(nameof(HasRepairPlan));
        NotifyCommands();
    }

    private void ApplyRepairResults(IReadOnlyList<BatchRepairItem> results)
    {
        var mapped = results.Join(
            _repairPlan,
            static repaired => repaired.ModId,
            static planned => planned.ModId,
            static (repaired, planned) => planned with
            {
                StateText = repaired.State.ToString(),
                Message = repaired.Message,
                MetadataActionCount = repaired.MetadataActionCount,
                AssistedActionCount = repaired.AssistedActionCount,
                CompanionRecoveryCount = repaired.CompanionRecoveryCount,
                Source = repaired,
            }).ToArray();
        ApplyRepairPlan(mapped);
    }

    private void NotifyCommands()
    {
        ((DelegateCommand)RefreshCommand).NotifyCanExecuteChanged();
        ((DelegateCommand)CheckVersionsCommand).NotifyCanExecuteChanged();
        ((DelegateCommand)ScanConflictsCommand).NotifyCanExecuteChanged();
        ((DelegateCommand)ScanArmorReuseCommand).NotifyCanExecuteChanged();
        ((DelegateCommand)CreateRepairPlanCommand).NotifyCanExecuteChanged();
        ((DelegateCommand)ExecuteRepairsCommand).NotifyCanExecuteChanged();
    }

    private void SetBusy(bool busy, string status)
    {
        IsBusy = busy;
        Status = status;
        NotifyCommands();
    }

    private void Log(string message) => Logs.Insert(0, new(DateTime.Now.ToString("HH:mm:ss"), message));
}
