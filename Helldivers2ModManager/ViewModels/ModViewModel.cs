using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Models.Nexus;
using Helldivers2ModManager.Services;
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

    public Guid Guid => _mod.Manifest.Guid;

    public string Name => _mod.Manifest.Name;

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

    public Brush ConflictStatusBrush => HasConflict
        ? new SolidColorBrush(Color.FromRgb(220, 80, 55))
        : new SolidColorBrush(Color.FromRgb(40, 160, 95));

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

    public ModViewModel(ModData mod, ILogger logger, SettingsService settingsService, INexusModsService nexusModsService, LocalizationService localizationService, VersionCheckService versionCheckService)
    {
        _mod = mod;
        _logger = logger;
        _settingsService = settingsService;
        _nexusModsService = nexusModsService;
        _localizationService = localizationService;
        _versionCheckService = versionCheckService;

        _mod.PropertyChanged += ModData_PropertyChanged;

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

    public void LoadIcon()
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            var path = _mod.Manifest.IconPath;
            if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(path))
                bmp.UriSource = new Uri(@"..\Resources\Images\logo_icon.png", UriKind.Relative);
            else
            {
                var iconFullPath = Path.Combine(_mod.Directory.FullName, path);
                if (File.Exists(iconFullPath))
                {
                    bmp.UriSource = new Uri(iconFullPath);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                }
                else
                {
                    _logger.LogWarning("Icon file not found at \"{Path}\", using default icon for mod \"{Name}\"", iconFullPath, _mod.Manifest.Name);
                    bmp.UriSource = new Uri(@"..\Resources\Images\logo_icon.png", UriKind.Relative);
                }
            }
            bmp.EndInit();
            Icon = bmp;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load icon for mod \"{Name}\", falling back to default icon", _mod.Manifest.Name);
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
            ModVersionStatus.Error => _localizationService["Converters.CheckFailed"],
            _ => _localizationService["Converters.Unknown"]
        };

        var detailResult = await _versionCheckService.CheckSingleModAsync(_mod, GameUnitVersion == 0 ? null : GameUnitVersion, includeDetailedAnalysis: true);
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
            ModVersionStatus.Error => _localizationService["Converters.CheckFailed"],
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
                    pf.FileEntriesInBounds ? _localizationService["ModViewModel.Yes"] : _localizationService["ModViewModel.No"]));
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
                            unit.DeclaredSizeMatchesInternal ? _localizationService["ModViewModel.Yes"] : _localizationService["ModViewModel.No"]));
                        sb.AppendLine(string.Format(_localizationService["ModViewModel.UnitLodInfo"],
                            unit.LODGroupOffset, unit.LODGroupSize, unit.LODGroupInBounds ? _localizationService["ModViewModel.Yes"] : _localizationService["ModViewModel.No"]));
                        if (unit.LayoutFormatChecked)
                        {
                            sb.AppendLine(string.Format(_localizationService["ModViewModel.UnitLayoutInfo"],
                                unit.LayoutFormatChecked, unit.LayoutFormatValid ? _localizationService["ModViewModel.Yes"] : _localizationService["ModViewModel.No"],
                                unit.LayoutFormatIssueCount));
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
        _mod.PropertyChanged -= ModData_PropertyChanged;
    }
}
