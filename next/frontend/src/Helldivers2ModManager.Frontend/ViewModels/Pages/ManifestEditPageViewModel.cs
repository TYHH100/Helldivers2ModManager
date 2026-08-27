using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Models;
using Helldivers2ModManager.Frontend.Navigation;
using Helldivers2ModManager.Frontend.Services;
using Microsoft.Win32;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed class ManifestEditPageViewModel : FrontendPageViewModel
{
    private readonly ModManifestEditorService _editor;
    private readonly ModSelectionStore _selection;
    private readonly INavigationStore _navigation;
    private readonly LocalizationCatalog _localization;
    private ModItem? _currentMod;
    private string _modName = string.Empty;
    private string _modDescription = string.Empty;
    private string _iconPath = string.Empty;
    private bool _isLegacyManifest;
    private bool _isBusy;
    private string _status = string.Empty;

    public ModItem? CurrentMod { get => _currentMod; private set => SetProperty(ref _currentMod, value); }
    public string ModName { get => _modName; set => SetProperty(ref _modName, value); }
    public string ModDescription { get => _modDescription; set => SetProperty(ref _modDescription, value); }
    public string IconPath { get => _iconPath; set => SetProperty(ref _iconPath, value); }
    public bool IsLegacyManifest { get => _isLegacyManifest; private set => SetProperty(ref _isLegacyManifest, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public ObservableCollection<CreateModOptionItem> Options { get; } = [];
    public ICommand BrowseIconCommand { get; }
    public ICommand AddOptionCommand { get; }
    public ICommand RemoveOptionCommand { get; }
    public ICommand AddSubOptionCommand { get; }
    public ICommand RemoveSubOptionCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DoneCommand { get; }
    public ICommand CancelCommand { get; }

    public override string Title => _localization.GetString("Nav.ManifestEdit");

    public ManifestEditPageViewModel(
        ModManifestEditorService editor,
        ModSelectionStore selection,
        INavigationStore navigation,
        LocalizationCatalog localization)
    {
        _editor = editor;
        _selection = selection;
        _navigation = navigation;
        _localization = localization;
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
                Options.FirstOrDefault(option => option.SubOptions.Contains(subOption))?.SubOptions.Remove(subOption);
            }
        });
        SaveCommand = new DelegateCommand(async _ => await SaveAsync(), _ => CanSave());
        DoneCommand = new DelegateCommand(async _ =>
        {
            if (await SaveAsync())
            {
                ReturnToLibrary();
            }
        }, _ => CanSave());
        CancelCommand = new DelegateCommand(_ => ReturnToLibrary());
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        LoadCurrent();
        return Task.CompletedTask;
    }

    private void LoadCurrent()
    {
        CurrentMod = _selection.Selected;
        Options.Clear();
        if (CurrentMod is null)
        {
            Status = _localization.GetString("Edit.NoSelection");
            return;
        }

        var draft = _editor.CreateDraft(CurrentMod);
        ModName = draft.Name;
        ModDescription = draft.Description;
        IconPath = draft.IconPath ?? string.Empty;
        IsLegacyManifest = CurrentMod.Source.Manifest.Version == ManifestVersion.Legacy;
        foreach (var option in draft.Options)
        {
            var item = new CreateModOptionItem
            {
                Name = option.Name,
                Description = option.Description,
                IncludePaths = string.Join(";", option.IncludePaths),
                ImagePath = option.ImagePath,
            };
            foreach (var sub in option.SubOptions)
            {
                item.SubOptions.Add(new CreateModSubOptionItem
                {
                    Name = sub.Name,
                    Description = sub.Description,
                    IncludePaths = string.Join(";", sub.IncludePaths),
                    ImagePath = sub.ImagePath,
                });
            }

            Options.Add(item);
        }

        Status = _localization.GetString("Edit.Loaded");
    }

    private bool CanSave() => !IsBusy && CurrentMod is not null && !string.IsNullOrWhiteSpace(ModName);

    private async Task<bool> SaveAsync()
    {
        if (!CanSave())
        {
            return false;
        }

        IsBusy = true;
        try
        {
            var draft = new ManifestEditDraft(
                ModName.Trim(),
                ModDescription,
                string.IsNullOrWhiteSpace(IconPath) ? null : IconPath,
                Options.Select(option => new CreateModOption(
                    option.Name,
                    option.Description,
                    SplitPaths(option.IncludePaths),
                    option.ImagePath,
                    option.SubOptions.Select(sub => new CreateModSubOption(
                        sub.Name,
                        sub.Description,
                        SplitPaths(sub.IncludePaths),
                        sub.ImagePath)).ToArray())).ToArray());
            var upgraded = await _editor.SaveAsync(CurrentMod!, draft).ConfigureAwait(true);
            Status = upgraded
                ? _localization.GetString("Manifest.SavedUpgraded")
                : _localization.GetString("Manifest.Saved");
            return true;
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string[] SplitPaths(string value) => value
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .ToArray();

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

    private void ReturnToLibrary()
    {
        _selection.Selected = null;
        _navigation.Navigate("Library");
    }
}
