using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Windows;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class ArmorReusePageViewModel : PageViewModelBase
{
    private readonly ILogger<ArmorReusePageViewModel> _logger;
    private readonly Lazy<NavigationStore> _navStore;
    private readonly ModService _modService;
    private readonly SettingsService _settingsService;
    private readonly ArmorReuseService _armorReuseService;
    private readonly BackgroundTaskService _backgroundTaskService;
    private readonly LocalizationService _localizationService;

    public override string Title => _localizationService["ArmorReusePage.Title"];
    public ObservableCollection<ArmorReuseItem> Items { get; } = [];

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    public int ReusedArmorCount => Items.Sum(static item => item.ReusedArmorCount);
    public int AffectedModCount => Items.Select(static item => item.ModGuid).Distinct().Count();
    public int ScannedModCount { get; private set; }
    public int ScannedPatchCount { get; private set; }
    public int ScannedUnitCount { get; private set; }
    public Visibility EmptyVisibility => Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResultVisibility => Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public ArmorReusePageViewModel(
        ILogger<ArmorReusePageViewModel> logger,
        IServiceProvider provider,
        ModService modService,
        SettingsService settingsService,
        ArmorReuseService armorReuseService,
        BackgroundTaskService backgroundTaskService,
        LocalizationService localizationService)
    {
        _logger = logger;
        _navStore = new Lazy<NavigationStore>(provider.GetRequiredService<NavigationStore>);
        _modService = modService;
        _settingsService = settingsService;
        _armorReuseService = armorReuseService;
        _backgroundTaskService = backgroundTaskService;
        _localizationService = localizationService;
        _localizationService.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
        Items.CollectionChanged += (_, _) => NotifyResultProperties();
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void GoBack() => _navStore.Value.Navigate<DashboardPageViewModel>();

    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task Refresh() => RefreshAsync();

    private async Task RefreshAsync()
    {
        if (!_settingsService.Initialized || !_modService.Initialized || IsScanning)
            return;

        IsScanning = true;
        SummaryText = _localizationService["ArmorReusePage.Scanning"];
        var task = _backgroundTaskService.Add(
            _localizationService["BackgroundTasksPage.TaskTypeArmorReuseScan"],
            SummaryText);

        try
        {
            var enabledMods = _modService.Mods.Where(static mod => mod.Enabled).ToArray();
            var result = await _armorReuseService.AnalyzeAsync(enabledMods);
            ApplyResult(result);
            _backgroundTaskService.Complete(task, SummaryText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Armor reuse scan failed");
            SummaryText = _localizationService["ArmorReusePage.ScanFailed"];
            _backgroundTaskService.Fail(task, ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void ApplyResult(ArmorReuseAnalysisResult result)
    {
        Items.Clear();
        foreach (var record in result.Records)
        {
            Items.Add(new ArmorReuseItem
            {
                ModGuid = record.ModGuid,
                ModName = record.ModName,
                SourceArmor = record.SourceArmorName,
                SourceArmorId = $"0x{record.SourceArmorId.ToUpperInvariant()}",
                ReusedBy = string.Join("、", record.ReusedBy.Select(static target => target.ArmorName)),
                SharedUnitText = _localizationService["ArmorReusePage.SharedUnits"]
                    .Replace("{count}", record.SharedUnitCount.ToString()),
                ReusedArmorCount = record.ReusedBy.Count,
            });
        }

        ScannedModCount = result.ScannedModCount;
        ScannedPatchCount = result.ScannedPatchCount;
        ScannedUnitCount = result.ScannedUnitCount;
        SummaryText = Items.Count == 0
            ? _localizationService["ArmorReusePage.None"]
            : _localizationService["ArmorReusePage.Found"]
                .Replace("{records}", Items.Count.ToString())
                .Replace("{armors}", ReusedArmorCount.ToString());
        NotifyResultProperties();
    }

    private void NotifyResultProperties()
    {
        OnPropertyChanged(nameof(ReusedArmorCount));
        OnPropertyChanged(nameof(AffectedModCount));
        OnPropertyChanged(nameof(ScannedModCount));
        OnPropertyChanged(nameof(ScannedPatchCount));
        OnPropertyChanged(nameof(ScannedUnitCount));
        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(ResultVisibility));
    }
}

internal sealed class ArmorReuseItem
{
    public required Guid ModGuid { get; init; }
    public required string ModName { get; init; }
    public required string SourceArmor { get; init; }
    public required string SourceArmorId { get; init; }
    public required string ReusedBy { get; init; }
    public required string SharedUnitText { get; init; }
    public required int ReusedArmorCount { get; init; }
}
