using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GongSolutions.Wpf.DragDrop;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
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
using System.Windows.Threading;
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
    private readonly IServiceProvider _provider;
    private readonly Lazy<NavigationStore> _navStore;
    private readonly EditModStore _editModStore;
    private readonly ModService _modService;
    private readonly SettingsService _settingsService;
    private readonly ProfileService _profileService;
    private readonly ProfileSaveCoordinator _profileSaveCoordinator;
    private readonly INexusModsService _nexusModsService;
    private readonly VersionCheckRepository _versionCheckRepository;
    private readonly ModLinkRepository _modLinkRepository;
    private readonly ModHashService _modHashService;
    private ObservableCollection<ModViewModel> _mods;
    private ObservableCollection<object> _orderedItems;
    private readonly SearchFilterService _searchFilterService;
    private readonly VersionCheckViewModel _versionCheckVm;
    private readonly LocalizationService _localizationService;
    private readonly BackgroundTaskService _backgroundTaskService;
    private readonly ModGroupService _modGroupService;
    private readonly ModConflictService _modConflictService;
    private readonly ModConflictRepository _modConflictRepository;
    private readonly ModTypeDetectionService _modTypeDetectionService;
    private readonly DispatcherTimer _searchDebounceTimer;

    [ObservableProperty]
    private string _searchText = string.Empty;
    [ObservableProperty]
    private bool _initialized = false;

    /// <summary>
    /// 是否有选中的 Mod（用于控制批量操作按钮的可见性）
    /// </summary>
    public bool HasSelection => _mods is not null && _modGroupService.FilterModViewModels(_mods).Any(static vm => vm.IsSelected);

    /// <summary>
    /// 选中数量文本（如 "已选 2 项"）
    /// </summary>
    public string SelectionCountText => _mods is null ? "" : $"{_localizationService["DashboardPage.SelectedCountPrefix"]}{_modGroupService.FilterModViewModels(_mods).Count(static vm => vm.IsSelected)}{_localizationService["DashboardPage.SelectedCountSuffix"]}";

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
    public int CompatibleModCount => _versionCheckVm.CompatibleModCount;
    public int IncompatibleModCount => _versionCheckVm.IncompatibleModCount;

    /// <summary>
    /// 上次版本检查是否有不兼容的模组
    /// </summary>
    public bool HasIncompatibleMods => _versionCheckVm.HasIncompatibleMods;

    [ObservableProperty]
    private bool _isScanningConflicts;

    private bool _conflictScanPending;
    private string? _appliedConflictCacheKey;
    private readonly Dictionary<string, ModConflictAnalysisResult> _conflictCache = new(StringComparer.Ordinal);

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
        ModLinkRepository modLinkRepository,
        ModHashService modHashService,
        SearchFilterService searchFilterService,
        VersionCheckViewModel versionCheckVm,
        LocalizationService localizationService,
        BackgroundTaskService backgroundTaskService,
        ModGroupService modGroupService,
        ModConflictService modConflictService,
        ModConflictRepository modConflictRepository,
        ModTypeDetectionService modTypeDetectionService,
        ModGroupSidebarViewModel groupSidebar)
    {
        _logger = logger;
        _provider = provider;
        _navStore = new(provider.GetRequiredService<NavigationStore>);
        _editModStore = editModStore;
        _settingsService = settingsService;
        _modService = modService;
        _profileService = profileService;
        _profileSaveCoordinator = profileSaveCoordinator;
        _nexusModsService = nexusModsService;
        _versionCheckRepository = versionCheckRepository;
        _modLinkRepository = modLinkRepository;
        _modHashService = modHashService;
        _searchFilterService = searchFilterService;
        _versionCheckVm = versionCheckVm;
        _versionCheckVm.PropertyChanged += VersionCheckVm_PropertyChanged;
        _localizationService = localizationService;
        _backgroundTaskService = backgroundTaskService;
        _modGroupService = modGroupService;
        _modConflictService = modConflictService;
        _modConflictRepository = modConflictRepository;
        _modTypeDetectionService = modTypeDetectionService;
        GroupSidebar = groupSidebar;
        GroupSidebar.Configure(GetSelectedModData, () => _mods?.Select(static vm => vm.Data) ?? [], SelectGroupAsync, UpdateGroupedView);

        // 监听语言切换，通知 Title 属性变更
        _localizationService.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
        };

        // 订阅哈希迁移进度事件：迁移完成后弹气泡提示（进行中的进度由任务中心的哈希任务展示，
        // 底部状态栏不再显示动态文字）
        _modHashService.MigrationProgressChanged += (progress) =>
        {
            if (!progress.IsCompleted)
                return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                WeakReferenceMessenger.Default.Send(new ToastMessage(
                    _localizationService["BackgroundTasksPage.TaskTypeHash"],
                    progress.Message ?? _localizationService["BackgroundTasksPage.TaskTypeHash"]));
            });
        };
        _mods = [];
        _orderedItems = [];

        _searchDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            UpdateView();
        };

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
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
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

    /// <summary>
    /// 自动识别模组类型并打标签（音效/UI/贴图/护甲/战略配备/支援武器/主武器/敌人/模型/脚本）。
    /// 默认关闭，需在设置中开启；开启后优先复用用户已有同名标签，默认不创建新标签。
    /// 识别是后台任务；标签合并与保存回 UI 线程执行，保留用户标签。
    /// </summary>
    private async Task ApplyAutoTypeTagsAsync(IReadOnlyCollection<ModViewModel> mods)
    {
        // 默认关闭：需在设置中显式开启自动打标签
        if (mods.Count == 0 || _settingsService.IsReadonly || !_settingsService.EnableAutoTagging)
            return;

        var targets = mods.Select(static vm => vm.Data).ToArray();
        Dictionary<string, ModTypeDetectionService.ModTypeDetectionResult> detections;
        try
        {
            detections = await _backgroundTaskService.RunAsync(
                _localizationService["DashboardPage.AutoTagTitle"],
                _localizationService["SettingsPage.PleaseWait"],
                (_, token) => Task.FromResult(_modTypeDetectionService.DetectAll(targets, token)),
                isForeground: false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "自动识别模组类型失败，跳过自动打标签");
            return;
        }

        var changed = _modTypeDetectionService.ApplyAutoTags(
            _settingsService,
            _localizationService,
            targets,
            detections,
            createMissingTags: _settingsService.AutoTagCreateMissingTags);
        if (changed == 0)
            return;

        _logger.LogInformation("自动识别并打标签：{Count} 个模组", changed);
        RequestProfileSave();
        WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
        {
            Message = _localizationService["DashboardPage.AutoTagSummary"].Replace("{count}", changed.ToString()),
        });
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
            RefreshPositionNumbers();
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

        RefreshPositionNumbers();
        OnPropertyChanged(nameof(Mods));
    }

    /// <summary>
    /// 按 _orderedItems 中模组的当前顺序刷新显示序号（1 基，不含分隔符）
    /// </summary>
    private void RefreshPositionNumbers()
    {
        int position = 1;
        foreach (var vm in _orderedItems.OfType<ModViewModel>())
            vm.PositionNumber = position++;
    }

    private void UpdateView()
    {
        IEnumerable<ModViewModel> filteredMods = _modGroupService.FilterModViewModels(_mods);

        // 搜索过滤
        filteredMods = _searchFilterService.ApplySearchFilter(filteredMods, SearchText);

        // 无搜索时使用完整的_orderedItems（分隔符可见）
        if (IsSearchEmpty)
        {
            // 重置 Mods 指向 _orderedItems，因为 else 分支可能已将 Mods 设为新的数组，
            // 导致 RebuildOrderedItems 修改 _orderedItems 后 UI 读取的仍是旧数组
            Mods = _orderedItems;
            RebuildOrderedItems();
        }
        else
        {
            // 有搜索时只显示过滤后的模组列表（不显示分隔符）
            // 此时 Mods 是只读的，拖拽不可用
            Mods = filteredMods.ToArray();
            OnPropertyChanged(nameof(Mods));
        }
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
            // 首次扫描全部模组（CPU/IO 密集）：后台线程执行 + 任务状态统一管理。
            // 有加载弹窗，属前台任务，任务页不显示。
            problems = await _backgroundTaskService.RunAsync(
                _localizationService["DashboardPage.LoadingMods"],
                _localizationService["SettingsPage.PleaseWait"],
                (_, _) => Task.FromResult(_modService.Init(_settingsService)),
                isForeground: true);
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
        // 一次性预取全部 Mod 链接，避免每个 VM 构造各开一条数据库连接
        var prefetchedLinks = _modLinkRepository.GetLinks(_settingsService.StorageDirectory);
        var modViewModels = _modService.GetOrCreateModViewModels(result, _logger, _settingsService, _nexusModsService, prefetchedLinks);
        foreach (var vm in modViewModels)
        {
            vm.OptionsChanged += ModViewModel_OptionsChanged;
            vm.PropertyChanged += ModViewModel_PropertyChanged;
            vm.VersionCheckRefreshed += ModViewModel_VersionCheckRefreshed;
        }
        _mods = new(modViewModels);
        _mods.CollectionChanged += Mods_CollectionChanged;
        // 预热模糊搜索的拼音缓存：首次调用会加载拼音字典（实测约 180ms，批量转换 1000 个
        // 名称仅需数毫秒）。放到后台线程执行，避免用户第一次输入搜索时在 UI 线程上卡顿；
        // 先取快照，避免与后续 ModAdded/ModRemoved 修改 _mods 集合产生并发枚举。
        PrewarmPinyinSearchCache(_mods.ToArray());
        await _modGroupService.InitAsync(_settingsService, _mods.Select(static vm => vm.Data).ToArray());
        _modGroupService.ApplyGroupState(_modGroupService.SelectedGroup.Id, _mods.Select(static vm => vm.Data));
        foreach (var vm in _mods)
            vm.RefreshGroupStateBindings();
        GroupSidebar.IsOpen = _modGroupService.IsSidebarOpen;
        GroupSidebar.RefreshSelectionProperties();
        RebuildOrderedItems();
        _ = CaptureProfileSnapshot();
        UpdateView();

        // 自动识别模组类型并打内置类型标签（识别在后台线程，打标签与保存回 UI 线程）
        await ApplyAutoTypeTagsAsync(_mods.ToArray());

        if (problems.Length > 0)
            ShowProblems(problems, _localizationService["DashboardPage.LoadProblemsPrefix"], false, true);

        // 从数据库加载已缓存的版本检测结果，避免每次启动都需要全量扫描
        _versionCheckVm.LoadCachedResults(_mods);

        var hasCachedConflictResult = RestoreCachedConflictStatuses();

        Initialized = true;
        _logger.LogInformation("Initialization successful");

        if (!hasCachedConflictResult)
            RequestAutomaticConflictScan();

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

    /// <summary>
    /// 后台预热模糊搜索的拼音缓存（触发拼音字典加载 + 批量转换），
    /// 确保用户第一次输入搜索时无需在 UI 线程上做首次字典加载。失败不影响功能：
    /// 搜索路径仍会按需惰性构建缓存。
    /// </summary>
    private static void PrewarmPinyinSearchCache(ModViewModel[] mods)
    {
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var vm in mods)
                    _ = vm.PinyinCache;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Pinyin cache prewarm failed: {ex}");
            }
        });
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
                    ModProblemKind.MissingIncludePath => w.ExtraData is not null
                        ? $"{_localizationService["DashboardPage.MissingIncludePathPrefix"]}{w.ExtraData}{_localizationService["DashboardPage.MissingIncludePathSuffix"]}"
                        : _localizationService["DashboardPage.MissingIncludePath"],
                    ModProblemKind.InvalidImagePath => w.ExtraData is not null
                        ? $"{_localizationService["DashboardPage.InvalidImagePathPrefix"]}{w.ExtraData}{_localizationService["DashboardPage.InvalidPathSuffix"]}"
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
        // 事件可能来自后台线程（如 Rescan、TryAddModFromArchive 等通过 BackgroundTaskService 调用），
        // 操作 ObservableCollection 必须在 UI 线程
        Application.Current.Dispatcher.Invoke(() =>
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
            RequestAutomaticConflictScan();
        });
    }

    private async void OnModAdded(ModData mod)
    {
        // 事件可能来自后台线程，OnPropertyChanged 必须在 UI 线程
        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await _versionCheckVm.CheckSingleModOnAddAsync(mod, _mods);
        });
    }

    private void ModService_ModRemoved(ModData mod)
    {
        // 使用 GUID 查找而不是引用相等性，避免因 ModData 引用不匹配导致界面不同步
        var vm = _mods.FirstOrDefault(vm => vm.Guid == mod.Manifest.Guid);
        if (vm is null)
            return;

        // 操作 ObservableCollection 必须在 UI 线程；
        // 此事件可能来自后台线程（如删除模组时 BackgroundTaskService.RunAsync 内调用）
        Application.Current.Dispatcher.Invoke(() =>
        {
            vm.OptionsChanged -= ModViewModel_OptionsChanged;
            vm.PropertyChanged -= ModViewModel_PropertyChanged;
            vm.VersionCheckRefreshed -= ModViewModel_VersionCheckRefreshed;
            _mods.Remove(vm);
            _modGroupService.CaptureGroupState(_modGroupService.SelectedGroup.Id, _modGroupService.FilterMods(_mods.Select(static vm => vm.Data)));
            GroupSidebar.RefreshSelectionProperties();
            UpdateView();
            RequestAutomaticConflictScan();
        });
    }

    private void ModViewModel_VersionCheckRefreshed(object? sender, EventArgs e)
    {
        if (sender is ModViewModel vm)
            _ = _versionCheckVm.RefreshAfterSingleModCheckAsync(_mods, vm);
    }

    private void ModViewModel_OptionsChanged()
    {
        RequestProfileSave();
        RequestAutomaticConflictScan();
    }

    private void ClearConflictStatuses()
    {
        foreach (var vm in _mods)
            vm.ClearConflictStatus();
    }

    private bool RestoreCachedConflictStatuses()
    {
        var deploymentMods = GetDeploymentMods(CaptureProfileSnapshot());
        var cacheKey = _modConflictService.BuildCacheKey(deploymentMods);
        if (TryGetCachedConflictResult(cacheKey, out var cachedResult))
        {
            ApplyConflictAnalysisResult(cacheKey, cachedResult, showReport: false);
            return true;
        }

        _appliedConflictCacheKey = null;
        return false;
    }

    private bool TryGetCachedConflictResult(string cacheKey, [NotNullWhen(true)] out ModConflictAnalysisResult? result)
    {
        if (_conflictCache.TryGetValue(cacheKey, out result))
            return true;

        if (string.IsNullOrEmpty(_settingsService.StorageDirectory))
        {
            result = null;
            return false;
        }

        result = _modConflictRepository.Load(_settingsService.StorageDirectory, cacheKey);
        if (result is null)
            return false;

        _conflictCache[cacheKey] = result;
        return true;
    }

    private void ApplyConflictAnalysisResult(string cacheKey, ModConflictAnalysisResult result, bool showReport)
    {
        var visibleConflicts = result.Conflicts
            .Where(static conflict => !string.IsNullOrWhiteSpace(conflict.FriendlyName))
            .ToArray();

        var conflictsByMod = result.Conflicts
            .SelectMany(conflict => conflict.Participants
                .Select(participant => (participant.ModGuid, Conflict: conflict)))
            .GroupBy(static item => item.ModGuid)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<ModConflictRecord>)group
                    .Select(static item => item.Conflict)
                    .Distinct()
                    .ToArray());

        foreach (var vm in _mods)
        {
            vm.ApplyConflictStatus(conflictsByMod.TryGetValue(vm.Guid, out var conflicts)
                ? conflicts
                : []);
        }

        _conflictCache[cacheKey] = result;
        _appliedConflictCacheKey = cacheKey;

        if (showReport)
        {
            // 手动刷新冲突检查：详细报告改为简化气泡（数量摘要，自动消失）；
            // 逐条冲突明细仍可点击模组卡片上的覆盖状态指示器查看
            WeakReferenceMessenger.Default.Send(new ToastMessage(
                _localizationService["BackgroundTasksPage.TaskTypeConflictScan"],
                visibleConflicts.Length > 0
                    ? _localizationService["Toast.ConflictScanSummary"]
                        .Replace("{mods}", result.ScannedModCount.ToString())
                        .Replace("{conflicts}", visibleConflicts.Length.ToString())
                    : _localizationService["Toast.ConflictScanClean"]
                        .Replace("{mods}", result.ScannedModCount.ToString()),
                IsError: false));
        }
    }

    private string GetCurrentConflictCacheKey()
    {
        return _modConflictService.BuildCacheKey(GetDeploymentMods(CaptureProfileSnapshot()));
    }

    private void RequestAutomaticConflictScan()
    {
        if (Initialized)
        {
            if (IsScanningConflicts)
            {
                _conflictScanPending = true;
                return;
            }

            var cacheKey = GetCurrentConflictCacheKey();
            if (string.Equals(_appliedConflictCacheKey, cacheKey, StringComparison.Ordinal)
                && _conflictCache.ContainsKey(cacheKey))
                return;

            if (TryGetCachedConflictResult(cacheKey, out var cachedResult))
            {
                ApplyConflictAnalysisResult(cacheKey, cachedResult, showReport: false);
                return;
            }

            _ = RunConflictScanAsync(showReport: false, allowCachedResult: false);
        }
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
    /// 是否为文件拖拽（资源管理器拖入的文件：gong 解析后为 FileDrop 的 string[]，也可能是原始 IDataObject）。
    /// 文件拖拽由主窗口 Preview 事件统一处理（压缩包导入），不进入排序拖拽管线。
    /// </summary>
    private static bool IsFileDrop(object? data)
    {
        return data is string[]
            || (data is IDataObject idata && idata.GetDataPresent(DataFormats.FileDrop));
    }

    /// <summary>
    /// 拖拽悬停 —— 分隔符不可拖拽，模组使用默认指示器
    /// </summary>
    void IDropTarget.DragOver(IDropInfo dropInfo)
    {
        if (IsFileDrop(dropInfo?.Data))
            return;

        // 模组和分隔符均可自由拖动
        new DefaultDropHandler().DragOver(dropInfo);
    }

    /// <summary>
    /// 拖拽放下 —— 支持分隔符归类、多选批量移动和单项目拖拽
    /// </summary>
    void IDropTarget.Drop(IDropInfo dropInfo)
    {
        if (IsFileDrop(dropInfo?.Data))
            return;

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
        RequestAutomaticConflictScan();
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
            RefreshPositionNumbers();
            return;
        }

        // 按 displayOrder 重新排序 _mods（一次性建位置表替代逐项 IndexOf；
        // Move 后仅重读受影响区间的位置，避免大列表上的 O(N²) 扫描）
        var positionOf = new Dictionary<ModViewModel, int>(_mods.Count);
        for (int i = 0; i < _mods.Count; i++)
            positionOf[_mods[i]] = i;

        for (int i = 0; i < displayOrder.Count; i++)
        {
            var vm = displayOrder[i];
            if (!positionOf.TryGetValue(vm, out var currentIdx))
                continue; // 防御：项不在当前集合中（分组视图等）时跳过
            var targetIdx = Math.Min(i, _mods.Count - 1);
            if (currentIdx == targetIdx)
                continue;

            _mods.Move(currentIdx, targetIdx);

            if (currentIdx < targetIdx)
            {
                for (int p = currentIdx; p < targetIdx; p++)
                    positionOf[_mods[p]] = p;
            }
            else
            {
                for (int p = targetIdx + 1; p <= currentIdx; p++)
                    positionOf[_mods[p]] = p;
            }
            positionOf[vm] = targetIdx;
        }

        RefreshPositionNumbers();
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
            RequestAutomaticConflictScan();
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
}
