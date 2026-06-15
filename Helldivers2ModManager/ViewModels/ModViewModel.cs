using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Models.Nexus;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Nexus;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    /// 是否正在检查版本兼容性
    /// </summary>
    [ObservableProperty]
    private bool _isCheckingVersion;

    /// <summary>
    /// 版本检测详细信息（用于弹窗显示）
    /// </summary>
    [ObservableProperty]
    private ModVersionCheckResult? _versionCheckResult;

    public ModData Data => _mod;

    public event Action? OptionsChanged;

    public void OnOptionsChanged()
    {
        OptionsChanged?.Invoke();
    }

    public string[]? LegacyOptions { get; }

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

    public ModOptionViewModel[]? Options { get; }

    private readonly ModData _mod;

    public ModViewModel(ModData mod, ILogger logger, SettingsService settingsService, INexusModsService nexusModsService)
    {
        _mod = mod;
        _logger = logger;
        _settingsService = settingsService;
        _nexusModsService = nexusModsService;

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

    public void LoadIcon()
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        var path = _mod.Manifest.IconPath;
        if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(path))
            bmp.UriSource = new Uri(@"..\Resources\Images\logo_icon.png", UriKind.Relative);
        else
        {
            bmp.UriSource = new Uri(Path.Combine(_mod.Directory.FullName, path));
            bmp.CacheOption = BitmapCacheOption.OnLoad;
        }
        bmp.EndInit();
        Icon = bmp;
    }

    [RelayCommand]
    public async Task CheckForUpdatesAsync()
    {
        var nexusData = GetNexusData();
        if (nexusData == null || nexusData.ModId == 0)
            return;

        IsCheckingUpdate = true;
        NexusUpdateStatus = "正在检查更新...";

        try
        {
            if (!_nexusModsService.Initialized && !string.IsNullOrEmpty(_settingsService.NexusApiKey))
            {
                _nexusModsService.Init(_settingsService.NexusApiKey);
            }

            if (!_nexusModsService.Initialized)
            {
                NexusUpdateStatus = "未配置 Nexus API Key";
                return;
            }

            var mod = await _nexusModsService.GetModAsync("helldivers2", nexusData.ModId.ToString());
            NexusModInfo = mod;

            var updateInfo = await _nexusModsService.CheckForUpdatesAsync(nexusData.ModId.ToString(), nexusData.Version);
            
            if (updateInfo.HasUpdate)
            {
                NexusUpdateStatus = $"有更新可用: {updateInfo.LatestVersion}";
            }
            else
            {
                NexusUpdateStatus = "已是最新版本";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check for updates for mod {ModName}", Name);
            NexusUpdateStatus = $"检查更新失败: {ex.Message}";
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
                Message = "此模组未配置 Nexus 数据"
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
                Message = $"无法打开 Nexus 页面: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// 显示版本兼容性详细信息
    /// </summary>
    [RelayCommand]
    public void ShowVersionDetail()
    {
        var result = VersionCheckResult;
        if (result == null)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
            {
                Message = "Please run version check first from the bottom toolbar."
            });
            return;
        }

        CompatibleCheckInfo info = new()
        {
            VersionStatus = result.Status,
            GameUnitVersion = result.GameVersion,
            LastChecked = result.LastChecked,
            PatchUnits = result.PatchUnits.ToList(),
            ModName = Name,
            ErrorMessage = result.ErrorMessage,
            DetailedAnalysis = result.DetailedAnalysis
        };

        WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
        {
            Message = info.ToString()
        });
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