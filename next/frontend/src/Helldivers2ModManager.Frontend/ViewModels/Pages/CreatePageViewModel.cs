using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Models;
using Helldivers2ModManager.Frontend.Navigation;
using Helldivers2ModManager.Frontend.Services;
using Microsoft.Win32;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed class CreatePageViewModel : FrontendPageViewModel
{
    private readonly ModCreationService _creation;
    private readonly INavigationStore _navigation;
    private readonly LocalizationCatalog _localization;
    private string _modName = string.Empty;
    private string _modDescription = string.Empty;
    private string _sourceDirectory = string.Empty;
    private string _iconPath = string.Empty;
    private bool _useV1Manifest = true;
    private bool _isBusy;
    private string _status = string.Empty;

    public string ModName
    {
        get => _modName;
        set
        {
            if (SetProperty(ref _modName, value))
            {
                NotifyCreateCommand();
            }
        }
    }

    public string ModDescription { get => _modDescription; set => SetProperty(ref _modDescription, value); }

    public string SourceDirectory
    {
        get => _sourceDirectory;
        set
        {
            if (SetProperty(ref _sourceDirectory, value))
            {
                NotifyCreateCommand();
            }
        }
    }

    public string IconPath { get => _iconPath; set => SetProperty(ref _iconPath, value); }
    public bool UseV1Manifest { get => _useV1Manifest; set => SetProperty(ref _useV1Manifest, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public ObservableCollection<CreateModOptionItem> Options { get; } = [];

    public ICommand BrowseSourceCommand { get; }
    public ICommand BrowseIconCommand { get; }
    public ICommand AddOptionCommand { get; }
    public ICommand RemoveOptionCommand { get; }
    public ICommand AddSubOptionCommand { get; }
    public ICommand RemoveSubOptionCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand CancelCommand { get; }

    public override string Title => _localization.GetString("Nav.Create");

    public CreatePageViewModel(
        ModCreationService creation,
        INavigationStore navigation,
        LocalizationCatalog localization)
    {
        _creation = creation;
        _navigation = navigation;
        _localization = localization;
        BrowseSourceCommand = new DelegateCommand(_ => SourceDirectory = BrowseFolder(SourceDirectory));
        BrowseIconCommand = new DelegateCommand(_ => IconPath = BrowseImage(IconPath));
        AddOptionCommand = new DelegateCommand(_ => Options.Add(new CreateModOptionItem()));
        RemoveOptionCommand = new DelegateCommand(parameter =>
        {
            if (parameter is CreateModOptionItem option)
            {
                Options.Remove(option);
            }
        });
        AddSubOptionCommand = new DelegateCommand(parameter =>
        {
            if (parameter is CreateModOptionItem option)
            {
                option.SubOptions.Add(new CreateModSubOptionItem());
            }
        });
        RemoveSubOptionCommand = new DelegateCommand(parameter =>
        {
            if (parameter is CreateModSubOptionItem subOption)
            {
                var owner = Options.FirstOrDefault(item => item.SubOptions.Contains(subOption));
                owner?.SubOptions.Remove(subOption);
            }
        });
        CreateCommand = new DelegateCommand(async _ => await CreateAsync(), _ => CanCreate());
        CancelCommand = new DelegateCommand(_ => _navigation.Navigate("Library"));
    }

    private bool CanCreate() => !IsBusy &&
        !string.IsNullOrWhiteSpace(ModName) &&
        Directory.Exists(SourceDirectory);

    private async Task CreateAsync()
    {
        if (!CanCreate())
        {
            return;
        }

        IsBusy = true;
        NotifyCreateCommand();
        Status = _localization.GetString("Create.Creating");
        try
        {
            var request = new CreateModRequest(
                new DirectoryInfo(SourceDirectory),
                ModName.Trim(),
                ModDescription,
                string.IsNullOrWhiteSpace(IconPath) ? null : IconPath,
                UseV1Manifest,
                Options.Select(option => new CreateModOption(
                    string.IsNullOrWhiteSpace(option.Name) ? "Option" : option.Name.Trim(),
                    option.Description,
                    SplitPaths(option.IncludePaths),
                    option.ImagePath,
                    option.SubOptions.Select(sub => new CreateModSubOption(
                        string.IsNullOrWhiteSpace(sub.Name) ? "SubOption" : sub.Name.Trim(),
                        sub.Description,
                        SplitPaths(sub.IncludePaths),
                        sub.ImagePath)).ToArray())).ToArray());
            var created = await _creation.CreateAsync(request).ConfigureAwait(true);
            Status = string.Format(_localization.GetString("Create.CreatedFormat"), created.Name);
            _navigation.Navigate("Library");
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyCreateCommand();
        }
    }

    private void NotifyCreateCommand() => ((DelegateCommand)CreateCommand).NotifyCanExecuteChanged();

    private static string[] SplitPaths(string value) => value
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .ToArray();

    private static string BrowseFolder(string initialPath)
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = Directory.Exists(initialPath) ? initialPath : AppContext.BaseDirectory,
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : initialPath;
    }

    private static string BrowseImage(string initialPath)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            InitialDirectory = Path.GetDirectoryName(initialPath) is { } directory && Directory.Exists(directory)
                ? directory
                : AppContext.BaseDirectory,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : initialPath;
    }
}
