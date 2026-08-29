using System.IO;
using System.Windows.Input;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Nexus;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Navigation;
using Helldivers2ModManager.Frontend.Services;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed class NexusDownloadPageViewModel : FrontendPageViewModel
{
    private readonly NexusDownloadService _nexus;
    private readonly ModLibraryService _library;
    private readonly INavigationStore _navigation;
    private readonly LocalizationCatalog _localization;
    private string _nexusUrl = string.Empty;
    private NexusMod? _selectedMod;
    private NexusFile? _selectedFile;
    private bool _isBusy;
    private string _status = string.Empty;

    public string NexusUrl { get => _nexusUrl; set => SetProperty(ref _nexusUrl, value); }
    public NexusMod? SelectedMod { get => _selectedMod; private set => SetProperty(ref _selectedMod, value); }
    public NexusFile? SelectedFile { get => _selectedFile; set => SetProperty(ref _selectedFile, value); }
    public IReadOnlyList<NexusFile> Files { get; private set; } = [];
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public ICommand FetchCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand BackCommand { get; }

    public override string Title => _localization.GetString("Nav.NexusDownload");

    public NexusDownloadPageViewModel(
        NexusDownloadService nexus,
        ModLibraryService library,
        INavigationStore navigation,
        LocalizationCatalog localization)
    {
        _nexus = nexus;
        _library = library;
        _navigation = navigation;
        _localization = localization;
        FetchCommand = new DelegateCommand(async _ => await FetchAsync(), _ => !IsBusy);
        DownloadCommand = new DelegateCommand(async _ => await DownloadAndImportAsync(), _ => CanDownload());
        BackCommand = new DelegateCommand(_ => _navigation.Navigate("Library"));
    }

    public bool CanDownload() => !IsBusy && SelectedMod is not null && SelectedFile is not null;

    private async Task FetchAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var parsed = NexusDownloadService.ParseUrl(NexusUrl);
        if (parsed is null)
        {
            Status = _localization.GetString("Next.Nexus.InvalidUrl");
            return;
        }

        IsBusy = true;
        NotifyCommands();
        Status = _localization.GetString("Nexus.Fetching");
        try
        {
            var result = await _nexus.FetchAsync(parsed.Value.GameDomain, parsed.Value.ModId).ConfigureAwait(true);
            SelectedMod = result.Mod;
            Files = result.Files;
            SelectedFile = result.Files.FirstOrDefault(file => file.IsPrimary == true) ?? result.Files.FirstOrDefault();
            Status = result.Files.Count == 0
                ? _localization.GetString("Nexus.NoFiles")
                : string.Format(_localization.GetString("Nexus.FoundFormat"), result.Files.Count);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private async Task DownloadAndImportAsync()
    {
        if (!CanDownload() || SelectedMod is null || SelectedFile is null)
        {
            return;
        }

        var parsed = NexusDownloadService.ParseUrl(NexusUrl);
        if (parsed is null)
        {
            Status = _localization.GetString("Next.Nexus.InvalidUrl");
            return;
        }

        IsBusy = true;
        NotifyCommands();
        string? downloadedPath = null;
        try
        {
            Status = _localization.GetString("Nexus.Downloading");
            downloadedPath = await _nexus.DownloadAsync(parsed.Value.GameDomain, SelectedMod, SelectedFile).ConfigureAwait(true);

            Status = _localization.GetString("Nexus.Importing");
            var imported = await _library.ImportAsync([downloadedPath]).ConfigureAwait(true);
            Status = imported.Problems.Count == 0
                ? string.Format(_localization.GetString("Nexus.ImportedFormat"), SelectedMod.Name)
                : string.Join(Environment.NewLine, imported.Problems);
            if (imported.Problems.Count == 0)
            {
                _navigation.Navigate("Library");
            }
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            if (downloadedPath is not null)
            {
                try
                {
                    if (File.Exists(downloadedPath))
                    {
                        File.Delete(downloadedPath);
                    }
                }
                catch (IOException)
                {
                }
            }

            IsBusy = false;
            NotifyCommands();
        }
    }

    private void NotifyCommands()
    {
        ((DelegateCommand)FetchCommand).NotifyCanExecuteChanged();
        ((DelegateCommand)DownloadCommand).NotifyCanExecuteChanged();
    }
}
