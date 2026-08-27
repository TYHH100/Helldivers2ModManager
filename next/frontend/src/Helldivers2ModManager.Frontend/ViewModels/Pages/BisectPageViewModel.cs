using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Deployment;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Models;
using Helldivers2ModManager.Frontend.Services;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed record BisectRoundItem(int Index, string TestedNames, bool? Crashed);

public sealed class BisectPageViewModel : FrontendPageViewModel
{
    private readonly BisectService _bisect;
    private readonly DeploymentServiceFacade _deployment;
    private readonly ApplicationSettingsService _settings;
    private readonly LocalizationCatalog _localization;
    private IReadOnlyList<BisectCandidate> _pendingTested = [];
    private IReadOnlyList<ModItem> _deployableMods = [];

    public ObservableCollection<BisectRoundItem> Rounds { get; } = [];
    public ObservableCollection<string> Candidates { get; } = [];
    public ObservableCollection<string> Suspects { get; } = [];

    private bool _isBusy;
    private string _status = string.Empty;
    private bool _hasSession;
    private bool _isSingleVerification;

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool HasSession { get => _hasSession; private set => SetProperty(ref _hasSession, value); }
    public bool IsSingleVerification { get => _isSingleVerification; private set => SetProperty(ref _isSingleVerification, value); }
    public bool HasSingleCandidate => Candidates.Count == 1;

    public ICommand StartCommand { get; }
    public ICommand ReportCrashedCommand { get; }
    public ICommand ReportWorkingCommand { get; }
    public ICommand PrepareSingleCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand FinishCommand { get; }

    public override string Title => _localization.GetString("Nav.Bisect");

    public BisectPageViewModel(
        BisectService bisect,
        DeploymentServiceFacade deployment,
        ApplicationSettingsService settings,
        LocalizationCatalog localization)
    {
        _bisect = bisect;
        _deployment = deployment;
        _settings = settings;
        _localization = localization;
        StartCommand = new DelegateCommand(async _ => await StartAsync(), _ => CanStart());
        ReportCrashedCommand = new DelegateCommand(async _ => await ReportAsync(true), _ => CanReport());
        ReportWorkingCommand = new DelegateCommand(async _ => await ReportAsync(false), _ => CanReport());
        PrepareSingleCommand = new DelegateCommand(
            async _ => await PrepareSingleVerificationAsync(),
            _ => HasSession && !IsBusy && !IsSingleVerification && Candidates.Count == 1);
        CancelCommand = new DelegateCommand(async _ => await CancelAsync(), _ => HasSession && !IsBusy);
        FinishCommand = new DelegateCommand(async _ => await FinishAsync(), _ => HasSession && !IsBusy);
    }

    private bool CanStart() => !HasSession && !IsBusy && !string.IsNullOrWhiteSpace(_settings.Current.GameDirectory);

    private bool CanReport() => HasSession && !IsBusy && (_pendingTested.Count > 0 || IsSingleVerification);

    private async Task StartAsync()
    {
        if (!ValidateGameDirectory())
        {
            return;
        }

        try
        {
            var round = await _bisect.StartAsync().ConfigureAwait(true);
            _pendingTested = round.Tested;
            _deployableMods = round.DeployableMods;
            RefreshSession(round.Session);
            Status = _localization.GetString("Bisect.SessionStarted");
            await DeployRoundAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    private async Task ReportAsync(bool crashed)
    {
        if (IsSingleVerification)
        {
            var session = await _bisect.ApplySingleReportAsync(crashed).ConfigureAwait(true);
            RefreshSession(session);
            Status = session.Suspects.Count > 0
                ? _localization.GetString("Bisect.SuspectConfirmed")
                : _localization.GetString("Bisect.SuspectCleared");
            return;
        }

        if (_pendingTested.Count == 0)
        {
            return;
        }

            var updated = await _bisect.ApplyReportAsync(_pendingTested, crashed).ConfigureAwait(true);
        RefreshSession(updated);
        if (updated.Candidates.Count == 1)
        {
            Status = _localization.GetString("Bisect.ReadySingleVerification");
            NotifyCommands();
            return;
        }

        if (updated.Candidates.Count == 0)
        {
            Status = _localization.GetString("Bisect.NoCandidates");
            return;
        }

        var nextRound = await _bisect.PrepareRoundAsync().ConfigureAwait(true);
        _pendingTested = nextRound.Tested;
        _deployableMods = nextRound.DeployableMods;
        RefreshSession(nextRound.Session);
        await DeployRoundAsync().ConfigureAwait(true);
    }

    public async Task PrepareSingleVerificationAsync()
    {
        if (!HasSession || IsBusy)
        {
            return;
        }

        try
        {
            var single = await _bisect.PrepareSingleVerificationAsync().ConfigureAwait(true);
            _pendingTested = single.Tested;
            _deployableMods = single.DeployableMods;
            IsSingleVerification = true;
            RefreshSession(single.Session);
            Status = _localization.GetString("Bisect.SingleVerificationDeploying");
            await DeployRoundAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    private async Task CancelAsync()
    {
        if (MessageBox.Show(
                _localization.GetString("Bisect.CancelConfirm"),
                _localization.GetString("Nav.Bisect"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true, _localization.GetString("Bisect.Restoring"));
        try
        {
            await _bisect.RestoreOriginalAsync().ConfigureAwait(true);
            ClearSession();
            Status = _localization.GetString("Bisect.Restored");
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async Task FinishAsync()
    {
        try
        {
            var result = await _bisect.FinishAsync(Suspects.Count > 0).ConfigureAwait(true);
            ClearSession();
            Status = result.Session.Suspects.Count > 0
                ? _localization.GetString("Bisect.FinishedWithSuspects")
                : _localization.GetString("Bisect.FinishedNoSuspects");
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    private async Task DeployRoundAsync()
    {
        SetBusy(true, _localization.GetString("Deployment.Deploying"));
        try
        {
            var progress = new Progress<DeploymentProgress>(item => Status = string.Format(
                _localization.GetString("Deployment.ProgressFormat"),
                item.CompletedFiles,
                item.TotalFiles,
                item.CurrentFile));
            var result = await _deployment.DeployAsync(_deployableMods, progress).ConfigureAwait(true);
            if (result.Status != BackgroundTaskStatus.Succeeded)
            {
                throw new InvalidOperationException(result.Error?.Message ?? _localization.GetString("Deployment.Failed"));
            }

            Status = _localization.GetString("Bisect.RoundDeployed");
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private void RefreshSession(BisectSession? session)
    {
        HasSession = session is not null;
        Rounds.Clear();
        Candidates.Clear();
        Suspects.Clear();
        if (session is null)
        {
            return;
        }

        foreach (var round in session.Rounds)
        {
            Rounds.Add(new(round.Index, string.Join(", ", round.Tested.Select(candidate => candidate.Name)), round.Crashed));
        }

        foreach (var candidate in session.Candidates)
        {
            Candidates.Add(candidate.Name);
        }

        foreach (var suspect in session.Suspects)
        {
            Suspects.Add(suspect.Name);
        }

        OnPropertyChanged(nameof(HasSingleCandidate));
        NotifyCommands();
    }

    private void ClearSession()
    {
        _pendingTested = [];
        _deployableMods = [];
        IsSingleVerification = false;
        RefreshSession(null);
    }

    private bool ValidateGameDirectory()
    {
        if (Directory.Exists(Path.Combine(_settings.Current.GameDirectory, "data")))
        {
            return true;
        }

        Status = _localization.GetString("Bisect.GameDirectoryMissing");
        return false;
    }

    private void SetBusy(bool busy, string status)
    {
        IsBusy = busy;
        Status = status;
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        ((DelegateCommand)StartCommand).NotifyCanExecuteChanged();
        ((DelegateCommand)ReportCrashedCommand).NotifyCanExecuteChanged();
        ((DelegateCommand)ReportWorkingCommand).NotifyCanExecuteChanged();
        ((DelegateCommand)PrepareSingleCommand).NotifyCanExecuteChanged();
        ((DelegateCommand)CancelCommand).NotifyCanExecuteChanged();
        ((DelegateCommand)FinishCommand).NotifyCanExecuteChanged();
    }
}
