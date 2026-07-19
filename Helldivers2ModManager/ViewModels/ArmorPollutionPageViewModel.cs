using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Helldivers2ModManager.ViewModels;

// Armor pollution detection is temporarily disabled.
// [RegisterService(ServiceLifetime.Transient)]
internal sealed partial class ArmorPollutionPageViewModel : PageViewModelBase
{
    private readonly ILogger<ArmorPollutionPageViewModel> _logger;
    private readonly Lazy<NavigationStore> _navStore;
    private readonly ModService _modService;
    private readonly SettingsService _settingsService;
    private readonly VersionCheckService _versionCheckService;
    private readonly BackgroundTaskService _backgroundTaskService;
    private readonly LocalizationService _localizationService;

    public override string Title => _localizationService["ArmorPollutionPage.Title"];

    public ObservableCollection<ArmorPollutionItem> Items { get; } = [];

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    public int PollutionCount => Items.Count;
    public int AffectedModCount { get; private set; }
    public int ScannedModCount { get; private set; }
    public int ScannedPatchCount { get; private set; }
    public int ScannedUnitCount { get; private set; }
    public bool HasPollution => Items.Count > 0;
    public Visibility NoPollutionVisibility => Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PollutionVisibility => Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public ArmorPollutionPageViewModel(
        ILogger<ArmorPollutionPageViewModel> logger,
        IServiceProvider provider,
        ModService modService,
        SettingsService settingsService,
        VersionCheckService versionCheckService,
        BackgroundTaskService backgroundTaskService,
        LocalizationService localizationService)
    {
        _logger = logger;
        _navStore = new Lazy<NavigationStore>(provider.GetRequiredService<NavigationStore>);
        _modService = modService;
        _settingsService = settingsService;
        _versionCheckService = versionCheckService;
        _backgroundTaskService = backgroundTaskService;
        _localizationService = localizationService;

        _localizationService.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PollutionCount));
            OnPropertyChanged(nameof(HasPollution));
            OnPropertyChanged(nameof(NoPollutionVisibility));
            OnPropertyChanged(nameof(PollutionVisibility));
        };

        _ = RefreshAsync();
    }

    [RelayCommand]
    private void GoBack()
    {
        _navStore.Value.Navigate<DashboardPageViewModel>();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task Refresh()
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_settingsService.Initialized == false || !_modService.Initialized)
            return;

        if (IsScanning)
            return;

        IsScanning = true;
        SummaryText = _localizationService["ArmorPollutionPage.Scanning"];
        var backgroundTask = _backgroundTaskService.Add(
            _localizationService["BackgroundTasksPage.TaskTypeArmorPollutionScan"],
            SummaryText);

        try
        {
            var enabledMods = _modService.Mods
                .Where(static mod => mod.Enabled)
                .ToArray();
            var result = await ScanEnabledModUnitsAsync(enabledMods);
            ApplyResult(result);

            _backgroundTaskService.Complete(backgroundTask, SummaryText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan enabled mod armor pollution");
            SummaryText = _localizationService["ArmorPollutionPage.ScanFailed"];
            _backgroundTaskService.Fail(backgroundTask, ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task<ArmorPollutionScanResult> ScanEnabledModUnitsAsync(IReadOnlyList<ModData> enabledMods)
    {
        var unitOccurrences = new Dictionary<long, List<ArmorUnitOccurrence>>();
        var scannedPatchCount = 0;
        var scannedUnitCount = 0;

        foreach (var mod in enabledMods)
        {
            IReadOnlyList<FileInfo> patchFiles;
            try
            {
                patchFiles = _modService.GetSelectedPatchFiles(mod);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to enumerate selected patch files for enabled mod {ModName}", mod.Manifest.Name);
                continue;
            }

            foreach (var patchFile in patchFiles)
            {
                scannedPatchCount++;
                var units = await _versionCheckService.ExtractUnitVersionsFromPatchFileAsync(patchFile);
                foreach (var unit in units)
                {
                    scannedUnitCount++;
                    if (!unitOccurrences.TryGetValue(unit.FileId, out var occurrences))
                    {
                        occurrences = [];
                        unitOccurrences.Add(unit.FileId, occurrences);
                    }

                    occurrences.Add(new ArmorUnitOccurrence(
                        mod.Manifest.Guid,
                        mod.Manifest.Name,
                        patchFile.Name,
                        unit.Version,
                        unit.DataSize));
                }
            }
        }

        var pollutedUnitIds = unitOccurrences
            .Where(static pair => pair.Value.Select(static item => item.ModGuid).Distinct().Count() > 1)
            .Select(static pair => pair.Key)
            .ToArray();
        var displayNames = await _versionCheckService.ResolveGameUnitDisplayNamesAsync(pollutedUnitIds);
        var groups = pollutedUnitIds
            .Select(unitId => new ArmorPollutionGroup(
                unitId,
                displayNames.TryGetValue(unitId, out var name) ? name : string.Empty,
                unitOccurrences[unitId]))
            .OrderBy(static group => string.IsNullOrWhiteSpace(group.DisplayName) ? $"0x{group.UnitId:X16}" : group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ArmorPollutionScanResult(
            enabledMods.Count,
            scannedPatchCount,
            scannedUnitCount,
            groups);
    }

    private void ApplyResult(ArmorPollutionScanResult result)
    {
        Items.Clear();
        foreach (var group in result.Groups)
        {
            var distinctMods = group.Occurrences
                .GroupBy(static occurrence => occurrence.ModGuid)
                .Select(static modGroup => modGroup.First())
                .OrderBy(static occurrence => occurrence.ModName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var modNames = string.Join(", ", distinctMods.Select(static occurrence => occurrence.ModName));
            var patchCount = group.Occurrences
                .Select(static occurrence => $"{occurrence.ModGuid:N}:{occurrence.PatchFileName}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            Items.Add(new ArmorPollutionItem
            {
                ResourceName = string.IsNullOrWhiteSpace(group.DisplayName)
                    ? _localizationService["ArmorPollutionPage.UnknownUnit"]
                    : group.DisplayName,
                UnitText = $"{_localizationService["ArmorPollutionPage.Unit"]}: 0x{group.UnitId:X16}",
                ModsText = $"{_localizationService["ArmorPollutionPage.InvolvedMods"]}: {modNames}",
                EvidenceText = _localizationService["ArmorPollutionPage.Evidence"]
                    .Replace("{mods}", distinctMods.Length.ToString())
                    .Replace("{patches}", patchCount.ToString()),
                Icon = "!",
                Brush = new SolidColorBrush(Color.FromRgb(220, 80, 55)),
            });
        }

        AffectedModCount = result.Groups
            .SelectMany(static group => group.Occurrences)
            .Select(static occurrence => occurrence.ModGuid)
            .Distinct()
            .Count();
        ScannedModCount = result.ScannedModCount;
        ScannedPatchCount = result.ScannedPatchCount;
        ScannedUnitCount = result.ScannedUnitCount;
        SummaryText = Items.Count > 0
            ? _localizationService["ArmorPollutionPage.Found"].Replace("{count}", Items.Count.ToString())
            : _localizationService["ArmorPollutionPage.None"];

        OnPropertyChanged(nameof(PollutionCount));
        OnPropertyChanged(nameof(AffectedModCount));
        OnPropertyChanged(nameof(ScannedModCount));
        OnPropertyChanged(nameof(ScannedPatchCount));
        OnPropertyChanged(nameof(ScannedUnitCount));
        OnPropertyChanged(nameof(HasPollution));
        OnPropertyChanged(nameof(NoPollutionVisibility));
        OnPropertyChanged(nameof(PollutionVisibility));
    }
}

internal sealed class ArmorPollutionItem
{
    public required string ResourceName { get; init; }
    public required string UnitText { get; init; }
    public required string ModsText { get; init; }
    public required string EvidenceText { get; init; }
    public required string Icon { get; init; }
    public required Brush Brush { get; init; }
}

internal sealed record ArmorUnitOccurrence(
    Guid ModGuid,
    string ModName,
    string PatchFileName,
    uint Version,
    int DataSize);

internal sealed record ArmorPollutionGroup(
    long UnitId,
    string DisplayName,
    IReadOnlyList<ArmorUnitOccurrence> Occurrences);

internal sealed record ArmorPollutionScanResult(
    int ScannedModCount,
    int ScannedPatchCount,
    int ScannedUnitCount,
    IReadOnlyList<ArmorPollutionGroup> Groups);
