using System.Collections.ObjectModel;
using System.Windows.Input;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Models;
using Helldivers2ModManager.Frontend.Navigation;
using Helldivers2ModManager.Frontend.Services;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed class EditPageViewModel : FrontendPageViewModel
{
    private readonly ModLibraryService _library;
    private readonly ModSelectionStore _selection;
    private readonly INavigationStore _navigation;
    private readonly LocalizationCatalog _localization;
    private ModItem? _currentMod;
    private string _status = string.Empty;

    public ModItem? CurrentMod { get => _currentMod; private set => SetProperty(ref _currentMod, value); }
    public ObservableCollection<EditableModOption> Options { get; } = [];
    public bool IsBusy { get; private set; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public ICommand SaveCommand { get; }
    public ICommand DoneCommand { get; }
    public ICommand CancelCommand { get; }

    public override string Title => _localization.GetString("Nav.Edit");

    public EditPageViewModel(
        ModLibraryService library,
        ModSelectionStore selection,
        INavigationStore navigation,
        LocalizationCatalog localization)
    {
        _library = library;
        _selection = selection;
        _navigation = navigation;
        _localization = localization;
        SaveCommand = new DelegateCommand(async _ => await SaveAsync(), _ => !IsBusy && CurrentMod is not null);
        DoneCommand = new DelegateCommand(async _ =>
        {
            await SaveAsync();
            ReturnToLibrary();
        }, _ => !IsBusy && CurrentMod is not null);
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

        var source = CurrentMod.Source.Manifest;
        if (source is LegacyModManifest legacy)
        {
            var selected = CurrentMod.SelectedOptions;
            foreach (var name in legacy.Options ?? [])
            {
                Options.Add(new EditableModOption
                {
                    Name = name,
                    IsEnabled = CurrentMod.EnabledOptions.Count > Options.Count && CurrentMod.EnabledOptions[Options.Count],
                    SelectedSubOption = selected.Count > 0 ? selected[0] : 0,
                });
            }
        }
        else if (source is V1ModManifest v1)
        {
            foreach (var option in v1.Options ?? [])
            {
                var index = Options.Count;
                Options.Add(new EditableModOption
                {
                    Name = option.Name,
                    Description = option.Description,
                    SubOptions = [.. (option.SubOptions ?? []).Select(sub => sub.Name)],
                    IsEnabled = CurrentMod.EnabledOptions.Count <= index || CurrentMod.EnabledOptions[index],
                    SelectedSubOption = CurrentMod.SelectedOptions.Count > index
                        ? CurrentMod.SelectedOptions[index]
                        : Math.Max(0, (option.SubOptions ?? []).ToList().FindIndex(sub => !string.IsNullOrWhiteSpace(sub.Name))),
                });
            }
        }

        Status = _localization.GetString("Edit.Loaded");
    }

    private async Task SaveAsync()
    {
        if (CurrentMod is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            CurrentMod.EnabledOptions = [.. Options.Select(option => option.IsEnabled)];
            CurrentMod.SelectedOptions = [.. Options.Select(option => option.SelectedSubOption)];
            await _library.SaveItemAsync(CurrentMod).ConfigureAwait(true);
            Status = _localization.GetString("Edit.Saved");
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReturnToLibrary()
    {
        _selection.Selected = null;
        _navigation.Navigate("Library");
    }
}
