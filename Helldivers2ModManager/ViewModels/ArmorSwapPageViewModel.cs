using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace Helldivers2ModManager.ViewModels;

/// <summary>
/// 一键换甲页面：选择来源模组（及其中的护甲外观组）→ 选择目标护甲 → 生成换甲模组。
/// </summary>
[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class ArmorSwapPageViewModel : PageViewModelBase
{
    private readonly ILogger<ArmorSwapPageViewModel> _logger;
    private readonly Lazy<NavigationStore> _navStore;
    private readonly ModService _modService;
    private readonly ArmorSwapService _armorSwapService;
    private readonly BackgroundTaskService _backgroundTaskService;
    private readonly LocalizationService _localizationService;
    private ArmorSwapSourceAnalysis? _analysis;
    private ArmorSwapTargetArmor? _target;
    private int _analysisVersion;
    private int _checkVersion;
    private bool _analyzing;
    private bool _analyzePending;
    private bool _checking;
    private bool _checkPending;

    public override string Title => _localizationService["ArmorSwapPage.Title"];

    public ObservableCollection<ModData> SourceMods { get; } = [];
    public ObservableCollection<ArmorSwapSourceGroupItem> SourceGroups { get; } = [];
    public ObservableCollection<ArmorSwapTargetItem> TargetArmors { get; } = [];
    public ObservableCollection<ArmorSwapIssueItem> Issues { get; } = [];

    [ObservableProperty]
    private ModData? _selectedSourceMod;

    [ObservableProperty]
    private ArmorSwapSourceGroupItem? _selectedSourceGroup;

    [ObservableProperty]
    private ArmorSwapTargetItem? _selectedTargetArmor;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isInitialized;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _warningsText = string.Empty;

    public Visibility EmptyVisibility => !IsInitialized && !IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ContentVisibility => IsInitialized ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IssuesVisibility => Issues.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HasErrorsVisibility => Issues.Any(static issue => issue.IsError) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoErrorsVisibility => IsInitialized && Issues.Count > 0 && !Issues.Any(static issue => issue.IsError) ? Visibility.Visible : Visibility.Collapsed;

    public ArmorSwapPageViewModel(
        ILogger<ArmorSwapPageViewModel> logger,
        IServiceProvider provider,
        ModService modService,
        ArmorSwapService armorSwapService,
        BackgroundTaskService backgroundTaskService,
        LocalizationService localizationService)
    {
        _logger = logger;
        _navStore = new Lazy<NavigationStore>(provider.GetRequiredService<NavigationStore>);
        _modService = modService;
        _armorSwapService = armorSwapService;
        _backgroundTaskService = backgroundTaskService;
        _localizationService = localizationService;
        _localizationService.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
        Issues.CollectionChanged += (_, _) => NotifyIssueProperties();
        _ = InitializeAsync();
    }

    [RelayCommand]
    private void GoBack() => _navStore.Value.Navigate<DashboardPageViewModel>();

    private async Task InitializeAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            var enabledMods = _modService.Mods.Where(static mod => mod.Enabled).ToArray();
            foreach (var mod in enabledMods)
                SourceMods.Add(mod);
            if (SourceMods.Count == 0)
            {
                SummaryText = _localizationService["ArmorSwapPage.NoSourceMods"];
                return;
            }

            SelectedSourceMod = SourceMods[0];
            await AnalyzeSourceAsync();

            // 目标护甲目录（后台加载，含污染标记）
            var catalog = _armorSwapService.GetArmorCatalog()
                .Where(static pair => !pair.Key.Equals("9ba626afa44a3aa3", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var pollution = await _backgroundTaskService.RunAsync(
                _localizationService["BackgroundTasksPage.TaskTypeArmorPollutionScan"],
                _localizationService["ArmorSwapPage.LoadingTargets"],
                (_, _) => _armorSwapService.GetArmorPollutionAsync(catalog.Select(static pair => pair.Key).ToArray()),
                _localizationService["ArmorSwapPage.LoadingTargets"]);
            foreach (var (armorId, name) in catalog)
            {
                pollution.TryGetValue(armorId, out var mods);
                TargetArmors.Add(new ArmorSwapTargetItem
                {
                    ArmorId = armorId,
                    Name = name,
                    PollutionText = mods is { Count: > 0 }
                        ? _localizationService["ArmorSwapPage.PollutedBy"]
                            .Replace("{count}", mods.Count.ToString())
                            .Replace("{mods}", string.Join("、", mods))
                        : string.Empty
                });
            }
            SelectedTargetArmor = TargetArmors.FirstOrDefault();
            if (SelectedTargetArmor is not null)
                await CheckCompatibilityAsync();

            IsInitialized = true;
            SummaryText = string.IsNullOrWhiteSpace(WarningsText)
                ? _localizationService["ArmorSwapPage.Ready"]
                : _localizationService["ArmorSwapPage.ReadyWithWarnings"];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Armor swap page initialization failed");
            SummaryText = _localizationService["ArmorSwapPage.InitializeFailed"];
        }
        finally
        {
            IsBusy = false;
            NotifyStateProperties();
        }
    }

    partial void OnSelectedSourceModChanged(ModData? value)
    {
        // 使进行中的旧模组分析失效（防重入标志会补跑新模组分析）
        _analysisVersion++;
        SourceGroups.Clear();
        SelectedSourceGroup = null;
        Issues.Clear();
        if (value is null)
            return;
        _ = AnalyzeSourceAsync();
    }

    partial void OnSelectedSourceGroupChanged(ArmorSwapSourceGroupItem? value)
    {
        Issues.Clear();
        if (value is not null && SelectedTargetArmor is not null)
            _ = CheckCompatibilityAsync();
    }

    partial void OnSelectedTargetArmorChanged(ArmorSwapTargetItem? value)
    {
        Issues.Clear();
        if (value is not null && SelectedSourceGroup is not null)
            _ = CheckCompatibilityAsync();
    }

    private async Task AnalyzeSourceAsync()
    {
        // 防重入（不依赖 IsBusy：页面初始化在 IsBusy 窗口内也会触发分析）。
        if (_analyzing)
        {
            _analyzePending = true;
            return;
        }

        _analyzing = true;
        try
        {
            do
            {
                _analyzePending = false;
                var mod = SelectedSourceMod;
                if (mod is null)
                    break;

                _logger.LogDebug("Analyzing armor swap source mod {ModName}", mod.Manifest.Name);
                var version = ++_analysisVersion;
                var analysis = await _backgroundTaskService.RunAsync(
                    _localizationService["BackgroundTasksPage.TaskTypeArmorSwapAnalyze"],
                    _localizationService["ArmorSwapPage.Analyzing"],
                    (_, _) => _armorSwapService.AnalyzeSourceModAsync(mod),
                    _localizationService["ArmorSwapPage.Analyzing"]);
                if (version != _analysisVersion)
                    continue;
                _analysis = analysis;

                SourceGroups.Clear();
                foreach (var group in analysis.Groups)
                {
                    SourceGroups.Add(new ArmorSwapSourceGroupItem
                    {
                        Group = group,
                        DisplayText = $"{group.DisplayName} {_localizationService["ArmorSwapPage.GroupParts"]
                            .Replace("{count}", group.Units.Count.ToString())}"
                    });
                }
                WarningsText = analysis.Warnings.Count > 0
                    ? string.Join(Environment.NewLine, analysis.Warnings)
                    : string.Empty;
                SelectedSourceGroup = SourceGroups.FirstOrDefault();
                SummaryText = SourceGroups.Count == 0
                    ? _localizationService["ArmorSwapPage.NoGroups"]
                    : _localizationService["ArmorSwapPage.GroupsFound"]
                        .Replace("{count}", SourceGroups.Count.ToString());
            }
            while (_analyzePending);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Armor swap source analysis failed");
            SummaryText = _localizationService["ArmorSwapPage.AnalysisFailed"];
        }
        finally
        {
            _analyzing = false;
        }
    }

    private async Task CheckCompatibilityAsync()
    {
        // 防重入：检查期间的新触发只置 pending 标志，当前检查完成后自动补跑一次，
        // 保证用户最终看到的是与当前选择一致的结果（不依赖 IsBusy，页面初始化
        // 时序里所有触发点都可能在 IsBusy 为 true 时到达）。
        if (_checking)
        {
            _checkPending = true;
            return;
        }

        _checking = true;
        try
        {
            do
            {
                _checkPending = false;
                var groupItem = SelectedSourceGroup;
                var targetItem = SelectedTargetArmor;
                if (groupItem is null || targetItem is null || _analysis is null)
                {
                    _target = null;
                    continue;
                }

                _logger.LogDebug("Checking armor swap compatibility for {Group} -> {Target}",
                    groupItem.Group.DisplayName, targetItem.ArmorId);
                var version = ++_checkVersion;
                var loadedTarget = await _backgroundTaskService.RunAsync(
                    _localizationService["BackgroundTasksPage.TaskTypeArmorSwapAnalyze"],
                    _localizationService["ArmorSwapPage.Checking"],
                    (_, _) => _armorSwapService.LoadTargetArmorAsync(targetItem.ArmorId),
                    _localizationService["ArmorSwapPage.Checking"]);
                if (version != _checkVersion || loadedTarget is null)
                {
                    _target = null;
                    if (loadedTarget is null)
                    {
                        Issues.Clear();
                        Issues.Add(new ArmorSwapIssueItem
                        {
                            IsError = true,
                            Text = _localizationService["ArmorSwapPage.CheckFailed"]
                        });
                        SummaryText = _localizationService["ArmorSwapPage.CheckFailed"];
                        NotifyIssueProperties();
                    }
                    continue;
                }
                _target = loadedTarget;

                var issues = await _armorSwapService.CheckCompatibilityAsync(groupItem.Group, loadedTarget);
                if (version != _checkVersion)
                    continue;
                Issues.Clear();
                foreach (var issue in issues)
                {
                    Issues.Add(new ArmorSwapIssueItem
                    {
                        IsError = issue.IsError,
                        Text = issue.Message
                    });
                }
                SummaryText = Issues.Count == 0
                    ? _localizationService["ArmorSwapPage.Compatible"]
                    : _localizationService["ArmorSwapPage.IssuesFound"]
                        .Replace("{count}", Issues.Count.ToString());
                NotifyIssueProperties();
            }
            while (_checkPending);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Armor swap compatibility check failed");
            SummaryText = _localizationService["ArmorSwapPage.CheckFailed"];
        }
        finally
        {
            _checking = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task Generate()
    {
        var mod = SelectedSourceMod;
        var groupItem = SelectedSourceGroup;
        var targetItem = SelectedTargetArmor;
        var target = _target;
        var analysis = _analysis;
        if (mod is null || groupItem is null || targetItem is null || analysis is null)
        {
            ShowMessage(_localizationService["ArmorSwapPage.ErrorIncompleteSelection"]);
            return;
        }
        if (target is null)
        {
            ShowMessage(_localizationService["ArmorSwapPage.ErrorTargetUnavailable"]);
            return;
        }
        if (IsBusy)
            return;
        if (Issues.Any(static issue => issue.IsError))
        {
            ShowMessage(_localizationService["ArmorSwapPage.ErrorBlockingIssues"]);
            return;
        }

        IsBusy = true;
        NotifyStateProperties();
        try
        {
            var sourceName = mod.Manifest.Name;
            var targetName = target.DisplayName;
            var modName = _localizationService["ArmorSwapPage.GeneratedModName"]
                .Replace("{target}", targetName)
                .Replace("{source}", sourceName);
            _logger.LogInformation("Generating armor swap mod {ModName} from {Source} onto {Target}",
                modName, sourceName, targetName);
            var outputDirectory = await _backgroundTaskService.RunAsync(
                _localizationService["BackgroundTasksPage.TaskTypeArmorSwap"],
                modName,
                (_, _) => _armorSwapService.GenerateArmorSwapModAsync(mod, analysis, groupItem.Group, target),
                modName);

            // 保留来源模组的选项结构（换甲后仍可切换部件搭配）
            var customOptions = mod.Manifest is V1ModManifest { Options: { } options }
                ? options.Select(CloneOption).ToList()
                : null;
            var problems = await _modService.TryAddModFromDirectoryAsync(
                new DirectoryInfo(outputDirectory),
                modName,
                _localizationService["ArmorSwapPage.GeneratedModDescription"]
                    .Replace("{target}", targetName)
                    .Replace("{source}", sourceName),
                customOptions,
                mod.Manifest.IconPath);
            if (problems.Length > 0)
                throw new InvalidDataException(string.Join(Environment.NewLine, problems.Select(static p => p.Kind.ToString())));

            // 启用并置于部署顺序末尾（最后部署 = 覆盖所有污染目标护甲的模组）
            var newMod = _modService.Mods.LastOrDefault(candidate => candidate.Manifest.Name == modName);
            if (newMod is null)
                throw new InvalidDataException(_localizationService["ArmorSwapPage.ErrorImportFailed"]);
            newMod.Enabled = true;
            _modService.MoveModTo(newMod, _modService.Mods.Count - 1);

            WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
            {
                Message = _localizationService["ArmorSwapPage.GenerateSuccess"]
                    .Replace("{name}", modName)
            });
            SummaryText = _localizationService["ArmorSwapPage.GenerateDone"];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Armor swap generation failed");
            WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
            {
                Message = _localizationService["ArmorSwapPage.GenerateFailed"]
                    .Replace("{reason}", ex.Message)
            });
        }
        finally
        {
            IsBusy = false;
            NotifyStateProperties();
        }
    }

    private void NotifyStateProperties()
    {
        OnPropertyChanged(nameof(IsInitialized));
        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(ContentVisibility));
    }

    private void ShowMessage(string message)
    {
        _logger.LogDebug("Armor swap user message: {Message}", message);
        WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
        {
            Message = message
        });
    }

    private static ModOption CloneOption(ModOption option) => new()
    {
        Name = option.Name,
        Description = option.Description,
        Include = option.Include,
        Image = option.Image,
        SubOptions = option.SubOptions?.Select(static sub => new ModSubOption
        {
            Name = sub.Name,
            Description = sub.Description,
            Include = sub.Include,
            Image = sub.Image
        }).ToList()
    };

    private void NotifyIssueProperties()
    {
        OnPropertyChanged(nameof(IssuesVisibility));
        OnPropertyChanged(nameof(HasErrorsVisibility));
        OnPropertyChanged(nameof(NoErrorsVisibility));
    }

    protected override void OnDispose()
    {
        _localizationService.PropertyChanged -= (_, _) => OnPropertyChanged(nameof(Title));
        base.OnDispose();
    }
}

internal sealed class ArmorSwapSourceGroupItem
{
    public required ArmorSwapSourceGroup Group { get; init; }
    public required string DisplayText { get; init; }
}

internal sealed class ArmorSwapTargetItem
{
    public required string ArmorId { get; init; }
    public required string Name { get; init; }
    public required string PollutionText { get; init; }
    public string DisplayName => Name;
}

internal sealed class ArmorSwapIssueItem
{
    public required bool IsError { get; init; }
    public required string Text { get; init; }
}
