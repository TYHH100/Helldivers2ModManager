using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class TagManagementPageViewModel : PageViewModelBase
{
    public override string Title => _localizationService["DashboardPage.TagManagement"];

    public ObservableCollection<ModTag> Tags => _settingsService.Initialized ? _settingsService.Tags : [];

    [ObservableProperty]
    private ModTag? _selectedTag;

    private readonly ILogger<TagManagementPageViewModel> _logger;
    private readonly SettingsService _settingsService;
    private readonly NavigationStore _navigationStore;
    private readonly ModService _modService;
    private readonly LocalizationService _localizationService;

    public TagManagementPageViewModel(ILogger<TagManagementPageViewModel> logger, SettingsService settingsService, NavigationStore navigationStore, ModService modService, LocalizationService localizationService)
    {
        _logger = logger;
        _settingsService = settingsService;
        _navigationStore = navigationStore;
        _modService = modService;
        _localizationService = localizationService;

        _localizationService.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
        };
    }

    [RelayCommand]
    void CreateTag()
    {
        WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
        {
            Title = _localizationService["TagManagementPage.CreateTitle"],
            Message = _localizationService["TagManagementPage.CreateMsg"],
            MaxLength = 16,
            Confirm = (tagName) =>
            {
                if (string.IsNullOrWhiteSpace(tagName))
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["TagManagementPage.CreateEmptyError"] });
                    return;
                }

                if (!_settingsService.IsReadonly)
                {
                    _settingsService.Tags.Add(new ModTag(tagName));
                    _ = _settingsService.SaveAsync();
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = _localizationService["TagManagementPage.CreateSuccess"] });
                }
                else
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["TagManagementPage.CreateReadonly"] });
                }
            }
        });
    }

    [RelayCommand]
    void RenameTag(ModTag? tag)
    {
        if (tag == null)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
        {
            Title = _localizationService["TagManagementPage.RenameTitle"],
            Message = _localizationService["TagManagementPage.RenameMsg"],
            MaxLength = 16,
            InitialText = tag.Name,
            Confirm = (newName) =>
            {
                if (string.IsNullOrWhiteSpace(newName))
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["TagManagementPage.CreateEmptyError"] });
                    return;
                }

                if (!_settingsService.IsReadonly)
                {
                    tag.Name = newName;
                    _ = _settingsService.SaveAsync();
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = _localizationService["TagManagementPage.RenameSuccess"] });
                }
                else
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["TagManagementPage.RenameReadonly"] });
                }
            }
        });
    }

    [RelayCommand]
    void ChangeColor(ModTag? tag)
    {
        if (tag == null)
            return;

        if (!_settingsService.IsReadonly)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxColorPickerMessage
            {
                Title = _localizationService["TagManagementPage.ColorTitle"],
                Message = $"{_localizationService["TagManagementPage.ColorPrefix"]}{tag.Name}{_localizationService["TagManagementPage.ColorSuffix"]}",
                CurrentColor = tag.Color,
                Confirm = (colorCode) =>
                {
                    tag.Color = colorCode;
                    _ = _settingsService.SaveAsync();
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = _localizationService["TagManagementPage.ColorUpdated"] });
                }
            });
        }
        else
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["TagManagementPage.ColorReadonly"] });
        }
    }

    [RelayCommand]
    void DeleteTag(ModTag? tag)
    {
        if (tag == null)
            return;

        if (!_settingsService.IsReadonly)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
            {
                Title = _localizationService["DashboardPage.DeleteConfirmTitle"],
                Message = _localizationService["TagManagementPage.DeletePrefix"] + tag.Name + _localizationService["TagManagementPage.DeleteSuffix"],
                Confirm = () =>
                {
                    _settingsService.Tags.Remove(tag);
                    _ = _settingsService.SaveAsync();

                    SelectedTag = null;
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = _localizationService["TagManagementPage.DeleteSuccess"] });
                }
            });
        }
        else
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["TagManagementPage.DeleteReadonly"] });
        }
    }

    [RelayCommand]
    void Back()
    {
        _navigationStore.Navigate<DashboardPageViewModel>();
    }
}

