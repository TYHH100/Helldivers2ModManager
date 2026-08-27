using System.Collections.ObjectModel;
using System.Windows.Input;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Navigation;
using Helldivers2ModManager.Frontend.Services;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed class EditableAutoTagPairing : ObservableObject
{
    private Guid? _selectedTagId;

    public EditableAutoTagPairing(
        ModType type,
        string typeName,
        string color,
        Guid? selectedTagId,
        IReadOnlyList<TagSetting> tags,
        AutoTagPairingPageViewModel owner)
    {
        Type = type;
        TypeName = typeName;
        Color = color;
        _selectedTagId = selectedTagId;
        Tags = new ObservableCollection<TagSetting>(tags);
        Owner = owner;
    }

    public ModType Type { get; }
    public string TypeName { get; }
    public string Color { get; }
    public ObservableCollection<TagSetting> Tags { get; }
    public AutoTagPairingPageViewModel Owner { get; }

    public Guid? SelectedTagId
    {
        get => _selectedTagId;
        set => SetProperty(ref _selectedTagId, value);
    }
}

public sealed class AutoTagPairingPageViewModel : FrontendPageViewModel
{
    private readonly AutoTagPairingService _pairing;
    private readonly INavigationStore _navigation;
    private readonly LocalizationCatalog _localization;
    private bool _isBusy;
    private string _status = string.Empty;

    public ObservableCollection<EditableAutoTagPairing> Items { get; } = [];
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public ICommand SaveCommand { get; }
    public ICommand CreateTagCommand { get; }
    public ICommand BackCommand { get; }

    public override string Title => _localization.GetString("Nav.AutoTagPairing");

    public AutoTagPairingPageViewModel(
        AutoTagPairingService pairing,
        INavigationStore navigation,
        LocalizationCatalog localization)
    {
        _pairing = pairing;
        _navigation = navigation;
        _localization = localization;
        SaveCommand = new DelegateCommand(async _ => await SaveAsync());
        CreateTagCommand = new DelegateCommand(parameter => _ = CreateTagAsync(parameter));
        BackCommand = new DelegateCommand(_ => _navigation.Navigate("System.Settings"));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        LoadCurrent();
        return Task.CompletedTask;
    }

    private void LoadCurrent()
    {
        Items.Clear();
        foreach (var definition in _pairing.Definitions)
        {
            var mapped = _pairing.GetMapping(definition.Type) ?? _pairing.GetExistingTagForType(definition.Type);
            Items.Add(new EditableAutoTagPairing(
                definition.Type,
                _localization.GetString(definition.NameKey),
                definition.Color,
                mapped,
                _pairing.Tags,
                this));
        }

        Status = _localization.GetString("Edit.Loaded");
    }

    private async Task CreateTagAsync(object? parameter)
    {
        if (parameter is not EditableAutoTagPairing item || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var tag = await _pairing.CreateTypeTagAsync(item.Type).ConfigureAwait(true);
            item.Tags.Add(tag);
            item.SelectedTagId = tag.Id;
            Status = _localization.GetString("Tags.Created");
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

    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var mappings = Items
                .Where(item => item.SelectedTagId.HasValue)
                .Select(item => new AutoTagMappingSetting((int)item.Type, item.SelectedTagId!.Value))
                .ToArray();
            await _pairing.SaveAsync(mappings).ConfigureAwait(true);
            Status = _localization.GetString("AutoTagPairingPage.SaveSuccess");
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
}
