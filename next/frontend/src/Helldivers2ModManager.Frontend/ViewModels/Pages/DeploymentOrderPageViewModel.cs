using System.Collections.ObjectModel;
using System.Windows.Input;
using Helldivers2ModManager.Core.Deployment;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Models;
using Helldivers2ModManager.Frontend.Services;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed class DeploymentOrderPageViewModel : FrontendPageViewModel
{
    private readonly DeploymentServiceFacade _deployment;
    private readonly LocalizationCatalog _localization;
    private bool _isBusy;
    private string _status = string.Empty;

    public ObservableCollection<ModItem> Mods { get; } = [];

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public ICommand RefreshCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand DeployCommand { get; }

    public ICommand PurgeCommand { get; }

    public override string Title => _localization.GetString("Nav.DeploymentOrder");

    public string DeployLabel => _localization.GetString("Frontend.Deploy");

    public string RefreshLabel => _localization.GetString("Library.Refresh");

    public string PurgeLabel => _localization.GetString("Deployment.Purge");

    public DeploymentOrderPageViewModel(
        DeploymentServiceFacade deployment,
        LocalizationCatalog localization)
    {
        _deployment = deployment;
        _localization = localization;
        RefreshCommand = new DelegateCommand(async _ => await RefreshAsync());
        MoveUpCommand = new DelegateCommand(MoveUp);
        MoveDownCommand = new DelegateCommand(MoveDown);
        DeployCommand = new DelegateCommand(async _ => await DeployAsync());
        PurgeCommand = new DelegateCommand(async _ => await PurgeAsync());
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Mods.Count == 0)
        {
            await RefreshCoreAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task RefreshAsync()
    {
        Mods.Clear();
        await RefreshCoreAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        SetBusy(true, _localization.GetString("Deployment.Loading"));
        try
        {
            var mods = await _deployment.LoadEnabledModsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in mods)
            {
                Mods.Add(item);
            }

            Status = string.Format(_localization.GetString("Deployment.LoadedFormat"), Mods.Count);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private void MoveUp(object? parameter)
    {
        Move(parameter, -1);
    }

    private void MoveDown(object? parameter)
    {
        Move(parameter, 1);
    }

    private void Move(object? parameter, int direction)
    {
        if (parameter is not ModItem item)
        {
            return;
        }

        var index = Mods.IndexOf(item);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Mods.Count)
        {
            return;
        }

        Mods.Move(index, target);
        Status = _localization.GetString("Deployment.OrderChanged");
    }

    private async Task DeployAsync()
    {
        if (Mods.Count == 0)
        {
            Status = _localization.GetString("Deployment.NoEnabledMods");
            return;
        }

        var progress = new Progress<DeploymentProgress>(item => Status = string.Format(
            _localization.GetString("Deployment.ProgressFormat"),
            item.CompletedFiles,
            item.TotalFiles,
            item.CurrentFile));
        SetBusy(true, _localization.GetString("Deployment.Deploying"));
        try
        {
            var result = await _deployment.DeployAsync([.. Mods], progress).ConfigureAwait(true);
            Status = result.Status switch
            {
                BackgroundTaskStatus.Succeeded => _localization.GetString("Deployment.Succeeded"),
                BackgroundTaskStatus.Canceled => _localization.GetString("Deployment.Canceled"),
                _ => result.Error?.Message ?? _localization.GetString("Deployment.Failed"),
            };
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async Task PurgeAsync()
    {
        if (System.Windows.MessageBox.Show(
                _localization.GetString("Deployment.PurgeConfirm"),
                _localization.GetString("Deployment.PurgeTitle"),
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true, _localization.GetString("Deployment.Purging"));
        try
        {
            var result = await _deployment.PurgeAsync().ConfigureAwait(true);
            Status = result.Status == BackgroundTaskStatus.Succeeded
                ? _localization.GetString("Deployment.PurgeSucceeded")
                : result.Error?.Message ?? _localization.GetString("Deployment.Failed");
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private void SetBusy(bool busy, string status)
    {
        IsBusy = busy;
        Status = status;
    }
}
