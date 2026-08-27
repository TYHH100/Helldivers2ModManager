using System.Collections.ObjectModel;
using System.Windows.Input;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Services;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed class ConflictScanPageViewModel : FrontendPageViewModel
{
    private readonly ConflictAnalysisFacade _conflicts;
    private readonly ModLibraryService _library;
    private readonly LocalizationCatalog _localization;
    private bool _isBusy;
    private string _status = string.Empty;

    public ObservableCollection<ConflictDisplayItem> Results { get; } = [];
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public ICommand ScanCommand { get; }

    public override string Title => _localization.GetString("Nav.ConflictScan");

    public ConflictScanPageViewModel(
        ConflictAnalysisFacade conflicts,
        ModLibraryService library,
        LocalizationCatalog localization)
    {
        _conflicts = conflicts;
        _library = library;
        _localization = localization;
        ScanCommand = new DelegateCommand(async _ => await ScanAsync(), _ => !IsBusy);
    }

    private async Task ScanAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = _localization.GetString("Conflict.Scanning");
        try
        {
            var mods = (await _library.LoadAsync().ConfigureAwait(true)).Mods;
            var result = await _conflicts.ScanEnabledAsync(mods).ConfigureAwait(true);
            Results.Clear();
            foreach (var conflict in result.Conflicts)
            {
                Results.Add(conflict);
            }

            Status = string.Format(
                _localization.GetString("Conflict.CompletedFormat"),
                result.ScannedModCount,
                result.ScannedUnitCount,
                result.Conflicts.Count,
                result.DefiniteConflictCount);
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
