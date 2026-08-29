using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Deployment;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Search;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Models;
using Helldivers2ModManager.Frontend.Navigation;
using Helldivers2ModManager.Frontend.Services;
using Microsoft.Win32;
using System.Windows;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed record GroupChoice(Guid? Id, string Name);

public sealed class TagChoiceItem : ObservableObject
{
    public TagChoiceItem(Guid id, string name)
    {
        Id = id;
        Name = name;
    }

    public Guid Id { get; }

    public string Name { get; }

    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }

    private bool _isChecked;
}

public sealed class LibraryPageViewModel : FrontendPageViewModel
{
    private readonly ModLibraryService _libraryService;
    private readonly ApplicationSettingsService _settingsService;
    private readonly LibraryDeploymentService _libraryDeployment;
    private readonly AutoTaggingFacade _autoTagging;
    private readonly ModSelectionStore _selectionStore;
    private readonly INavigationStore _navigationStore;
    private readonly LocalizationCatalog _localization;
    private readonly ConcurrentDictionary<Guid, (string FullPinyin, string FirstLetters)> _pinyinCache = [];
    private string _searchText = string.Empty;
    private bool _isBusy;
    private string _status = string.Empty;
    private GroupChoice _selectedFilterChoice;
    private bool _suppressGroupEvents;

    public ObservableCollection<ModItem> Mods { get; } = [];

    public ObservableCollection<ModGroupInfo> Groups { get; } = [];

    public ObservableCollection<TagChoiceItem> TagChoices { get; } = [];

    public ICollectionView ModsView { get; }

    public IReadOnlyList<GroupChoice> FilterChoices { get; private set; } = [];

    public IReadOnlyList<GroupChoice> AssignGroupChoices { get; private set; } = [];

    public GroupChoice SelectedFilterChoice
    {
        get => _selectedFilterChoice;
        set
        {
            if (SetProperty(ref _selectedFilterChoice, value))
            {
                ModsView.Refresh();
                OnPropertyChanged(nameof(CanManageSelectedGroup));
                OnPropertyChanged(nameof(SelectedGroupName));
            }
        }
    }

    public bool CanManageSelectedGroup => SelectedFilterChoice.Id is { } id && id != Guid.Empty;

    public string? SelectedGroupName => CanManageSelectedGroup ? SelectedFilterChoice.Name : null;

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

    public string DeployEnabledLabel => _localization.GetString("Next.Library.DeployEnabled");

    public string OptionsLabel => _localization.GetString("Next.Library.Options");

    public string EditLabel => _localization.GetString("Next.Library.Edit");

    public string ManifestLabel => _localization.GetString("Next.Library.Manifest");

    public string OptionsSubtitle => _localization.GetString("Next.Library.OptionsSubtitle");

    public string SaveDeployOptionsLabel => _localization.GetString("Next.Library.SaveDeployOptions");

    public string GroupFilterLabel => _localization.GetString("Next.Groups.FilterLabel");

    public string CreateGroupLabel => _localization.GetString("ModGroup.CreateGroup");

    public string RenameGroupLabel => _localization.GetString("Next.Groups.RenameGroup");

    public string DeleteGroupLabel => _localization.GetString("ModGroup.DeleteGroup");

    public string TagsButtonLabel => _localization.GetString("Next.Library.TagsButton");

    public string TagsSubtitle => _localization.GetString("Next.Library.TagsSubtitle");

    public string SaveTagsLabel => _localization.GetString("Next.Library.SaveTags");

    public string SearchWatermark => _localization.GetString("Frontend.Search");

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
    public ICommand CreateGroupCommand { get; }
    public ICommand RenameGroupCommand { get; }
    public ICommand DeleteGroupCommand { get; }
    public ICommand ShowTagsCommand { get; }
    public ICommand SaveTagsCommand { get; }

    public ObservableCollection<DeployOptionItem> SelectedModOptions { get; } = [];

    private ModItem? _selectedOptionsItem;
    public ModItem? SelectedOptionsItem
    {
        get => _selectedOptionsItem;
        private set => SetProperty(ref _selectedOptionsItem, value);
    }

    private ModItem? _selectedTagsItem;
    public ModItem? SelectedTagsItem
    {
        get => _selectedTagsItem;
        private set => SetProperty(ref _selectedTagsItem, value);
    }

    public bool HasSelectedModOptions => SelectedOptionsItem is not null;

    public bool HasSelectedTags => SelectedTagsItem is not null;

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
        _selectedFilterChoice = new GroupChoice(null, localization.GetString("Next.Groups.All"));
        RebuildGroupChoices();
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
        CreateGroupCommand = new DelegateCommand(async _ => await CreateGroupAsync());
        RenameGroupCommand = new DelegateCommand(async _ => await RenameGroupAsync(), _ => CanManageSelectedGroup);
        DeleteGroupCommand = new DelegateCommand(async _ => await DeleteGroupAsync(), _ => CanManageSelectedGroup);
        ShowTagsCommand = new DelegateCommand(ShowTags);
        SaveTagsCommand = new DelegateCommand(async _ => await SaveTagsAsync());
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
            _suppressGroupEvents = true;
            Mods.Clear();
            SelectedOptionsItem = null;
            SelectedTagsItem = null;
            SelectedModOptions.Clear();
            TagChoices.Clear();
            OnPropertyChanged(nameof(HasSelectedModOptions));
            OnPropertyChanged(nameof(HasSelectedTags));
            foreach (var item in result.Mods)
            {
                item.PropertyChanged += OnModItemPropertyChanged;
                Mods.Add(item);
                RefreshTagSummary(item);
            }

            Groups.Clear();
            foreach (var group in result.Groups)
            {
                Groups.Add(group);
            }

            RebuildGroupChoices();
            _suppressGroupEvents = false;

            Status = BuildLoadStatus(result);
            WarmUpPinyinCache(result.Mods);
            _ = _autoTagging.RunAsync([.. Mods]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _suppressGroupEvents = false;
            Status = exception.Message;
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private string BuildLoadStatus(ModLibraryLoadResult result)
    {
        var status = result.Problems.Count == 0
            ? string.Format(_localization.GetString("Library.LoadedFormat"), Mods.Count)
            : string.Format(_localization.GetString("Library.LoadedWithProblemsFormat"), Mods.Count, result.Problems.Count);
        if (string.IsNullOrWhiteSpace(_settingsService.Current.GameDirectory))
        {
            status += Environment.NewLine + _localization.GetString("Next.Library.FirstRunGameHint");
        }

        return status;
    }

    private void WarmUpPinyinCache(IReadOnlyList<ModItem> mods)
    {
        // 拼音字典首次加载发生在调用线程（约 180ms），放到后台线程预热，
        // 避免用户第一次输入搜索词时 UI 卡顿。
        _ = Task.Run(() =>
        {
            foreach (var mod in mods)
            {
                _pinyinCache.GetOrAdd(mod.Id, static (_, item) => PinyinCache.Get(item.Name), mod);
            }
        });
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

    private void ShowTags(object? parameter)
    {
        SelectedTagsItem = parameter as ModItem;
        TagChoices.Clear();
        if (SelectedTagsItem is not null)
        {
            var assigned = SelectedTagsItem.TagIds.ToHashSet();
            foreach (var tag in _settingsService.Current.Tags)
            {
                TagChoices.Add(new TagChoiceItem(tag.Id, tag.Name) { IsChecked = assigned.Contains(tag.Id) });
            }
        }

        OnPropertyChanged(nameof(HasSelectedTags));
    }

    private async Task SaveTagsAsync()
    {
        if (SelectedTagsItem is null)
        {
            return;
        }

        SelectedTagsItem.TagIds = [.. TagChoices.Where(choice => choice.IsChecked).Select(choice => choice.Id)];
        RefreshTagSummary(SelectedTagsItem);
        await _libraryService.SaveAsync([.. Mods]).ConfigureAwait(true);
        Status = _localization.GetString("Tags.Saved");
    }

    private void RefreshTagSummary(ModItem item)
    {
        var namesById = _settingsService.Current.Tags.ToDictionary(tag => tag.Id, tag => tag.Name);
        item.TagSummary = string.Join(" / ", item.TagIds
            .Where(namesById.ContainsKey)
            .Select(tagId => namesById[tagId]));
    }

    private async void OnModItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressGroupEvents || sender is not ModItem item || e.PropertyName != nameof(ModItem.GroupId))
        {
            return;
        }

        item.SetGroup(item.GroupId, ResolveGroupName(item.GroupId));
        try
        {
            await _libraryService.SetModsGroupAsync([.. Mods], [item], item.GroupId, item.GroupName).ConfigureAwait(true);
            Status = _localization.GetString("Next.Groups.MembershipUpdated");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.Message;
        }
    }

    private string? ResolveGroupName(Guid? groupId) =>
        groupId is { } id ? Groups.FirstOrDefault(group => group.Id == id)?.Name : null;

    private async Task CreateGroupAsync()
    {
        var name = PromptForText(
            _localization.GetString("ModGroup.CreateGroup"),
            _localization.GetString("Next.Groups.CreatePrompt"));
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var group = await _libraryService.CreateGroupAsync(name).ConfigureAwait(true);
            Groups.Add(group);
            RebuildGroupChoices();
            Status = _localization.GetString("Next.Groups.Created");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.Message;
        }
    }

    private async Task RenameGroupAsync()
    {
        if (SelectedFilterChoice.Id is not { } groupId || groupId == Guid.Empty)
        {
            return;
        }

        var name = PromptForText(
            _localization.GetString("Next.Groups.RenameGroup"),
            _localization.GetString("Next.Groups.RenamePrompt"),
            SelectedFilterChoice.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            await _libraryService.RenameGroupAsync(groupId, name).ConfigureAwait(true);
            await RefreshCoreAsync(CancellationToken.None).ConfigureAwait(true);
            Status = _localization.GetString("Next.Groups.Renamed");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.Message;
        }
    }

    private async Task DeleteGroupAsync()
    {
        if (SelectedFilterChoice.Id is not { } groupId || groupId == Guid.Empty)
        {
            return;
        }

        if (System.Windows.MessageBox.Show(
                string.Format(_localization.GetString("ModGroup.DeleteConfirm"), SelectedFilterChoice.Name),
                _localization.GetString("ModGroup.DeleteGroup"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _libraryService.DeleteGroupAsync(groupId, [.. Mods]).ConfigureAwait(true);
            await RefreshCoreAsync(CancellationToken.None).ConfigureAwait(true);
            Status = _localization.GetString("Next.Groups.Deleted");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.Message;
        }
    }

    private static string? PromptForText(string title, string prompt, string initial = "")
    {
        var dialog = new Views.TextInputDialog(title, prompt, initial);
        return dialog.ShowDialog() == true ? dialog.InputText : null;
    }

    private void RebuildGroupChoices()
    {
        var all = new GroupChoice(null, _localization.GetString("Next.Groups.All"));
        var ungrouped = new GroupChoice(Guid.Empty, _localization.GetString("Next.Groups.Ungrouped"));
        var groups = Groups.Select(group => new GroupChoice(group.Id, group.Name)).ToArray();
        FilterChoices = [all, ungrouped, .. groups];
        AssignGroupChoices = [ungrouped, .. groups];
        if (FilterChoices.All(choice => choice.Id != SelectedFilterChoice.Id))
        {
            _selectedFilterChoice = all;
            OnPropertyChanged(nameof(SelectedFilterChoice));
            ModsView.Refresh();
        }

        OnPropertyChanged(nameof(FilterChoices));
        OnPropertyChanged(nameof(AssignGroupChoices));
        OnPropertyChanged(nameof(CanManageSelectedGroup));
        OnPropertyChanged(nameof(SelectedGroupName));
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
                BackgroundTaskStatus.Succeeded => string.Format(
                    _localization.GetString("Library.DeployedFormat"),
                    changedModName ?? _localization.GetString("Next.Library.EnabledModsFallback")),
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

        var filterId = SelectedFilterChoice.Id;
        if (filterId == Guid.Empty && item.GroupId is not null)
        {
            return false;
        }

        if (filterId is { } groupId && groupId != Guid.Empty && item.GroupId != groupId)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        // 仅当拼音缓存就绪时才走模糊匹配，避免 IsMatch 在热路径内部重复转换拼音；
        // 缓存由加载后的后台预热填充，未命中时退化为普通子串匹配。
        if (_settingsService.Current.EnableFuzzySearch &&
            _pinyinCache.TryGetValue(item.Id, out var pinyin) &&
            FuzzySearchMatcher.IsMatch(item.Name, SearchText, false, pinyin.FullPinyin, pinyin.FirstLetters))
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
