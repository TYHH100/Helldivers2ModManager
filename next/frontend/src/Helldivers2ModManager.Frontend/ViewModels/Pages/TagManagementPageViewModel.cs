using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Models;
using Helldivers2ModManager.Frontend.Services;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed class TagManagementPageViewModel : FrontendPageViewModel
{
    private readonly TagManagementService _tags;
    private readonly LocalizationCatalog _localization;
    private string _newTagName = string.Empty;
    private bool _isBusy;
    private string _status = string.Empty;

    public ObservableCollection<EditableTag> Tags { get; } = [];
    public string NewTagName { get => _newTagName; set => SetProperty(ref _newTagName, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public ICommand AddCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }

    public override string Title => _localization.GetString("Nav.TagManagement");

    public TagManagementPageViewModel(TagManagementService tags, LocalizationCatalog localization)
    {
        _tags = tags;
        _localization = localization;
        AddCommand = new DelegateCommand(async _ => await AddAsync());
        SaveCommand = new DelegateCommand(async _ => await SaveAsync());
        DeleteCommand = new DelegateCommand(async parameter => await DeleteAsync(parameter));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        LoadCurrent();
        return Task.CompletedTask;
    }

    private void LoadCurrent()
    {
        Tags.Clear();
        foreach (var tag in _tags.LoadTags())
        {
            Tags.Add(new EditableTag { Id = tag.Id, Name = tag.Name, Color = tag.Color });
        }

        Status = _localization.GetString("Tags.Loaded");
    }

    private async Task AddAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var tag = await _tags.AddAsync(NewTagName, "#FF60CDFF").ConfigureAwait(true);
            Tags.Add(new EditableTag { Id = tag.Id, Name = tag.Name, Color = tag.Color });
            NewTagName = string.Empty;
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
            await _tags.SaveAsync(Tags.Select(tag => new TagSetting(tag.Id, tag.Name.Trim(), tag.Color)).ToArray())
                .ConfigureAwait(true);
            Status = _localization.GetString("Tags.Saved");
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

    private async Task DeleteAsync(object? parameter)
    {
        if (parameter is not EditableTag tag || IsBusy)
        {
            return;
        }

        if (MessageBox.Show(
                string.Format(_localization.GetString("Tags.DeleteConfirmFormat"), tag.Name),
                _localization.GetString("Tags.DeleteTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _tags.DeleteAsync(tag.Id).ConfigureAwait(true);
            Tags.Remove(tag);
            Status = _localization.GetString("Tags.Deleted");
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
