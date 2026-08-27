using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Deployment;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Models;
using Helldivers2ModManager.Frontend.Navigation;
using Helldivers2ModManager.Frontend.Services;
using Microsoft.Win32;
using System.Windows;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed class LibraryPageViewModel : FrontendPageViewModel
{
    private readonly ModLibraryService _libraryService;
    private readonly ApplicationSettingsService _settingsService;
    private readonly LibraryDeploymentService _libraryDeployment;
    private readonly AutoTaggingFacade _autoTagging;
    private readonly ModSelectionStore _selectionStore;
    private readonly INavigationStore _navigationStore;
    private readonly LocalizationCatalog _localization;
    private string _searchText = string.Empty;
    private bool _isBusy;
    private string _status = string.Empty;

    public ObservableCollection<ModItem> Mods { get; } = [];

    public ICollectionView ModsView { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ModsView.Refresh();
            }
        }
    }

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public override string Title => _localization.GetString("Nav.Library");

    public string ImportLabel => _localization.GetString("Frontend.Import");

    public string RefreshLabel => _localization.GetString("Library.Refresh");

    public string EnableSelectedLabel => _localization.GetString("Library.EnableSelected");

    public string DisableSelectedLabel => _localization.GetString("Library.DisableSelected");

    public string DeleteSelectedLabel => _localization.GetString("Library.DeleteSelected");

    public string ExportLabel => _localization.GetString("Library.Export");

    public ICommand RefreshCommand { get; }

    public ICommand ImportCommand { get; }

    public ICommand SaveStateCommand { get; }

    public ICommand EnableSelectedCommand { get; }

    public ICommand DisableSelectedCommand { get; }

    public ICommand DeleteSelectedCommand { get; }

    public ICommand ExportCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand ManifestCommand { get; }
    public ICommand ToggleEnabledCommand { get; }
    public ICommand ShowOptionsCommand { get; }
    public ICommand DeployAllEnabledCommand { get; }
    public ICommand DeployOptionsCommand { get; }
    public ObservableCollection<DeployOptionItem> SelectedModOptions { get; } = [];

    private ModItem? _selectedOptionsItem;
    public ModItem? SelectedOptionsItem
    {
        get => _selectedOptionsItem;
        private set => SetProperty(ref _selectedOptionsItem, value);
    }

    public bool HasSelectedModOptions => SelectedOptionsItem is not null;
    public bool IsLegacyOptions => SelectedOptionsItem?.Source.Manifest is LegacyModManifest;

    public LibraryPageViewModel(
        ModLibraryService libraryService,
        ApplicationSettingsService settingsService,
        LibraryDeploymentService libraryDeployment,
        AutoTaggingFacade autoTagging,
        ModSelectionStore selectionStore,
        INavigationStore navigationStore,
        LocalizationCatalog localization)
    {
        _libraryService = libraryService;
        _settingsService = settingsService;
        _libraryDeployment = libraryDeployment;
        _autoTagging = autoTagging;
        _selectionStore = selectionStore;
        _navigationStore = navigationStore;
        _localization = localization;
        ModsView = CollectionViewSource.GetDefaultView(Mods);
        ModsView.Filter = FilterMods;
        RefreshCommand = new DelegateCommand(async _ => await RefreshCoreAsync(CancellationToken.None));
        ImportCommand = new DelegateCommand(async _ => await ImportAsync());
        SaveStateCommand = new DelegateCommand(async item => await SaveStateAsync(item));
        EnableSelectedCommand = new DelegateCommand(_ => SetSelectedState(true));
        DisableSelectedCommand = new DelegateCommand(_ => SetSelectedState(false));
        DeleteSelectedCommand = new DelegateCommand(async _ => await DeleteSelectedAsync());
        ExportCommand = new DelegateCommand(async item => await ExportAsync(item));
        EditCommand = new DelegateCommand(item =>
        {
            if (item is ModItem mod)
            {
                _selectionStore.Selected = mod;
                _navigationStore.Navigate("Tools.Edit");
            }
        });
        ManifestCommand = new DelegateCommand(item =>
        {
            if (item is ModItem mod)
            {
                _selectionStore.Selected = mod;
                _navigationStore.Navigate("Tools.Manifest");
            }
        });
        ToggleEnabledCommand = new DelegateCommand(async parameter => await ToggleEnabledAsync(parameter));
        ShowOptionsCommand = new DelegateCommand(ShowOptions);
        DeployAllEnabledCommand = new DelegateCommand(async _ => await DeployAllEnabledAsync());
        DeployOptionsCommand = new DelegateCommand(async _ => await DeploySelectedOptionsAsync());
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Mods.Count > 0)
        {
            return;
        }

        await RefreshCoreAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        SetBusy(true, _localization.GetString("Library.Loading"));
        try
        {
            var result = await _libraryService.LoadAsync(cancellationToken).ConfigureAwait(false);
            Mods.Clear();
            SelectedOptionsItem = null;
            SelectedModOptions.Clear();
            OnPropertyChanged(nameof(HasSelectedModOptions));
            foreach (var item in result.Mods)
            {
                Mods.Add(item);
            }

            Status = result.Problems.Count == 0
                ? string.Format(_localization.GetString("Library.LoadedFormat"), Mods.Count)
                : string.Format(_localization.GetString("Library.LoadedWithProblemsFormat"), Mods.Count, result.Problems.Count);
            _ = _autoTagging.RunAsync([.. Mods]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.Message;
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = _localization.GetString("Library.ImportTitle"),
            Filter = "Mod archives (*.zip;*.7z;*.rar;*.tar)|*.zip;*.7z;*.rar;*.tar|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        SetBusy(true, string.Format(_localization.GetString("Library.ImportingFormat"), dialog.FileNames.Length));
        try
        {
            var result = await _libraryService.ImportAsync(dialog.FileNames).ConfigureAwait(true);
            await RefreshCoreAsync(CancellationToken.None).ConfigureAwait(true);
            Status = result.Problems.Count == 0
                ? string.Format(_localization.GetString("Library.ImportedFormat"), result.ImportedCount)
                : string.Join(Environment.NewLine, result.Problems);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.Message;
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async Task SaveStateAsync(object? parameter)
    {
        if (parameter is ModItem item)
        {
            await _libraryService.SaveAsync([.. Mods]).ConfigureAwait(true);
            Status = string.Format(_localization.GetString("Library.SavedFormat"), item.Name);
        }
    }

    private void SetSelectedState(bool enabled)
    {
        foreach (var item in Mods.Where(item => item.IsSelected))
        {
            item.IsEnabled = enabled;
        }

        Status = _localization.GetString(enabled ? "Library.SelectionEnabled" : "Library.SelectionDisabled");
    }

    private void ShowOptions(object? parameter)
    {
        SelectedOptionsItem = parameter as ModItem;
        SelectedModOptions.Clear();
        if (SelectedOptionsItem is not null)
        {
            foreach (var option in LibraryDeploymentService.CreateOptions(SelectedOptionsItem))
            {
                SelectedModOptions.Add(option);
            }
        }

        OnPropertyChanged(nameof(HasSelectedModOptions));
        OnPropertyChanged(nameof(IsLegacyOptions));
    }

    private async Task ToggleEnabledAsync(object? parameter)
    {
        if (parameter is not ModItem item || IsBusy)
        {
            return;
        }

        var options = LibraryDeploymentService.CreateOptions(item).ToArray();
        if (item.Source.Manifest is V1ModManifest && options.Length > 0 && !item.IsEnabled)
        {
            foreach (var option in options)
            {
                option.IsEnabled = false;
            }
        }

        LibraryDeploymentService.ApplyOptions(item, options);
        await DeployAllEnabledCoreAsync(item.Name).ConfigureAwait(true);
    }

    private async Task DeployAllEnabledAsync()
    {
        if (!IsBusy)
        {
            await DeployAllEnabledCoreAsync(null).ConfigureAwait(true);
        }
    }

    private async Task DeploySelectedOptionsAsync()
    {
        if (IsBusy || SelectedOptionsItem is null)
        {
            return;
        }

        LibraryDeploymentService.ApplyOptions(SelectedOptionsItem, SelectedModOptions);
        SelectedOptionsItem.IsEnabled = true;
        await DeployAllEnabledCoreAsync(SelectedOptionsItem.Name).ConfigureAwait(true);
    }

    private async Task DeployAllEnabledCoreAsync(string? changedModName)
    {
        SetBusy(true, _localization.GetString("Deployment.Deploying"));
        try
        {
            var progress = new Progress<DeploymentProgress>(item => Status = string.Format(
                _localization.GetString("Deployment.ProgressFormat"),
                item.CompletedFiles,
                item.TotalFiles,
                item.CurrentFile));
            var result = await _libraryDeployment.DeployEnabledModsAsync([.. Mods], progress).ConfigureAwait(true);
            Status = result.Status switch
            {
                BackgroundTaskStatus.Succeeded => string.Format(_localization.GetString("Library.DeployedFormat"), changedModName ?? "启用模组"),
                BackgroundTaskStatus.Canceled => _localization.GetString("Deployment.Canceled"),
                _ => result.Error?.Message ?? _localization.GetString("Deployment.Failed"),
            };
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async Task DeleteSelectedAsync()
    {
        var selected = Mods.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        if (System.Windows.MessageBox.Show(
                string.Format(_localization.GetString("Library.DeleteConfirmFormat"), selected.Length),
                _localization.GetString("Library.DeleteTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true, _localization.GetString("Library.Deleting"));
        try
        {
            foreach (var item in selected)
            {
                var result = await _libraryService.DeleteAsync(item).ConfigureAwait(true);
                if (result.Failed)
                {
                    Status = result.Error.Message;
                    return;
                }
            }

            await RefreshCoreAsync(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async Task ExportAsync(object? parameter)
    {
        if (parameter is not ModItem item)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = _localization.GetString("Library.ExportTitle"),
            FileName = item.Name,
            Filter = "ZIP archive (*.zip)|*.zip",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SetBusy(true, string.Format(_localization.GetString("Library.ExportingFormat"), item.Name));
        try
        {
            await _libraryService.ExportAsync(item, dialog.FileName, ArchiveExportFormat.Zip).ConfigureAwait(true);
            Status = string.Format(_localization.GetString("Library.ExportedFormat"), dialog.FileName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.Message;
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private bool FilterMods(object? parameter)
    {
        if (parameter is not ModItem item)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return item.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
               item.Description.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
               item.OptionNames.Any(name => name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));
    }

    private void SetBusy(bool busy, string status)
    {
        IsBusy = busy;
        Status = status;
    }
}
