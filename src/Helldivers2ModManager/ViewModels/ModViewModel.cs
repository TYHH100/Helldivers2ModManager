using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Models.Nexus;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Helldivers2ModManager.Services.Nexus;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class ModViewModel : ObservableObject, IDisposable
{
    private readonly ILogger _logger;
    private readonly SettingsService _settingsService;
    private readonly INexusModsService _nexusModsService;
    private readonly LocalizationService _localizationService;
    private readonly VersionCheckService _versionCheckService;
    private readonly ModLinkRepository _modLinkRepository;

    public Guid Guid => _mod.Manifest.Guid;

    public string Name => _mod.Manifest.Name;

    /// <summary>
    /// 模糊搜索用的惰性拼音缓存：[0] 全拼小写（如 "diyuqianbing 4k caizhibao"），
    /// [1] 首字母小写（如 "dyqb 4k czb"）。名称在 Mod 生命周期内不变，
    /// 缓存随 VM 实例释放，避免搜索防抖热路径重复做拼音转换。
    /// </summary>
    private string[]? _pinyinCache;

    internal string[] PinyinCache => _pinyinCache ??= BuildPinyinCache(_mod.Manifest.Name);

    private static string[] BuildPinyinCache(string name)
    {
        // false：无音调输出；英文/数字原样保留（如 "Helldivers2" → "Helldivers2"）。
        string full = ToolGood.Words.Pinyin.WordsHelper.GetPinyin(name, false).ToLowerInvariant();
        string first = ToolGood.Words.Pinyin.WordsHelper.GetFirstPinyin(name).ToLowerInvariant();
        return [full, first];
    }

    public string Description => _mod.Manifest.Description;

    public Visibility OptionsVisible
    {
        get
        {
            if (_mod.Manifest.Version == ManifestVersion.Legacy && (_mod.Manifest as LegacyModManifest)!.Options is not null)
                return Visibility.Visible;
            return Visibility.Collapsed;
        }
    }

    public Visibility EditVisible => _mod.Manifest.Version == ManifestVersion.V1 ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private ImageSource? _icon;

    [ObservableProperty]
    private Mod? _nexusModInfo;

    [ObservableProperty]
    private string? _nexusUpdateStatus;

    [ObservableProperty]
    private bool _isCheckingUpdate;

    // ===== 版本兼容性检测属性 =====

    /// <summary>
    /// 版本兼容性状态
    /// </summary>
    [ObservableProperty]
    private ModVersionStatus _versionStatus = ModVersionStatus.Unknown;

    /// <summary>
    /// 游戏当前 Unit 版本号
    /// </summary>
    [ObservableProperty]
    private uint _gameUnitVersion;

    /// <summary>
    /// 最后检查时间
    /// </summary>
    [ObservableProperty]
    private DateTime _lastVersionCheck;

    /// <summary>
    /// 最后检查时间的本地化显示文本
    /// </summary>
    public string LastCheckDisplayText => LastVersionCheck == default
        ? ""
        : string.Format(_localizationService["DashboardPage.LastCheckFormat"], LastVersionCheck);

    partial void OnLastVersionCheckChanged(DateTime value)
    {
        OnPropertyChanged(nameof(LastCheckDisplayText));
    }

    /// <summary>
    /// 是否正在检查版本兼容性
    /// </summary>
    [ObservableProperty]
    private bool _isCheckingVersion;

    /// <summary>
    /// 版本检查提取到的 Unit 明细
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<PatchUnitInfo> _patchUnits = [];

    /// <summary>
    /// 版本检查的详细结构分析结果
    /// </summary>
    [ObservableProperty]
    private ModDetailedAnalysis? _detailedAnalysis;

    /// <summary>
    /// 是否已经完成过一次覆盖扫描。未扫描时不显示状态图标，避免把“未知”误认为“无覆盖”。
    /// </summary>
    [ObservableProperty]
    private bool _conflictScanCompleted;

    [ObservableProperty]
    private bool _hasConflict;

    [ObservableProperty]
    private string _conflictStatusTooltip = string.Empty;

    public string ConflictStatusIcon => HasConflict ? "!" : "✓";

    /// <summary>
    /// 冲突状态颜色固定，静态缓存避免每次绑定读取都新建 Brush（产生 GC 垃圾）。
    /// 必须 Freeze：Brush 是 DependencyObject，static 初始化线程不固定，
    /// 未冻结的 Brush 在跨线程（后台任务触发类型初始化后 UI 绑定使用）时会抛
    /// "必须在与 DependencyObject 相同的 Thread 上创建 DependencySource"。
    /// </summary>
    private static readonly Brush s_conflictBrush = CreateFrozenBrush(220, 80, 55);
    private static readonly Brush s_noConflictBrush = CreateFrozenBrush(40, 160, 95);

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public Brush ConflictStatusBrush => HasConflict ? s_conflictBrush : s_noConflictBrush;

    // ===== Mod 链接（作者主页 / 发布页，仅存管理器数据库） =====

    /// <summary>
    /// Mod 链接文本（作者主页或发布页）。仅保存到管理器数据库，不写入模组档案 JSON。
    /// </summary>
    [ObservableProperty]
    private string? _link;

    /// <summary>
    /// 是否已设置有效链接（控制链接图标样式/行为）。
    /// </summary>
    public bool HasLink => !string.IsNullOrWhiteSpace(Link);

    partial void OnLinkChanged(string? value)
    {
        OnPropertyChanged(nameof(HasLink));
    }

    [RelayCommand]
    public void OpenLink()
    {
        if (!HasLink)
        {
            EditLink();
            return;
        }

        var url = Link!.Trim();
        // 未带协议时按 https 处理，保证能直接跳转
        if (!url.Contains("://"))
            url = "https://" + url;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open link for mod {ModName}", Name);
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = _localizationService["ModViewModel.OpenLinkFailed"].Replace("{message}", ex.Message)
            });
        }
    }

    [RelayCommand]
    public void EditLink()
    {
        if (_settingsService.IsReadonly)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = _localizationService["ModViewModel.ModLinkReadonly"]
            });
            return;
        }

        WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
        {
            Title = _localizationService["ModViewModel.ModLinkEditTitle"],
            Message = _localizationService["ModViewModel.ModLinkEditMsg"],
            InitialText = Link ?? string.Empty,
            MaxLength = 2048,
            Confirm = (input) =>
            {
                var newLink = string.IsNullOrWhiteSpace(input) ? null : input.Trim();
                Link = newLink;
                _ = PersistLinkAsync(newLink);
            }
        });
    }

    /// <summary>
    /// 后台持久化链接到数据库（fire-and-forget，异常自行捕获并提示）。
    /// </summary>
    private async Task PersistLinkAsync(string? link)
    {
        try
        {
            await _modLinkRepository.SaveLinkAsync(_settingsService.StorageDirectory, Guid, link);
            WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
            {
                Message = _localizationService["ModViewModel.ModLinkUpdated"]
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save link for mod {ModName}", Name);
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = _localizationService["ModViewModel.ModLinkSaveFailed"].Replace("{message}", ex.Message)
            });
        }
    }

    public ModData Data => _mod;

    public event Action? OptionsChanged;

    public event EventHandler? VersionCheckRefreshed;

    public void OnOptionsChanged()
    {
        OptionsChanged?.Invoke();
    }

    public void ApplyConflictStatus(IReadOnlyList<ModConflictRecord> conflicts)
    {
        var visibleConflicts = conflicts
            .Where(static conflict => !string.IsNullOrWhiteSpace(conflict.FriendlyName))
            .ToArray();
        ConflictRecords = visibleConflicts;

        HasConflict = visibleConflicts.Length > 0;
        ConflictScanCompleted = true;

        if (visibleConflicts.Length == 0)
        {
            ConflictStatusTooltip = _localizationService["DashboardPage.ConflictStatusNoConflict"];
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(_localizationService["DashboardPage.ConflictStatusConflict"]);
        foreach (var conflict in visibleConflicts.Take(12))
        {
            var others = string.Join(", ", conflict.Participants
                .Where(p => p.ModGuid != Guid)
                .Select(static p => p.ModName)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            sb.AppendLine(_localizationService["DashboardPage.ConflictStatusItem"]
                .Replace("{resource}", conflict.FriendlyName)
                .Replace("{mods}", others)
                .Replace("{winner}", conflict.Winner.ModName));
        }

        if (visibleConflicts.Length > 12)
            sb.AppendLine(_localizationService["DashboardPage.ConflictStatusMore"]
                .Replace("{count}", (visibleConflicts.Length - 12).ToString()));

        ConflictStatusTooltip = sb.ToString().TrimEnd();
    }

    public void ClearConflictStatus()
    {
        ConflictRecords = [];
        ConflictScanCompleted = false;
        HasConflict = false;
        ConflictStatusTooltip = string.Empty;
    }

    [RelayCommand]
    private void ShowConflictDetail()
    {
        if (!ConflictScanCompleted)
            return;

        var conflicts = ConflictRecords;
        WeakReferenceMessenger.Default.Send(new ModConflictDetailMessage
        {
            ModName = Name,
            Conflicts = conflicts,
        });
    }

    private IReadOnlyList<ModConflictRecord> ConflictRecords { get; set; } = [];

    partial void OnHasConflictChanged(bool value)
    {
        OnPropertyChanged(nameof(ConflictStatusIcon));
        OnPropertyChanged(nameof(ConflictStatusBrush));
    }

    public string[]? LegacyOptions { get; private set; }

    public bool Enabled
    {
        get => _mod.Enabled;

        set
        {
            if (_mod.Enabled == value)
                return;
            OnPropertyChanging();
            _mod.Enabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 在 Dashboard 列表中的显示序号（1 基，与右键菜单“移动到指定位置”的编号一致）
    /// </summary>
    [ObservableProperty]
    private int _positionNumber;

    /// <summary>
    /// 多选状态 —— 在 Dashboard 列表中标记选中，用于批量操作
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    public IEnumerable<ModTag> Tags
    {
        get
        {
            if (!_settingsService.Initialized)
                return [];
            return _settingsService.Tags.Where(t => _mod.TagIds.Contains(t.Id));
        }
    }

    public IEnumerable<ModTag> DisplayTags => CalculateDisplayTags();

    public IEnumerable<ModTag> HiddenTags => CalculateHiddenTags();

    public bool HasHiddenTags => Tags.Count() > DisplayTags.Count();

    public int HiddenTagCount => Math.Max(0, Tags.Count() - DisplayTags.Count());

    private const int MaxTotalLength = 55; // 标签总长度上限（字符数）
    private const int TagPadding = 2; // 每个标签的额外字符数（用于边距）

    private List<ModTag> CalculateDisplayTags()
    {
        var tags = Tags.ToList();
        if (!tags.Any())
            return [];

        var result = new List<ModTag>();
        int currentLength = 0;

        foreach (var tag in tags)
        {
            var tagLength = tag.Name.Length + TagPadding;
            if (result.Any())
                tagLength += TagPadding; // 标签之间的间距

            if (currentLength + tagLength <= MaxTotalLength)
            {
                result.Add(tag);
                currentLength += tagLength;
            }
            else
            {
                break;
            }
        }

        return result;
    }

    private List<ModTag> CalculateHiddenTags()
    {
        var displayTags = DisplayTags.ToList();
        return Tags.Where(t => !displayTags.Contains(t)).ToList();
    }

    public int LegacySelectedOption
    {
        get => _mod.Manifest.Version == ManifestVersion.Legacy ? _mod.SelectedOptions[0] : -1;

        set
        {
            if (_mod.Manifest.Version != ManifestVersion.Legacy)
                return;
            OnPropertyChanging();
            _mod.SelectedOptions[0] = value;
            OnPropertyChanged();
        }
    }

    public ModOptionViewModel[]? Options { get; private set; }

    private readonly ModData _mod;

    public ModViewModel(ModData mod, ILogger logger, SettingsService settingsService, INexusModsService nexusModsService, LocalizationService localizationService, VersionCheckService versionCheckService, ModLinkRepository modLinkRepository, IReadOnlyDictionary<Guid, string?>? prefetchedLinks = null)
    {
        _mod = mod;
        _logger = logger;
        _settingsService = settingsService;
        _nexusModsService = nexusModsService;
        _localizationService = localizationService;
        _versionCheckService = versionCheckService;
        _modLinkRepository = modLinkRepository;

        _mod.PropertyChanged += ModData_PropertyChanged;
        // 批量构造时传入预取的链接字典（一次 SQL 取回全表），
        // 避免每个 VM 各开一条 SQLite 连接逐条查询；未传入时保持单条查询行为。
        Link = prefetchedLinks is not null
            ? prefetchedLinks.TryGetValue(Guid, out var link) ? link : null
            : _modLinkRepository.GetLink(_settingsService.StorageDirectory, Guid);

        switch (_mod.Manifest.Version)
        {
            case ManifestVersion.Legacy:
                LegacyOptions = ((LegacyModManifest)_mod.Manifest).Options?.ToArray();
                break;

            case ManifestVersion.V1:
            {
                var manifest = (V1ModManifest)_mod.Manifest;                    
                if (manifest.Options is null)
                    break;
                Options = new ModOptionViewModel[manifest.Options.Count];
                for (int i = 0; i < manifest.Options.Count; i++)
                    Options[i] = new ModOptionViewModel(this, i);
                break;
            }
            
            case ManifestVersion.V2:
                throw new NotSupportedException();
            
            default:
                throw new NotImplementedException();
        }

        LoadIcon();
    }

    private void ModData_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModData.Manifest))
        {
            // 名称可能已随新清单改变，旧的拼音缓存必须失效重建，否则模糊搜索按旧名匹配
            _pinyinCache = null;

            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(OptionsVisible));
            OnPropertyChanged(nameof(EditVisible));
            OnPropertyChanged(nameof(LegacySelectedOption));

            // 当 Manifest 被替换后（如更新模组），重新构建 Options/LegacyOptions
            switch (_mod.Manifest.Version)
            {
                case ManifestVersion.Legacy:
                    Options = null;
                    LegacyOptions = ((LegacyModManifest)_mod.Manifest).Options?.ToArray();
                    break;

                case ManifestVersion.V1:
                {
                    LegacyOptions = null;
                    var manifest = (V1ModManifest)_mod.Manifest;
                    if (manifest.Options is null)
                    {
                        Options = null;
                        break;
                    }
                    Options = new ModOptionViewModel[manifest.Options.Count];
                    for (int i = 0; i < manifest.Options.Count; i++)
                        Options[i] = new ModOptionViewModel(this, i);
                    break;
                }
            }
            OnPropertyChanged(nameof(Options));
            OnPropertyChanged(nameof(LegacyOptions));

            LoadIcon();
        }
        else if (e.PropertyName == nameof(ModData.TagIds))
        {
            OnPropertyChanged(nameof(Tags));
            OnPropertyChanged(nameof(DisplayTags));
            OnPropertyChanged(nameof(HiddenTags));
            OnPropertyChanged(nameof(HasHiddenTags));
            OnPropertyChanged(nameof(HiddenTagCount));
        }
    }

    public void RefreshGroupStateBindings()
    {
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(LegacySelectedOption));
        if (_mod.Manifest.Version == ManifestVersion.V1)
        {
            var manifest = (V1ModManifest)_mod.Manifest;
            Options = manifest.Options is null
                ? null
                : manifest.Options.Select((_, i) => new ModOptionViewModel(this, i)).ToArray();
            OnPropertyChanged(nameof(Options));
        }
    }

    /// <summary>
    /// 图标加载序号，用于丢弃过期的异步解码结果（快速刷新时避免旧图标覆盖新图标）。
    /// </summary>
    private int _iconLoadGeneration;

    /// <summary>
    /// 图标解码并发上限：启动加载大量模组时避免同时解码上百张图片
    /// （线程池压力与内存峰值），超出上限的解码请求排队等待。
    /// </summary>
    private static readonly SemaphoreSlim s_iconDecodeGate = new(4, 4);

    public void LoadIcon()
    {
        var generation = ++_iconLoadGeneration;
        var path = _mod.Manifest.IconPath;
        string? iconFullPath = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var candidate = Path.Combine(_mod.Directory.FullName, path);
            if (File.Exists(candidate))
                iconFullPath = candidate;
            else
                _logger.LogWarning("Icon file not found at \"{Path}\", using default icon for mod \"{Name}\"", candidate, _mod.Manifest.Name);
        }

        if (iconFullPath is null)
        {
            SetDefaultIcon();
            return;
        }

        // 后台线程解码并冻结：避免启动加载大量模组时在 UI 线程全量解码卡顿；
        // OnLoad 在 EndInit 时把像素读入内存并立即释放文件句柄，
        // 删除模组时目录不再被已显示的图标占用。
        _ = Task.Run(async () =>
        {
            await s_iconDecodeGate.WaitAsync();
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(iconFullPath);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                // 列表卡片图标仅显示约 60px：按 128px 上限解码（覆盖高 DPI），
                // 避免大尺寸封面图全量解码后常驻内存（每张可达数十 MB）。
                bmp.DecodePixelWidth = 128;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load icon for mod \"{Name}\", falling back to default icon", _mod.Manifest.Name);
                return null;
            }
            finally
            {
                s_iconDecodeGate.Release();
            }
        }).ContinueWith(t =>
        {
            if (generation != _iconLoadGeneration)
                return; // 过期结果不回写
            Icon = t.Result is { } bmp ? bmp : null;
            if (t.Result is null)
                SetDefaultIcon();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SetDefaultIcon()
    {
        try
        {
            var defaultBmp = new BitmapImage();
            defaultBmp.BeginInit();
            defaultBmp.UriSource = new Uri(@"..\Resources\Images\logo_icon.png", UriKind.Relative);
            defaultBmp.EndInit();
            Icon = defaultBmp;
        }
        catch
        {
            Icon = null;
        }
    }

    [RelayCommand]
    public async Task CheckForUpdatesAsync()
    {
        var nexusData = GetNexusData();
        if (nexusData == null || nexusData.ModId == 0)
            return;

        IsCheckingUpdate = true;
        NexusUpdateStatus = _localizationService["ModViewModel.CheckingUpdate"];

        try
        {
            if (!_nexusModsService.Initialized && !string.IsNullOrEmpty(_settingsService.NexusApiKey))
            {
                _nexusModsService.Init(_settingsService.NexusApiKey);
            }

            if (!_nexusModsService.Initialized)
            {
                NexusUpdateStatus = _localizationService["ModViewModel.NoNexusApiKey"];
                return;
            }

            var mod = await _nexusModsService.GetModAsync("helldivers2", nexusData.ModId.ToString());
            NexusModInfo = mod;

            var updateInfo = await _nexusModsService.CheckForUpdatesAsync(nexusData.ModId.ToString(), nexusData.Version);
            
            if (updateInfo.HasUpdate)
            {
                NexusUpdateStatus = _localizationService["ModViewModel.UpdateAvailable"].Replace("{version}", updateInfo.LatestVersion);
            }
            else
            {
                NexusUpdateStatus = _localizationService["ModViewModel.UpToDate"];
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check for updates for mod {ModName}", Name);
            NexusUpdateStatus = _localizationService["ModViewModel.CheckFailed"].Replace("{message}", ex.Message);
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    [RelayCommand]
    public void OpenNexusPage()
    {
        var nexusData = GetNexusData();
        if (nexusData == null || nexusData.ModId == 0)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
            {
                Message = _localizationService["ModViewModel.NoNexusData"]
            });
            return;
        }
        
        var url = $"https://www.nexusmods.com/helldivers2/mods/{nexusData.ModId}";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open Nexus page for mod {ModName}", Name);
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = _localizationService["ModViewModel.OpenNexusFailed"].Replace("{message}", ex.Message)
            });
        }
    }

    /// <summary>
    /// 显示版本兼容性详细信息
    /// </summary>
    [RelayCommand]
    public async Task ShowVersionDetail()
    {
        // 从未检查过时提示用户
        if (VersionStatus == ModVersionStatus.Unknown && LastVersionCheck == default)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
            {
                Message = _localizationService["ModViewModel.NoVersionCheckHint"]
            });
            return;
        }

        var statusText = VersionStatus switch
        {
            ModVersionStatus.Compatible => _localizationService["Converters.Compatible"],
            ModVersionStatus.Incompatible => _localizationService["Converters.Incompatible"],
            ModVersionStatus.Unknown => _localizationService["Converters.UnableToConfirm"],
            ModVersionStatus.Checking => _localizationService["Converters.Checking"],
            ModVersionStatus.Error => _localizationService["VersionCheck.CheckFailed"],
            _ => _localizationService["Converters.Unknown"]
        };

        // 深度分析 + 游戏参考解析是 CPU/IO 密集操作，放到后台线程执行，避免阻塞 UI
        var detailResult = await Task.Run(() =>
            _versionCheckService.CheckSingleModAsync(_mod, GameUnitVersion == 0 ? null : GameUnitVersion, includeDetailedAnalysis: true));
        if (detailResult is not null)
        {
            GameUnitVersion = detailResult.GameVersion;
            LastVersionCheck = detailResult.LastChecked;
            PatchUnits = new ObservableCollection<PatchUnitInfo>(detailResult.PatchUnits);
            DetailedAnalysis = detailResult.DetailedAnalysis;
            VersionStatus = detailResult.Status;
        }

        var patchUnits = PatchUnits.ToList();
        var detailedAnalysis = DetailedAnalysis;
        var effectiveStatus = VersionStatus;
        var effectiveGameVersion = GameUnitVersion;
        statusText = effectiveStatus switch
        {
            ModVersionStatus.Compatible => _localizationService["Converters.Compatible"],
            ModVersionStatus.Incompatible => _localizationService["Converters.Incompatible"],
            ModVersionStatus.Unknown => _localizationService["Converters.UnableToConfirm"],
            ModVersionStatus.Checking => _localizationService["Converters.Checking"],
            ModVersionStatus.Error => _localizationService["VersionCheck.CheckFailed"],
            _ => _localizationService["Converters.Unknown"]
        };

        var sb = new StringBuilder();

        // 标题行
        sb.AppendLine(_localizationService["ModViewModel.VersionInfoHeader"].Replace("{name}", Name));
        sb.AppendLine(_localizationService["ModViewModel.VersionStatusLabel"].Replace("{status}", statusText));
        sb.AppendLine(_localizationService["ModViewModel.VersionUnitLabel"].Replace("{unit}", $"0x{effectiveGameVersion:X8} ({effectiveGameVersion})"));
        sb.AppendLine(_localizationService["ModViewModel.VersionTimeLabel"].Replace("{time}", LastVersionCheck.ToString("yyyy-MM-dd HH:mm:ss")));
        sb.AppendLine();

        // Patch Units 部分
        if (patchUnits.Count > 0)
        {
            sb.AppendLine(_localizationService["ModViewModel.PatchUnitVersions"]);
            var distinctVersions = patchUnits.Select(p => p.Version).Distinct().ToList();
            foreach (var version in distinctVersions)
            {
                var count = patchUnits.Count(p => p.Version == version);
                var match = version == effectiveGameVersion ? _localizationService["ModViewModel.MatchOk"] : _localizationService["ModViewModel.MatchMismatch"];
                var fileSuffix = count == 1 ? "file" : "files";
                sb.AppendLine(string.Format("  {0} 0x{1:X8} ({1}) - {2} {3}", match, version, count, fileSuffix));
            }
            sb.AppendLine();
            sb.AppendLine(_localizationService["ModViewModel.DetailsHeader"]);
            foreach (var unit in patchUnits)
            {
                var match = unit.Version == effectiveGameVersion ? _localizationService["ModViewModel.MatchOk"] : _localizationService["ModViewModel.MatchMismatch"];
                sb.AppendLine(string.Format("  {0} {1}  0x{2:X16}  0x{3:X8}", match, unit.FileName, unit.FileId, unit.Version));
            }
        }
        else
        {
            sb.AppendLine(_localizationService["ModViewModel.NoUnitResources"]);
        }

        // Detailed Analysis 部分
        if (detailedAnalysis is { } analysis)
        {
            sb.AppendLine();
            sb.AppendLine("=== " + _localizationService["ModViewModel.DeepAnalysisTitle"] + " ===");
            sb.AppendLine(string.Format(_localizationService["ModViewModel.PatchFilesSummary"], analysis.TotalPatchFiles, analysis.FilesWithUnits));
            sb.AppendLine(string.Format(_localizationService["ModViewModel.FileHealthSummary"],
                analysis.HealthyFileCount, analysis.WarningFileCount, analysis.CorruptedFileCount));

            if (analysis.HasStructuralIssues)
                sb.AppendLine("! " + _localizationService["ModViewModel.WarningStructuralIssues"]);

            if (analysis.HasCompanionFileIssues)
                sb.AppendLine("! " + _localizationService["ModViewModel.WarningCompanionFilesMissing"]);

            if (analysis.HasUnitStructuralIssues)
                sb.AppendLine("! " + _localizationService["ModViewModel.WarningUnitStructuralIssues"]);

            if (analysis.HasGpuResourceIssues)
                sb.AppendLine("! " + _localizationService["ModViewModel.WarningGpuResourceIssues"]);

            if (analysis.HasStreamResourceIssues)
                sb.AppendLine("! " + _localizationService["ModViewModel.WarningStreamResourceIssues"]);

            // Per-file details
            foreach (var pf in analysis.PatchFiles)
            {
                sb.AppendLine();
                sb.AppendLine(string.Format("--- {0} ---", pf.FileName));
                sb.AppendLine(string.Format(_localizationService["ModViewModel.FileInfoSummary"], pf.FileSize, pf.HealthStatus));
                sb.AppendLine(string.Format(_localizationService["ModViewModel.FileHeaderInfo"],
                    pf.HeaderValid ? _localizationService["ModViewModel.Valid"] : _localizationService["ModViewModel.Invalid"],
                    pf.FileEntriesInBounds ? _localizationService["CreateWizard.Yes"] : _localizationService["CreateWizard.No"]));
                sb.AppendLine(string.Format(_localizationService["ModViewModel.FileTypeCounts"],
                    pf.NumTypes, pf.NumFiles, pf.TotalResources));
                sb.AppendLine(string.Format(_localizationService["ModViewModel.FileTypeDistributionInfo"],
                    pf.TypeDistributionValid ? _localizationService["ModViewModel.Valid"] : _localizationService["ModViewModel.Invalid"],
                    pf.TypeDistributionIssueCount));
                sb.AppendLine(string.Format(_localizationService["ModViewModel.FileMainBoundsInfo"],
                    pf.MainDataBoundsValid ? _localizationService["ModViewModel.Valid"] : _localizationService["ModViewModel.Invalid"],
                    pf.MainDataIssueCount));
                sb.AppendLine(string.Format(_localizationService["ModViewModel.FileEntryIndexInfo"],
                    pf.EntryIndicesValid ? _localizationService["ModViewModel.Valid"] : _localizationService["ModViewModel.Invalid"],
                    pf.EntryIndexIssueCount));
                sb.AppendLine(string.Format(_localizationService["ModViewModel.FileCompanionInfo"],
                    pf.HasGpuResources ? _localizationService["ModViewModel.Present"] : _localizationService["ModViewModel.Missing"],
                    pf.RequiresGpuResources ? _localizationService["ModViewModel.Required"] : _localizationService["ModViewModel.Optional"],
                    pf.HasStream ? _localizationService["ModViewModel.Present"] : _localizationService["ModViewModel.Missing"],
                    pf.RequiresStream ? _localizationService["ModViewModel.Required"] : _localizationService["ModViewModel.Optional"]));
                sb.AppendLine(string.Format(_localizationService["ModViewModel.FileGpuBoundsInfo"],
                    pf.GpuResourceBoundsValid ? _localizationService["ModViewModel.Valid"] : _localizationService["ModViewModel.Invalid"],
                    pf.GpuResourceIssueCount, pf.GpuAlignmentIssueCount));
                sb.AppendLine(string.Format(_localizationService["ModViewModel.FileStreamBoundsInfo"],
                    pf.StreamBoundsValid ? _localizationService["ModViewModel.Valid"] : _localizationService["ModViewModel.Invalid"],
                    pf.StreamIssueCount, pf.StreamAlignmentIssueCount));

                if (pf.UnitDetails.Count > 0)
                {
                    sb.AppendLine("  " + _localizationService["ModViewModel.UnitInternalStructure"]);
                    foreach (var unit in pf.UnitDetails)
                    {
                        sb.AppendLine(string.Format("    #{0} [0x{1:X16}] v{2:X8}",
                            unit.EntryIndex, unit.FileId, unit.Version));
                        sb.AppendLine(string.Format(_localizationService["ModViewModel.UnitSizeInfo"],
                            unit.DataSize, unit.ExpectedDataSize,
                            unit.DeclaredSizeMatchesInternal ? _localizationService["CreateWizard.Yes"] : _localizationService["CreateWizard.No"]));
                        sb.AppendLine(string.Format(_localizationService["ModViewModel.UnitLodInfo"],
                            unit.LODGroupOffset, unit.LODGroupSize, unit.LODGroupInBounds ? _localizationService["CreateWizard.Yes"] : _localizationService["CreateWizard.No"]));
                        if (unit.LayoutFormatChecked)
                        {
                            sb.AppendLine(string.Format(_localizationService["ModViewModel.UnitLayoutInfo"],
                                unit.LayoutFormatChecked, unit.LayoutFormatValid ? _localizationService["CreateWizard.Yes"] : _localizationService["CreateWizard.No"],
                                unit.LayoutFormatIssueCount));
                        }
                        if (unit.GpuStructureChecked)
                        {
                            sb.AppendLine(string.Format(_localizationService["ModViewModel.UnitGpuStructureInfo"],
                                unit.GpuStreamCount,
                                unit.GpuStructureValid ? _localizationService["CreateWizard.Yes"] : _localizationService["CreateWizard.No"],
                                unit.GpuStructureIssueCount,
                                unit.UnknownGpuComponentCount));
                        }
                        if (!string.IsNullOrEmpty(unit.Warning))
                            sb.AppendLine(string.Format(_localizationService["ModViewModel.UnitWarning"], unit.Warning));
                    }
                }

                if (!string.IsNullOrEmpty(pf.Message))
                    sb.AppendLine(string.Format(_localizationService["ModViewModel.FileNote"], pf.Message));
            }
        }

        WeakReferenceMessenger.Default.Send(new VersionCheckDetailMessage
        {
            ModName = Name,
            Status = effectiveStatus,
            GameVersion = effectiveGameVersion,
            UnitsMissingGameReference = detailResult?.UnitsMissingGameReference ?? new HashSet<long>(),
            LastChecked = LastVersionCheck,
            PatchUnits = patchUnits,
            Analysis = detailedAnalysis ?? new ModDetailedAnalysis(),
            FullReport = sb.ToString(),
            ModDirectory = _mod.Directory,
            RefreshAsync = ShowVersionDetail
        });

        if (detailResult is not null)
            VersionCheckRefreshed?.Invoke(this, EventArgs.Empty);
    }

    public bool HasNexusData => GetNexusData() != null && GetNexusData()!.ModId != 0;

    private V1ModManifest.NexusDataModel? GetNexusData()
    {
        if (_mod.Manifest is V1ModManifest v1Manifest)
        {
            return v1Manifest.NexusData;
        }
        return null;
    }

    public void Dispose()
    {
        _iconLoadGeneration++;
        Icon = null;
        _mod.PropertyChanged -= ModData_PropertyChanged;
    }
}
