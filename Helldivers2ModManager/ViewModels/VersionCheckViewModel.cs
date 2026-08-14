using CommunityToolkit.Mvvm.ComponentModel;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.IO;

namespace Helldivers2ModManager.ViewModels;

internal enum VersionAutoCheckReason
{
    None,
    ModChanged,
    GameExeUpdated
}

/// <summary>
/// 版本检查视图模型 —— 封装版本兼容性检查的所有逻辑和状态
/// 从 DashboardPageViewModel 中拆分出来，独立管理版本检测相关的 UI 状态和业务逻辑
/// </summary>
[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class VersionCheckViewModel : ObservableObject
{
    private static readonly Dictionary<Guid, DateTime> s_knownModTimestamps = [];
    private static readonly EnumerationOptions s_modTimestampEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private readonly ILogger<VersionCheckViewModel> _logger;
    private readonly VersionCheckService _versionCheckService;
    private readonly VersionCheckRepository _versionCheckRepository;
    private readonly ModService _modService;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly BackgroundTaskService _backgroundTaskService;

    [ObservableProperty]
    private bool _isCheckingVersion;

    [ObservableProperty]
    private string _versionCheckSummary = string.Empty;

    [ObservableProperty]
    private int _compatibleModCount;

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

    public VersionCheckViewModel(
        ILogger<VersionCheckViewModel> logger,
        VersionCheckService versionCheckService,
        VersionCheckRepository versionCheckRepository,
        ModService modService,
        SettingsService settingsService,
        LocalizationService localizationService,
        BackgroundTaskService backgroundTaskService)
    {
        _logger = logger;
        _versionCheckService = versionCheckService;
        _versionCheckRepository = versionCheckRepository;
        _modService = modService;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _backgroundTaskService = backgroundTaskService;
    }

    /// <summary>
    /// 检查所有模组的版本兼容性。
    /// 首次点击时执行全量扫描建立参考版本，后续只检查新增/变动的模组。
    /// 检查结果仅保留状态字段，详细诊断在用户点击详情时按需扫描，避免常驻占用内存。
    /// </summary>
    public async Task CheckVersionCompatibilityAsync(
        ObservableCollection<ModViewModel> mods,
        bool forceFullScan = false)
    {
        if (IsCheckingVersion)
            return;

        IsCheckingVersion = true;
        VersionCheckSummary = _localizationService["VersionCheck.ScanningMods"];

        try
        {
            bool needsFullScan = forceFullScan || !VersionCheckService.HasCachedReference;

            // 检测游戏 exe 是否已更新：若 exe 文件时间变化，说明游戏已更新，必须全量重新扫描
            if (!needsFullScan)
            {
                var gameExePath = GetGameExePath();
                if (File.Exists(gameExePath))
                {
                    var currentExeTime = new FileInfo(gameExePath).LastWriteTimeUtc;
                    var lastExeTime = _versionCheckRepository.GetGameExeLastWriteTime(_settingsService.StorageDirectory);
                    if (lastExeTime != DateTime.MinValue && currentExeTime != lastExeTime)
                    {
                        _logger.LogInformation("检测到游戏 exe 已更新 (上次: {Last}, 当前: {Current})，强制全量扫描",
                            lastExeTime, currentExeTime);
                        needsFullScan = true;
                    }
                }
            }

            // 只有增量检查需要提前计算时间戳（后台一次）；全量扫描无需等待文件枚举
            Dictionary<Guid, DateTime>? modTimestamps = null;
            if (!needsFullScan)
            {
                modTimestamps = await _backgroundTaskService.RunAsync(
                    _localizationService["SettingsPage.VersionCheck"],
                    VersionCheckSummary,
                    (_, _) => Task.FromResult(BuildModTimestamps(mods)),
                    VersionCheckSummary);
                var changedMods = GetNewOrChangedMods(mods, modTimestamps).ToList();
                needsFullScan = changedMods.Count == mods.Count;
            }

            if (needsFullScan)
            {
                await FullScanAsync(mods);
            }
            else
            {
                await IncrementalCheckAsync(mods, modTimestamps!);
            }

            UpdateStatistics(mods);
            // 立即展示结果摘要；时间戳枚举与数据库保存放到后台完成，避免整体模组规模越大越慢
            UpdateSummaryText();

            await _backgroundTaskService.RunAsync(
                _localizationService["SettingsPage.VersionCheck"],
                VersionCheckSummary,
                async (_, _) =>
                {
                    var timestamps = modTimestamps ?? BuildModTimestamps(mods);
                    UpdateModTimestampTracking(mods, timestamps);
                    await SaveVersionCheckResultsToDatabaseAsync(mods, timestamps);
                },
                VersionCheckSummary);

            if (needsFullScan)
                await UpdateGameExeTimestampAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "版本兼容性检查失败");
            VersionCheckSummary = _localizationService["VersionCheck.CheckFailed"];
        }
        finally
        {
            IsCheckingVersion = false;
        }
    }

    /// <summary>
    /// 单个模组重新检测后，同步统计、文件时间戳与数据库缓存。
    /// </summary>
    public async Task RefreshAfterSingleModCheckAsync(ObservableCollection<ModViewModel> mods, ModViewModel changedVm)
    {
        try
        {
            UpdateStatistics(mods);
            // 只计算/保存变化的单个模组：避免整体模组规模越大越慢，也避免重复保存全部模组
            await Task.Run(async () =>
            {
                var timestamps = BuildModTimestamps([changedVm]);
                s_knownModTimestamps[changedVm.Guid] = timestamps[changedVm.Guid];
                await SaveVersionCheckResultsToDatabaseAsync([changedVm], timestamps);
            });
            UpdateSummaryText();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to synchronize a refreshed mod version result");
        }
    }

    /// <summary>
    /// 新模组添加后，如果启用了自动检查，仅扫描该新增模组（使用缓存的参考版本）
    /// </summary>
    public async Task CheckSingleModOnAddAsync(ModData mod, ObservableCollection<ModViewModel> mods)
    {
        try
        {
            if (!_settingsService.AutoCheckVersionOnStartup)
                return;

            _logger.LogInformation("New mod \"{Name}\", checking version compatibility...", mod.Manifest.Name);
            var result = await _versionCheckService.CheckSingleModAsync(mod);
            if (result is not null)
            {
                var vm = mods.FirstOrDefault(v => v.Guid == mod.Manifest.Guid);
                if (vm is not null)
                {
                    vm.GameUnitVersion = result.GameVersion;
                    vm.LastVersionCheck = result.LastChecked;
                    vm.VersionStatus = result.Status;
                }

                VersionCheckSummary = result.Status == ModVersionStatus.Incompatible
                    ? $"{_localizationService["VersionCheck.IncompatibleFound"]}{mod.Manifest.Name}"
                    : $"{_localizationService["VersionCheck.NewModPrefix"]}{mod.Manifest.Name}{_localizationService["VersionCheck.NewModSuffix"]}";
                OnPropertyChanged(nameof(HasVersionCheckResult));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing newly added mod \"{Name}\"", mod.Manifest.Name);
        }
    }

    /// <summary>
    /// 从数据库加载已缓存的版本检测结果并应用到每个 ModViewModel
    /// </summary>
    public void LoadCachedResults(ObservableCollection<ModViewModel> mods)
    {
        try
        {
            if (!_settingsService.Initialized || string.IsNullOrEmpty(_settingsService.StorageDirectory))
                return;

            var cached = _versionCheckRepository.LoadAll(_settingsService.StorageDirectory);
            foreach (var vm in mods)
            {
                if (cached.TryGetValue(vm.Guid, out var entry))
                {
                    vm.VersionStatus = entry.Status;
                    vm.GameUnitVersion = entry.GameVersion;
                    vm.LastVersionCheck = entry.LastChecked;
                    if (entry.ModLastWriteTimeUtc != DateTime.MinValue)
                        s_knownModTimestamps[vm.Guid] = entry.ModLastWriteTimeUtc;
                }
            }

            UpdateStatistics(mods);

            if (cached.Count > 0)
            {
                VersionCheckSummary = IncompatibleModCount > 0
                    ? _localizationService["VersionCheck.IncompatibleCached"].Replace("{IncompatibleModCount}", IncompatibleModCount.ToString())
                    : _localizationService["VersionCheck.AllCompatibleCached"].Replace("{CompatibleModCount}", CompatibleModCount.ToString());
                OnPropertyChanged(nameof(HasVersionCheckResult));
            }

            _logger.LogInformation("Loaded {Count} version check results from database", cached.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "从数据库加载版本检测结果失败");
        }
    }

    /// <summary>
    /// 判断是否需要自动触发版本检查（启动时检测新增/变动模组或游戏 exe 更新）
    /// </summary>
    public bool ShouldAutoCheck(ObservableCollection<ModViewModel> mods)
    {
        return GetAutoCheckReason(mods) != VersionAutoCheckReason.None;
    }

    public VersionAutoCheckReason GetAutoCheckReason(ObservableCollection<ModViewModel> mods)
    {
        if (!_settingsService.AutoCheckVersionOnStartup || mods.Count == 0)
            return VersionAutoCheckReason.None;

        var changedMods = GetNewOrChangedMods(mods).ToList();
        if (changedMods.Count > 0)
            return VersionAutoCheckReason.ModChanged;

        // 检测游戏 exe 是否已更新
        var gameExePath = GetGameExePath();
        if (File.Exists(gameExePath))
        {
            var currentExeTime = new FileInfo(gameExePath).LastWriteTimeUtc;
            var lastExeTime = _versionCheckRepository.GetGameExeLastWriteTime(_settingsService.StorageDirectory);
            if (lastExeTime != DateTime.MinValue && currentExeTime != lastExeTime)
                return VersionAutoCheckReason.GameExeUpdated;
        }

        return VersionAutoCheckReason.None;
    }

    #region Private Methods

    private async Task FullScanAsync(ObservableCollection<ModViewModel> mods)
    {
        // 全量扫描（含游戏参考解析）在后台线程执行，结果回 UI 线程应用
        var results = await _backgroundTaskService.RunAsync(
            _localizationService["SettingsPage.VersionCheck"],
            _localizationService["VersionCheck.ScanningMods"],
            async (context, _) =>
            {
                context.Report(_localizationService["VersionCheck.ScanningMods"], 0, false);
                return await _versionCheckService.CheckAllModsAsync(_modService.Mods);
            },
            _localizationService["VersionCheck.ScanningMods"]);

        foreach (var vm in mods)
        {
            if (results.TryGetValue(vm.Guid, out var result))
            {
                vm.GameUnitVersion = result.GameVersion;
                vm.LastVersionCheck = result.LastChecked;
                vm.VersionStatus = result.Status;
            }
        }
    }

    private async Task IncrementalCheckAsync(ObservableCollection<ModViewModel> mods, IReadOnlyDictionary<Guid, DateTime> modTimestamps)
    {
        var changedMods = GetNewOrChangedMods(mods, modTimestamps).ToList();
        if (changedMods.Count == 0)
            return;

        VersionCheckSummary = _localizationService["VersionCheck.CheckingChanged"].Replace("{changedModCount}", changedMods.Count.ToString());
        // 单模组检查（含游戏参考解析）在后台线程执行，结果回 UI 线程应用
        var results = await _backgroundTaskService.RunAsync(
            _localizationService["SettingsPage.VersionCheck"],
            VersionCheckSummary,
            async (context, _) =>
            {
                var collected = new Dictionary<Guid, ModVersionCheckResult>();
                for (var i = 0; i < changedMods.Count; i++)
                {
                    var vm = changedMods[i];
                    context.Report(
                        vm.Name,
                        changedMods.Count > 0 ? (double)i / changedMods.Count : 1,
                        false);
                    var result = await _versionCheckService.CheckSingleModAsync(vm.Data);
                    if (result is not null)
                        collected[vm.Guid] = result;
                }
                return collected;
            },
            VersionCheckSummary);

        foreach (var vm in changedMods)
        {
            if (results.TryGetValue(vm.Guid, out var result))
            {
                vm.GameUnitVersion = result.GameVersion;
                vm.LastVersionCheck = result.LastChecked;
                vm.VersionStatus = result.Status;
            }
        }
    }

    private void UpdateStatistics(ObservableCollection<ModViewModel> mods)
    {
        int compatible = 0, incompatible = 0;
        foreach (var vm in mods)
        {
            if (vm.VersionStatus == Models.ModVersionStatus.Compatible)
                compatible++;
            else if (vm.VersionStatus == Models.ModVersionStatus.Incompatible)
                incompatible++;
        }

        CompatibleModCount = compatible;
        IncompatibleModCount = incompatible;
        OnPropertyChanged(nameof(HasIncompatibleMods));
    }

    private void UpdateSummaryText()
    {
        if (IncompatibleModCount > 0)
        {
            VersionCheckSummary = _localizationService["VersionCheck.IncompatibleFoundMsg"].Replace("{IncompatibleModCount}", IncompatibleModCount.ToString());
        }
        else if (CompatibleModCount > 0)
        {
            VersionCheckSummary = _localizationService["VersionCheck.AllCompatible"].Replace("{CompatibleModCount}", CompatibleModCount.ToString());
        }
        else
        {
            VersionCheckSummary = _localizationService["VersionCheck.NoneCheckable"];
        }

        OnPropertyChanged(nameof(HasVersionCheckResult));
    }

    /// <summary>
    /// 一次性计算所有模组的最新修改时间（递归枚举文件，IO 密集）。应在后台线程调用。
    /// </summary>
    private static Dictionary<Guid, DateTime> BuildModTimestamps(IEnumerable<ModViewModel> mods)
    {
        var timestamps = new Dictionary<Guid, DateTime>();
        foreach (var vm in mods)
        {
            if (!timestamps.ContainsKey(vm.Guid))
                timestamps[vm.Guid] = GetModContentLastWriteTimeUtc(vm.Data.Directory);
        }
        return timestamps;
    }

    /// <summary>
    /// 获取本次新增或文件变动的模组（与上次跟踪快照对比）
    /// </summary>
    private IEnumerable<ModViewModel> GetNewOrChangedMods(ObservableCollection<ModViewModel> mods)
    {
        foreach (var vm in mods)
        {
            if (!s_knownModTimestamps.TryGetValue(vm.Guid, out var lastTime))
            {
                yield return vm;
            }
            else if (GetModContentLastWriteTimeUtc(vm.Data.Directory) != lastTime)
            {
                yield return vm;
            }
        }
    }

    /// <summary>
    /// 获取本次新增或文件变动的模组（复用已计算的时间戳，避免重复枚举文件）
    /// </summary>
    private IEnumerable<ModViewModel> GetNewOrChangedMods(ObservableCollection<ModViewModel> mods, IReadOnlyDictionary<Guid, DateTime> timestamps)
    {
        foreach (var vm in mods)
        {
            if (!s_knownModTimestamps.TryGetValue(vm.Guid, out var lastTime))
            {
                yield return vm;
            }
            else if (!timestamps.TryGetValue(vm.Guid, out var current) || current != lastTime)
            {
                yield return vm;
            }
        }
    }

    /// <summary>
    /// 更新模组跟踪快照，记录当前所有模组的 GUID 和目录修改时间
    /// </summary>
    private void UpdateModTimestampTracking(IEnumerable<ModViewModel> mods, IReadOnlyDictionary<Guid, DateTime> timestamps)
    {
        var currentGuids = mods.Select(static vm => vm.Guid).ToHashSet();
        foreach (var guid in s_knownModTimestamps.Keys.Where(g => !currentGuids.Contains(g)).ToList())
            s_knownModTimestamps.Remove(guid);

        foreach (var vm in mods)
        {
            if (timestamps.TryGetValue(vm.Guid, out var lastWrite))
                s_knownModTimestamps[vm.Guid] = lastWrite;
        }
    }

    /// <summary>
    /// 将当前所有 ModViewModel 的版本检测状态持久化到数据库
    /// </summary>
    private async Task SaveVersionCheckResultsToDatabaseAsync(IEnumerable<ModViewModel> mods, IReadOnlyDictionary<Guid, DateTime> timestamps)
    {
        try
        {
            if (!_settingsService.Initialized || string.IsNullOrEmpty(_settingsService.StorageDirectory))
                return;

            var results = new Dictionary<Guid, (ModVersionStatus Status, uint GameVersion, DateTime LastChecked, DateTime ModLastWriteTimeUtc)>();
            foreach (var vm in mods)
            {
                if (vm.VersionStatus != ModVersionStatus.Unknown || vm.LastVersionCheck != default)
                {
                    results[vm.Guid] = (
                        vm.VersionStatus,
                        vm.GameUnitVersion,
                        vm.LastVersionCheck,
                        timestamps.TryGetValue(vm.Guid, out var lastWrite)
                            ? lastWrite
                            : GetModContentLastWriteTimeUtc(vm.Data.Directory));
                }
            }

            await _versionCheckRepository.SaveAllAsync(_settingsService.StorageDirectory, results);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存版本检测结果到数据库失败");
        }
    }

    /// <summary>
    /// 获取 Helldivers 2 游戏可执行文件的完整路径
    /// </summary>
    private string GetGameExePath()
    {
        return Path.Combine(_settingsService.GameDirectory, "bin", "helldivers2.exe");
    }

    private static DateTime GetModContentLastWriteTimeUtc(DirectoryInfo directory)
    {
        var latest = directory.Exists ? directory.LastWriteTimeUtc : DateTime.MinValue;
        try
        {
            foreach (var file in directory.EnumerateFiles("*", s_modTimestampEnumerationOptions))
            {
                if (file.LastWriteTimeUtc > latest)
                    latest = file.LastWriteTimeUtc;
            }
        }
        catch (IOException)
        {
            // A concurrent mod update may temporarily hide a file; the directory timestamp remains a safe fallback.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep the best timestamp collected so far when a nested folder is unreadable.
        }

        return latest;
    }

    /// <summary>
    /// 更新数据库中游戏 exe 的最后写入时间，用于检测游戏版本变化
    /// </summary>
    private async Task UpdateGameExeTimestampAsync()
    {
        try
        {
            var exePath = GetGameExePath();
            if (File.Exists(exePath))
            {
                var lastWrite = new FileInfo(exePath).LastWriteTimeUtc;
                await _versionCheckRepository.UpdateGameExeLastWriteTimeAsync(_settingsService.StorageDirectory, lastWrite);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新游戏 exe 时间戳失败");
        }
    }

    #endregion
}
