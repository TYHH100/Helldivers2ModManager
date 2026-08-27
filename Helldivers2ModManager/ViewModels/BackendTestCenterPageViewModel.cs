using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class BackendTestCenterPageViewModel : PageViewModelBase
{
    private readonly ModService _modService;
    private readonly SettingsService _settingsService;
    private readonly VersionCheckService _versionCheckService;
    private readonly ModConflictService _conflictService;
    private readonly ArmorReuseService _armorReuseService;
    private readonly BackgroundTaskService _backgroundTaskService;
    private readonly LocalizationService _localizationService;
    private readonly Lazy<NavigationStore> _navigationStore;

    public ObservableCollection<LogEntry> Logs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    private bool _isBusy;

    public override string Title => _localizationService["BackendTestCenter.Title"];

    public bool IsReady => !IsBusy;

    public string BusyText => IsBusy
        ? _localizationService["BackendTestCenter.Busy"]
        : _localizationService["BackendTestCenter.Ready"];

    public string StorageStatus => SafeSettings(settings => _localizationService["BackendTestCenter.Storage"]
        .Replace("{path}", settings.StorageDirectory));

    public string GameStatus => SafeSettings(settings => _localizationService["BackendTestCenter.Game"]
        .Replace("{path}", settings.GameDirectory));

    public string ModCountStatus => _modService.Initialized
        ? _localizationService["BackendTestCenter.ModCount"].Replace("{count}", _modService.Mods.Count.ToString())
        : _localizationService["BackendTestCenter.NotReady"];

    public string DeploymentMode => SafeSettings(settings => _localizationService[settings.UseSymbolicLinks
        ? "BackendTestCenter.SymbolicLinks"
        : "BackendTestCenter.FileCopy"]);

    public bool CanDeploy => IsReady && _modService.Initialized && _modService.Mods.Any(static mod => mod.Enabled);

    public BackendTestCenterPageViewModel(
        ModService modService,
        SettingsService settingsService,
        VersionCheckService versionCheckService,
        ModConflictService conflictService,
        ArmorReuseService armorReuseService,
        BackgroundTaskService backgroundTaskService,
        LocalizationService localizationService,
        IServiceProvider serviceProvider)
    {
        _modService = modService;
        _settingsService = settingsService;
        _versionCheckService = versionCheckService;
        _conflictService = conflictService;
        _armorReuseService = armorReuseService;
        _backgroundTaskService = backgroundTaskService;
        _localizationService = localizationService;
        _navigationStore = new Lazy<NavigationStore>(serviceProvider.GetRequiredService<NavigationStore>);
        RefreshMods();
        Log(_localizationService["BackendTestCenter.Welcome"]);
    }

    [RelayCommand]
    private void GoBack() => _navigationStore.Value.Navigate<DashboardPageViewModel>();

    [RelayCommand]
    private void OpenDashboard() => _navigationStore.Value.Navigate<DashboardPageViewModel>();

    [RelayCommand]
    private void OpenResourceViewer() => _navigationStore.Value.Navigate<PatchResourceViewerPageViewModel>();

    [RelayCommand]
    private void OpenModelPreview() => _navigationStore.Value.Navigate<ModelPreviewPageViewModel>();

    [RelayCommand]
    private void OpenSettings() => _navigationStore.Value.Navigate<SettingsPageViewModel>();

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RescanMods()
    {
        await RunResultAsync<ModProblem[]>("RescanMods", async () =>
        {
            var problems = await Task.Run(_modService.RescanMods);
            RefreshMods();
            Log(_localizationService["BackendTestCenter.RescanDone"]
                .Replace("{count}", _modService.Mods.Count.ToString())
                .Replace("{problems}", problems.Length.ToString()));
            foreach (var problem in problems.Take(10))
                Log($"{_localizationService["BackendTestCenter.WarningPrefix"]} {problem.Kind}");
            return problems;
        });
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ImportArchive()
    {
        var dialog = new OpenFileDialog
        {
            Filter = _localizationService["BackendTestCenter.ArchiveFilter"],
            Multiselect = false,
            Title = _localizationService["BackendTestCenter.ImportArchive"]
        };
        if (dialog.ShowDialog() != true)
            return;

        var file = new FileInfo(dialog.FileName);
        await RunActionAsync("ImportArchive", async (context, _) =>
        {
            context.ReportStep(_localizationService["BackendTestCenter.Importing"]
                .Replace("{name}", file.Name));
            var problems = await _modService.TryAddModFromArchiveAsync(
                file,
                (done, total, name) => context.ReportStepDetail(
                    _localizationService["BackendTestCenter.ImportProgress"]
                        .Replace("{done}", done.ToString())
                        .Replace("{total}", total.ToString())
                        .Replace("{name}", name)));
            RefreshMods();
            Log(_localizationService["BackendTestCenter.ImportDone"]
                .Replace("{count}", _modService.Mods.Count.ToString())
                .Replace("{problems}", problems.Length.ToString()));
        });
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task CheckVersions()
    {
        await RunResultAsync("CheckVersions", () => _versionCheckService.CheckAllModsAsync(_modService.Mods), result =>
        {
            Log(_localizationService["BackendTestCenter.VersionDone"]
                .Replace("{count}", result.Count.ToString()));
            foreach (var group in result.Values.GroupBy(static item => item.Status).OrderByDescending(static group => group.Count()))
                Log($"  {group.Key}: {group.Count()}");
        });
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ScanConflicts()
    {
        var mods = GetEnabledMods();
        if (mods.Length == 0)
        {
            LogNoEnabledMods();
            return;
        }

        await RunResultAsync("ScanConflicts", () => _conflictService.AnalyzeAsync(mods), result =>
        {
            Log(_localizationService["BackendTestCenter.ConflictDone"]
                .Replace("{mods}", result.ScannedModCount.ToString())
                .Replace("{patches}", result.ScannedPatchCount.ToString())
                .Replace("{units}", result.ScannedUnitCount.ToString())
                .Replace("{conflicts}", result.Conflicts.Count.ToString()));
        });
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ScanArmorReuse()
    {
        var mods = GetEnabledMods();
        if (mods.Length == 0)
        {
            LogNoEnabledMods();
            return;
        }

        await RunResultAsync("ScanArmorReuse", () => _armorReuseService.AnalyzeAsync(mods), result =>
        {
            Log(_localizationService["BackendTestCenter.ArmorDone"]
                .Replace("{mods}", result.ScannedModCount.ToString())
                .Replace("{patches}", result.ScannedPatchCount.ToString())
                .Replace("{units}", result.ScannedUnitCount.ToString())
                .Replace("{records}", result.Records.Count.ToString()));
        });
    }

    [RelayCommand]
    private void DeployEnabledMods()
    {
        var mods = GetEnabledMods();
        if (mods.Length == 0)
        {
            LogNoEnabledMods();
            return;
        }

        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = _localizationService["BackendTestCenter.DeployConfirmTitle"],
            Message = _localizationService["BackendTestCenter.DeployConfirmMessage"]
                .Replace("{count}", mods.Length.ToString()),
            Confirm = () => _ = DeployEnabledCoreAsync(mods)
        });
    }

    private async Task DeployEnabledCoreAsync(ModData[] mods)
    {
        await RunActionAsync("DeployEnabled", async (context, _) =>
        {
            context.ReportStep(_localizationService["BackendTestCenter.Deploying"]
                .Replace("{count}", mods.Length.ToString()));

            await _modService.DeployAsync(
                mods,
                reportStep: name => context.ReportStep(name),
                reportStepCompleted: context.CompleteStep,
                reportStepFailed: context.FailStep);

            Log(_localizationService["BackendTestCenter.DeployDone"].Replace("{count}", mods.Length.ToString()));
        });
    }

    [RelayCommand]
    private void OpenLogs()
    {
        OpenDirectory(Path.Combine(AppContext.BaseDirectory, "logs"));
    }

    private async Task RunResultAsync<T>(string operation, Func<Task<T>> work, Action<T>? onComplete = null)
    {
        SetBusy(true);
        try
        {
            var result = await _backgroundTaskService.RunAsync(
                _localizationService["BackendTestCenter.Title"],
                _localizationService[$"BackendTestCenter.Operation.{operation}"],
                (_, _) => work());
            if (onComplete is not null)
                RunOnUi(() => onComplete(result));
            Log(_localizationService["BackendTestCenter.Success"]);
        }
        catch (OperationCanceledException)
        {
            Log(_localizationService["BackendTestCenter.Cancelled"]);
        }
        catch (Exception ex)
        {
            Log(_localizationService["BackendTestCenter.Failed"].Replace("{message}", ex.Message));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RunActionAsync(
        string operation,
        Func<BackgroundTaskService.BackgroundTaskContext, CancellationToken, Task> work)
    {
        SetBusy(true);
        try
        {
            await _backgroundTaskService.RunAsync(
                _localizationService["BackendTestCenter.Title"],
                _localizationService[$"BackendTestCenter.Operation.{operation}"],
                work);
            Log(_localizationService["BackendTestCenter.Success"]);
        }
        catch (OperationCanceledException)
        {
            Log(_localizationService["BackendTestCenter.Cancelled"]);
        }
        catch (Exception ex)
        {
            Log(_localizationService["BackendTestCenter.Failed"].Replace("{message}", ex.Message));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private ModData[] GetEnabledMods() => _modService.Initialized
        ? _modService.Mods.Where(static mod => mod.Enabled).ToArray()
        : [];

    private void RefreshMods()
    {
        RunOnUi(() =>
        {
            OnPropertyChanged(nameof(ModCountStatus));
            OnPropertyChanged(nameof(CanDeploy));
        });
    }

    private void LogNoEnabledMods() => Log(_localizationService["BackendTestCenter.NoEnabledMods"]);

    private void Log(string message) => RunOnUi(() => Logs.Insert(0, new LogEntry(DateTime.Now, message)));

    private void SetBusy(bool value)
    {
        RunOnUi(() =>
        {
            IsBusy = value;
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(BusyText));
            OnPropertyChanged(nameof(CanDeploy));
        });
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    private string SafeSettings(Func<SettingsService, string> selector)
    {
        if (!_settingsService.Initialized)
            return _localizationService["BackendTestCenter.NotReady"];

        try
        {
            return selector(_settingsService);
        }
        catch
        {
            return _localizationService["BackendTestCenter.NotReady"];
        }
    }

    private static void OpenDirectory(string path)
    {
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

internal sealed class LogEntry(DateTime time, string message)
{
    public string DisplayTime => time.ToString("HH:mm:ss");

    public string Message { get; } = message;
}
