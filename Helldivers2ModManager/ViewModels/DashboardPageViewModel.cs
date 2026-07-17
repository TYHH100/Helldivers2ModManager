using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GongSolutions.Wpf.DragDrop;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Extensions;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Helldivers2ModManager.Core.UI;
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
    private readonly INavigationService _navigationService;
    private readonly EditModStore _editModStore;
    private readonly ModService _modService;
    private readonly SettingsService _settingsService;
    private readonly ProfileService _profileService;
    private readonly ProfileSaveCoordinator _profileSaveCoordinator;
    private readonly VersionCheckRepository _versionCheckRepository;
    private readonly ModHashService _modHashService;
    private ObservableCollection<ModViewModel> _mods;
    private ObservableCollection<object> _orderedItems;
    private readonly SearchFilterService _searchFilterService;
    private readonly SortService _sortService;
    private readonly VersionCheckViewModel _versionCheckVm;
    private readonly BatchRepairCoordinator _batchRepairCoordinator;
    private readonly RepairDisclaimerService _repairDisclaimerService;
    private readonly LocalizationService _localizationService;
    private readonly BackgroundTaskService _backgroundTaskService;
    private readonly IBackgroundTaskRunner _backgroundTaskRunner;
    private readonly IDialogService _dialogService;
    private readonly ModGroupService _modGroupService;
    private readonly ModViewModelFactory _modViewModelFactory;
    private readonly Dictionary<Guid, ModViewModel> _modViewModelsById = [];

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
    public string SelectionCountText => _mods is null
        ? ""
        : _localizationService.Format("DashboardPage.SelectionCount", new { count = _modGroupService.FilterModViewModels(_mods).Count(static vm => vm.IsSelected) });

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
        INavigationService navigationService,
        SettingsService settingsService,
        ModService modService,
        ProfileService profileService,
        ProfileSaveCoordinator profileSaveCoordinator,
        EditModStore editModStore,
        VersionCheckRepository versionCheckRepository,
        ModHashService modHashService,
        SearchFilterService searchFilterService,
        SortService sortService,
        VersionCheckViewModel versionCheckVm,
        BatchRepairCoordinator batchRepairCoordinator,
        RepairDisclaimerService repairDisclaimerService,
        LocalizationService localizationService,
        BackgroundTaskService backgroundTaskService,
        IBackgroundTaskRunner backgroundTaskRunner,
        IDialogService dialogService,
        ModGroupService modGroupService,
        ModViewModelFactory modViewModelFactory,
        ModGroupSidebarViewModel groupSidebar)
    {
        _logger = logger;
        _navigationService = navigationService;
        _editModStore = editModStore;
        _settingsService = settingsService;
        _modService = modService;
        _profileService = profileService;
        _profileSaveCoordinator = profileSaveCoordinator;
        _versionCheckRepository = versionCheckRepository;
        _modHashService = modHashService;
        _searchFilterService = searchFilterService;
        _sortService = sortService;
        _versionCheckVm = versionCheckVm;
        _batchRepairCoordinator = batchRepairCoordinator;
        _repairDisclaimerService = repairDisclaimerService;
        _versionCheckVm.PropertyChanged += VersionCheckVm_PropertyChanged;
        _localizationService = localizationService;
        _backgroundTaskService = backgroundTaskService;
        _backgroundTaskRunner = backgroundTaskRunner;
        _dialogService = dialogService;
        _modGroupService = modGroupService;
        _modViewModelFactory = modViewModelFactory;
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
        IProgressDialogSession? progressDialog = null;
        if (showProgress)
        {
            progressDialog = await _dialogService.OpenProgressAsync(
                new ProgressDialogRequest(
                    _localizationService["DashboardPage.SavingModConfig"],
                    _localizationService["SettingsPage.PleaseWait"]),
                CancellationToken.None);
        }

        try
        {
            await _profileSaveCoordinator.SaveNowAsync(snapshot);
        }
        finally
        {
            if (progressDialog is not null)
                await progressDialog.CloseAsync(CancellationToken.None);
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
        await using var progressDialog = await _dialogService.OpenProgressAsync(
            new ProgressDialogRequest(
                _localizationService["SettingsPage.LoadingSettings"],
                _localizationService["SettingsPage.PleaseWait"]),
            CancellationToken.None);
        try
        {
            if (!await _settingsService.InitAsync(false))
                _settingsService.InitDefault(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading settings failed");
            if (await _dialogService.ShowAsync(
                new DialogRequest(
                    _localizationService["SettingsPage.LoadSettingsFailed"],
                    _localizationService["DashboardPage.GoToSettings"]),
                CancellationToken.None))
                _navigationService.Navigate(typeof(SettingsPageViewModel));
            return;
        }
        _logger.LogInformation("Settings loaded successfully");
        progressDialog.Report(new ProgressDialogRequest(
            _localizationService["DashboardPage.LoadingMods"],
            _localizationService["SettingsPage.PleaseWait"]));

        // 将用户设置的日志级别同步到 App.Current，FileLogger 依赖此值进行过滤
        App.Current.LogLevel = _settingsService.LogLevel;

        _logger.LogInformation("Validating settings");
        if (!_settingsService.Validate())
        {
            _logger.LogError("Settings invalid");
            if (await _dialogService.ShowAsync(
                new DialogRequest(
                    _localizationService["DashboardPage.SettingsInvalid"],
                    _localizationService["DashboardPage.GoToSettings"]),
                CancellationToken.None))
                _navigationService.Navigate(typeof(SettingsPageViewModel));
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
            await ShowDashboardMessageAsync(
                _localizationService.Format("DashboardPage.LoadConfigFailed", new { message = ex.Message }),
                MessageDialogSeverity.Error,
                CancellationToken.None);
            return;
        }

        _logger.LogInformation("Loading mods...");
        progressDialog.Report(new ProgressDialogRequest(
            _localizationService["DashboardPage.LoadingMods"],
            _localizationService["SettingsPage.PleaseWait"]));
        ModProblem[] problems;
        try
        {
            problems = await _modService.InitAsync(_settingsService, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading mods failed");
            await ShowDashboardMessageAsync(
                _localizationService.Format("DashboardPage.LoadModsFailed", new { message = ex.Message }),
                MessageDialogSeverity.Error,
                CancellationToken.None);
            return;
        }
        _modService.ModAdded += ModService_ModAdded;
        _modService.ModAdded += OnModAdded;
        _modService.ModRemoved += ModService_ModRemoved;
        if (problems.Length != 0)
            _logger.LogWarning("Loaded mods with {} problems", problems.Length);
        else
            _logger.LogInformation("Mods loaded successfully");
        _logger.LogInformation("Loading profile...");
        progressDialog.Report(new ProgressDialogRequest(
            _localizationService["DashboardPage.LoadingConfig"],
            _localizationService["SettingsPage.PleaseWait"]));
        IReadOnlyList<ModData>? result;
        try
        {
            result = await _profileService.LoadAsync(_settingsService, _modService);
            result ??= _profileService.InitDefault(_modService);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading profile failed");
            await ShowDashboardMessageAsync(
                _localizationService.Format("DashboardPage.LoadConfigFailed", new { message = ex.Message }),
                MessageDialogSeverity.Error,
                CancellationToken.None);
            return;
        }
        await progressDialog.CloseAsync(CancellationToken.None);
        _logger.LogInformation("Profile loaded successfully");

        _logger.LogInformation("Applying profile");
        var modViewModels = result.Select(GetOrCreateModViewModel).ToList();
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
            await ShowDashboardMessageAsync(
                BuildProblems(problems, _localizationService["DashboardPage.LoadProblemsPrefix"], true),
                MessageDialogSeverity.Warning,
                CancellationToken.None);

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
            RunVersionCheckCompatibilityAsync(false).Observe(
                ex => _logger.LogError(ex, "Automatic version compatibility check failed"));
        }

#if DEBUG && FALSE
	BuildProblems(Enum.GetValues<ModProblemKind>().Select(static k => new ModProblem { Directory = new DirectoryInfo(@"C:\ModStorage\Test"), Kind = k }), "Problem test:");
#endif
    }

    private string BuildProblems(IEnumerable<ModProblem> problems, string prefix, bool isInit = false)
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
                        ? _localizationService.Format("DashboardPage.InvalidPathMessage", new { path = e.ExtraData })
                        : _localizationService["DashboardPage.InvalidPathError"],
                    ModProblemKind.CantReadArchive => e.ExtraData is not null
                        ? _localizationService.Format("DashboardPage.CantReadArchiveMessage", new { message = e.ExtraData })
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
                        ? _localizationService.Format("DashboardPage.InvalidImagePathMessage", new { path = w.ExtraData })
                        : _localizationService["DashboardPage.InvalidImagePathError"],
                    ModProblemKind.EmptyImagePath => _localizationService["DashboardPage.EmptyImagePath"],
                    _ => throw new NotImplementedException()
                };
                sb.AppendLine(desc);
            }
        }

        return sb.ToString();
    }

    private void ModService_ModAdded(ModData mod)
    {
        var vm = GetOrCreateModViewModel(mod);
        vm.OptionsChanged += ModViewModel_OptionsChanged;
        vm.PropertyChanged += ModViewModel_PropertyChanged;
        vm.VersionCheckRefreshed += ModViewModel_VersionCheckRefreshed;
        _mods.Add(vm);
        SearchText = string.Empty;
        _modGroupService.CaptureGroupState(ModGroup.DefaultGroupId, _mods.Select(static vm => vm.Data));
        GroupSidebar.RefreshSelectionProperties();
        UpdateView();
    }

    private void OnModAdded(ModData mod)
    {
        _backgroundTaskRunner.RunAsync(
            _localizationService["BackgroundTasksPage.TaskTypeVersionCheck"],
            async (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _versionCheckVm.CheckSingleModOnAddAsync(mod, _mods);
                OnPropertyChanged(nameof(VersionCheckSummary));
                OnPropertyChanged(nameof(HasVersionCheckResult));
                return Core.Operations.OperationResult.Success();
            },
            CancellationToken.None).Observe(
                ex => _logger.LogError(ex, "Failed to supervise automatic mod version check"));
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
            _modViewModelsById.Remove(vm.Guid);
            vm.Dispose();
            _modGroupService.CaptureGroupState(_modGroupService.SelectedGroup.Id, _modGroupService.FilterMods(_mods.Select(static vm => vm.Data)));
            GroupSidebar.RefreshSelectionProperties();
            UpdateView();
        }
    }

    private void ModViewModel_VersionCheckRefreshed(object? sender, EventArgs e)
    {
        _versionCheckVm.RefreshAfterSingleModCheckAsync(_mods).Observe(
            ex => _logger.LogError(ex, "Failed to refresh version check summary"));
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
        _settingsService.SaveAsync().Observe(
            ex => _logger.LogError(ex, "Failed to persist separator order"));
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
    async Task BatchDelete(CancellationToken cancellationToken)
    {
        var selected = _modGroupService.FilterModViewModels(_mods).Where(static vm => vm.IsSelected).ToArray();
        if (selected.Length == 0)
            return;

        var confirmKey = _settingsService.DeleteToRecycleBin
            ? "DashboardPage.BatchDeleteRecycleConfirm"
            : "DashboardPage.BatchDeletePermanentConfirm";

        if (!await _dialogService.ShowAsync(
            new DialogRequest(
                _localizationService["DashboardPage.BatchDeleteTitle"],
                _localizationService.Format(confirmKey, new { count = selected.Length })),
            cancellationToken))
            return;

        await using var progressDialog = await _dialogService.OpenProgressAsync(
            new ProgressDialogRequest(
                _localizationService["DashboardPage.BatchDeleteProgress"],
                _localizationService["SettingsPage.PleaseWait"]),
            cancellationToken);

        try
        {
            foreach (var vm in selected)
            {
                vm.IsSelected = false;
                await _modService.RemoveAsync(vm.Data);
            }

            if (!_settingsService.IsReadonly)
            {
                var guids = selected.Select(static vm => vm.Guid).ToList();
                await _profileService.DeleteEnabledDataAsync(_settingsService.StorageDirectory, guids);
                await _modGroupService.RemoveModsFromAllGroupsAsync(guids);
                foreach (var guid in guids)
                    await _versionCheckRepository.DeleteByGuidAsync(_settingsService.StorageDirectory, guid);
            }

            await progressDialog.CloseAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, _localizationService["DashboardPage.BatchDeleteFailed2"]);
            await progressDialog.CloseAsync(CancellationToken.None);
            await ShowDashboardMessageAsync(
                _localizationService.Format("DashboardPage.BatchDeleteFailedMessage", new { message = ex.Message }),
                MessageDialogSeverity.Error,
                CancellationToken.None);
        }

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionCountText));
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
    async Task BatchAddTags(CancellationToken cancellationToken)
    {
        var selected = _modGroupService.FilterModViewModels(_mods).Where(static vm => vm.IsSelected).ToArray();
        if (selected.Length == 0 || !_settingsService.Initialized)
            return;

        // 使用第一个选中模组的标签作为初始选择状态（方便用户基于现有标签增减）
        var initialTagIds = selected[0].Data.TagIds.ToList();
        var options = _settingsService.Tags.Select(tag => new ChecklistDialogOption(
            tag.Id.ToString(),
            tag.Name,
            tag.Color,
            initialTagIds.Contains(tag.Id))).ToArray();
        var selectedTagIds = await _dialogService.SelectManyAsync(
            new ChecklistDialogRequest(
                _localizationService["DashboardPage.BatchTagTitle"],
                _localizationService.Format("DashboardPage.BatchTagMessage", new { count = selected.Length }),
                options),
            cancellationToken);
        if (selectedTagIds is null)
            return;
        if (_settingsService.IsReadonly)
        {
            await ShowDashboardMessageAsync(
                _localizationService["DashboardPage.BatchTagReadonly"],
                MessageDialogSeverity.Error,
                cancellationToken);
            return;
        }

        var newTagIds = selectedTagIds
            .Select(static id => Guid.TryParse(id, out var value) ? (Guid?)value : null)
            .Where(static id => id.HasValue)
            .Select(static id => id!.Value)
            .ToList();
        foreach (var vm in selected)
            vm.Data.TagIds = newTagIds;
        RequestProfileSave();
        await ShowDashboardMessageAsync(
            _localizationService.Format("DashboardPage.BatchTagUpdated", new { count = selected.Length }),
            MessageDialogSeverity.Information,
            cancellationToken);
    }

    [RelayCommand]
    async Task AddModsToGroup(ModViewModel? source, CancellationToken cancellationToken)
    {
        if (!_settingsService.Initialized)
            return;

        var selected = source is not null && !source.IsSelected
            ? [source]
            : _modGroupService.FilterModViewModels(_mods).Where(static vm => vm.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            await ShowDashboardMessageAsync(
                _localizationService["ModGroup.NoSelectedMods"],
                MessageDialogSeverity.Error,
                cancellationToken);
            return;
        }

        var groups = _modGroupService.Groups.Where(static group => !group.IsDefault).ToArray();
        if (groups.Length == 0)
        {
            await ShowDashboardMessageAsync(
                _localizationService["ModGroup.NoCustomGroups"],
                MessageDialogSeverity.Error,
                cancellationToken);
            return;
        }

        var groupNames = groups.Select(static group => group.Name).ToArray();
        var selectedName = await _dialogService.SelectAsync(
            new SelectionDialogRequest(
                _localizationService["ModGroup.AddToGroupTitle"],
                _localizationService.Format("ModGroup.AddToGroupMessage", new { count = selected.Length }),
                groupNames),
            cancellationToken);
        var group = groups.FirstOrDefault(candidate => string.Equals(candidate.Name, selectedName, StringComparison.Ordinal));
        if (group is not null)
            await AddModsToGroupAsync(group, selected, cancellationToken);
    }

    private async Task AddModsToGroupAsync(ModGroup group, ModViewModel[] selected, CancellationToken cancellationToken)
    {
        try
        {
            await _modGroupService.AddModsToGroupAsync(group.Id, selected.Select(static vm => vm.Data));
            GroupSidebar.RefreshSelectionProperties();
            await ShowDashboardMessageAsync(
                _localizationService.Format("ModGroup.AddedToGroup", new { count = selected.Length, name = group.Name }),
                MessageDialogSeverity.Information,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加入分组失败");
            await ShowDashboardMessageAsync(ex.Message, MessageDialogSeverity.Error, cancellationToken);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task Add(string? filePath, CancellationToken cancellationToken)
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
        await using var progressDialog = await _dialogService.OpenProgressAsync(
            new ProgressDialogRequest(
                isBatch ? _localizationService.Format("DashboardPage.BatchAddProgressTitle", new { current = 0, total = totalFiles }) : _localizationService["DashboardPage.AddSingleProgress"],
                isBatch ? _localizationService.Format("DashboardPage.BatchAddWaitMsg", new { total = totalFiles }) : _localizationService["SettingsPage.PleaseWait"]),
            cancellationToken);

        var backgroundTask = _backgroundTaskService.Add(
            _localizationService["BackgroundTasksPage.TaskTypeImport"],
            isBatch ? _localizationService.Format("DashboardPage.BatchAddWaitMsg", new { total = totalFiles }) : Path.GetFileName(selectedFiles[0]));
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
                        ? _localizationService.Format("DashboardPage.BatchAddProcessing", new { file = Path.GetFileName(selectedFiles[i]), remaining = remainingCount })
                        : _localizationService.Format("DashboardPage.BatchAddProcessing", new { file = Path.GetFileName(selectedFiles[i]), remaining = "?" });
                    progressDialog.Report(new ProgressDialogRequest(
                        _localizationService.Format("DashboardPage.BatchAddProgressTitle", new { current = i + 1, total = totalFiles }),
                        description));
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
                    var nestedDescription = _localizationService.Format("DashboardPage.BatchAddProcessing", new { file = nestedFileName, remaining = nestedTotal - nestedIndex - 1 });
                    // 根据是否为批量导入模式，组合显示外层批量进度和内层嵌套进度
                    if (isBatch)
                    {
                        // 批量导入 + 嵌套处理：显示双层进度
                        progressDialog.Report(new ProgressDialogRequest(
                            _localizationService.Format("DashboardPage.BatchAddNestedTitle", new { current = currentBatchIndex + 1, total = totalFiles, nested = nestedIndex + 1, nestedTotal }),
                            nestedDescription));
                    }
                    else
                    {
                        // 单文件 + 嵌套处理：显示嵌套进度
                        progressDialog.Report(new ProgressDialogRequest(
                            _localizationService.Format("DashboardPage.BatchAddNestedProgress", new { current = nestedIndex + 1, total = nestedTotal }),
                            nestedDescription));
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

            _backgroundTaskService.Complete(backgroundTask, _localizationService.Format("BackgroundTasksPage.ImportComplete", new { success = successCount, fail = failCount }));

            // 汇总结果
            if (isBatch)
            {
                if (failCount == 0 && allProblems.Count == 0)
                {
                    await progressDialog.CloseAsync(cancellationToken);
                    await ShowDashboardMessageAsync(
                        _localizationService.Format("DashboardPage.BatchAddSuccess", new { count = successCount }),
                        MessageDialogSeverity.Information,
                        cancellationToken);
                }
                else if (allProblems.Count > 0)
                {
                    await progressDialog.CloseAsync(cancellationToken);
                    var error = allProblems.Any(static p => p.IsError);
                    var prefix = error
                        ? _localizationService.Format("DashboardPage.BatchAddDoneErrors", new { success = successCount, fail = failCount })
                        : _localizationService.Format("DashboardPage.BatchAddDoneWarnings", new { count = successCount });
                    await ShowDashboardMessageAsync(
                        BuildProblems([.. allProblems], prefix),
                        error ? MessageDialogSeverity.Error : MessageDialogSeverity.Warning,
                        CancellationToken.None);
                }
            }
            else
            {
                // 单文件模式保持原有行为
                if (allProblems.Count > 0)
                {
                    await progressDialog.CloseAsync(cancellationToken);
                    var error = allProblems.Any(static p => p.IsError);
                    var prefix = error
                        ? _localizationService["DashboardPage.AddSingleError"]
                        : _localizationService["DashboardPage.AddSingleWarning"];
                    await ShowDashboardMessageAsync(
                        BuildProblems([.. allProblems], prefix),
                        error ? MessageDialogSeverity.Error : MessageDialogSeverity.Warning,
                        CancellationToken.None);
                }
                else
                    await progressDialog.CloseAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add mod");
            _backgroundTaskService.Fail(backgroundTask, ex.Message);
            await progressDialog.CloseAsync(CancellationToken.None);
            await ShowDashboardMessageAsync(ex.Message, MessageDialogSeverity.Error, CancellationToken.None);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task UpdateMod(ModViewModel vm, CancellationToken cancellationToken)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            CheckPathExists = true,
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Download"),
            Filter = _localizationService["Common.FileFilterArchive"],
            Multiselect = false,
            Title = _localizationService.Format("DashboardPage.UpdateModDialogTitle", new { modName = vm.Name })
        };

        if (!(dialog.ShowDialog() ?? false))
            return;

        await using var progressDialog = await _dialogService.OpenProgressAsync(
            new ProgressDialogRequest(
                _localizationService["DashboardPage.UpdateModProgress"],
                vm.Name),
            cancellationToken);

        var backgroundTask = _backgroundTaskService.Add(
            _localizationService["BackgroundTasksPage.TaskTypeUpdate"],
            vm.Name);

        try
        {
            string? completionMessage = null;
            // 创建进度报告回调，将服务层进度映射为UI消息
            var progress = new Progress<UpdateProgressInfo>(info =>
            {
                if (info.IsCompleted)
                {
                    completionMessage = info.Message ?? _localizationService["DashboardPage.UpdateModDone"];
                    _backgroundTaskService.Complete(backgroundTask, info.Message ?? _localizationService["DashboardPage.UpdateModDone"]);
                }
                else
                {
                    var taskProgress = info.TotalCount > 0
                        ? (double)info.ProcessedCount / info.TotalCount
                        : 0;
                    progressDialog.Report(new ProgressDialogRequest(
                        _localizationService["DashboardPage.UpdateModProgress"],
                        info.Message ?? vm.Name,
                        info.CurrentFile));
                    _backgroundTaskService.Update(backgroundTask, info.Message, taskProgress, info.TotalCount <= 0);
                }
            });

            await _modService.UpdateModFromArchiveAsync(vm.Data, new FileInfo(dialog.FileName), progress);

            // 更新后保存状态到数据库，确保 EnabledOptions/SelectedOptions 与新清单同步
            await SaveProfileNowAsync(false);
            await progressDialog.CloseAsync(cancellationToken);
            await ShowDashboardMessageAsync(
                completionMessage ?? _localizationService["DashboardPage.UpdateModDone"],
                MessageDialogSeverity.Information,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update mod \"{}\"", vm.Name);
            _backgroundTaskService.Fail(backgroundTask, ex.Message);
            await progressDialog.CloseAsync(CancellationToken.None);
            await ShowDashboardMessageAsync(
                _localizationService.Format("DashboardPage.UpdateModFailed", new { message = ex.Message }),
                MessageDialogSeverity.Error,
                CancellationToken.None);
        }
    }

    // void Browse()
    // {
    //     throw new NotImplementedException();
    // }

    [RelayCommand]
    void Create()
    {
        _navigationService.Navigate(typeof(CreatePageViewModel));
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

        _navigationService.Navigate(typeof(TagManagementPageViewModel));
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task Settings()
    {
        await SaveProfileNowAsync();

        _navigationService.Navigate(typeof(SettingsPageViewModel));
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task DeploymentOrder()
    {
        await SaveProfileNowAsync();

        _navigationService.Navigate(typeof(DeploymentOrderPageViewModel));
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task Purge(CancellationToken cancellationToken)
    {
        if (!_settingsService.Initialized || string.IsNullOrEmpty(_settingsService.GameDirectory))
        {
            await ShowDashboardMessageAsync(
                _localizationService["DashboardPage.PurgeNoGameDir"],
                MessageDialogSeverity.Error,
                cancellationToken);
            return;
        }

        await using var progressDialog = await _dialogService.OpenProgressAsync(
            new ProgressDialogRequest(
                _localizationService["DashboardPage.PurgeProgress"],
                _localizationService["SettingsPage.PleaseWait"]),
            cancellationToken);

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
            await progressDialog.CloseAsync(CancellationToken.None);
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
    async Task Deploy(CancellationToken cancellationToken)
    {
        if (!_settingsService.Initialized || string.IsNullOrEmpty(_settingsService.GameDirectory))
        {
            await ShowDashboardMessageAsync(
                _localizationService["DashboardPage.DeployNoGameDir"],
                MessageDialogSeverity.Error,
                cancellationToken);
            return;
        }

        await using var progressDialog = await _dialogService.OpenProgressAsync(
            new ProgressDialogRequest(
                _localizationService["DashboardPage.DeployProgress"],
                _localizationService["SettingsPage.PleaseWait"]),
            cancellationToken);

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

            await progressDialog.CloseAsync(cancellationToken);
            await ShowDashboardMessageAsync(
                _localizationService["DashboardPage.DeploySuccess"],
                MessageDialogSeverity.Information,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unknown deployment error");
            _backgroundTaskService.Fail(backgroundTask, ex.Message);
            await progressDialog.CloseAsync(CancellationToken.None);
            await ShowDashboardMessageAsync(ex.Message, MessageDialogSeverity.Error, CancellationToken.None);
        }
    }

    [RelayCommand]
    async Task RescanMods(CancellationToken cancellationToken)
    {
        await using var progressDialog = await _dialogService.OpenProgressAsync(
            new ProgressDialogRequest(
                _localizationService["DashboardPage.RescanMods"],
                _localizationService["SettingsPage.PleaseWait"]),
            cancellationToken);

        try
        {
            var problems = _modService.RescanMods();

            await progressDialog.CloseAsync(cancellationToken);

            if (problems.Length > 0)
                await ShowDashboardMessageAsync(
                    BuildProblems(problems, _localizationService["DashboardPage.RescanProblemsPrefix"], true),
                    MessageDialogSeverity.Warning,
                    CancellationToken.None);

            RebuildOrderedItems();
            UpdateView();

            if (!_settingsService.IsReadonly)
            {
                await SaveProfileNowAsync(false);
            }

            await ShowDashboardMessageAsync(
                _localizationService["DashboardPage.RescanComplete"],
                MessageDialogSeverity.Information,
                cancellationToken);
        }
        catch (Exception ex)
        {
            await progressDialog.CloseAsync(CancellationToken.None);
            _logger.LogError(ex, "Rescan mods failed");
            await ShowDashboardMessageAsync(ex.Message, MessageDialogSeverity.Error, CancellationToken.None);
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
    async Task Remove(ModViewModel modVm, CancellationToken cancellationToken)
    {
        if (!await _dialogService.ShowAsync(
            new DialogRequest(
                _localizationService["DashboardPage.DeleteConfirmTitle"],
                _localizationService.Format(
                _settingsService.DeleteToRecycleBin ? "DashboardPage.DeleteRecycleConfirm" : "DashboardPage.DeletePermanentConfirm",
                new { modName = modVm.Name })),
            cancellationToken))
            return;

        await DeleteModAsync(modVm, cancellationToken);
    }

    private async Task DeleteModAsync(ModViewModel modVm, CancellationToken cancellationToken)
    {
        await using var progressDialog = await _dialogService.OpenProgressAsync(
            new ProgressDialogRequest(
                _localizationService["DashboardPage.DeleteModProgress"],
                _localizationService["SettingsPage.PleaseWait"]),
            cancellationToken);

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

            await progressDialog.CloseAsync(cancellationToken);
            _backgroundTaskService.Complete(backgroundTask, _localizationService.Format("BackgroundTasksPage.DeleteComplete", new { name = modVm.Name }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unknown mod removal error");
            _backgroundTaskService.Fail(backgroundTask, ex.Message);
            await progressDialog.CloseAsync(CancellationToken.None);
            await ShowDashboardMessageAsync(ex.Message, MessageDialogSeverity.Error, CancellationToken.None);
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
    async Task OpenFileLocation(ModViewModel modVm, CancellationToken cancellationToken)
    {
        try
        {
            Process.Start(new ProcessStartInfo(modVm.Data.Directory.FullName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open file location for mod {ModName}", modVm.Name);
            await ShowDashboardMessageAsync(
                _localizationService.Format("DashboardPage.OpenFileLocationFailed", new { message = ex.Message }),
                MessageDialogSeverity.Error,
                cancellationToken);
        }
    }

    [RelayCommand]
    async Task EditName(ModViewModel modVm, CancellationToken cancellationToken)
    {
        try
        {
            var newName = await _dialogService.PromptAsync(
                new InputDialogRequest(
                    _localizationService["DashboardPage.EditNameTitle"],
                    _localizationService["DashboardPage.EditNameMsg"],
                    modVm.Name,
                    64),
                cancellationToken);
            if (newName is null)
                return;
            if (string.IsNullOrWhiteSpace(newName))
            {
                await ShowDashboardMessageAsync(
                    _localizationService["DashboardPage.EditNameEmptyError"],
                    MessageDialogSeverity.Error,
                    cancellationToken);
                return;
            }
            modVm.Data.UpdateManifestName(newName);
            await ShowDashboardMessageAsync(
                _localizationService["DashboardPage.EditNameUpdated"],
                MessageDialogSeverity.Information,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit mod name for mod {ModName}", modVm.Name);
            await ShowDashboardMessageAsync(
                _localizationService.Format("DashboardPage.EditNameFailed", new { message = ex.Message }),
                MessageDialogSeverity.Error,
                cancellationToken);
        }
    }

    [RelayCommand]
    async Task EditDescription(ModViewModel modVm, CancellationToken cancellationToken)
    {
        try
        {
            var newDescription = await _dialogService.PromptAsync(
                new InputDialogRequest(
                    _localizationService["DashboardPage.EditDescTitle"],
                    _localizationService["DashboardPage.EditDescMsg"],
                    modVm.Description,
                    1024),
                cancellationToken);
            if (newDescription is null)
                return;
            modVm.Data.UpdateManifestDescription(newDescription);
            await ShowDashboardMessageAsync(
                _localizationService["DashboardPage.EditDescUpdated"],
                MessageDialogSeverity.Information,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit mod description for mod {ModName}", modVm.Name);
            await ShowDashboardMessageAsync(
                _localizationService.Format("DashboardPage.EditDescFailed", new { message = ex.Message }),
                MessageDialogSeverity.Error,
                cancellationToken);
        }
    }

    [RelayCommand]
    async Task EditImage(ModViewModel modVm, CancellationToken cancellationToken)
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
            await using var progressDialog = await _dialogService.OpenProgressAsync(
                new ProgressDialogRequest(
                    _localizationService["DashboardPage.EditImageProgress"],
                    _localizationService["SettingsPage.PleaseWait"]),
                cancellationToken);

            try
            {
                string imageFileName = Path.GetFileName(dialog.FileName);
                string destinationPath = Path.Combine(modVm.Data.Directory.FullName, imageFileName);
                await CopyFileAsync(dialog.FileName, destinationPath, true);

                modVm.Data.UpdateManifestIconPath(imageFileName);

                modVm.LoadIcon();

                await SaveProfileNowAsync(false);

                await progressDialog.CloseAsync(cancellationToken);
                await ShowDashboardMessageAsync(
                    _localizationService["DashboardPage.EditImageSuccess"],
                    MessageDialogSeverity.Information,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to edit image for mod {ModName}", modVm.Name);
                await progressDialog.CloseAsync(CancellationToken.None);
                await ShowDashboardMessageAsync(
                    _localizationService.Format("DashboardPage.EditImageFailed", new { message = ex.Message }),
                    MessageDialogSeverity.Error,
                    CancellationToken.None);
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
        _navigationService.Navigate(typeof(EditPageViewModel));
    }

    [RelayCommand]
    void EditManifest(ModViewModel vm)
    {
        _editModStore.CurrentMod = vm;
        _navigationService.Navigate(typeof(ManifestEditPageViewModel));
    }

    internal bool TryOpenFirstManifestForUiTest()
    {
        if (!Initialized || _mods.Count == 0)
            return false;
        _editModStore.CurrentMod = _mods[0];
        _navigationService.Navigate(typeof(ManifestEditPageViewModel), root: true);
        return true;
    }

    /// <summary>
    /// Pack the mod as a zip/7z archive and export to a specified location for distribution.
    /// Supports 5 gears: ZIP standard / 7z Fast / 7z Normal / 7z High / 7z Ultra.
    /// Shows memory usage warning for high-compression options on large mods.
    /// </summary>
    [RelayCommand]
    async Task ExportMod(ModViewModel vm, CancellationToken cancellationToken)
    {
        var modDir = vm.Data.Directory;

        var opt = await _dialogService.SelectAsync(
            new SelectionDialogRequest(
                _localizationService["DashboardPage.ExportTitle"],
                _localizationService["DashboardPage.ExportMsg"],
                new List<string>
            {
                _localizationService["DashboardPage.ExportZip"],
                _localizationService["DashboardPage.Export7zFast"],
                _localizationService["DashboardPage.Export7zStandard"],
                _localizationService["DashboardPage.Export7zHigh"],
                _localizationService["DashboardPage.Export7zUltra"]
            }),
            cancellationToken);
        if (opt is null)
            return;

        var is7z = opt.StartsWith("7z", StringComparison.OrdinalIgnoreCase);

        // Parse compression level
        SharpSevenZip.CompressionLevel level;
        string dictSize;
        bool isHighMemory;
        string levelName;

        if (opt == _localizationService["DashboardPage.Export7zFast"]) { level = SharpSevenZip.CompressionLevel.Fast; dictSize = "8m"; isHighMemory = false; levelName = "Fast"; }
        else if (opt == _localizationService["DashboardPage.Export7zHigh"]) { level = SharpSevenZip.CompressionLevel.High; dictSize = "64m"; isHighMemory = true; levelName = "High"; }
        else if (opt == _localizationService["DashboardPage.Export7zUltra"]) { level = SharpSevenZip.CompressionLevel.Ultra; dictSize = "128m"; isHighMemory = true; levelName = "Ultra"; }
        else { level = SharpSevenZip.CompressionLevel.Normal; dictSize = "32m"; isHighMemory = false; levelName = "Normal"; }

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

            if (!await _dialogService.ShowAsync(
                new DialogRequest(
                    _localizationService["DashboardPage.ExportMemoryWarning"],
                    _localizationService.Format("DashboardPage.ExportMemoryMessage", new { size = sizeText, level = levelName, dictionary = dictDesc })),
                cancellationToken))
                return;

            await DoExportAsync(vm, modDir, dialog.FileName, is7z, level, dictSize, levelName, excludedExtensions, cancellationToken);
        }
        else
        {
            await DoExportAsync(vm, modDir, dialog.FileName, is7z, level, dictSize, levelName, excludedExtensions, cancellationToken);
        }
    }

    private ModViewModel GetOrCreateModViewModel(ModData mod)
    {
        if (_modViewModelsById.TryGetValue(mod.Manifest.Guid, out var existing))
            return existing;

        var created = _modViewModelFactory.Create(mod);
        _modViewModelsById.Add(mod.Manifest.Guid, created);
        return created;
    }

    /// <summary>
    /// Execute the actual export with the chosen format and settings.
    /// Shows a real-time progress dialog with compression speed and ratio.
    /// </summary>
    private async Task DoExportAsync(ModViewModel vm, DirectoryInfo modDir, string outputPath, bool is7z,
        SharpSevenZip.CompressionLevel level, string dictSize, string levelName, HashSet<string> excludedExtensions,
        CancellationToken cancellationToken)
    {
        await using var progressDialog = await _dialogService.OpenProgressAsync(
            new ProgressDialogRequest(
                $"{_localizationService["DashboardPage.ExportSaveDialog"]} - {vm.Name}",
                _localizationService["SettingsPage.PleaseWait"]),
            cancellationToken);

        var backgroundTask = _backgroundTaskService.Add(
            _localizationService["BackgroundTasksPage.TaskTypeExport"],
            vm.Name);
        _backgroundTaskService.Update(backgroundTask, progress: 0, isIndeterminate: false);

        try
        {
            await Task.Run(
                () => DoExportCore(
                    vm, modDir, outputPath, is7z, level, dictSize, levelName,
                    excludedExtensions, backgroundTask, progressDialog, cancellationToken),
                cancellationToken);
            await progressDialog.CloseAsync(cancellationToken);
            await ShowDashboardMessageAsync(
                _localizationService.Format("BackgroundTasksPage.ExportComplete", new { name = vm.Name }),
                MessageDialogSeverity.Information,
                cancellationToken);
        }
        catch (Exception ex)
        {
            await progressDialog.CloseAsync(CancellationToken.None);
            await ShowDashboardMessageAsync(
                _localizationService.Format("DashboardPage.ExportError", new { message = ex.Message }),
                MessageDialogSeverity.Error,
                CancellationToken.None);
        }
    }

    /// <summary>
    /// Background export with real-time progress reporting.
    /// </summary>
    private void DoExportCore(ModViewModel vm, DirectoryInfo modDir, string outputPath, bool is7z,
        SharpSevenZip.CompressionLevel level, string dictSize, string levelName, HashSet<string> excludedExtensions,
        BackgroundTaskItem backgroundTask, IProgressDialogSession progressDialog, CancellationToken cancellationToken)
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

            progressDialog.Report(new ProgressDialogRequest(
                _localizationService["DashboardPage.ExportSaveDialog"],
                string.Join(" ", new[] { speedText, ratioText }.Where(static value => !string.IsNullOrWhiteSpace(value))),
                currentFile));
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
                    cancellationToken.ThrowIfCancellationRequested();
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

            _backgroundTaskService.Complete(backgroundTask, _localizationService.Format("BackgroundTasksPage.ExportComplete", new { name = vm.Name }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export mod");
            _backgroundTaskService.Fail(backgroundTask, ex.Message);
            throw;
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
    async Task DownloadFromNexus(CancellationToken cancellationToken)
    {
        await _dialogService.ShowMessageAsync(
            new MessageDialogRequest(
                _localizationService["MessageBox.Info"],
                _localizationService["DashboardPage.NexusDownloadInfo"]),
            cancellationToken);

        _navigationService.Navigate(typeof(NexusDownloadPageViewModel));
    }

    [RelayCommand]
    void ShowDownloadProgress()
    {
        _navigationService.Navigate(typeof(DownloadProgressViewModel));
    }

    [RelayCommand]
    void ShowBackgroundTasks()
    {
        _navigationService.Navigate(typeof(BackgroundTasksPageViewModel));
    }

    [RelayCommand]
    async Task EditModTags(ModViewModel modVm, CancellationToken cancellationToken)
    {
        if (modVm == null || !_settingsService.Initialized)
            return;

        var selectedTagIds = modVm.Data.TagIds.ToList();
        var options = _settingsService.Tags.Select(tag => new ChecklistDialogOption(
            tag.Id.ToString(),
            tag.Name,
            tag.Color,
            selectedTagIds.Contains(tag.Id))).ToArray();
        var newTagIds = await _dialogService.SelectManyAsync(
            new ChecklistDialogRequest(
                _localizationService["DashboardPage.EditTagsTitle"],
                _localizationService["DashboardPage.EditTagsMsg"],
                options),
            cancellationToken);
        if (newTagIds is null)
            return;
        if (_settingsService.IsReadonly)
        {
            await ShowDashboardMessageAsync(
                _localizationService["DashboardPage.BatchTagReadonly"],
                MessageDialogSeverity.Error,
                cancellationToken);
            return;
        }

        modVm.Data.TagIds = newTagIds
            .Select(static id => Guid.TryParse(id, out var value) ? (Guid?)value : null)
            .Where(static id => id.HasValue)
            .Select(static id => id!.Value)
            .ToList();
        RequestProfileSave();
        await ShowDashboardMessageAsync(
            _localizationService["DashboardPage.EditTagsUpdated"],
            MessageDialogSeverity.Information,
            cancellationToken);
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
    async Task CreateSeparator(CancellationToken cancellationToken)
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
        await _settingsService.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// 重命名分隔符
    /// </summary>
    [RelayCommand]
    async Task RenameSeparator(ModSeparator separator, CancellationToken cancellationToken)
    {
        if (separator == null || _settingsService.IsReadonly)
            return;

        var newName = await _dialogService.PromptAsync(
            new InputDialogRequest(
                _localizationService["DashboardPage.RenameSeparatorTitle"],
                _localizationService["DashboardPage.RenameSeparatorMsg"],
                separator.Name,
                32),
            cancellationToken);
        if (newName is null)
            return;
        if (string.IsNullOrWhiteSpace(newName))
        {
            await ShowDashboardMessageAsync(
                _localizationService["DashboardPage.RenameSeparatorEmptyError"],
                MessageDialogSeverity.Error,
                cancellationToken);
            return;
        }
        separator.Name = newName;
        OnPropertyChanged(nameof(Mods));
        await _settingsService.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// 更改分隔符颜色
    /// </summary>
    [RelayCommand]
    async Task ChangeSeparatorColor(ModSeparator separator, CancellationToken cancellationToken)
    {
        if (separator == null || _settingsService.IsReadonly)
            return;

        var selectedColor = await _dialogService.PickColorAsync(
            new ColorDialogRequest(
                _localizationService["DashboardPage.ChangeSeparatorColorTitle"],
                _localizationService.Format("DashboardPage.ChangeSeparatorColorMessage", new { name = separator.Name }),
                separator.Color),
            cancellationToken);
        if (selectedColor is null)
            return;
        separator.Color = selectedColor;
        OnPropertyChanged(nameof(Mods));
        await _settingsService.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// 删除分隔符
    /// </summary>
    [RelayCommand]
    async Task DeleteSeparator(ModSeparator separator)
    {
        if (separator == null || _settingsService.IsReadonly)
            return;

        if (!await _dialogService.ShowAsync(
            new DialogRequest(
                _localizationService["DashboardPage.DeleteSeparatorHint"],
                _localizationService.Format("DashboardPage.DeleteSeparatorMessage", new { name = separator.Name })),
            CancellationToken.None))
            return;

        _settingsService.Separators.Remove(separator);
        RebuildOrderedItems();
        await _settingsService.SaveAsync();
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
                vm.Dispose();
            }
            _orderedItems.Clear();
            _mods.Clear();
            _modViewModelsById.Clear();
        }
    }
}
