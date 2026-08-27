using System.Collections.ObjectModel;
using System.Windows.Input;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Services;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed class VersionCheckPageViewModel : FrontendPageViewModel
{
    private readonly VersionCheckFacade _versionCheck;
    private readonly ModLibraryService _library;
    private readonly LocalizationCatalog _localization;
    private bool _isBusy;
    private string _status = string.Empty;

    public ObservableCollection<VersionCheckItem> Results { get; } = [];
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public ICommand CheckAllCommand { get; }

    public override string Title => _localization.GetString("Nav.VersionCheck");

    public VersionCheckPageViewModel(
        VersionCheckFacade versionCheck,
        ModLibraryService library,
        LocalizationCatalog localization)
    {
        _versionCheck = versionCheck;
        _library = library;
        _localization = localization;
        CheckAllCommand = new DelegateCommand(async _ => await CheckAllAsync(), _ => !IsBusy);
    }

    private async Task CheckAllAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = _localization.GetString("Version.Checking");
        try
        {
            var mods = (await _library.LoadAsync().ConfigureAwait(true)).Mods;
            var results = await _versionCheck.CheckAllAsync(mods).ConfigureAwait(true);
            Results.Clear();
            foreach (var result in results.OrderByDescending(item => item.Status).ThenBy(item => item.ModName))
            {
                Results.Add(result);
            }

            Status = string.Format(_localization.GetString("Version.CompletedFormat"), results.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
