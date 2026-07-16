using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GongSolutions.Wpf.DragDrop;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Nexus;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SharpSevenZip;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using MessageBox = Helldivers2ModManager.Components.MessageBox;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class DashboardPageViewModel : PageViewModelBase, IDropTarget
{
    public override string Title => _localizationService["DashboardPage.Title"];

    public IEnumerable<object> Mods { get; private set; }

    public bool IsSearchEmpty => string.IsNullOrEmpty(SearchText);

    private static readonly ProcessStartInfo s_gameStartInfo = new("steam://run/553850") { UseShellExecute = true };
    private static readonly ProcessStartInfo s_reportStartInfo = new("https://github.com/TYHH100/Helldivers2ModManager/issues") { UseShellExecute = true };
    private static readonly ProcessStartInfo s_discordStartInfo = new("https://discord.gg/helldiversmodding") { UseShellExecute = true };
    private static readonly ProcessStartInfo s_githubStartInfo = new("https://github.com/teutinsa/Helldivers2ModManager") { UseShellExecute = true };
    private static readonly ProcessStartInfo s_githubForkStartInfo = new("https://github.com/TYHH100/Helldivers2ModManager") { UseShellExecute = true };
    private readonly ILogger<DashboardPageViewModel> _logger;
    private readonly Lazy<NavigationStore> _navStore;
    private readonly EditModStore _editModStore;
    private readonly ModService _modService;
    private readonly SettingsService _settingsService;
    private readonly ProfileService _profileService;
    private readonly ProfileSaveCoordinator _profileSaveCoordinator;
    private readonly INexusModsService _nexusModsService;
    private readonly VersionCheckRepository _versionCheckRepository;
    private readonly ModHashService _modHashService;
    private ObservableCollection<ModViewModel> _mods;
    private ObservableCollection<object> _orderedItems;
    private readonly SearchFilterService _searchFilterService;
    private readonly SortService _sortService;
    private readonly VersionCheckViewModel _versionCheckVm;
    private readonly LocalizationService _localizationService;
    private readonly BackgroundTaskService _backgroundTaskService;
    private readonly ModGroupService _modGroupService;

    [ObservableProperty]
    private string _searchText = string.Empty;
    [ObservableProperty]
    private bool _initialized = false;

    [ObservableProperty]
    private SortMode _currentSortMode = SortMode.Default;

    /// <summary>
    /// 是否有选中的 Mod（用于控制批量操作按钮的可见性）
    /// </summary>
    public bool HasSelection => _mods is not null && _modGroupService.FilterModViewModels(_mods).Any(static vm => vm.IsSelected);

    /// <summary>
    /// 选中数量文本（如 "已选 2 项"）
    /// </summary>
    public string SelectionCountText => _mods is null ? "" : $"{_localizationService["DashboardPage.AlreadySelectedPrefix"]}{_modGroupService.FilterModViewModels(_mods).Count(static vm => vm.IsSelected)}{_localizationService["DashboardPage.SelectedCountSuffix"]}";

    /// <summary>
    /// 排序功能是否在设置中启用
    /// </summary>
    public bool IsSortingEnabled => _sortService.IsSortingEnabled;

    /// <summary>
    /// 自定义部署顺序功能是否在设置中启用
    /// </summary>
    public bool IsDeploymentOrderEnabled => _settingsService.Initialized && _settingsService.UseDeploymentOrder;

    /// <summary>
    /// 在主页导航面板中显示分隔线
    /// </summary>
    public bool ShowSeparator => _settingsService.Initialized && _settingsService.ShowSeparator;

    public ModGroupSidebarViewModel GroupSidebar { get; }

    // ===== 版本兼容性检测属性（委托给 VersionCheckViewModel） =====

    public bool IsCheckingVersion => _versionCheckVm.IsCheckingVersion;
    public string VersionCheckSummary => _versionCheckVm.VersionCheckSummary;
    public int CompatibleModCount => _versionCheckVm.CompatibleModCount;
    public int IncompatibleModCount => _versionCheckVm.IncompatibleModCount;

    /// <summary>
    /// 上次版本检查是否有不兼容的模组
    /// </summary>
    public bool HasIncompatibleMods => _versionCheckVm.HasIncompatibleMods;

    /// <summary>
    /// 是否已完成版本检查
    /// </summary>
    public bool HasVersionCheckResult => _versionCheckVm.HasVersionCheckResult;

    /// <summary>
    /// 哈希迁移状态文本，显示在底部状态栏中。
    /// 后台哈希计算（版本升级迁移）进行中时显示进度，完成后显示结果摘要。
    /// </summary>
    [ObservableProperty]
    private string _hashMigrationStatusText = string.Empty;

    public IEnumerable<SortMode> SortModes { get; } = [SortMode.Default, SortMode.NameAsc, SortMode.NameDesc, SortMode.EnabledFirst, SortMode.DisabledFirst];

    public DashboardPageViewModel(
        ILogger<DashboardPageViewModel> logger,
        IServiceProvider provider,
        SettingsService settingsService,
        ModService modService,
        ProfileService profileService,
        ProfileSaveCoordinator profileSaveCoordinator,
        EditModStore editModStore,
        INexusModsService nexusModsService,
        VersionCheckRepository versionCheckRepository,
        ModHashService modHashService,
        SearchFilterService searchFilterService,
        SortService sortService,
        VersionCheckViewModel versionCheckVm,
        LocalizationService localizationService,
        BackgroundTaskService backgroundTaskService,
        ModGroupService modGroupService,
        ModGroupSidebarViewModel groupSidebar)
    {
        _logger = logger;
        _navStore = new(provider.GetRequiredService<NavigationStore>);
        _editModStore = editModStore;
        _settingsService = settingsService;
        _modService = modService;
        _profileService = profileService;
        _profileSaveCoordinator = profileSaveCoordinator;
        _nexusModsService = nexusModsService;
        _versionCheckRepository = versionCheckRepository;
        _modHashService = modHashService;
        _searchFilterService = searchFilterService;
        _sortService = sortService;
        _versionCheckVm = versionCheckVm;
        _versionCheckVm.PropertyChanged += VersionCheckVm_PropertyChanged;
        _localizationService = localizationService;
        _backgroundTaskService = backgroundTaskService;
        _modGroupService = modGroupService;
        GroupSidebar = groupSidebar;
        GroupSidebar.Configure(GetSelectedModData, () => _mods?.Select(static vm => vm.Data) ?? [], SelectGroupAsync, UpdateGroupedView);

        // 监听语言切换，通知 Title 属性变更
        _localizationService.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
        };

        // 订阅哈希迁移进度事件，将后台计算状态同步到 UI
        _modHashService.MigrationProgressChanged += (progress) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                HashMigrationStatusText = progress.Message ?? string.Empty;
            });
        };
        _mods = [];
        _orderedItems = [];

        Mods = _orderedItems;

        if (MessageBox.IsRegistered)
            _ = Init();
        else
            MessageBox.Registered += (_, _) => _ = Init();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchText))
        {
            OnPropertyChanged(nameof(IsSearchEmpty));
            ClearSearchCommand.NotifyCanExecuteChanged();
            UpdateView();
        }

        base.OnPropertyChanged(e);
    }

    private ProfileSnapshot CaptureProfileSnapshot()
    {
        var group = _modGroupService.SelectedGroup;
        var groupMods = _modGroupService.FilterModViewModels(_mods).ToArray();
        var order = _orderedItems.OfType<ModViewModel>().Select(static vm => vm.Guid).ToArray();
        return _profileSaveCoordinator.Capture(
            groupMods.Select(static vm => vm.Data),
            order,
            group.Id,
            group.IsDefault);
    }

    private void RequestProfileSave()
    {
        if (!_settingsService.IsReadonly)
            _profileSaveCoordinator.RequestSave(CaptureProfileSnapshot());
    }

    private async Task SaveProfileNowAsync(bool showProgress = true, ProfileSnapshot? snapshot = null)
    {
        if (_settingsService.IsReadonly)
            return;

        snapshot ??= CaptureProfileSnapshot();
        if (showProgress)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
            {
                Title = _localizationService["DashboardPage.SavingModConfig"],
                Message = _localizationService["SettingsPage.PleaseWait"]
            });
        }

        try
        {
            await _profileSaveCoordinator.SaveNowAsync(snapshot);
        }
        finally
        {
            if (showProgress)
                WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
        }
    }

    private void RebuildOrderedItems()
    {
        _orderedItems.Clear();
        var groupedMods = _modGroupService.FilterModViewModels(_mods).ToArray();

        if (!ShowSeparator)
        {
            // 分隔符未启用时，只显示所有模组
            foreach (var mod in groupedMods)
                _orderedItems.Add(mod);
            OnPropertyChanged(nameof(Mods));
            return;
        }

        // 先添加所有模组
        foreach (var mod in groupedMods)
            _orderedItems.Add(mod);

        // 按 DisplayIndex 排序后插入分隔符到对应位置
        if (_settingsService.Initialized)
        {
            var sortedSeps = _settingsService.Separators
                .OrderBy(s => s.DisplayIndex >= 0 ? s.DisplayIndex : int.MaxValue)
                .ToList();
            foreach (var sep in sortedSeps)
            {
                int insertAt = sep.DisplayIndex >= 0
                    ? Math.Min(sep.DisplayIndex, _orderedItems.Count)
                    : _orderedItems.Count;
                _orderedItems.Insert(insertAt, sep);
            }
        }

        OnPropertyChanged(nameof(Mods));
    }

    private void UpdateView()
    {
        IEnumerable<ModViewModel> filteredMods = _modGroupService.FilterModViewModels(_mods);

        // 搜索过滤
        filteredMods = _searchFilterService.ApplySearchFilter(filteredMods, SearchText);

        // 排序
        bool hasActiveSort = _sortService.IsActiveSort(CurrentSortMode);
        if (hasActiveSort)
            filteredMods = _sortService.ApplySort(filteredMods, CurrentSortMode);

        // 无任何筛选/排序时使用完整的_orderedItems（分隔符可见）
        if (IsSearchEmpty && !hasActiveSort)
        {
            // 重置 Mods 指向 _orderedItems，因为 else 分支可能已将 Mods 设为新的数组，
            // 导致 RebuildOrderedItems 修改 _orderedItems 后 UI 读取的仍是旧数组
            Mods = _orderedItems;
            RebuildOrderedItems();
        }
        else
        {
            // 有筛选/排序时只显示过滤后的模组列表（不显示分隔符）
            // 此时 Mods 是只读的，拖拽不可用
            Mods = filteredMods.ToArray();
            OnPropertyChanged(nameof(Mods));
        }
    }

    /// <summary>
    /// 排序方式变更时刷新列表
    /// </summary>
    partial void OnCurrentSortModeChanged(SortMode value)
    {
        UpdateView();
    }

    private IEnumerable<ModData> GetSelectedModData()
    {
        return _modGroupService.FilterModViewModels(_mods).Where(static vm => vm.IsSelected).Select(static vm => vm.Data);
    }

    private async Task SelectGroupAsync(Guid groupId)
    {
        await _modGroupService.SelectGroupAsync(groupId, _mods.Select(static vm => vm.Data));
        foreach (var vm in _mods)
        {
            vm.IsSelected = false;
            vm.RefreshGroupStateBindings();
        }
        UpdateGroupedView();
    }

    private void UpdateGroupedView()
    {
        GroupSidebar.RefreshSelectionProperties();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionCountText));
        UpdateView();
    }

    private async Task Init()
    {
        _logger.LogInformation("Initializing dashboard...");

        _logger.LogInformation("Loading settings...");
        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
        {
            Title = _localizationService["SettingsPage.LoadingSettings"],
            Message = _localizationService["SettingsPage.PleaseWait"],
        });
        try
        {
            if (!await _settingsService.InitAsync(false))
                _settingsService.InitDefault(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading settings failed");
            WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
            {
                Title = _localizationService["SettingsPage.LoadSettingsFailed"],
                Message = _localizationService["DashboardPage.GoToSettings"],
                Confirm = _navStore.Value.Navigate<SettingsPageViewModel>,
            });
            return;
        }
        _logger.LogInformation("Settings loaded successfully");
        WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());

        // 将用户设置的日志级别同步到 App.Current，FileLogger 依赖此值进行过滤
        App.Current.LogLevel = _settingsService.LogLevel;

        _logger.LogInformation("Validating settings");
        if (!_settingsService.Validate())
        {
            _logger.LogError("Settings invalid");
            WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
            {
                Title = _localizationService["DashboardPage.SettingsInvalid"],
                Message = _localizationService["DashboardPage.GoToSettings"],
                Confirm = _navStore.Value.Navigate<SettingsPageViewModel>,
            });
            return;
        }
        _logger.LogInformation("Settings valid");

        try
        {
            await _profileSaveCoordinator.FlushAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flushing pending profile state before dashboard load failed");
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = $"{_localizationService["DashboardPage.LoadConfigFailed"]}\n\n{ex}",
            });
            return;
        }

        _logger.LogInformation("Loading mods...");
        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
        {
            Title = _localizationService["DashboardPage.LoadingMods"],
            Message = _localizationService["SettingsPage.PleaseWait"],
        });
        ModProblem[] problems;
        try
        {
            problems = await Task.Run(() => _modService.Init(_settingsService));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading mods failed");
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = $"{_localizationService["DashboardPage.LoadModsFailed"]}\n\n{ex}",
            });
            return;
        }
        _modService.ModAdded += ModService_ModAdded;
        _modService.ModAdded += OnModAdded;
        _modService.ModRemoved += ModService_ModRemoved;
        if (problems.Length != 0)
            _logger.LogWarning("Loaded mods with {} problems", problems.Length);
        else
            _logger.LogInformation("Mods loaded successfully");
        WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());

        _logger.LogInformation("Loading profile...");
        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
        {
            Title = _localizationService["DashboardPage.LoadingConfig"],
            Message = _localizationService["SettingsPage.PleaseWait"],
        });
        IReadOnlyList<ModData>? result;
        try
        {
            result = await _profileService.LoadAsync(_settingsService, _modService);
            result ??= _profileService.InitDefault(_modService);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading profile failed");
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = $"{_localizationService["DashboardPage.LoadConfigFailed"]}\n\n{ex}",
            });
            return;
        }
        WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
        _logger.LogInformation("Profile loaded successfully");

        _logger.LogInformation("Applying profile");
        var modViewModels = result.Select(data => _modService.GetOrCreateModViewModel(data, _logger, _settingsService, _nexusModsService)).ToList();
        foreach (var vm in modViewModels)
        {
            vm.OptionsChanged += ModViewModel_OptionsChanged;
            vm.PropertyChanged += ModViewModel_PropertyChanged;
            vm.VersionCheckRefreshed += ModViewModel_VersionCheckRefreshed;
        }
        _mods = new(modViewModels);
        _mods.CollectionChanged += Mods_CollectionChanged;
        await _modGroupService.InitAsync(_settingsService, _mods.Select(static vm => vm.Data).ToArray());
        _modGroupService.ApplyGroupState(_modGroupService.SelectedGroup.Id, _mods.Select(static vm => vm.Data));
        foreach (var vm in _mods)
            vm.RefreshGroupStateBindings();
        GroupSidebar.IsOpen = _modGroupService.IsSidebarOpen;
        GroupSidebar.RefreshSelectionProperties();
        RebuildOrderedItems();
        _ = CaptureProfileSnapshot();
        UpdateView();

        if (problems.Length > 0)
            ShowProblems(problems, _localizationService["DashboardPage.LoadProblemsPrefix"], false, true);

        // 从数据库加载已缓存的版本检测结果，避免每次启动都需要全量扫描
        _versionCheckVm.LoadCachedResults(_mods);

        Initialized = true;
        _logger.LogInformation("Initialization successful");

        // 检测新增或变动的模组，自动触发版本兼容性检查
        var autoCheckReason = _versionCheckVm.GetAutoCheckReason(_mods);
        if (autoCheckReason != VersionAutoCheckReason.None)
        {
            var message = autoCheckReason == VersionAutoCheckReason.GameExeUpdated
                ? _localizationService["DashboardPage.VersionCheckAutoGameExeMsg"]
                : _localizationService["DashboardPage.VersionCheckAutoModMsg"];
            _logger.LogInformation("{Message}", message);
            _ = RunVersionCheckCompatibilityAsync(false);
        }

#if DEBUG && FALSE
		ShowProblems(Enum.GetValues<ModProblemKind>().Select(static k => new ModProblem { Directory = new DirectoryInfo(@"C:\ModStorage\Test"), Kind = k }), "Problem test:", true);
#endif
    }

    private void ShowProblems(IEnumerable<ModProblem> problems, string prefix, bool error, bool isInit = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine(prefix);

        var errors = problems.Where(static p => p.IsError).ToArray();
        if (errors.Length != 0)
        {
            sb.AppendLine(_localizationService["Common.ErrorPrefix"]);
            foreach (var e in errors)
            {
                sb.Append("\t - \"");
                sb.Append(e.Directory.FullName);
                sb.AppendLine("\"");

                sb.Append("\t\t");
                string desc = e.Kind switch
                {
                    ModProblemKind.CantParseManifest => _localizationService["DashboardPage.CantParseManifest"],
                    ModProblemKind.UnknownManifestVersion => _localizationService["DashboardPage.UnknownManifestVersion"],
                    ModProblemKind.OutOfSupportManifest => $"{_localizationService["DashboardPage.OutOfSupportManifest"]}{App.Version}{_localizationService["DashboardPage.VersionNotSupported"]}",
                    ModProblemKind.Duplicate => _localizationService["DashboardPage.DuplicateGuid"],
                    ModProblemKind.InvalidPath => e.ExtraData is not null
                        ? $"{_localizationService["DashboardPage.InvalidPathPrefix"]}{e.ExtraData}{_localizationService["DashboardPage.InvalidPathSuffix"]}"
                        : _localizationService["DashboardPage.InvalidPathError"],
                    ModProblemKind.CantReadArchive => e.ExtraData is not null
                        ? $"{_localizationService["DashboardPage.CantReadArchivePrefix"]}{e.ExtraData}"
                        : _localizationService["DashboardPage.CantReadArchive"],
                    _ => throw new NotImplementedException()
                };
                sb.AppendLine(desc);
            }
        }

        var warnings = problems.Where(static p => !p.IsError).ToArray();
        if (warnings.Length != 0)
        {
            sb.AppendLine(_localizationService["Common.WarningPrefix"]);
            foreach (var w in warnings)
            {
                sb.Append("\t - \"");
                sb.Append(w.Directory.FullName);
                sb.AppendLine("\"");

                sb.Append("\t\t");
                string desc = w.Kind switch
                {
                    ModProblemKind.NoManifestFound => isInit
                        ? _localizationService["DashboardPage.NoManifestFoundDelete"]
                        : _localizationService["DashboardPage.NoManifestFoundInfer"],
                    ModProblemKind.EmptyOptions => _localizationService["DashboardPage.EmptyOptions"],
                    ModProblemKind.EmptySubOptions => _localizationService["DashboardPage.EmptySubOptions"],
                    ModProblemKind.EmptyIncludes => _localizationService["DashboardPage.EmptyIncludes"],
                    ModProblemKind.InvalidImagePath => w.ExtraData is not null
                        ? $"{_localizationService["DashboardPage.InvalidImagePathPrefix"]}{w.ExtraData}{_localizationService["DashboardPage.InvalidImagePathSuffix"]}"
                        : _localizationService["DashboardPage.InvalidImagePathError"],
                    ModProblemKind.EmptyImagePath => _localizationService["DashboardPage.EmptyImagePath"],
                    _ => throw new NotImplementedException()
                };
                sb.AppendLine(desc);
            }
        }

        if (error)
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = sb.ToString(),
            });
        else
            WeakReferenceMessenger.Default.Send(new MessageBoxWarningMessage
            {
                Message = sb.ToString(),
            });
    }

    private void ModService_ModAdded(ModData mod)
    {
        var vm = _modService.GetOrCreateModViewModel(mod, _logger, _settingsService, _nexusModsService);
        vm.OptionsChanged += ModViewModel_OptionsChanged;
        vm.PropertyChanged += ModViewModel_PropertyChanged;
        vm.VersionCheckRefreshed += ModViewModel_VersionCheckRefreshed;
        _mods.Add(vm);
        SearchText = string.Empty;
        _modGroupService.CaptureGroupState(ModGroup.DefaultGroupId, _mods.Select(static vm => vm.Data));
        GroupSidebar.RefreshSelectionProperties();
        UpdateView();
    }

    private async void OnModAdded(ModData mod)
    {
        await _versionCheckVm.CheckSingleModOnAddAsync(mod, _mods);
        // 通知 UI 属性变更
        OnPropertyChanged(nameof(VersionCheckSummary));
        OnPropertyChanged(nameof(HasVersionCheckResult));
    }

    private void ModService_ModRemoved(ModData mod)
    {
        // 使用 GUID 查找而不是引用相等性，避免因 ModData 引用不匹配导致界面不同步
        var vm = _mods.FirstOrDefault(vm => vm.Guid == mod.Manifest.Guid);
        if (vm is not null)
        {
            vm.OptionsChanged -= ModViewModel_OptionsChanged;
            vm.PropertyChanged -= ModViewModel_PropertyChanged;
            vm.VersionCheckRefreshed -= ModViewModel_VersionCheckRefreshed;
            _mods.Remove(vm);
            _modGroupService.CaptureGroupState(_modGroupService.SelectedGroup.Id, _modGroupService.FilterMods(_mods.Select(static vm => vm.Data)));
            GroupSidebar.RefreshSelectionProperties();
            UpdateView();
        }
    }

    private void ModViewModel_VersionCheckRefreshed(object? sender, EventArgs e)
    {
        _ = _versionCheckVm.RefreshAfterSingleModCheckAsync(_mods);
    }

    private void ModViewModel_OptionsChanged()
    {
        RequestProfileSave();
    }

    /// <summary>
    /// 监听集合变动，当用户拖拽排序后触发自动保存（Move 操作不触发 OptionsChanged）
    /// </summary>
    private void Mods_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Add
            or System.Collections.Specialized.NotifyCollectionChangedAction.Remove
            or System.Collections.Specialized.NotifyCollectionChangedAction.Move)
            RequestProfileSave();
    }

    /// <summary>
    /// 拖拽悬停 —— 分隔符不可拖拽，模组使用默认指示器
    /// </summary>
    void IDropTarget.DragOver(IDropInfo dropInfo)
    {
        // 模组和分隔符均可自由拖动
        new DefaultDropHandler().DragOver(dropInfo);
    }

    /// <summary>
    /// 拖拽放下 —— 支持分隔符归类、多选批量移动和单项目拖拽
    /// </summary>
    void IDropTarget.Drop(IDropInfo dropInfo)
    {
        // 处理分隔符拖拽重排
        if (dropInfo?.Data is ModSeparator)
        {
            new DefaultDropHandler().Drop(dropInfo);
            // 同步分隔符顺序到 SettingsService
            SyncSeparatorsOrder();
            return;
        }

        if (dropInfo?.Data is not ModViewModel sourceVm)
        {
            new DefaultDropHandler().Drop(dropInfo);
            return;
        }

        // 获取选中项（含当前拖拽项），按原始位置排序
        var selected = _mods.Where(vm => vm.IsSelected).ToList();
        if (selected.Contains(sourceVm) && selected.Count > 1)
        {
            var sortedSelected = selected.OrderBy(vm => _orderedItems.IndexOf(vm)).ToList();
            var targetIdx = dropInfo.InsertIndex;

            // 从 _orderedItems 中移除所有选中项（倒序删除以保持索引正确）
            foreach (var vm in sortedSelected.AsEnumerable().Reverse())
                _orderedItems.Remove(vm);

            // 如果目标索引位于删除区域之后，需修正插入位置
            if (targetIdx > 0 && targetIdx <= _orderedItems.Count)
            {
                // 重新计算插入位置
                var beforeCount = sortedSelected.Count(vm => _orderedItems.IndexOf(vm) < targetIdx);
                targetIdx -= beforeCount;
            }

            targetIdx = Math.Clamp(targetIdx, 0, _orderedItems.Count);

            // 按原始顺序插入到 _orderedItems
            for (int i = 0; i < sortedSelected.Count; i++)
                _orderedItems.Insert(targetIdx + i, sortedSelected[i]);

            // 同步 _orderedItems 顺序到 _mods
            SyncModsOrderFromDisplay();
        }
        else
        {
            // 单项目拖拽
            // 使用默认处理器在 _orderedItems 上操作
            new DefaultDropHandler().Drop(dropInfo);
            // 同步到 _mods
            SyncModsOrderFromDisplay();
        }

        RequestProfileSave();
    }

    /// <summary>
    /// 将 _orderedItems 中的分隔符顺序同步回 SettingsService.Separators
    /// </summary>
    private void SyncSeparatorsOrder()
    {
        if (!_settingsService.Initialized)
            return;
        // 从 _orderedItems 中读取分隔符的当前显示位置
        foreach (var sep in _settingsService.Separators)
        {
            int idx = _orderedItems.IndexOf(sep);
            sep.DisplayIndex = idx >= 0 ? idx : -1;
        }
        _ = _settingsService.SaveAsync();
    }

    /// <summary>
    /// 将 _orderedItems 中的模组顺序同步回 _mods
    /// </summary>
    private void SyncModsOrderFromDisplay()
    {
        var displayOrder = _orderedItems.OfType<ModViewModel>().ToList();
        if (!_modGroupService.SelectedGroup.IsDefault)
        {
            var group = _modGroupService.SelectedGroup;
            group.ModGuids.Clear();
            foreach (var vm in displayOrder)
                group.ModGuids.Add(vm.Guid);
            return;
        }

        // 按 displayOrder 重新排序 _mods
        for (int i = 0; i < displayOrder.Count; i++)
        {
            var currentIdx = _mods.IndexOf(displayOrder[i]);
            if (currentIdx != i)
            {
                _mods.Move(currentIdx, Math.Min(i, _mods.Count - 1));
            }
        }
    }

    /// <summary>
    /// 监听 ModViewModel 属性变更，捕获 IsSelected 变化以刷新批量操作 UI
    /// </summary>
    private void ModViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectionCountText));
        }
        else if (e.PropertyName == nameof(ModViewModel.Enabled))
        {
            RequestProfileSave();
        }
    }

    /// <summary>
    /// 当 Mod 的 IsSelected 变更时，更新批量操作按钮的可见性和选中计数
    /// </summary>
    private void ModViewModel_IsSelectedChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionCountText));
    }

    [RelayCommand]
    void SelectAll()
    {
        foreach (var vm in _modGroupService.FilterModViewModels(_mods))
            vm.IsSelected = true;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionCountText));
    }

    [RelayCommand]
    void DeselectAll()
    {
        foreach (var vm in _modGroupService.FilterModViewModels(_mods))
            vm.IsSelected = false;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionCountText));
    }

    [RelayCommand]
    void ToggleModSelection(ModViewModel vm)
    {
        vm.IsSelected = !vm.IsSelected;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    Task BatchDelete()
    {
        var selected = _modGroupService.FilterModViewModels(_mods).Where(static vm => vm.IsSelected).ToArray();
        if (selected.Length == 0)
            return Task.CompletedTask;

        var deleteMessage = _settingsService.DeleteToRecycleBin
            ? _localizationService["DashboardPage.RecycleBinConfirm"]
            : _localizationService["DashboardPage.PermanentDeleteConfirm"];

        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = _localizationService["DashboardPage.BatchDeleteTitle"],
            Message = $"{_localizationService["DashboardPage.BatchDeleteConfirm"].Replace("{count}", selected.Length.ToString())}{deleteMessage}",
            Confirm = async () =>
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
                {
                    Title = _localizationService["DashboardPage.BatchDeleteProgress"],
                    Message = _localizationService["SettingsPage.PleaseWait"]
                });

                try
                {
                    foreach (var vm in selected)
                    {
                        vm.IsSelected = false;
                        await _modService.RemoveAsync(vm.Data);
                    }

                    // 批量删除后同步更新数据库：直接删除这些模组对应的记录
                    if (!_settingsService.IsReadonly)
                    {
                        var guids = selected.Select(static vm => vm.Guid).ToList();
                        await _profileService.DeleteEnabledDataAsync(_settingsService.StorageDirectory, guids);
                        await _modGroupService.RemoveModsFromAllGroupsAsync(guids);
                        // 同时删除这些模组的版本检测记录
                        foreach (var guid in guids)
                            await _versionCheckRepository.DeleteByGuidAsync(_settingsService.StorageDirectory, guid);
                    }

                    WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, _localizationService["DashboardPage.BatchDeleteFailed2"]);
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
                    {
                        Message = $"{_localizationService["DashboardPage.BatchDeleteFailed"]}{ex.Message}"
                    });
                }

                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectionCountText));
            }
        });

        return Task.CompletedTask;
    }

    [RelayCommand]
    void BatchEnable()
    {
        foreach (var vm in _modGroupService.FilterModViewModels(_mods).Where(static vm => vm.IsSelected))
            vm.Enabled = true;
    }

    [RelayCommand]
    void BatchDisable()
    {
        foreach (var vm in _modGroupService.FilterModViewModels(_mods).Where(static vm => vm.IsSelected))
            vm.Enabled = false;
    }

    /// <summary>
    /// 批量打标签 —— 为所有选中的模组统一设置标签
    /// </summary>
    [RelayCommand]
    void BatchAddTags()
    {
        var selected = _modGroupService.FilterModViewModels(_mods).Where(static vm => vm.IsSelected).ToArray();
        if (selected.Length == 0 || !_settingsService.Initialized)
            return;

        // 使用第一个选中模组的标签作为初始选择状态（方便用户基于现有标签增减）
        var initialTagIds = selected[0].Data.TagIds.ToList();
        var selectableTags = _settingsService.Tags.Select(t => new TagSelectionItem(t, initialTagIds.Contains(t.Id))).ToList();

        WeakReferenceMessenger.Default.Send(new MessageBoxTagSelectionMessage
        {
            Title = _localizationService["DashboardPage.BatchTagTitle"],
            Message = $"{_localizationService["DashboardPage.BatchTagPrefix"]}{selected.Length}{_localizationService["DashboardPage.BatchTagSuffix"]}",
            Tags = selectableTags,
            Confirm = (selectedTags) =>
            {
                if (_settingsService.IsReadonly)
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["DashboardPage.BatchTagReadonly"] });
                    return;
                }

                var newTagIds = selectedTags.Select(static t => t.Tag.Id).ToList();
                foreach (var vm in selected)
                {
                    vm.Data.TagIds = newTagIds;
                }
                RequestProfileSave();
                WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = $"{_localizationService["DashboardPage.BatchTagUpdatedPrefix"]}{selected.Length}{_localizationService["DashboardPage.BatchTagUpdatedSuffix"]}" });
            }
        });
    }

    [RelayCommand]
    void AddModsToGroup(ModViewModel? source = null)
    {
        if (!_settingsService.Initialized)
            return;

        var selected = source is not null && !source.IsSelected
            ? [source]
            : _modGroupService.FilterModViewModels(_mods).Where(static vm => vm.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["ModGroup.NoSelectedMods"] });
            return;
        }

        var groups = _modGroupService.Groups.Where(static group => !group.IsDefault).Cast<object>().ToArray();
        if (groups.Length == 0)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["ModGroup.NoCustomGroups"] });
            return;
        }

        WeakReferenceMessenger.Default.Send(new MessageBoxSelectionMessage
        {
            Title = _localizationService["ModGroup.AddToGroupTitle"],
            Message = _localizationService["ModGroup.AddToGroupMessage"].Replace("{count}", selected.Length.ToString()),
            Options = groups,
            Confirm = option =>
            {
                if (option is not ModGroup group)
                    return;

                _ = AddModsToGroupAsync(group, selected);
            }
        });
    }

    private async Task AddModsToGroupAsync(ModGroup group, ModViewModel[] selected)
    {
        try
        {
            await _modGroupService.AddModsToGroupAsync(group.Id, selected.Select(static vm => vm.Data));
            GroupSidebar.RefreshSelectionProperties();
            WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
            {
                Message = _localizationService["ModGroup.AddedToGroup"].Replace("{count}", selected.Length.ToString()).Replace("{name}", group.Name)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加入分组失败");
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = ex.Message });
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
        async Task Add(string? filePath = null)
        {
            // 支持单文件路径传入（如拖拽场景）或批量文件选择
            List<string> selectedFiles = [];

            if (filePath is not null)
            {
                selectedFiles.Add(filePath);
            }
            else
            {
                var dialog = new OpenFileDialog
                {
                    CheckFileExists = true,
                    CheckPathExists = true,
                    InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Download"),
                    Filter = _localizationService["Common.FileFilterArchive"],
                    Multiselect = true,
                    Title = _localizationService["DashboardPage.AddModDialogTitle"]
                };

                if (!(dialog.ShowDialog() ?? false))
                    return;

                selectedFiles.AddRange(dialog.FileNames);
            }

            if (selectedFiles.Count == 0)
                return;

            // 单文件时使用原有提示文案，多文件时显示进度
            var isBatch = selectedFiles.Count > 1;
            var totalFiles = selectedFiles.Count;
            WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
            {
                Title = isBatch ? _localizationService["DashboardPage.BatchAddProgressTitle"].Replace("{current}", "0").Replace("{total}", totalFiles.ToString()) : _localizationService["DashboardPage.AddSingleProgress"],
                Message = isBatch ? _localizationService["DashboardPage.BatchAddWaitMsg"].Replace("{total}", totalFiles.ToString()) : _localizationService["SettingsPage.PleaseWait"]
            });

            var backgroundTask = _backgroundTaskService.Add(
                _localizationService["BackgroundTasksPage.TaskTypeImport"],
                isBatch ? _localizationService["DashboardPage.BatchAddWaitMsg"].Replace("{total}", totalFiles.ToString()) : Path.GetFileName(selectedFiles[0]));
            _backgroundTaskService.Update(backgroundTask, progress: 0, isIndeterminate: false);

            try
            {
                var allProblems = new List<ModProblem>();
                int successCount = 0;
                int failCount = 0;

                for (int i = 0; i < selectedFiles.Count; i++)
                {
                    // 批量模式下更新进度提示（含剩余数量）
                    if (isBatch)
                    {
                        var remainingCount = totalFiles - i - 1;
                        var description = remainingCount > 0
                            ? _localizationService["DashboardPage.BatchAddProcessing"].Replace("{file}", Path.GetFileName(selectedFiles[i])).Replace("{remaining}", remainingCount.ToString())
                            : _localizationService["DashboardPage.BatchAddProcessing"].Replace("{file}", Path.GetFileName(selectedFiles[i])).Replace("{remaining}", "?");
                        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
                        {
                            Title = _localizationService["DashboardPage.BatchAddProgressTitle"].Replace("{current}", (i + 1).ToString()).Replace("{total}", totalFiles.ToString()),
                            Message = description
                        });
                        _backgroundTaskService.Update(backgroundTask, description, (double)i / totalFiles, false);
                    }
                    else
                    {
                        _backgroundTaskService.Update(backgroundTask, Path.GetFileName(selectedFiles[i]), (double)i / totalFiles, false);
                    }

                    // 创建嵌套压缩包处理进度回调，用于在处理嵌套压缩包时更新UI进度显示
                    var currentBatchIndex = i;
                    var currentFileName = selectedFiles[i];
                    Action<int, int, string> nestedProgress = (nestedIndex, nestedTotal, nestedFileName) =>
                    {
                        var nestedDescription = _localizationService["DashboardPage.BatchAddProcessing"].Replace("{file}", nestedFileName).Replace("{remaining}", (nestedTotal - nestedIndex - 1).ToString());
                        // 根据是否为批量导入模式，组合显示外层批量进度和内层嵌套进度
                        if (isBatch)
                        {
                            // 批量导入 + 嵌套处理：显示双层进度
                            WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
                            {
                                Title = _localizationService["DashboardPage.BatchAddNestedTitle"].Replace("{current}", (currentBatchIndex + 1).ToString()).Replace("{total}", totalFiles.ToString()).Replace("{nested}", (nestedIndex + 1).ToString()).Replace("{nestedTotal}", nestedTotal.ToString()),
                                Message = nestedDescription
                            });
                        }
                        else
                        {
                            // 单文件 + 嵌套处理：显示嵌套进度
                            WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
                            {
                                Title = _localizationService["DashboardPage.BatchAddNestedProgress"].Replace("{current}", (nestedIndex + 1).ToString()).Replace("{total}", nestedTotal.ToString()),
                                Message = nestedDescription
                            });
                        }

                        var outerProgress = (double)currentBatchIndex / totalFiles;
                        var nestedRatio = nestedTotal > 0 ? (double)(nestedIndex + 1) / nestedTotal : 0;
                        _backgroundTaskService.Update(backgroundTask, nestedDescription, outerProgress + nestedRatio / totalFiles, false);
                    };

                    try
                    {
                        var problems = await _modService.TryAddModFromArchiveAsync(new FileInfo(selectedFiles[i]), nestedProgress);
                        if (problems.Length > 0)
                        {
                            allProblems.AddRange(problems);
                            if (problems.Any(static p => p.IsError))
                                failCount++;
                            else
                                successCount++;
                        }
                        else
                        {
                            successCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to add mod: {File}", selectedFiles[i]);
                        // 使用 CantReadArchive 表示读取/解压失败，ExtraData 存储异常信息
                        allProblems.Add(new ModProblem
                        {
                            Directory = new DirectoryInfo(Path.GetDirectoryName(selectedFiles[i]) ?? ""),
                            Kind = ModProblemKind.CantReadArchive,
                            ExtraData = $"{Path.GetFileName(selectedFiles[i])}: {ex.Message}"
                        });
                        failCount++;
                    }
                }

                _backgroundTaskService.Complete(backgroundTask, _localizationService["BackgroundTasksPage.ImportComplete"].Replace("{success}", successCount.ToString()).Replace("{fail}", failCount.ToString()));

                // 汇总结果
                if (isBatch)
                {
                    if (failCount == 0 && allProblems.Count == 0)
                    {
                        WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
                        WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
                        {
                            Message = _localizationService["DashboardPage.BatchAddSuccess"].Replace("{count}", successCount.ToString())
                        });
                    }
                    else if (allProblems.Count > 0)
                    {
                        WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
                        var error = allProblems.Any(static p => p.IsError);
                        var prefix = error
                            ? _localizationService["DashboardPage.BatchAddDoneErrors"].Replace("{success}", successCount.ToString()).Replace("{fail}", failCount.ToString())
                            : _localizationService["DashboardPage.BatchAddDoneWarnings"].Replace("{count}", successCount.ToString());
                        ShowProblems([.. allProblems], prefix, error);
                    }
                }
                else
                {
                    // 单文件模式保持原有行为
                    if (allProblems.Count > 0)
                    {
                        var error = allProblems.Any(static p => p.IsError);
                        var prefix = error
                            ? _localizationService["DashboardPage.AddSingleError"]
                            : _localizationService["DashboardPage.AddSingleWarning"];
                        ShowProblems([.. allProblems], prefix, error);
                    }
                    else
                        WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add mod");
                _backgroundTaskService.Fail(backgroundTask, ex.Message);
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
                {
                    Message = ex.Message
                });
            }
        }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task UpdateMod(ModViewModel vm)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            CheckPathExists = true,
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Download"),
            Filter = _localizationService["Common.FileFilterArchive"],
            Multiselect = false,
            Title = $"{_localizationService["DashboardPage.UpdateModDialogPrefix"]}{vm.Name}{_localizationService["DashboardPage.UpdateModDialogSuffix"]}"
        };

        if (!(dialog.ShowDialog() ?? false))
            return;

        // 发送初始进度消息，显示更新进度UI
        WeakReferenceMessenger.Default.Send(new MessageBoxUpdateProgressMessage
        {
            Title = _localizationService["DashboardPage.UpdateModProgress"],
            ModName = vm.Name
        });

        var backgroundTask = _backgroundTaskService.Add(
            _localizationService["BackgroundTasksPage.TaskTypeUpdate"],
            vm.Name);

        try
        {
            // 创建进度报告回调，将服务层进度映射为UI消息
            var progress = new Progress<UpdateProgressInfo>(info =>
            {
                if (info.IsCompleted)
                {
                    // 更新完成，发送完成消息
                    WeakReferenceMessenger.Default.Send(new MessageBoxUpdateProgressUpdateMessage
                    {
                        IsCompleted = true
                    });

                    // 显示统计信息
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
                    {
                        Message = info.Message ?? _localizationService["DashboardPage.UpdateModDone"]
                    });
                    _backgroundTaskService.Complete(backgroundTask, info.Message ?? _localizationService["DashboardPage.UpdateModDone"]);
                }
                else
                {
                    var taskProgress = info.TotalCount > 0
                        ? (double)info.ProcessedCount / info.TotalCount
                        : 0;
                    WeakReferenceMessenger.Default.Send(new MessageBoxUpdateProgressUpdateMessage
                    {
                        PhaseText = info.Message,
                        CurrentFile = info.CurrentFile,
                        ProcessedCount = info.ProcessedCount,
                        TotalCount = info.TotalCount,
                        NeedUpdateCount = info.NeedUpdateCount,
                        CacheHits = info.CacheHits,
                        Progress = taskProgress
                    });
                    _backgroundTaskService.Update(backgroundTask, info.Message, taskProgress, info.TotalCount <= 0);
                }
            });

            await _modService.UpdateModFromArchiveAsync(vm.Data, new FileInfo(dialog.FileName), progress);

            // 更新后保存状态到数据库，确保 EnabledOptions/SelectedOptions 与新清单同步
            await SaveProfileNowAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update mod \"{}\"", vm.Name);
            _backgroundTaskService.Fail(backgroundTask, ex.Message);
            WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = $"{_localizationService["DashboardPage.UpdateModFailed"]}{ex.Message}"
            });
        }
    }

    // void Browse()
    // {
    //     throw new NotImplementedException();
    // }

    [RelayCommand]
    void Create()
    {
        _navStore.Value.Navigate<CreatePageViewModel>();
    }

    [RelayCommand]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is a command of a view model and should not be static.")]
    void ReportBug()
    {
        Process.Start(s_reportStartInfo);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task TagManagement()
    {
        await SaveProfileNowAsync();

        _navStore.Value.Navigate<TagManagementPageViewModel>();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task Settings()
    {
        await SaveProfileNowAsync();

        _navStore.Value.Navigate<SettingsPageViewModel>();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task DeploymentOrder()
    {
        await SaveProfileNowAsync();

        _navStore.Value.Navigate<DeploymentOrderPageViewModel>();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task Purge()
    {
        if (!_settingsService.Initialized || string.IsNullOrEmpty(_settingsService.GameDirectory))
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = _localizationService["DashboardPage.PurgeNoGameDir"]
            });
            return;
        }

        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
        {
            Title = _localizationService["DashboardPage.PurgeProgress"],
            Message = _localizationService["SettingsPage.PleaseWait"]
        });

        var backgroundTask = _backgroundTaskService.Add(
            _localizationService["BackgroundTasksPage.TaskTypePurge"],
            _localizationService["SettingsPage.PleaseWait"]);

        try
        {
            await _modService.PurgeAsync();
            _backgroundTaskService.Complete(backgroundTask, _localizationService["BackgroundTasksPage.PurgeComplete"]);
        }
        catch (Exception ex)
        {
            _backgroundTaskService.Fail(backgroundTask, ex.Message);
            throw;
        }
        finally
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
        }
    }

    /// <summary>
    /// 根据当前设置获取按部署顺序排列的主页快照模组
    /// 如果 UseDeploymentOrder 启用，按 DeploymentOrderGuids 顺序；否则按 Dashboard 顺序；最后应用部署方向设置
    /// </summary>
    private ModData[] GetDeploymentMods(ProfileSnapshot snapshot)
    {
        var enabledMods = snapshot.Mods.Where(static mod => mod.Enabled).ToArray();
        if (_settingsService.UseDeploymentOrder && _settingsService.DeploymentOrderGuids.Count > 0)
        {
            var enabledGuids = enabledMods.Select(static mod => mod.Guid).ToArray();
            var enabledSet = enabledGuids.ToHashSet();
            var modsByGuid = enabledMods.ToDictionary(static mod => mod.Guid);
            var result = new List<ModData>();

            foreach (var guid in _settingsService.DeploymentOrderGuids)
            {
                if (enabledSet.Contains(guid))
                {
                    result.Add(modsByGuid[guid].CreateDeploymentMod());
                    enabledSet.Remove(guid);
                }
            }

            // 添加不在 DeploymentOrderGuids 中的已启用模组（防御性）
            result.AddRange(enabledGuids
                .Where(enabledSet.Contains)
                .Select(guid => modsByGuid[guid].CreateDeploymentMod()));

            if (_settingsService.DeployBottomToTop)
                result.Reverse();

            _logger.LogDebug("Using custom deployment order for {} mods", result.Count);
            return result.ToArray();
        }
        else
        {
            var mods = enabledMods.Select(static mod => mod.CreateDeploymentMod()).ToArray();

            if (_settingsService.DeployBottomToTop)
                Array.Reverse(mods);

            return mods;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task Deploy()
    {
        if (!_settingsService.Initialized || string.IsNullOrEmpty(_settingsService.GameDirectory))
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = _localizationService["DashboardPage.DeployNoGameDir"]
            });
            return;
        }

        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
        {
            Title = _localizationService["DashboardPage.DeployProgress"],
            Message = _localizationService["SettingsPage.PleaseWait"]
        });

        var snapshot = CaptureProfileSnapshot();
        var deploymentMods = GetDeploymentMods(snapshot);
        var backgroundTask = _backgroundTaskService.Add(
            _localizationService["BackgroundTasksPage.TaskTypeDeploy"],
            _localizationService["SettingsPage.PleaseWait"]);

        try
        {
            await SaveProfileNowAsync(false, snapshot);

            await _modService.DeployAsync(deploymentMods);

            _backgroundTaskService.Complete(backgroundTask, _localizationService["DashboardPage.DeploySuccess"]);

            WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage()
            {
                Message = _localizationService["DashboardPage.DeploySuccess"]
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unknown deployment error");
            _backgroundTaskService.Fail(backgroundTask, ex.Message);
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = ex.Message
            });
        }
    }

    [RelayCommand]
    async Task RescanMods()
    {
        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
        {
            Title = _localizationService["DashboardPage.RescanMods"],
            Message = _localizationService["SettingsPage.PleaseWait"]
        });

        try
        {
            var problems = _modService.RescanMods();

            WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());

            if (problems.Length > 0)
                ShowProblems(problems, _localizationService["DashboardPage.RescanProblemsPrefix"], false, true);

            RebuildOrderedItems();
            UpdateView();

            if (!_settingsService.IsReadonly)
            {
                await SaveProfileNowAsync(false);
            }

            WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage()
            {
                Message = _localizationService["DashboardPage.RescanComplete"]
            });
        }
        catch (Exception ex)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
            _logger.LogError(ex, "Rescan mods failed");
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = ex.Message
            });
        }
    }

    [RelayCommand]
    void MoveUp(ModViewModel modVm)
    {
        var index = _mods.IndexOf(modVm);
        if (index <= 0)
            return;
        _mods.Move(index, index - 1);
    }

    [RelayCommand]
    void MoveDown(ModViewModel modVm)
    {
        var index = _mods.IndexOf(modVm);
        if (index >= _mods.Count - 1)
            return;
        _mods.Move(index, index + 1);
    }

    [RelayCommand]
    void Remove(ModViewModel modVm)
    {
        var deleteMessage = _settingsService.DeleteToRecycleBin
            ? _localizationService["DashboardPage.RecycleBinConfirm"]
            : _localizationService["DashboardPage.PermanentDeleteConfirm"];
        
        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = _localizationService["DashboardPage.DeleteConfirmTitle"],
            Message = $"{_localizationService["DashboardPage.DeleteConfirmPrefix"]}{modVm.Name}{_localizationService["DashboardPage.DeleteConfirmSuffix"]}{deleteMessage}",
            Confirm = () =>
            {
                _ = DeleteModAsync(modVm);
            }
        });
    }

    private async Task DeleteModAsync(ModViewModel modVm)
    {
        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
        {
            Title = _localizationService["DashboardPage.DeleteModProgress"],
            Message = _localizationService["SettingsPage.PleaseWait"]
        });

        var backgroundTask = _backgroundTaskService.Add(
            _localizationService["BackgroundTasksPage.TaskTypeDelete"],
            modVm.Name);

        try
        {
            await _modService.RemoveAsync(modVm.Data);

            // 删除后同步更新数据库：直接删除该模组对应的记录
            if (!_settingsService.IsReadonly)
            {
                await _profileService.DeleteEnabledDataAsync(_settingsService.StorageDirectory, modVm.Guid);
                await _modGroupService.RemoveModsFromAllGroupsAsync([modVm.Guid]);
                // 同时删除该模组的版本检测记录
                await _versionCheckRepository.DeleteByGuidAsync(_settingsService.StorageDirectory, modVm.Guid);
            }

            WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
            _backgroundTaskService.Complete(backgroundTask, _localizationService["BackgroundTasksPage.DeleteComplete"].Replace("{name}", modVm.Name));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unknown mod removal error");
            _backgroundTaskService.Fail(backgroundTask, ex.Message);
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = ex.Message
            });
        }
    }

    [RelayCommand]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is a command of a view model and should not be static.")]
    void Run()
    {
        Process.Start(s_gameStartInfo);
    }

    [RelayCommand]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is a command of a view model and should not be static.")]
    void Github()
    {
        Process.Start(s_githubStartInfo);
    }

    [RelayCommand]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is a command of a view model and should not be static.")]
    void GithubFork()
    {
        Process.Start(s_githubForkStartInfo);
    }

    [RelayCommand]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is a command of a view model and should not be static.")]
    void Discord()
    {
        Process.Start(s_discordStartInfo);
    }

    // ===== 版本兼容性检查命令（委托给 VersionCheckViewModel） =====

    /// <summary>
    /// 检查所有模组的版本兼容性
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task CheckVersionCompatibility()
    {
        await RunVersionCheckCompatibilityAsync(true);
    }

    private async Task RunVersionCheckCompatibilityAsync(bool forceFullScan)
    {
        await _versionCheckVm.CheckVersionCompatibilityAsync(_mods, forceFullScan);
    }

    private void VersionCheckVm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsCheckingVersion));
        OnPropertyChanged(nameof(VersionCheckSummary));
        OnPropertyChanged(nameof(CompatibleModCount));
        OnPropertyChanged(nameof(IncompatibleModCount));
        OnPropertyChanged(nameof(HasIncompatibleMods));
        OnPropertyChanged(nameof(HasVersionCheckResult));
    }

    [RelayCommand]
    void OpenFileLocation(ModViewModel modVm)
    {
        try
        {
            Process.Start(new ProcessStartInfo(modVm.Data.Directory.FullName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open file location for mod {ModName}", modVm.Name);
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = $"{_localizationService["DashboardPage.OpenFileLocationFailed"]}{ex.Message}"
            });
        }
    }

    [RelayCommand]
    void EditName(ModViewModel modVm)
    {
        try
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
            {
                Title = _localizationService["DashboardPage.EditNameTitle"],
                Message = _localizationService["DashboardPage.EditNameMsg"],
                MaxLength = 64,
                InitialText = modVm.Name,
                Confirm = (newName) =>
                {
                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["DashboardPage.EditNameEmptyError"] });
                        return;
                    }

                    modVm.Data.UpdateManifestName(newName);
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = _localizationService["DashboardPage.EditNameUpdated"] });
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit mod name for mod {ModName}", modVm.Name);
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = $"{_localizationService["DashboardPage.EditNameFailed"]}{ex.Message}"
            });
        }
    }

    [RelayCommand]
    void EditDescription(ModViewModel modVm)
    {
        try
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
            {
                Title = _localizationService["DashboardPage.EditDescTitle"],
                Message = _localizationService["DashboardPage.EditDescMsg"],
                MaxLength = 1024,
                InitialText = modVm.Description,
                Confirm = (newDescription) =>
                {
                    modVm.Data.UpdateManifestDescription(newDescription);
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = _localizationService["DashboardPage.EditDescUpdated"] });
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit mod description for mod {ModName}", modVm.Name);
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = $"{_localizationService["DashboardPage.EditDescFailed"]}{ex.Message}"
            });
        }
    }

    [RelayCommand]
    async Task EditImage(ModViewModel modVm)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            CheckPathExists = true,
            Filter = _localizationService["Common.SelectImageFilter"],
            Title = _localizationService["DashboardPage.EditImageDialog"]
        };

        if (dialog.ShowDialog() ?? false)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
            {
                Title = _localizationService["DashboardPage.EditImageProgress"],
                Message = _localizationService["SettingsPage.PleaseWait"]
            });

            try
            {
                string imageFileName = Path.GetFileName(dialog.FileName);
                string destinationPath = Path.Combine(modVm.Data.Directory.FullName, imageFileName);
                await CopyFileAsync(dialog.FileName, destinationPath, true);

                modVm.Data.UpdateManifestIconPath(imageFileName);

                modVm.LoadIcon();

                await SaveProfileNowAsync();

                WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
                WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage()
                {
                    Message = _localizationService["DashboardPage.EditImageSuccess"]
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to edit image for mod {ModName}", modVm.Name);
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
                {
                    Message = $"{_localizationService["DashboardPage.EditImageFailed"]}{ex.Message}"
                });
            }
        }
    }

    private async Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite)
    {
        using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true))
        using (var destinationStream = new FileStream(destinationPath, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
        {
            await sourceStream.CopyToAsync(destinationStream);
        }
    }

    [RelayCommand]
    void Edit(ModViewModel vm)
    {
        _editModStore.CurrentMod = vm;
        _navStore.Value.Navigate<EditPageViewModel>();
    }

    [RelayCommand]
    void EditManifest(ModViewModel vm)
    {
        _editModStore.CurrentMod = vm;
        _navStore.Value.Navigate<ManifestEditPageViewModel>();
    }

    /// <summary>
    /// Pack the mod as a zip/7z archive and export to a specified location for distribution.
    /// Supports 5 gears: ZIP standard / 7z Fast / 7z Normal / 7z High / 7z Ultra.
    /// Shows memory usage warning for high-compression options on large mods.
    /// </summary>
    [RelayCommand]
    void ExportMod(ModViewModel vm)
    {
        var modDir = vm.Data.Directory;

        // Step 1: Show format/compression selection dialog (5 gears)
        WeakReferenceMessenger.Default.Send(new MessageBoxSelectionMessage
        {
            Title = _localizationService["DashboardPage.ExportTitle"],
            Message = _localizationService["DashboardPage.ExportMsg"],
            Options = new List<object>
            {
                _localizationService["DashboardPage.ExportZip"],
                _localizationService["DashboardPage.Export7zFast"],
                _localizationService["DashboardPage.Export7zStandard"],
                _localizationService["DashboardPage.Export7zHigh"],
                _localizationService["DashboardPage.Export7zUltra"]
            },
            Confirm = (selectedOption) =>
            {
                var opt = selectedOption.ToString()!;
                var is7z = opt.StartsWith("7z", StringComparison.OrdinalIgnoreCase);

                // Parse compression level
                SharpSevenZip.CompressionLevel level;
                string dictSize;
                bool isHighMemory;
                string levelName;

                if (opt == _localizationService["DashboardPage.Export7zFast"])    { level = SharpSevenZip.CompressionLevel.Fast;   dictSize = "8m";  isHighMemory = false; levelName = "Fast"; }
                else if (opt == _localizationService["DashboardPage.Export7zHigh"]) { level = SharpSevenZip.CompressionLevel.High;   dictSize = "64m"; isHighMemory = true;  levelName = "High"; }
                else if (opt == _localizationService["DashboardPage.Export7zUltra"])   { level = SharpSevenZip.CompressionLevel.Ultra;  dictSize = "128m"; isHighMemory = true;  levelName = "Ultra"; }
                else                             { level = SharpSevenZip.CompressionLevel.Normal; dictSize = "32m"; isHighMemory = false; levelName = "Normal"; }

                // Step 2: Show save file dialog
                var dialog = new SaveFileDialog
                {
                    Title = _localizationService["DashboardPage.ExportSaveDialog"],
                    FileName = $"{vm.Name}.{(is7z ? "7z" : "zip")}",
                    Filter = is7z ? _localizationService["Common.FileFilter7z"] : _localizationService["Common.FileFilterZip"],
                };

                if (dialog.ShowDialog() != true)
                    return;

                // Step 3: Calculate total mod size
                var excludedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz"
                };

                long totalSize = 0;
                foreach (var f in modDir.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (!excludedExtensions.Contains(f.Extension))
                        totalSize += f.Length;
                }

                // Step 4: Warn if > 1GB and high-memory compression
                if (isHighMemory && totalSize > 1024L * 1024 * 1024)
                {
                    var sizeText = totalSize >= 1024L * 1024 * 1024 * 1024
                        ? $"{totalSize / (1024.0 * 1024 * 1024 * 1024):F2} TB"
                        : $"{totalSize / (1024.0 * 1024 * 1024):F2} GB";

                    var dictDesc = dictSize switch
                    {
                        "64m" => "64MB",
                        "128m" => "128MB",
                        _ => dictSize
                    };

                    WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
                    {
                        Title = _localizationService["DashboardPage.ExportMemoryWarning"],
                        Message = $"{_localizationService["DashboardPage.ExportMemoryMsgPrefix"]}{sizeText}{_localizationService["DashboardPage.ExportMemoryMsgMid"]}{levelName}{_localizationService["DashboardPage.ExportMemoryMsgCompression"]}{dictDesc}{_localizationService["DashboardPage.ExportMemoryMsgSuffix"]}",
                        Confirm = () => DoExport(vm, modDir, dialog.FileName, is7z, level, dictSize, levelName, excludedExtensions),
                        Abort = () => { }
                    });
                }
                else
                {
                    DoExport(vm, modDir, dialog.FileName, is7z, level, dictSize, levelName, excludedExtensions);
                }
            }
        });
    }

    /// <summary>
    /// Execute the actual export with the chosen format and settings.
    /// Shows a real-time progress dialog with compression speed and ratio.
    /// </summary>
    private void DoExport(ModViewModel vm, DirectoryInfo modDir, string outputPath, bool is7z,
        SharpSevenZip.CompressionLevel level, string dictSize, string levelName, HashSet<string> excludedExtensions)
    {
        // Show progress dialog on UI thread
        WeakReferenceMessenger.Default.Send(new MessageBoxExportProgressMessage
        {
            Title = $"{_localizationService["DashboardPage.ExportSaveDialog"]} - {vm.Name}"
        });

        var backgroundTask = _backgroundTaskService.Add(
            _localizationService["BackgroundTasksPage.TaskTypeExport"],
            vm.Name);
        _backgroundTaskService.Update(backgroundTask, progress: 0, isIndeterminate: false);

        // Run export on background thread to keep UI responsive
        Task.Run(() => DoExportAsync(vm, modDir, outputPath, is7z, level, dictSize, levelName, excludedExtensions, backgroundTask));
    }

    /// <summary>
    /// Background export with real-time progress reporting.
    /// </summary>
    private void DoExportAsync(ModViewModel vm, DirectoryInfo modDir, string outputPath, bool is7z,
        SharpSevenZip.CompressionLevel level, string dictSize, string levelName, HashSet<string> excludedExtensions, BackgroundTaskItem backgroundTask)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastUpdateBytes = 0;
        double lastUpdateSec = 0;
        double lastUiUpdate = 0;  // 用于节流 UI 更新

        // Calculate total input size for progress tracking
        long totalInputSize = 0;
        foreach (var f in modDir.EnumerateFiles("*", SearchOption.AllDirectories))
            if (!excludedExtensions.Contains(f.Extension))
                totalInputSize += f.Length;

        // Helper to send progress updates to UI thread (throttled)
        void ReportProgress(double progress, string? currentFile, long bytesProcessed)
        {
            // 节流：最多每 120ms 更新一次 UI，避免高频 Dispatcher.Invoke 卡死 UI 线程
            var now = sw.Elapsed.TotalSeconds;
            if (now - lastUiUpdate < 0.12 && progress < 1.0)
                return;
            lastUiUpdate = now;

            var elapsed = now;
            var speed = elapsed > 0 ? bytesProcessed / elapsed : 0;

            // Smooth speed calculation over 1-second intervals
            var deltaBytes = bytesProcessed - lastUpdateBytes;
            var deltaSec = elapsed - lastUpdateSec;
            if (deltaSec >= 1.0 || progress >= 1.0)
            {
                lastUpdateBytes = bytesProcessed;
                lastUpdateSec = elapsed;
            }

            var speedText = speed >= 1024 * 1024
                ? $"{_localizationService["DashboardPage.ExportSpeed"]}{speed / (1024.0 * 1024):F1}{_localizationService["DashboardPage.ExportMBS"]}"
                : speed >= 1024
                    ? $"{_localizationService["DashboardPage.ExportSpeed"]}{speed / 1024.0:F0}{_localizationService["DashboardPage.ExportKBS"]}"
                    : $"{_localizationService["DashboardPage.ExportSpeed"]}{speed:F0}{_localizationService["DashboardPage.ExportBS"]}";

            // Read output file size for ratio (if file exists)
            string ratioText = "";
            try
            {
                var outFile = new FileInfo(outputPath);
                if (outFile.Exists && outFile.Length > 0 && totalInputSize > 0)
                {
                    // 压缩率 = (1 - 输出大小/输入大小) * 100，表示压缩了多少
                    var saved = (1.0 - (double)outFile.Length / totalInputSize) * 100;
                    ratioText = $"{_localizationService["DashboardPage.ExportRatio"]}{saved:F1}{_localizationService["DashboardPage.ExportPercent"]}";
                }
            }
            catch { }

            Application.Current.Dispatcher.Invoke(() =>
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxExportProgressUpdateMessage
                {
                    Progress = progress,
                    CurrentFile = currentFile,
                    SpeedText = speedText,
                    RatioText = ratioText,
                });
            });
            _backgroundTaskService.Update(backgroundTask, currentFile, progress, false);
        }

        try
        {
            if (is7z)
            {
                // --- 7z export with SharpSevenZipCompressor ---
                var compressor = new SharpSevenZipCompressor
                {
                    ArchiveFormat = OutArchiveFormat.SevenZip,
                    CompressionMethod = CompressionMethod.Lzma2,
                    CompressionLevel = level,
                    DirectoryStructure = true,
                    PreserveDirectoryRoot = false,
                };

                // 根据选择的挡位设置字典大小，控制内存占用
                //   Fast  → 8MB 字典，内存占用低
                //   Normal → 32MB 字典，平衡
                //   High  → 64MB 字典，较高压缩率
                //   Ultra → 128MB 字典，最高压缩率但内存占用高
                compressor.CustomParameters.Add("d", dictSize);

                var files = modDir.EnumerateFiles("*", SearchOption.AllDirectories)
                    .Where(f => !excludedExtensions.Contains(f.Extension))
                    .Select(f => f.FullName)
                    .ToArray();

                var commonRootLength = modDir.FullName.Length;
                if (!modDir.FullName.EndsWith(Path.DirectorySeparatorChar))
                    commonRootLength++;

                // Track current file from event
                string currentFile = "";
                compressor.FileCompressionStarted += (_, args) =>
                {
                    currentFile = Path.GetFileName(args.FileName);
                };
                compressor.Compressing += (_, args) =>
                {
                    // args.PercentDone is int 0-100 from 7z native
                    var pct = Math.Max(0.0, Math.Min(100, (int)args.PercentDone)) / 100.0;
                    var estimatedBytes = (long)(totalInputSize * pct);
                    ReportProgress(pct, currentFile, estimatedBytes);
                };

                // 直接写文件路径而非 Stream，避免内存缓冲整个归档数据
                compressor.CompressFiles(outputPath, commonRootLength, files);
                ReportProgress(1.0, "", totalInputSize);

                _logger.LogInformation("Exported mod \"{Name}\" to {Path} (7z LZMA2 {Level}, dict {Dict})",
                    vm.Name, outputPath, levelName, dictSize);
            }
            else
            {
                // --- ZIP export with manual byte tracking ---
                long totalWritten = 0;
                string currentFile = "";

                using var fileStream = new FileStream(outputPath, FileMode.Create);
                using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

                foreach (var file in modDir.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (excludedExtensions.Contains(file.Extension))
                        continue;

                    currentFile = file.Name;
                    var relativePath = Path.GetRelativePath(modDir.FullName, file.FullName);
                    var entry = archive.CreateEntryFromFile(file.FullName, relativePath, System.IO.Compression.CompressionLevel.Optimal);
                    
                    // Approximate progress by file count / total input size
                    totalWritten += file.Length;
                    var progress = totalInputSize > 0 ? Math.Min((double)totalWritten / totalInputSize, 1.0) : 0;
                    ReportProgress(progress, currentFile, totalWritten);
                }

                ReportProgress(1.0, "", totalInputSize);

                _logger.LogInformation("Exported mod \"{Name}\" to {Path} (ZIP standard)", vm.Name, outputPath);
            }

            // Signal completion - keep final stats visible with OK button
            Application.Current.Dispatcher.Invoke(() =>
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxExportProgressUpdateMessage { IsCompleted = true });
            });
            _backgroundTaskService.Complete(backgroundTask, _localizationService["BackgroundTasksPage.ExportComplete"].Replace("{name}", vm.Name));
            // Don't auto-close - user clicks OK to dismiss and see final ratio/speed
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export mod");
            _backgroundTaskService.Fail(backgroundTask, ex.Message);
            Application.Current.Dispatcher.Invoke(() =>
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
                WeakReferenceMessenger.Default.Send(new MessageBoxWarningMessage
                {
                    Message = $"{_localizationService["DashboardPage.ExportError"]}{ex.Message}"
                });
            });
        }
    }

    bool CanClearSearch()
    {
        return !IsSearchEmpty;
    }

    [RelayCommand(CanExecute = nameof(CanClearSearch))]
    void ClearSearch()
    {
        SearchText = string.Empty;
    }

    [RelayCommand]
    void ApplyAll()
    {
    }

    [RelayCommand]
    void ShowImagePreview(ImageSource imageSource)
    {
        WeakReferenceMessenger.Default.Send(new ImagePreviewShowMessage { ImageSource = imageSource });
    }

    [RelayCommand]
    void HideImagePreview()
    {
        WeakReferenceMessenger.Default.Send(new ImagePreviewHideMessage());
    }

    [RelayCommand]
    void DownloadFromNexus()
    {
        WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = _localizationService["DashboardPage.NexusDownloadInfo"] });
        
        _navStore.Value.Navigate<NexusDownloadPageViewModel>();
    }

    [RelayCommand]
    void ShowDownloadProgress()
    {
        _navStore.Value.Navigate<DownloadProgressViewModel>();
    }

    [RelayCommand]
    void ShowBackgroundTasks()
    {
        _navStore.Value.Navigate<BackgroundTasksPageViewModel>();
    }

    [RelayCommand]
    void EditModTags(ModViewModel modVm)
    {
        if (modVm == null || !_settingsService.Initialized)
            return;

        var selectedTagIds = modVm.Data.TagIds.ToList();
        var selectableTags = _settingsService.Tags.Select(t => new TagSelectionItem(t, selectedTagIds.Contains(t.Id))).ToList();

        WeakReferenceMessenger.Default.Send(new MessageBoxTagSelectionMessage
            {
                Title = _localizationService["DashboardPage.EditTagsTitle"],
                Message = _localizationService["DashboardPage.EditTagsMsg"],
                Tags = selectableTags,
                Confirm = (selectedTags) =>
                {
                    if (!_settingsService.IsReadonly)
                    {
                        modVm.Data.TagIds = selectedTags.Select(t => t.Tag.Id).ToList();
                        RequestProfileSave();
                        WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = _localizationService["DashboardPage.EditTagsUpdated"] });
                    }
                    else
                    {
                        WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["DashboardPage.BatchTagReadonly"] });
                    }
                }
            });
    }

    public IReadOnlyList<ModTag> AllTags => _settingsService.Initialized ? _settingsService.Tags : [];
    public IEnumerable<object> TagItems => _settingsService.Initialized ? _settingsService.Tags : [];

    // ===== 分隔符命令 =====

    /// <summary>
    /// 是否可以创建分隔符（分隔符功能必须在设置中启用）
    /// </summary>
    bool CanCreateSeparator() => ShowSeparator;

    /// <summary>
    /// 创建新的分隔符
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreateSeparator))]
    void CreateSeparator()
    {
        if (!_settingsService.Initialized || _settingsService.IsReadonly)
            return;

        var separator = new ModSeparator
        {
            Name = _localizationService["DashboardPage.DefaultSeparatorName"],
            Color = "#FF6200EE",
            IsExpanded = true,
            DisplayIndex = _orderedItems.Count
        };
        _settingsService.Separators.Add(separator);
        RebuildOrderedItems();
        _ = _settingsService.SaveAsync();
    }

    /// <summary>
    /// 重命名分隔符
    /// </summary>
    [RelayCommand]
    void RenameSeparator(ModSeparator separator)
    {
        if (separator == null || _settingsService.IsReadonly)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
        {
            Title = _localizationService["DashboardPage.RenameSeparatorTitle"],
            Message = _localizationService["DashboardPage.RenameSeparatorMsg"],
            MaxLength = 32,
            InitialText = separator.Name,
            Confirm = (newName) =>
            {
                if (string.IsNullOrWhiteSpace(newName))
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["DashboardPage.RenameSeparatorEmptyError"] });
                    return;
                }
                separator.Name = newName;
                OnPropertyChanged(nameof(Mods));
                _ = _settingsService.SaveAsync();
            }
        });
    }

    /// <summary>
    /// 更改分隔符颜色
    /// </summary>
    [RelayCommand]
    void ChangeSeparatorColor(ModSeparator separator)
    {
        if (separator == null || _settingsService.IsReadonly)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxColorPickerMessage
        {
            Title = _localizationService["DashboardPage.ChangeSeparatorColorTitle"],
            Message = $"{_localizationService["DashboardPage.ChangeSeparatorColorPrefix"]}{separator.Name}{_localizationService["DashboardPage.ChangeSeparatorColorSuffix"]}",
            CurrentColor = separator.Color,
            Confirm = (selectedColor) =>
            {
                separator.Color = selectedColor;
                OnPropertyChanged(nameof(Mods));
                _ = _settingsService.SaveAsync();
            }
        });
    }

    /// <summary>
    /// 删除分隔符
    /// </summary>
    [RelayCommand]
    void DeleteSeparator(ModSeparator separator)
    {
        if (separator == null || _settingsService.IsReadonly)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = _localizationService["DashboardPage.DeleteSeparatorHint"],
            Message = $"{_localizationService["DashboardPage.DeleteSeparatorPrefix"]}{separator.Name}{_localizationService["DashboardPage.DeleteSeparatorSuffix"]}",
            Confirm = () =>
            {
                _settingsService.Separators.Remove(separator);
                RebuildOrderedItems();
                _ = _settingsService.SaveAsync();
            }
        });
    }

    protected override void OnDispose()
    {
        _modService.ModAdded -= ModService_ModAdded;
        _modService.ModAdded -= OnModAdded;
        _modService.ModRemoved -= ModService_ModRemoved;
        _versionCheckVm.PropertyChanged -= VersionCheckVm_PropertyChanged;

        if (Initialized && !_settingsService.IsReadonly)
            _profileSaveCoordinator.RequestSave(CaptureProfileSnapshot());

        if (_mods is not null)
        {
            _mods.CollectionChanged -= Mods_CollectionChanged;
            foreach (var vm in _mods)
            {
                vm.OptionsChanged -= ModViewModel_OptionsChanged;
                vm.PropertyChanged -= ModViewModel_PropertyChanged;
                vm.VersionCheckRefreshed -= ModViewModel_VersionCheckRefreshed;
            }
            _orderedItems.Clear();
            _mods.Clear();
        }
    }
}
