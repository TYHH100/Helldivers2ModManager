using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Core.UI;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class TagManagementPageViewModel : PageViewModelBase
{
    public override string Title => _localizationService["DashboardPage.TagManagement"];

    public ObservableCollection<ModTag> Tags => _settingsService.Initialized ? _settingsService.Tags : [];

    [ObservableProperty]
    private ModTag? _selectedTag;

    private readonly ILogger<TagManagementPageViewModel> _logger;
    private readonly SettingsService _settingsService;
    private readonly INavigationService _navigationService;
    private readonly IDialogService _dialogService;
    private readonly ModService _modService;
    private readonly LocalizationService _localizationService;

    public TagManagementPageViewModel(
        ILogger<TagManagementPageViewModel> logger,
        SettingsService settingsService,
        INavigationService navigationService,
        IDialogService dialogService,
        ModService modService,
        LocalizationService localizationService)
    {
        _logger = logger;
        _settingsService = settingsService;
        _navigationService = navigationService;
        _dialogService = dialogService;
        _modService = modService;
        _localizationService = localizationService;

        _localizationService.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
        };
    }

    [RelayCommand]
    async Task CreateTag(CancellationToken cancellationToken)
    {
        var tagName = await _dialogService.PromptAsync(
            new InputDialogRequest(
                _localizationService["TagManagementPage.CreateTitle"],
                _localizationService["TagManagementPage.CreateMsg"],
                MaxLength: 16),
            cancellationToken);
        if (tagName is null)
            return;
        if (string.IsNullOrWhiteSpace(tagName))
        {
            await ShowErrorAsync("TagManagementPage.CreateEmptyError", cancellationToken);
            return;
        }
        if (_settingsService.IsReadonly)
        {
            await ShowErrorAsync("TagManagementPage.CreateReadonly", cancellationToken);
            return;
        }

        _settingsService.Tags.Add(new ModTag(tagName));
        await _settingsService.SaveAsync(cancellationToken);
        await ShowInfoAsync("TagManagementPage.CreateSuccess", cancellationToken);
    }

    [RelayCommand]
    async Task RenameTag(ModTag? tag, CancellationToken cancellationToken)
    {
        if (tag == null)
            return;

        var newName = await _dialogService.PromptAsync(
            new InputDialogRequest(
                _localizationService["TagManagementPage.RenameTitle"],
                _localizationService["TagManagementPage.RenameMsg"],
                tag.Name,
                16),
            cancellationToken);
        if (newName is null)
            return;
        if (string.IsNullOrWhiteSpace(newName))
        {
            await ShowErrorAsync("TagManagementPage.RenameEmptyError", cancellationToken);
            return;
        }
        if (_settingsService.IsReadonly)
        {
            await ShowErrorAsync("TagManagementPage.RenameReadonly", cancellationToken);
            return;
        }

        tag.Name = newName;
        await _settingsService.SaveAsync(cancellationToken);
        await ShowInfoAsync("TagManagementPage.RenameSuccess", cancellationToken);
    }

    [RelayCommand]
    async Task ChangeColor(ModTag? tag, CancellationToken cancellationToken)
    {
        if (tag == null)
            return;

        if (_settingsService.IsReadonly)
        {
            await ShowErrorAsync("TagManagementPage.ColorReadonly", cancellationToken);
            return;
        }

        var colorCode = await _dialogService.PickColorAsync(
            new ColorDialogRequest(
                _localizationService["TagManagementPage.ColorTitle"],
                _localizationService.Format("TagManagementPage.ColorMessage", new { tagName = tag.Name }),
                tag.Color),
            cancellationToken);
        if (colorCode is null)
            return;

        tag.Color = colorCode;
        await _settingsService.SaveAsync(cancellationToken);
        await ShowInfoAsync("TagManagementPage.ColorUpdated", cancellationToken);
    }

    [RelayCommand]
    async Task DeleteTag(ModTag? tag, CancellationToken cancellationToken)
    {
        if (tag == null)
            return;

        if (!_settingsService.IsReadonly)
        {
            var confirmed = await _dialogService.ShowAsync(
                new DialogRequest(
                    _localizationService["DashboardPage.DeleteConfirmTitle"],
                    _localizationService.Format("TagManagementPage.DeleteMessage", new { tagName = tag.Name })),
                cancellationToken);
            if (!confirmed)
                return;

            _settingsService.Tags.Remove(tag);
            await _settingsService.SaveAsync(cancellationToken);
            SelectedTag = null;
            await ShowInfoAsync("TagManagementPage.DeleteSuccess", cancellationToken);
        }
        else
        {
            await ShowErrorAsync("TagManagementPage.DeleteReadonly", cancellationToken);
        }
    }

    [RelayCommand]
    void Back()
    {
        _navigationService.Navigate(typeof(DashboardPageViewModel), root: true);
    }

    private Task ShowInfoAsync(string messageKey, CancellationToken cancellationToken) =>
        _dialogService.ShowMessageAsync(
            new MessageDialogRequest(
                _localizationService["MessageBox.Info"],
                _localizationService[messageKey]),
            cancellationToken);

    private Task ShowErrorAsync(string messageKey, CancellationToken cancellationToken) =>
        _dialogService.ShowMessageAsync(
            new MessageDialogRequest(
                _localizationService["MessageBox.Error"],
                _localizationService[messageKey],
                MessageDialogSeverity.Error),
            cancellationToken);
}
