using System.Collections.ObjectModel;
using System.Windows.Input;
using Helldivers2ModManager.Core.Analysis;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Services;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed class ArmorReusePageViewModel : FrontendPageViewModel
{
    private readonly ArmorReuseFacade _armorReuse;
    private readonly ModLibraryService _library;
    private readonly LocalizationCatalog _localization;
    private bool _isBusy;
    private string _status = string.Empty;

    public ObservableCollection<ArmorReuseRecord> Results { get; } = [];
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public ICommand ScanCommand { get; }

    public override string Title => _localization.GetString("Nav.ArmorReuse");

    public ArmorReusePageViewModel(
        ArmorReuseFacade armorReuse,
        ModLibraryService library,
        LocalizationCatalog localization)
    {
        _armorReuse = armorReuse;
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
        Status = _localization.GetString("ArmorReusePage.Scanning");
        try
        {
            var mods = (await _library.LoadAsync().ConfigureAwait(true)).Mods;
            var result = await _armorReuse.ScanEnabledAsync(mods).ConfigureAwait(true);
            Results.Clear();
            foreach (var record in result.Records)
            {
                Results.Add(record);
            }

            Status = string.Format(
                _localization.GetString("ArmorReusePage.Found"),
                result.Records.Count,
                result.ScannedModCount,
                result.ScannedUnitCount);
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
