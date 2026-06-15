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
using System.Threading;
using System.Windows;
using System.Windows.Media;
using MessageBox = Helldivers2ModManager.Components.MessageBox;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class DashboardPageViewModel : PageViewModelBase, IDropTarget
{
    public override string Title => "Mods";

    public IEnumerable<ModViewModel> Mods { get; private set; }

    public bool IsSearchEmpty => string.IsNullOrEmpty(SearchText);

    /// <summary>
    /// 排序方式枚举
    /// </summary>
    public enum SortMode
    {
        Default,
        NameAsc,
        NameDesc,
        EnabledFirst,
        DisabledFirst
    }

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
    private readonly INexusModsService _nexusModsService;
    private readonly VersionCheckService _versionCheckService;
    private ObservableCollection<ModViewModel> _mods;
    private Timer? _saveTimer;
    private volatile bool _isSavePending;
    private readonly object _saveLock = new();
    /// <summary>
    /// 跟踪已知模组 GUID → 目录最后写入时间，用于检测新增或变动的模组
    /// </summary>
    private static readonly Dictionary<Guid, DateTime> s_knownModTimestamps = [];
    
    [ObservableProperty]
    private string _searchText = string.Empty;
    [ObservableProperty]
    private Visibility _imagePreviewVisibility = Visibility.Hidden;
    [ObservableProperty]
    private ImageSource? _previewImageSource;
    [ObservableProperty]
    private bool _initialized = false;

    [ObservableProperty]
    private SortMode _currentSortMode = SortMode.Default;

    /// <summary>
    /// 是否有选中的 Mod（用于控制批量操作按钮的可见性）
    /// </summary>
    public bool HasSelection => _mods is not null && _mods.Any(static vm => vm.IsSelected);

    /// <summary>
    /// 选中数量文本（如 "已选 2 项"）
    /// </summary>
    public string SelectionCountText => _mods is null ? "" : $"已选 {_mods.Count(static vm => vm.IsSelected)} 项";

    /// <summary>
    /// 排序功能是否在设置中启用
    /// </summary>
    public bool IsSortingEnabled => _settingsService.Initialized && _settingsService.EnableSorting;

    // ===== 版本兼容性检测属性 =====

    /// <summary>
    /// 是否正在检查版本兼容性
    /// </summary>
    [ObservableProperty]
    private bool _isCheckingVersion;

    /// <summary>
    /// 版本检查摘要文本
    /// </summary>
    [ObservableProperty]
    private string _versionCheckSummary = string.Empty;

    /// <summary>
    /// 兼容模组数量
    /// </summary>
    [ObservableProperty]
    private int _compatibleModCount;

    /// <summary>
    /// 不兼容模组数量
    /// </summary>
    [ObservableProperty]
    private int _incompatibleModCount;

    /// <summary>
    /// 上次版本检查是否有不兼容的模组
    /// </summary>
    public bool HasIncompatibleMods => IncompatibleModCount > 0;

    /// <summary>
    /// 是否已完成版本检查
    /// </summary>
    public bool HasVersionCheckResult => !string.IsNullOrEmpty(VersionCheckSummary);

    public IEnumerable<SortMode> SortModes { get; } = [SortMode.Default, SortMode.NameAsc, SortMode.NameDesc, SortMode.EnabledFirst, SortMode.DisabledFirst];
    private object? _selectedGroupItem = "无";
    public object? SelectedGroupItem
    {
        get
        {
            return _selectedGroupItem;
        }
        set
        {
            if (_selectedGroupItem != value)
            {
                _selectedGroupItem = value;
                OnPropertyChanged(nameof(SelectedGroupItem));
                OnPropertyChanged(nameof(SelectedGroup));
                
                // Enable only mods in the selected group
                var selectedGroup = SelectedGroup;
                foreach (var mod in _mods)
                {
                    if (selectedGroup == null)
                    {
                        // If no group is selected, enable only mods without a group
                        mod.Enabled = mod.Data.GroupId == null;
                    }
                    else
                    {
                        // Enable only mods in the selected group
                        mod.Enabled = mod.Data.GroupId == selectedGroup.Id;
                    }
                }
                
                UpdateView();
            }
        }
    }
    public ModGroup? SelectedGroup
    {
        get
        {
            if (_selectedGroupItem is ModGroup group)
            {
                return group;
            }
            return null;
        }
        set
        {
            if (value == null)
            {
                SelectedGroupItem = "无";
            }
            else
            {
                SelectedGroupItem = value;
            }
        }
    }
    public IReadOnlyList<ModGroup> Groups => _settingsService.Initialized ? _settingsService.Groups : [];
    public IEnumerable<object> GroupItems
    {
        get
        {
            yield return "无";
            if (_settingsService.Initialized)
            {
                foreach (var group in _settingsService.Groups)
                {
                    yield return group;
                }
            }
        }
    }

    public DashboardPageViewModel(ILogger<DashboardPageViewModel> logger, IServiceProvider provider, SettingsService settingsService, ModService modService, ProfileService profileService, EditModStore editModStore, INexusModsService nexusModsService, VersionCheckService versionCheckService)
    {
        _logger = logger;
        _navStore = new(provider.GetRequiredService<NavigationStore>);
        _editModStore = editModStore;
        _settingsService = settingsService;
        _modService = modService;
        _profileService = profileService;
        _nexusModsService = nexusModsService;
        _versionCheckService = versionCheckService;
        _mods = [];
        _saveTimer = new Timer(OnSaveTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);

        Mods = _mods;

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
        else if (e.PropertyName == nameof(SelectedGroup))
        {
            UpdateView();
        }

        base.OnPropertyChanged(e);
    }

    private async Task SaveEnabled(bool showProgress = true)
    {
        if (!_settingsService.IsReadonly)
        {
            if (showProgress)
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
                {
                    Title = "保存模组配置中",
                    Message = "请民主官耐心等待."
                });
            }

            // 保存前将当前显示顺序告诉 ProfileService，确保其以正确的顺序写入 SQLite
            _profileService.SetLastSavedOrder(_mods.Select(static vm => vm.Guid));
            await _profileService.SaveAsync(_settingsService, _mods.Select(static vm => vm.Data));

            if (showProgress)
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
            }
        }
    }

    private void UpdateView()
    {
        IEnumerable<ModViewModel> filteredMods = _mods;

        if (SelectedGroup != null)
        {
            filteredMods = filteredMods.Where(vm => vm.Data.GroupId == SelectedGroup.Id);
        }

        if (!IsSearchEmpty && _settingsService.Initialized)
        {
            var searchText = SearchText.Trim();

            if (searchText.StartsWith("@"))
            {
                var tagName = searchText.Substring(1);
                if (!string.IsNullOrEmpty(tagName))
                {
                    filteredMods = filteredMods.Where(vm =>
                        vm.Tags.Any(t => t.Name.Contains(tagName, StringComparison.InvariantCultureIgnoreCase)));
                }
            }
            else
            {
                filteredMods = filteredMods.Where(vm =>
                {
                    if (_settingsService.CaseSensitiveSearch)
                        return vm.Name.Contains(searchText, StringComparison.InvariantCulture);
                    return vm.Name.Contains(searchText, StringComparison.InvariantCultureIgnoreCase);
                });
            }
        }

        // 排序 —— 仅当设置中启用了排序功能才生效
        bool hasActiveSort = false;
        if (_settingsService.Initialized && _settingsService.EnableSorting)
        {
            hasActiveSort = CurrentSortMode != SortMode.Default;
            if (hasActiveSort)
            {
                filteredMods = CurrentSortMode switch
                {
                    SortMode.NameAsc => filteredMods.OrderBy(static vm => vm.Name),
                    SortMode.NameDesc => filteredMods.OrderByDescending(static vm => vm.Name),
                    SortMode.EnabledFirst => filteredMods.OrderByDescending(static vm => vm.Enabled),
                    SortMode.DisabledFirst => filteredMods.OrderBy(static vm => vm.Enabled),
                    _ => filteredMods,
                };
            }
        }

        // 无任何筛选/排序时直接使用原始 ObservableCollection，保证拖拽功能可用
        if (SelectedGroup is null && IsSearchEmpty && !hasActiveSort)
        {
            Mods = _mods;
        }
        else
        {
            Mods = filteredMods.ToArray();
        }
        OnPropertyChanged(nameof(Mods));
    }

    /// <summary>
    /// 排序方式变更时刷新列表
    /// </summary>
    partial void OnCurrentSortModeChanged(SortMode value)
    {
        UpdateView();
    }

    private async Task Init()
    {
        _logger.LogInformation("Initializing dashboard...");

        _logger.LogInformation("Loading settings...");
        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
        {
            Title = "加载设置中",
            Message = "请民主官耐心等待.",
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
                Title = $"加载设置失败!",
                Message = "是否立刻前往设置?",
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
                Title = $"设置无效!",
                Message = "是否立刻前往设置?",
                Confirm = _navStore.Value.Navigate<SettingsPageViewModel>,
            });
            return;
        }
        _logger.LogInformation("Settings valid");

        _logger.LogInformation("Loading mods...");
        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
        {
            Title = "加载模组中",
            Message = "请民主官耐心等待.",
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
                Message = $"加载模组失败!\n\n{ex}",
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
            Title = "加载配置文件中",
            Message = "请民主官耐心等待.",
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
                Message = $"加载配置文件失败!\n\n{ex}",
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
        }
        _mods = new(modViewModels);
        _mods.CollectionChanged += Mods_CollectionChanged;
        _profileService.SetLastSavedOrder(_mods.Select(static vm => vm.Guid));
        UpdateView();

        if (problems.Length > 0)
            ShowProblems(problems, "加载模组时出现问题:", false, true);
        Initialized = true;
        _logger.LogInformation("Initialization successful");

        // 检测新增或变动的模组，自动触发版本兼容性检查
        if (_settingsService.AutoCheckVersionOnStartup && _mods.Count > 0)
        {
            var changedMods = GetNewOrChangedMods().ToList();
            if (changedMods.Count > 0)
            {
                _logger.LogInformation("检测到 {Count} 个新增/变动的模组，自动检查版本兼容性...", changedMods.Count);
                _ = CheckVersionCompatibility();
            }
        }

        // 更新模组跟踪快照
        UpdateModTimestampTracking();

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
            sb.AppendLine("错误:");
            foreach (var e in errors)
            {
                sb.Append("\t - \"");
                sb.Append(e.Directory.FullName);
                sb.AppendLine("\"");

                sb.Append("\t\t");
                string desc = e.Kind switch
                {
                    ModProblemKind.CantParseManifest => "无法解析清单文件!",
                    ModProblemKind.UnknownManifestVersion => "未知清单版本!",
                    ModProblemKind.OutOfSupportManifest => $"不支持的清单版本!请更新.\n\t\t管理器版本 {App.Version} 不支持此版本的清单文件.",
                    ModProblemKind.Duplicate => "已添加一个具有相同 GUID 的模组。!",
                    ModProblemKind.InvalidPath => e.ExtraData is not null
                        ? $"包含路径  \"{e.ExtraData}\" 无效!"
                        : "包含路径无效!",
                    ModProblemKind.CantReadArchive => e.ExtraData is not null
                        ? $"无法读取压缩文件! 错误: {e.ExtraData}"
                        : "无法读取压缩文件!",
                    _ => throw new NotImplementedException()
                };
                sb.AppendLine(desc);
            }
        }

        var warnings = problems.Where(static p => !p.IsError).ToArray();
        if (warnings.Length != 0)
        {
            sb.AppendLine("警告:");
            foreach (var w in warnings)
            {
                sb.Append("\t - \"");
                sb.Append(w.Directory.FullName);
                sb.AppendLine("\"");

                sb.Append("\t\t");
                string desc = w.Kind switch
                {
                    ModProblemKind.NoManifestFound => isInit
                        ? "目录中未找到清单文件!\n\t\t\t执行操作: 删除(Deleting)"
                        : "目录中未找到清单文件!\n\t\t\t执行操作: 从目录推断(Inferring from directory)",
                    ModProblemKind.EmptyOptions => "清单包含空选项! 此模组可能不会产生任何效果.",
                    ModProblemKind.EmptySubOptions => "清单包含空的子选项！此模组可能无法按预期运行.",
                    ModProblemKind.EmptyIncludes => "清单包含空的包含列表！此模组可能不会产生任何作用.",
                    ModProblemKind.InvalidImagePath => w.ExtraData is not null
                        ? $"清单图片路径 \"{w.ExtraData}\" 无效!"
                        : "清单包含无效的图片路径!",
                    ModProblemKind.EmptyImagePath => "清单包含空的图片路径​!",
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
        var vm = new ModViewModel(mod, _logger, _settingsService, _nexusModsService);
        vm.OptionsChanged += ModViewModel_OptionsChanged;
        vm.PropertyChanged += ModViewModel_PropertyChanged;
        _mods.Add(vm);
        SearchText = string.Empty;
        UpdateView();
    }

    private async void OnModAdded(ModData mod)
    {
        // 新模组添加后，如果启用了自动检查，仅扫描该新增模组（使用缓存的参考版本）
        if (_settingsService.AutoCheckVersionOnStartup)
        {
            _logger.LogInformation("New mod \"{Name}\", checking version compatibility...", mod.Manifest.Name);
            var result = await _versionCheckService.CheckSingleModAsync(mod);
            if (result is not null)
            {
                var vm = _mods.FirstOrDefault(v => v.Guid == mod.Manifest.Guid);
                if (vm is not null)
                {
                    vm.GameUnitVersion = result.GameVersion;
                    vm.LastVersionCheck = result.LastChecked;
                    vm.VersionStatus = result.Status;
                    vm.VersionCheckResult = result;
                }

                if (result.Status == ModVersionStatus.Incompatible)
                    VersionCheckSummary = $"发现不兼容的新增模组: {mod.Manifest.Name}";
                else
                    VersionCheckSummary = $"新增模组 \"{mod.Manifest.Name}\" 版本检测完成";
                OnPropertyChanged(nameof(HasVersionCheckResult));
            }
        }
    }

    private void ModService_ModRemoved(ModData mod)
    {
        var vm = _mods.FirstOrDefault((vm) => vm.Data == mod);
        if (vm is not null)
        {
            vm.OptionsChanged -= ModViewModel_OptionsChanged;
            vm.PropertyChanged -= ModViewModel_PropertyChanged;
            _mods.Remove(vm);
            UpdateView();
        }
    }

    private void ModViewModel_OptionsChanged()
    {
        lock (_saveLock)
        {
            _isSavePending = true;
            _saveTimer?.Change(300, Timeout.Infinite);
        }
    }

    /// <summary>
    /// 监听集合变动，当用户拖拽排序后触发自动保存（Move 操作不触发 OptionsChanged）
    /// </summary>
    private void Mods_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move)
        {
            lock (_saveLock)
            {
                _isSavePending = true;
                _saveTimer?.Change(300, Timeout.Infinite);
            }
        }
    }

    /// <summary>
    /// 拖拽悬停 —— 委托给默认处理器以显示拖拽位置指示线
    /// </summary>
    void IDropTarget.DragOver(IDropInfo dropInfo)
    {
        new DefaultDropHandler().DragOver(dropInfo);
    }

    /// <summary>
    /// 拖拽放下 —— 当有多个选中项时一并移动，否则按默认单项目行为
    /// </summary>
    void IDropTarget.Drop(IDropInfo dropInfo)
    {
        if (dropInfo?.Data is not ModViewModel sourceVm)
        {
            new DefaultDropHandler().Drop(dropInfo);
            return;
        }

        // 获取选中项（含当前拖拽项），按原始位置排序
        var selected = _mods.Where(vm => vm.IsSelected).ToList();
        if (selected.Contains(sourceVm) && selected.Count > 1)
        {
            var sortedSelected = selected.OrderBy(vm => _mods.IndexOf(vm)).ToList();
            var targetIdx = dropInfo.InsertIndex;

            // 从集合中移除所有选中项（倒序删除以保持索引正确）
            foreach (var vm in sortedSelected.AsEnumerable().Reverse())
                _mods.Remove(vm);

            // 如果目标索引位于删除区域之后，需修正插入位置
            var firstRemovedIdx = _mods.IndexOf(sortedSelected[0]);
            if (firstRemovedIdx == -1) // 所有项都在目标之前被删除了
            {
                // 计算目标在删除后的新位置
                var beforeCount = sortedSelected.Count(vm => _mods.IndexOf(vm) < targetIdx);
                targetIdx -= beforeCount;
            }

            targetIdx = Math.Clamp(targetIdx, 0, _mods.Count);

            // 按原始顺序插入
            for (int i = 0; i < sortedSelected.Count; i++)
                _mods.Insert(targetIdx + i, sortedSelected[i]);

            // 多选重排完成后触发自动保存（Remove/Insert 不会触发 CollectionChanged Move）
            lock (_saveLock)
            {
                _isSavePending = true;
                _saveTimer?.Change(300, Timeout.Infinite);
            }
        }
        else
        {
            // 单项目拖拽 —— 使用默认处理器
            new DefaultDropHandler().Drop(dropInfo);
        }
    }

    private async void OnSaveTimerElapsed(object? state)
    {
        bool shouldSave;
        lock (_saveLock)
        {
            shouldSave = _isSavePending;
            _isSavePending = false;
        }
        
        if (shouldSave)
        {
            await SaveEnabled(false);
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
        foreach (var vm in _mods)
            vm.IsSelected = true;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionCountText));
    }

    [RelayCommand]
    void DeselectAll()
    {
        foreach (var vm in _mods)
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
    async Task BatchDelete()
    {
        var selected = _mods.Where(static vm => vm.IsSelected).ToArray();
        if (selected.Length == 0)
            return;

        var deleteMessage = _settingsService.DeleteToRecycleBin
            ? "模组文件将被移动到回收站。"
            : "模组文件将被永久删除，此操作不可恢复！";

        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = "批量删除",
            Message = $"确定要删除选中的 {selected.Length} 个模组吗？\n{deleteMessage}",
            Confirm = async () =>
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
                {
                    Title = "批量删除中",
                    Message = "请民主官耐心等待."
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
                    }

                    WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "批量删除模组失败");
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
                    {
                        Message = $"批量删除失败: {ex.Message}"
                    });
                }

                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectionCountText));
            }
        });
    }

    [RelayCommand]
    void BatchEnable()
    {
        foreach (var vm in _mods.Where(static vm => vm.IsSelected))
            vm.Enabled = true;
    }

    [RelayCommand]
    void BatchDisable()
    {
        foreach (var vm in _mods.Where(static vm => vm.IsSelected))
            vm.Enabled = false;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
        async Task Add(string? filePath = null)
        {
            string? selectedFile = filePath;

            if (selectedFile == null)
            {
                var dialog = new OpenFileDialog
                {
                    CheckFileExists = true,
                    CheckPathExists = true,
                    InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Download"),
                    Filter = "Mod档案|*.rar;*.7z;*.zip;*.tar",
                    Multiselect = false,
                    Title = "请选择要添加的模组压缩包..."
                };

                if (!(dialog.ShowDialog() ?? false))
                    return;

                selectedFile = dialog.FileName;
            }

            WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
            {
                Title = "添加模组中",
                Message = "请民主官耐心等待."
            });
            try
            {
                var problems = await _modService.TryAddModFromArchiveAsync(new FileInfo(selectedFile));
                if (problems.Length > 0)
                {
                    var error = problems.Any(static p => p.IsError);
                    var prefix = error
                        ? "由于出现问题，模组添加失败:"
                        : "模组已添加, 但有些相关问题:";
                    ShowProblems(problems, prefix, error);
                }
                else
                    WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add mod");
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
                {
                    Message = ex.Message
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
        await SaveEnabled();

        _navStore.Value.Navigate<TagManagementPageViewModel>();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task Settings()
    {
        await SaveEnabled();

        _navStore.Value.Navigate<SettingsPageViewModel>();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task Purge()
    {
        if (!_settingsService.Initialized || string.IsNullOrEmpty(_settingsService.GameDirectory))
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = "无法清理模组! 因为游戏路径未设置."
            });
            return;
        }

        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
        {
            Title = "清理模组中",
            Message = "请民主官耐心等待."
        });

        await _modService.PurgeAsync();

        WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task Deploy()
    {
        if (!_settingsService.Initialized || string.IsNullOrEmpty(_settingsService.GameDirectory))
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = "无法部署模组! 因为游戏路径未设置."
            });
            return;
        }

        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
        {
            Title = "部署模组中",
            Message = "请民主官耐心等待."
        });

        var mods = _mods.Where(static vm => vm.Enabled).ToArray();
        var guids = mods.Select(static vm => vm.Guid).ToArray();

        try
        {
            await SaveEnabled();

            await _modService.DeployAsync(guids);

            WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage()
            {
                Message = "部署成功."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unknown deployment error");
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
            ? "模组文件将被移动到回收站。"
            : "模组文件将被永久删除，此操作不可恢复！";
        
        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = "确认删除",
            Message = $"确定要删除模组 '{modVm.Name}' 吗？\n{deleteMessage}",
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
            Title = "删除模组中",
            Message = "请民主官耐心等待."
        });

        try
        {
            await _modService.RemoveAsync(modVm.Data);

            // 删除后同步更新数据库：直接删除该模组对应的记录
            if (!_settingsService.IsReadonly)
            {
                await _profileService.DeleteEnabledDataAsync(_settingsService.StorageDirectory, modVm.Guid);
            }

            WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unknown mod removal error");
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

    // ===== 版本兼容性检查命令 =====

    /// <summary>
    /// 检查所有模组的版本兼容性
    /// 采用"模组间横向对比"策略: 取多数模组的 Unit 版本作为参考，标记偏离的模组
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task CheckVersionCompatibility()
    {
        IsCheckingVersion = true;
        VersionCheckSummary = "正在扫描模组补丁文件...";

        try
        {
            // 扫描所有模组的补丁文件，以多数版本为参考进行横向对比
            var results = await _versionCheckService.CheckAllModsAsync(_modService.Mods);

            // 将检测结果同步到每个 ModViewModel
            int compatible = 0, incompatible = 0;
            foreach (var vm in _mods)
            {
                if (results.TryGetValue(vm.Guid, out var result))
                {
                    vm.GameUnitVersion = result.GameVersion;
                    vm.LastVersionCheck = result.LastChecked;
                    vm.VersionStatus = result.Status;
                    vm.VersionCheckResult = result;

                    if (result.Status == Models.ModVersionStatus.Compatible)
                        compatible++;
                    else if (result.Status == Models.ModVersionStatus.Incompatible)
                        incompatible++;
                }
            }

            CompatibleModCount = compatible;
            IncompatibleModCount = incompatible;

            if (incompatible > 0)
            {
                VersionCheckSummary = $"发现 {incompatible} 个可能不兼容的模组";
                OnPropertyChanged(nameof(HasIncompatibleMods));
            }
            else if (compatible > 0)
            {
                VersionCheckSummary = $"{compatible} 个模组均兼容";
            }
            else
            {
                VersionCheckSummary = "未发现可检查的模组";
            }

            OnPropertyChanged(nameof(HasVersionCheckResult));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "版本兼容性检查失败");
            VersionCheckSummary = "检查失败";
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = $"版本兼容性检查失败:\n\n{ex.Message}"
            });
        }
        finally
        {
            IsCheckingVersion = false;
        }
    }

    /// <summary>
    /// 获取本次新增或文件变动的模组（与上次跟踪快照对比）
    /// </summary>
    private IEnumerable<ModViewModel> GetNewOrChangedMods()
    {
        foreach (var vm in _mods)
        {
            if (!s_knownModTimestamps.TryGetValue(vm.Guid, out var lastTime))
            {
                // GUID 不存在 → 新增模组
                yield return vm;
            }
            else if (vm.Data.Directory.LastWriteTimeUtc != lastTime)
            {
                // 目录修改时间变化 → 模组文件变动
                yield return vm;
            }
        }
    }

    /// <summary>
    /// 更新模组跟踪快照，记录当前所有模组的 GUID 和目录修改时间
    /// </summary>
    private void UpdateModTimestampTracking()
    {
        // 清除已不存在的模组
        var currentGuids = _mods.Select(static vm => vm.Guid).ToHashSet();
        foreach (var guid in s_knownModTimestamps.Keys.Where(g => !currentGuids.Contains(g)).ToList())
            s_knownModTimestamps.Remove(guid);

        // 更新当前模组的目录修改时间
        foreach (var vm in _mods)
            s_knownModTimestamps[vm.Guid] = vm.Data.Directory.LastWriteTimeUtc;
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
                Message = $"无法打开文件位置: {ex.Message}"
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
                Title = "编辑名称",
                Message = "请输入新的模组名称：",
                MaxLength = 64,
                InitialText = modVm.Name,
                Confirm = (newName) =>
                {
                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "模组名称不能为空" });
                        return;
                    }

                    modVm.Data.UpdateManifestName(newName);
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = "模组名称已更新" });
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit mod name for mod {ModName}", modVm.Name);
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = $"无法编辑模组名称: {ex.Message}"
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
                Title = "编辑描述",
                Message = "请输入新的模组描述：",
                MaxLength = 1024,
                InitialText = modVm.Description,
                Confirm = (newDescription) =>
                {
                    modVm.Data.UpdateManifestDescription(newDescription);
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = "模组描述已更新" });
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit mod description for mod {ModName}", modVm.Name);
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = $"无法编辑模组描述: {ex.Message}"
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
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp",
            Title = "请选择要设置的模组图片..."
        };

        if (dialog.ShowDialog() ?? false)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
            {
                Title = "更新图片中",
                Message = "请民主官耐心等待."
            });

            try
            {
                string imageFileName = Path.GetFileName(dialog.FileName);
                string destinationPath = Path.Combine(modVm.Data.Directory.FullName, imageFileName);
                await CopyFileAsync(dialog.FileName, destinationPath, true);

                modVm.Data.UpdateManifestIconPath(imageFileName);

                modVm.LoadIcon();

                await SaveEnabled();

                WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
                WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage()
                {
                    Message = "图片更新成功."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to edit image for mod {ModName}", modVm.Name);
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
                {
                    Message = $"图片更新失败: {ex.Message}"
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
            Title = "导出设置",
            Message = "请选择导出格式和压缩方式：",
            Options = new List<object>
            {
                "ZIP (标准 - 兼容性最好, 内存低)",
                "7z (快速 LZMA2 - 速度快, 内存低)",
                "7z (标准 LZMA2 - 平衡, 内存中)",
                "7z (高压缩 LZMA2 - 体积小, 内存中)",
                "7z (极限 LZMA2 - 体积最小, 内存高 ⚠)"
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

                if (opt.Contains("快速"))    { level = SharpSevenZip.CompressionLevel.Fast;   dictSize = "8m";  isHighMemory = false; levelName = "Fast"; }
                else if (opt.Contains("高压缩")) { level = SharpSevenZip.CompressionLevel.High;   dictSize = "64m"; isHighMemory = true;  levelName = "High"; }
                else if (opt.Contains("极限"))   { level = SharpSevenZip.CompressionLevel.Ultra;  dictSize = "128m"; isHighMemory = true;  levelName = "Ultra"; }
                else                             { level = SharpSevenZip.CompressionLevel.Normal; dictSize = "32m"; isHighMemory = false; levelName = "Normal"; }

                // Step 2: Show save file dialog
                var dialog = new SaveFileDialog
                {
                    Title = "导出模组",
                    FileName = $"{vm.Name}.{(is7z ? "7z" : "zip")}",
                    Filter = is7z ? "7z 压缩包|*.7z|所有文件|*.*" : "ZIP 压缩包|*.zip|所有文件|*.*",
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
                        Title = "内存占用警告",
                        Message = $"模组文件总大小 {sizeText}，选择了「{levelName}」级别。\n\n" +
                                  $"该级别使用 {dictDesc} 字典进行 LZMA2 压缩，\n" +
                                  $"压缩过程中内存占用较高，且部分旧版解压工具可能无法解压。\n\n" +
                                  "是否继续导出？",
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
            Title = $"导出模组 - {vm.Name}"
        });

        // Run export on background thread to keep UI responsive
        Task.Run(() => DoExportAsync(vm, modDir, outputPath, is7z, level, dictSize, levelName, excludedExtensions));
    }

    /// <summary>
    /// Background export with real-time progress reporting.
    /// </summary>
    private void DoExportAsync(ModViewModel vm, DirectoryInfo modDir, string outputPath, bool is7z,
        SharpSevenZip.CompressionLevel level, string dictSize, string levelName, HashSet<string> excludedExtensions)
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
                ? $"速度: {speed / (1024.0 * 1024):F1} MB/s"
                : speed >= 1024
                    ? $"速度: {speed / 1024.0:F0} KB/s"
                    : $"速度: {speed:F0} B/s";

            // Read output file size for ratio (if file exists)
            string ratioText = "";
            try
            {
                var outFile = new FileInfo(outputPath);
                if (outFile.Exists && outFile.Length > 0 && totalInputSize > 0)
                {
                    // 压缩率 = (1 - 输出大小/输入大小) * 100，表示压缩了多少
                    var saved = (1.0 - (double)outFile.Length / totalInputSize) * 100;
                    ratioText = $"压缩率: {saved:F1}%";
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
            // Don't auto-close - user clicks OK to dismiss and see final ratio/speed
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export mod");
            Application.Current.Dispatcher.Invoke(() =>
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
                WeakReferenceMessenger.Default.Send(new MessageBoxWarningMessage
                {
                    Message = $"导出模组时出现错误：{ex.Message}"
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
    void CreateGroup()
    {
        WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
        {
            Title = "创建分组",
            Message = "请输入新分组的名称：",
            MaxLength = 32,
            Confirm = (groupName) =>
            {
                if (string.IsNullOrWhiteSpace(groupName))
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "分组名称不能为空" });
                    return;
                }

                _settingsService.Groups.Add(new ModGroup(groupName));
                if (!_settingsService.IsReadonly)
                {
                    _ = _settingsService.SaveAsync();
                    OnPropertyChanged(nameof(GroupItems));
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = "分组创建成功" });
                }
                else
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "无法创建分组，设置处于只读模式" });
                }
            }
        });
    }

    [RelayCommand]
    void DeleteGroup(ModGroup? group)
    {
        group ??= SelectedGroup;
        if (group == null)
            return;

        var modsInGroup = _mods.Where(vm => vm.Data.GroupId == group.Id).ToArray();

        if (!_settingsService.IsReadonly)
        {
            var message = modsInGroup.Length > 0
                ? $"确定要删除分组 '{group.Name}' 吗？此操作将清除 {modsInGroup.Length} 个模组的分组信息，且不可恢复。"
                : $"确定要删除分组 '{group.Name}' 吗？此操作不可恢复。";

            WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
            {
                Title = "确认删除",
                Message = message,
                Confirm = () =>
                {
                    foreach (var mod in modsInGroup)
                    {
                        mod.Data.GroupId = null;
                    }

                    _settingsService.Groups.Remove(group);
                    if (SelectedGroup == group)
                    {
                        SelectedGroup = null;
                    }
                    _ = _settingsService.SaveAsync();
                    OnPropertyChanged(nameof(GroupItems));
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = "分组删除成功" });
                }
            });
        }
        else
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "无法删除分组，设置处于只读模式" });
        }
    }

    [RelayCommand]
    void RenameGroup(ModGroup? group)
    {
        group ??= SelectedGroup;
        if (group == null)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
        {
            Title = "重命名分组",
            Message = "请输入新的分组名称：",
            MaxLength = 32,
            InitialText = group.Name,
            Confirm = (newName) =>
            {
                if (string.IsNullOrWhiteSpace(newName))
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "分组名称不能为空" });
                    return;
                }

                if (!_settingsService.IsReadonly)
                {
                    group.Name = newName;
                    _ = _settingsService.SaveAsync();
                    OnPropertyChanged(nameof(GroupItems));
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = "分组重命名成功" });
                }
                else
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "无法重命名分组，设置处于只读模式" });
                }
            }
        });
    }

    [RelayCommand]
    void SetGroup(ModViewModel modVm)
    {
        if (modVm == null || !_settingsService.Initialized)
            return;

        // Create a list of group options including "None"
        var groupOptions = new List<object> { "无" };
        groupOptions.AddRange(_settingsService.Groups);

        // Show a selection dialog
        WeakReferenceMessenger.Default.Send(new MessageBoxSelectionMessage
        {
            Title = "设置分组",
            Message = "请为模组选择一个分组：",
            Options = groupOptions,
            Confirm = (selectedOption) =>
            {
                if (!_settingsService.IsReadonly)
                {
                    if (selectedOption.ToString() == "无")
                    {
                        modVm.Data.GroupId = null;
                    }
                    else if (selectedOption is ModGroup selectedGroup)
                    {
                        modVm.Data.GroupId = selectedGroup.Id;
                    }

                    _ = SaveEnabled();
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = "模组分组已更新" });
                }
                else
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "无法设置分组，设置处于只读模式" });
                }
            }
        });
    }

    [RelayCommand]
    void ApplyAll()
    {
    }

    [RelayCommand]
    void ShowImagePreview(ImageSource imageSource)
    {
        PreviewImageSource = imageSource;
        ImagePreviewVisibility = Visibility.Visible;
    }

    [RelayCommand]
    void HideImagePreview()
    {
        ImagePreviewVisibility = Visibility.Hidden;
        PreviewImageSource = null;
    }

    [RelayCommand]
    void DownloadFromNexus()
    {
        var message = @"从 Nexus Mods 下载模组功能需要 Nexus Mods Premium
不过由于我没有N网会员这个功能运行效果如何尚且未知所以不要使用
但是可以考虑使用扩展的方式替代";

        WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = message });
        
        _navStore.Value.Navigate<NexusDownloadPageViewModel>();
    }

    [RelayCommand]
    void ShowDownloadProgress()
    {
        _navStore.Value.Navigate<DownloadProgressViewModel>();
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
                Title = "设置标签",
                Message = "请选择模组的标签：",
                Tags = selectableTags,
                Confirm = (selectedTags) =>
                {
                    if (!_settingsService.IsReadonly)
                    {
                        modVm.Data.TagIds = selectedTags.Select(t => t.Tag.Id).ToList();
                        _ = SaveEnabled();
                        WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = "模组标签已更新" });
                    }
                    else
                    {
                        WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "无法设置标签，设置处于只读模式" });
                    }
                }
            });
    }

    public IReadOnlyList<ModTag> AllTags => _settingsService.Initialized ? _settingsService.Tags : [];
    public IEnumerable<object> TagItems => _settingsService.Initialized ? _settingsService.Tags : [];

    protected override void OnDispose()
    {
        _modService.ModAdded -= ModService_ModAdded;
        _modService.ModRemoved -= ModService_ModRemoved;

        if (_mods is not null)
        {
            _mods.CollectionChanged -= Mods_CollectionChanged;
            _mods.Clear();
        }

        // 在页面退出前刷新待保存的更改，否则定时器销毁后未触发的保存会丢失
        if (_saveTimer is not null)
        {
            bool shouldSave;
            lock (_saveLock)
            {
                shouldSave = _isSavePending;
                _isSavePending = false;
            }
            _saveTimer.Dispose();

            if (shouldSave)
            {
                try
                {
                    SaveEnabled(false).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "退出页面时保存配置失败");
                }
            }
        }

        _saveTimer = null;
    }
}