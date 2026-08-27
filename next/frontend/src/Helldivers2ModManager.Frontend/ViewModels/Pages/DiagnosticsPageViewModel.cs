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
    public DiagnosticsStatus? CurrentStatus { get => _currentStatus; private set => SetProperty(ref _currentStatus, value); }
    public int VersionCount { get => _versionCount; private set => SetProperty(ref _versionCount, value); }
    public int ConflictCount { get => _conflictCount; private set => SetProperty(ref _conflictCount, value); }
    public int DefiniteConflictCount { get => _definiteConflictCount; private set => SetProperty(ref _definiteConflictCount, value); }
    public int ArmorReuseCount { get => _armorReuseCount; private set => SetProperty(ref _armorReuseCount, value); }
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
            Status = "诊断状态已刷新。";
            Log("诊断状态已刷新。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.Message;
            Log(exception.Message);
        }
    }

    private async Task CheckVersionsAsync()
    {
        SetBusy(true, "正在检查版本兼容性…");
        try
        {
            var mods = (await _library.LoadAsync().ConfigureAwait(true)).Mods;
            var results = await _versionCheck.CheckAllAsync(mods).ConfigureAwait(true);
            VersionCount = results.Count;
            Status = $"版本检查完成：{results.Count} 个模组。";
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
        SetBusy(true, "正在扫描冲突…");
        try
        {
            var mods = (await _library.LoadAsync().ConfigureAwait(true)).Mods;
            var result = await _conflicts.ScanEnabledAsync(mods).ConfigureAwait(true);
            ConflictCount = result.Conflicts.Count;
            DefiniteConflictCount = result.DefiniteConflictCount;
            Status = $"冲突扫描完成：{result.ScannedUnitCount} 个 Unit，{result.Conflicts.Count} 个冲突。";
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
        SetBusy(true, "正在扫描护甲复用…");
        try
        {
            var mods = (await _library.LoadAsync().ConfigureAwait(true)).Mods;
            var result = await _armorReuse.ScanEnabledAsync(mods).ConfigureAwait(true);
            ArmorReuseCount = result.Records.Count;
            Status = $"护甲复用扫描完成：{result.Records.Count} 条记录。";
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
        SetBusy(true, "正在生成修复计划…");
        try
        {
            var plan = await _diagnostics.CreateRepairPlanAsync().ConfigureAwait(true);
            ApplyRepairPlan(plan);
            Status = $"修复计划已生成：{plan.Count(item => item.State == BatchRepairState.Repairable)} 项可修复。";
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
                "修复会修改模组补丁文件并创建备份。\n\n确定执行当前修复计划？",
                Title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true, "正在执行修复…");
        try
        {
            var results = await _diagnostics.ExecuteRepairsAsync([.. _repairPlan.Select(item => item.Source)]).ConfigureAwait(true);
            var changed = results.Count(result =>
                result.MetadataActionCount > 0 ||
                result.AssistedActionCount > 0 ||
                result.CompanionRecoveryCount > 0);
            ApplyRepairResults(results);
            Status = $"修复完成：{changed} 个计划项发生变更，{results.Count(item => item.State == BatchRepairState.Blocked)} 项被阻断。";
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
