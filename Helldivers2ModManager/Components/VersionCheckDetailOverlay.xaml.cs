using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Helldivers2ModManager.Components;

internal sealed class VersionCheckDetailMessage
{
    public required string ModName { get; init; }
    public required ModVersionStatus Status { get; init; }
    public uint GameVersion { get; init; }
    public DateTime LastChecked { get; init; }
    public required IReadOnlyList<PatchUnitInfo> PatchUnits { get; init; }
    public required ModDetailedAnalysis Analysis { get; init; }
    public required string FullReport { get; init; }
    public required DirectoryInfo ModDirectory { get; init; }
    public required Func<Task> RefreshAsync { get; init; }
}

internal sealed class VersionCheckDiagnosticIssue
{
    public required string Icon { get; init; }
    public required Brush Brush { get; init; }
    public required string Title { get; init; }
    public required string FileName { get; init; }
    public required string Description { get; init; }
}

internal sealed class VersionCheckDetailViewData
{
    public required string ModName { get; init; }
    public required string StatusIcon { get; init; }
    public required string StatusText { get; init; }
    public required string Summary { get; init; }
    public required Brush StatusBrush { get; init; }
    public required Brush StatusBackground { get; init; }
    public required Brush IssueCountBrush { get; init; }
    public int PatchFileCount { get; init; }
    public int ResourceCount { get; init; }
    public int UnitCount { get; init; }
    public int TotalIssueCount { get; init; }
    public required string HealthyUnitSummary { get; init; }
    public required IReadOnlyList<VersionCheckDiagnosticIssue> VisibleIssues { get; init; }
    public required string HiddenIssueSummary { get; init; }
    public Visibility IssuesVisibility { get; init; }
    public Visibility NoIssuesVisibility { get; init; }
    public Visibility HiddenIssuesVisibility { get; init; }
    public required string TechnicalReport { get; init; }
}

internal sealed record LodStrategyOption(
    AssistedLodStrategy? Strategy,
    string Label)
{
    public override string ToString() => Label;
}

internal partial class VersionCheckDetailOverlay : UserControl, IRecipient<VersionCheckDetailMessage>
{
    private const int MaxVisibleIssues = 50;
    private readonly LocalizationService? _localizationService;
    private readonly VersionCheckService? _versionCheckService;
    private VersionCheckDetailMessage? _currentMessage;
    private ModRepairPlan? _repairPlan;
    private AssistedModRepairPlan? _automaticLodPlan;
    private AssistedModRepairPlan? _preserveLodPlan;
    private AssistedModRepairPlan? _gameLodPlan;
    private bool _useGameReferences;
    private string _fullReport = string.Empty;

    public VersionCheckDetailOverlay()
    {
        InitializeComponent();
        WeakReferenceMessenger.Default.Register<VersionCheckDetailMessage>(this);

        if (Application.Current is App app &&
            app.Host?.Services?.GetService(typeof(LocalizationService)) is LocalizationService localizationService)
        {
            _localizationService = localizationService;
        }

        if (Application.Current is App currentApp &&
            currentApp.Host?.Services?.GetService(typeof(VersionCheckService)) is VersionCheckService versionCheckService)
        {
            _versionCheckService = versionCheckService;
        }
    }

    public void Receive(VersionCheckDetailMessage message)
    {
        _currentMessage = message;
        _repairPlan = null;
        _automaticLodPlan = null;
        _preserveLodPlan = null;
        _gameLodPlan = null;
        _useGameReferences = false;
        _fullReport = message.FullReport;
        copyStatus.Visibility = Visibility.Collapsed;
        repairStatus.Visibility = Visibility.Collapsed;
        repairProgress.Visibility = Visibility.Collapsed;
        repairButton.Visibility = Visibility.Collapsed;
        advancedRepairButton.Visibility = Visibility.Collapsed;
        repairButton.IsEnabled = false;
        DataContext = BuildViewData(message);
        Visibility = Visibility.Visible;
        Focus();
        _ = LoadRepairPlanAsync(message);
    }

    private async Task LoadRepairPlanAsync(VersionCheckDetailMessage message)
    {
        if (_versionCheckService is null)
            return;

        repairProgress.Text = L("VersionCheckDetail.CheckingRepair", "Checking repair options...");
        repairProgress.Visibility = Visibility.Visible;
        try
        {
            var plan = await _versionCheckService.CreateRepairPlanAsync(message.ModDirectory);
            if (!ReferenceEquals(_currentMessage, message))
                return;

            _repairPlan = plan;
            if (plan.CanRepair)
            {
                _useGameReferences = false;
                repairButtonText.Text = L("VersionCheckDetail.RepairButton", "Repair {count} issue(s)")
                    .Replace("{count}", plan.ActionCount.ToString());
                repairButton.IsEnabled = true;
                repairButton.Visibility = Visibility.Visible;
                return;
            }

            repairProgress.Text = L("VersionCheckDetail.IndexingGameUnits", "Checking current game Unit references...");
            var automaticLodPlan = await _versionCheckService.CreateAutomaticAssistedRepairPlanAsync(
                message.ModDirectory);
            if (!ReferenceEquals(_currentMessage, message))
                return;

            _automaticLodPlan = automaticLodPlan;
            if (automaticLodPlan.CanRepair)
            {
                _useGameReferences = true;
                repairButtonText.Text = L("VersionCheckDetail.AutomaticRepairButton", "Automatically repair {count} Unit(s)")
                    .Replace("{count}", automaticLodPlan.ActionCount.ToString());
                repairButton.IsEnabled = true;
                repairButton.Visibility = Visibility.Visible;
                advancedRepairButton.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            repairButton.Visibility = Visibility.Collapsed;
        }
        finally
        {
            if (ReferenceEquals(_currentMessage, message))
                repairProgress.Visibility = Visibility.Collapsed;
        }
    }

    private VersionCheckDetailViewData BuildViewData(VersionCheckDetailMessage message)
    {
        var issues = BuildIssues(message);
        var unitCount = message.Analysis.PatchFiles.Sum(p => p.UnitDetails.Count);
        var problematicUnits = message.Analysis.PatchFiles
            .SelectMany(p => p.UnitDetails.Select(u => (Patch: p, Unit: u)))
            .Count(x => IsProblematicUnit(x.Unit));
        var healthyUnits = Math.Max(0, unitCount - problematicUnits);
        var truncatedCount = message.Analysis.PatchFiles
            .SelectMany(p => p.UnitDetails)
            .Count(u => u.IsTruncated);

        var statusBrush = message.Status switch
        {
            ModVersionStatus.Compatible => GetBrush("SuccessBrush", Colors.ForestGreen),
            ModVersionStatus.Incompatible or ModVersionStatus.Error => GetBrush("DangerBrush", Colors.IndianRed),
            ModVersionStatus.Checking => GetBrush("SystemAccentBrush", Colors.DodgerBlue),
            _ => GetBrush("WarningBrush", Colors.Goldenrod)
        };
        var statusColor = statusBrush is SolidColorBrush solid ? solid.Color : Colors.Gray;
        var statusBackground = new SolidColorBrush(Color.FromArgb(28, statusColor.R, statusColor.G, statusColor.B));

        var statusText = message.Status switch
        {
            ModVersionStatus.Compatible => L("Converters.Compatible", "Compatible"),
            ModVersionStatus.Incompatible => L("Converters.Incompatible", "Incompatible"),
            ModVersionStatus.Checking => L("Converters.Checking", "Checking"),
            ModVersionStatus.Error => L("Converters.CheckFailed", "Check failed"),
            _ => L("Converters.UnableToConfirm", "Unable to confirm")
        };
        var statusIcon = message.Status switch
        {
            ModVersionStatus.Compatible => "\uE73E",
            ModVersionStatus.Incompatible or ModVersionStatus.Error => "\uE783",
            ModVersionStatus.Checking => "\uE895",
            _ => "\uE946"
        };

        string summary;
        if (truncatedCount > 0)
        {
            summary = L("VersionCheckDetail.SummaryTruncated", "{count} Unit resource(s) are truncated by their TOC size.")
                .Replace("{count}", truncatedCount.ToString());
        }
        else if (message.Analysis.CorruptedFileCount > 0)
        {
            summary = L("VersionCheckDetail.SummaryCorrupted", "{count} patch file(s) contain structural damage.")
                .Replace("{count}", message.Analysis.CorruptedFileCount.ToString());
        }
        else if (message.Status == ModVersionStatus.Incompatible)
        {
            summary = L("VersionCheckDetail.SummaryVersionMismatch", "One or more Unit versions differ from the reference version.");
        }
        else if (message.Status == ModVersionStatus.Compatible)
        {
            summary = L("VersionCheckDetail.SummaryHealthy", "No blocking compatibility issues were detected.");
        }
        else
        {
            summary = L("VersionCheckDetail.SummaryUnknown", "There is not enough Unit version information to confirm compatibility.");
        }

        var hiddenCount = Math.Max(0, issues.Count - MaxVisibleIssues);
        return new VersionCheckDetailViewData
        {
            ModName = message.ModName,
            StatusIcon = statusIcon,
            StatusText = statusText,
            Summary = summary,
            StatusBrush = statusBrush,
            StatusBackground = statusBackground,
            IssueCountBrush = issues.Count > 0 ? GetBrush("DangerBrush", Colors.IndianRed) : GetBrush("SuccessBrush", Colors.ForestGreen),
            PatchFileCount = message.Analysis.TotalPatchFiles,
            ResourceCount = message.Analysis.PatchFiles.Sum(p => p.TotalResources),
            UnitCount = unitCount,
            TotalIssueCount = issues.Count,
            HealthyUnitSummary = healthyUnits > 0
                ? L("VersionCheckDetail.HealthyUnits", "{count} Unit(s) passed structural checks").Replace("{count}", healthyUnits.ToString())
                : string.Empty,
            VisibleIssues = issues.Take(MaxVisibleIssues).ToList(),
            HiddenIssueSummary = hiddenCount > 0
                ? L("VersionCheckDetail.HiddenIssues", "{count} more issue(s) are available in technical details.").Replace("{count}", hiddenCount.ToString())
                : string.Empty,
            IssuesVisibility = issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
            NoIssuesVisibility = issues.Count == 0 ? Visibility.Visible : Visibility.Collapsed,
            HiddenIssuesVisibility = hiddenCount > 0 ? Visibility.Visible : Visibility.Collapsed,
            TechnicalReport = message.FullReport
        };
    }

    private List<VersionCheckDiagnosticIssue> BuildIssues(VersionCheckDetailMessage message)
    {
        var issues = new List<VersionCheckDiagnosticIssue>();
        var danger = GetBrush("DangerBrush", Colors.IndianRed);
        var warning = GetBrush("WarningBrush", Colors.Goldenrod);
        var versionMismatches = message.PatchUnits
            .Where(u => message.GameVersion != 0 && u.Version != message.GameVersion)
            .ToList();

        if (versionMismatches.Count > 0)
        {
            var versions = string.Join(", ", versionMismatches.Select(u => $"0x{u.Version:X8}").Distinct());
            issues.Add(new VersionCheckDiagnosticIssue
            {
                Icon = "\uE7BA",
                Brush = danger,
                Title = L("VersionCheckDetail.VersionMismatchTitle", "Unit version mismatch"),
                FileName = string.Empty,
                Description = L("VersionCheckDetail.VersionMismatchDescription", "{count} Unit(s) use {versions}; reference is {reference}.")
                    .Replace("{count}", versionMismatches.Count.ToString())
                    .Replace("{versions}", versions)
                    .Replace("{reference}", $"0x{message.GameVersion:X8}")
            });
        }

        foreach (var patch in message.Analysis.PatchFiles)
        {
            var fileIssueCountBefore = issues.Count;
            AddFileIssues(issues, patch, danger, warning);

            foreach (var unit in patch.UnitDetails)
            {
                if (unit.IsTruncated)
                {
                    issues.Add(new VersionCheckDiagnosticIssue
                    {
                        Icon = "\uE7BA",
                        Brush = danger,
                        Title = L("VersionCheckDetail.UnitTruncatedTitle", "Unit #{index} data is truncated")
                            .Replace("{index}", unit.EntryIndex.ToString()),
                        FileName = patch.FileName,
                        Description = L("VersionCheckDetail.UnitTruncatedDescription", "TOC declares {declared} bytes, internal size is {expected}; {difference} bytes are missing. ID {fileId}")
                            .Replace("{declared}", unit.DataSize.ToString())
                            .Replace("{expected}", unit.ExpectedDataSize.ToString())
                            .Replace("{difference}", Math.Max(0, unit.ExpectedDataSize - unit.DataSize).ToString())
                            .Replace("{fileId}", $"0x{unit.FileId:X16}")
                    });
                }
                else if (!unit.DeclaredSizeMatchesInternal)
                {
                    issues.Add(new VersionCheckDiagnosticIssue
                    {
                        Icon = "\uE7BA",
                        Brush = warning,
                        Title = L("VersionCheckDetail.UnitSizeMismatchTitle", "Unit #{index} size mismatch")
                            .Replace("{index}", unit.EntryIndex.ToString()),
                        FileName = patch.FileName,
                        Description = L("VersionCheckDetail.UnitSizeMismatchDescription", "TOC declares {declared} bytes; internal size is {expected}. ID {fileId}")
                            .Replace("{declared}", unit.DataSize.ToString())
                            .Replace("{expected}", unit.ExpectedDataSize.ToString())
                            .Replace("{fileId}", $"0x{unit.FileId:X16}")
                    });
                }

                if (!unit.UnitDataInBounds)
                    AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.UnitBoundsTitle", "Unit data exceeds patch bounds", unit.Warning);
                else if (!unit.LODGroupInBounds)
                    AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.LodBoundsTitle", "Unit LOD data exceeds its declared bounds", unit.Warning);

                if (unit.LayoutFormatChecked && !unit.LayoutFormatValid)
                    AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.LayoutTitle", "Legacy Unit layout requires repair", unit.Warning);
            }

            if (patch.HealthStatus is PatchHealthStatus.Corrupted or PatchHealthStatus.Warning &&
                issues.Count == fileIssueCountBefore && !string.IsNullOrWhiteSpace(patch.Message))
            {
                AddSimpleIssue(issues,
                    patch.HealthStatus == PatchHealthStatus.Corrupted ? danger : warning,
                    patch.FileName,
                    "VersionCheckDetail.GenericFileIssueTitle",
                    "Patch file warning",
                    patch.Message);
            }
        }

        return issues;
    }

    private void AddFileIssues(List<VersionCheckDiagnosticIssue> issues, PatchFileAnalysis patch, Brush danger, Brush warning)
    {
        if (!patch.HeaderValid || !patch.FileEntriesInBounds)
            AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.HeaderIssueTitle", "Invalid patch header or TOC", patch.Message);
        if (!patch.TypeDistributionValid)
            AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.TypeTableTitle", "Resource type table is inconsistent", L("VersionCheckDetail.TypeTableDescription", "The type table does not match the {count} actual file entries.").Replace("{count}", patch.NumFiles.ToString()));
        if (!patch.MainDataBoundsValid)
            AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.MainBoundsTitle", "Main resource data is out of bounds", L("VersionCheckDetail.MainBoundsDescription", "{count} invalid or overlapping range(s).").Replace("{count}", patch.MainDataIssueCount.ToString()));
        if (!patch.EntryIndicesValid)
            AddSimpleIssue(issues, warning, patch.FileName, "VersionCheckDetail.EntryIndexTitle", "TOC entry indices are not sequential", L("VersionCheckDetail.EntryIndexDescription", "{count} invalid index value(s).").Replace("{count}", patch.EntryIndexIssueCount.ToString()));
        if (patch.RequiresGpuResources && !patch.HasGpuResources)
            AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.MissingGpuTitle", "Required GPU resource file is missing", L("VersionCheckDetail.MissingGpuDescription", "The patch contains non-zero GPU resource references."));
        if (patch.RequiresStream && !patch.HasStream)
            AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.MissingStreamTitle", "Required stream file is missing", L("VersionCheckDetail.MissingStreamDescription", "The patch contains non-zero stream resource references."));
        if (!patch.GpuResourceBoundsValid || patch.GpuAlignmentIssueCount > 0)
            AddSimpleIssue(issues, patch.GpuResourceBoundsValid ? warning : danger, patch.FileName, "VersionCheckDetail.GpuIssueTitle", "GPU resource range problem", L("VersionCheckDetail.ResourceRangeDescription", "Out of bounds: {bounds}; misaligned: {alignment}.").Replace("{bounds}", patch.GpuResourceIssueCount.ToString()).Replace("{alignment}", patch.GpuAlignmentIssueCount.ToString()));
        if (!patch.StreamBoundsValid || patch.StreamAlignmentIssueCount > 0)
            AddSimpleIssue(issues, patch.StreamBoundsValid ? warning : danger, patch.FileName, "VersionCheckDetail.StreamIssueTitle", "stream resource range problem", L("VersionCheckDetail.ResourceRangeDescription", "Out of bounds: {bounds}; misaligned: {alignment}.").Replace("{bounds}", patch.StreamIssueCount.ToString()).Replace("{alignment}", patch.StreamAlignmentIssueCount.ToString()));
    }

    private void AddSimpleIssue(List<VersionCheckDiagnosticIssue> issues, Brush brush, string fileName, string titleKey, string titleFallback, string? description)
    {
        issues.Add(new VersionCheckDiagnosticIssue
        {
            Icon = "\uE7BA",
            Brush = brush,
            Title = L(titleKey, titleFallback),
            FileName = fileName,
            Description = description ?? string.Empty
        });
    }

    private static bool IsProblematicUnit(UnitResourceDetail unit)
    {
        return !unit.UnitDataInBounds || !unit.LODGroupInBounds || !unit.DeclaredSizeMatchesInternal ||
               (unit.LayoutFormatChecked && !unit.LayoutFormatValid);
    }

    private string L(string key, string fallback)
    {
        return _localizationService?.Get(key, fallback) ?? fallback;
    }

    private static Brush GetBrush(string resourceKey, Color fallback)
    {
        return Application.Current.TryFindResource(resourceKey) as Brush ?? new SolidColorBrush(fallback);
    }

    private void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMessage is null)
            return;

        if (_useGameReferences)
        {
            ShowAutomaticRepairConfirmation();
            return;
        }

        var fileCount = _repairPlan?.FileCount ?? 0;
        var actionCount = _repairPlan?.ActionCount ?? 0;
        if (actionCount == 0)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = L("VersionCheckDetail.RepairConfirmTitle", "Repair mod"),
            Message = L(
                    "VersionCheckDetail.RepairConfirmMessage",
                    "HD2MM will back up {files} patch file(s), apply {count} verified metadata repair(s), and validate the result before replacing the originals.")
                .Replace("{files}", fileCount.ToString())
                .Replace("{count}", actionCount.ToString()),
            Confirm = () => _ = ExecuteRepairAsync(false, null, null)
        });
    }

    private void ShowAutomaticRepairConfirmation()
    {
        if (_automaticLodPlan?.CanRepair != true)
            return;

        var plan = _automaticLodPlan;
        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = L("VersionCheckDetail.AutomaticRepairTitle", "Automatically repair mod"),
            Message = L(
                    "VersionCheckDetail.AutomaticRepairConfirm",
                    "HD2MM analyzed the Unit mesh and GPU structure. It will preserve mod LOD for {preserve} Unit(s), use current game LOD for {game} Unit(s), back up the patch files, and validate the rebuilt result before replacement.")
                .Replace("{preserve}", plan.AutomaticPreserveUnitCount.ToString())
                .Replace("{game}", plan.AutomaticGameLodUnitCount.ToString()),
            Confirm = () => _ = ExecuteRepairAsync(true, null, null)
        });
    }

    private async void AdvancedRepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (_versionCheckService is null || _currentMessage is null)
            return;

        var message = _currentMessage;
        repairButton.IsEnabled = false;
        advancedRepairButton.IsEnabled = false;
        repairProgress.Text = L("VersionCheckDetail.LoadingAdvancedRepair", "Loading advanced LOD strategies...");
        repairProgress.Visibility = Visibility.Visible;
        try
        {
            _preserveLodPlan ??= await _versionCheckService.CreateAssistedRepairPlanAsync(
                message.ModDirectory,
                AssistedLodStrategy.PreserveMod);
            _gameLodPlan ??= await _versionCheckService.CreateAssistedRepairPlanAsync(
                message.ModDirectory,
                AssistedLodStrategy.UseGameReference);
            if (!ReferenceEquals(_currentMessage, message))
                return;

            ShowLodStrategySelection();
        }
        catch (Exception ex)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = L("VersionCheckDetail.AdvancedRepairFailed", "Failed to load advanced repair strategies: {message}")
                    .Replace("{message}", ex.Message)
            });
        }
        finally
        {
            if (ReferenceEquals(_currentMessage, message))
            {
                repairProgress.Visibility = Visibility.Collapsed;
                repairButton.IsEnabled = true;
                advancedRepairButton.IsEnabled = true;
            }
        }
    }

    private void ShowLodStrategySelection()
    {
        var options = new List<LodStrategyOption>();
        var selectableUnitCount = _gameLodPlan?.Actions
            .Where(action => action.LodDataDiffers)
            .Select(action => action.FileId)
            .Distinct()
            .Count() ?? 0;
        if (selectableUnitCount > 0)
        {
            options.Add(new LodStrategyOption(
                null,
                L("VersionCheckDetail.LodStrategyPerUnit", "Choose per Unit manually ({count} selectable)")
                    .Replace("{count}", selectableUnitCount.ToString())));
        }
        if (_gameLodPlan?.CanRepair == true)
        {
            options.Add(new LodStrategyOption(
                AssistedLodStrategy.UseGameReference,
                L("VersionCheckDetail.LodStrategyGameReference", "Use current game LOD (standard, {count} Unit(s))")
                    .Replace("{count}", _gameLodPlan.ActionCount.ToString())));
        }
        if (_preserveLodPlan?.CanRepair == true)
        {
            options.Add(new LodStrategyOption(
                AssistedLodStrategy.PreserveMod,
                L("VersionCheckDetail.LodStrategyPreserveMod", "Preserve mod LOD (custom models, {count} Unit(s))")
                    .Replace("{count}", _preserveLodPlan.ActionCount.ToString())));
        }
        if (options.Count == 0)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxSelectionMessage
        {
            Title = L("VersionCheckDetail.LodStrategyTitle", "Choose Unit LOD strategy"),
            Message = L(
                "VersionCheckDetail.LodStrategyMessage",
                "LOD compatibility can differ between Units in the same patch. Per-Unit selection is the safest option. Game LOD can prevent preview crashes but may hide custom models; preserving mod LOD keeps custom models but an obsolete group can crash the preview. Every repair creates a backup."),
            Options = options,
            Confirm = selected =>
            {
                if (selected is not LodStrategyOption option)
                    return;
                if (option.Strategy is AssistedLodStrategy strategy)
                    _ = ExecuteRepairAsync(true, strategy, null);
                else
                    ShowUnitLodSelection();
            }
        });
    }

    private void ShowUnitLodSelection()
    {
        var candidates = _gameLodPlan?.Actions
            .Where(action => action.LodDataDiffers)
            .GroupBy(action => action.FileId)
            .Select(group =>
            {
                var first = group.First();
                var title = string.IsNullOrWhiteSpace(first.FriendlyName)
                    ? L("VersionCheckDetail.UnitSelectionUnnamed", "Unit {id}")
                        .Replace("{id}", $"0x{(ulong)first.FileId:X16}")
                    : first.FriendlyName;
                var lodSizes = string.Join(", ", group
                    .Select(action => $"{action.CurrentLodSize}->{action.ReferenceLodSize}")
                    .Distinct(StringComparer.Ordinal));
                var description = L(
                        "VersionCheckDetail.UnitSelectionDescription",
                        "{occurrences} patch entry(s) | LOD {lodSizes} | ID {id}")
                    .Replace("{occurrences}", group.Count().ToString())
                    .Replace("{lodSizes}", lodSizes)
                    .Replace("{id}", $"0x{(ulong)first.FileId:X16}");
                return new ChecklistSelectionItem
                {
                    Value = first.FileId,
                    Title = title,
                    Description = description
                };
            })
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Value)
            .ToList() ?? [];
        if (candidates.Count == 0)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxChecklistMessage
        {
            Title = L("VersionCheckDetail.UnitSelectionTitle", "Choose Units that keep mod LOD"),
            Message = L(
                "VersionCheckDetail.UnitSelectionMessage",
                "Checked Units preserve the mod LOD; unchecked Units use current game LOD. Equal LOD sizes can still contain incompatible data. If the mod was already converted entirely to game LOD, restore its HD2MM backup before using this screen."),
            Items = candidates,
            Confirm = selected =>
            {
                var preserveIds = selected.Select(item => item.Value).ToHashSet();
                _ = ExecuteRepairAsync(true, null, preserveIds);
            }
        });
    }
    private async Task ExecuteRepairAsync(
        bool useGameReferences,
        AssistedLodStrategy? lodStrategy,
        IReadOnlySet<long>? preserveModLodUnitIds)
    {
        if (_versionCheckService is null || _currentMessage is null)
            return;

        var message = _currentMessage;
        repairButton.IsEnabled = false;
        advancedRepairButton.IsEnabled = false;
        var usesMixedLod = preserveModLodUnitIds is not null;
        var usesGameLod = lodStrategy == AssistedLodStrategy.UseGameReference;
        var usesAutomaticLod = useGameReferences &&
            lodStrategy is null &&
            preserveModLodUnitIds is null;
        repairProgress.Text = useGameReferences
            ? L(
                usesAutomaticLod
                    ? "VersionCheckDetail.AutomaticRepairing"
                    : usesMixedLod
                        ? "VersionCheckDetail.AssistedRepairingMixedLod"
                        : usesGameLod
                            ? "VersionCheckDetail.AssistedRepairingGameLod"
                            : "VersionCheckDetail.AssistedRepairingPreserveLod",
                usesAutomaticLod
                    ? "Automatically classifying each Unit, rebuilding the patch, and validating..."
                    : usesMixedLod
                        ? "Applying per-Unit LOD choices, then validating..."
                        : usesGameLod
                            ? "Replacing Unit LOD data with current game references, then validating..."
                            : "Upgrading Units while preserving mod LOD data, then validating...")
            : L("VersionCheckDetail.Repairing", "Backing up, repairing, and validating...");
        repairProgress.Visibility = Visibility.Visible;
        repairStatus.Visibility = Visibility.Collapsed;

        ModRepairResult result;
        if (!useGameReferences)
        {
            result = await _versionCheckService.RepairModAsync(message.ModDirectory);
        }
        else if (usesAutomaticLod)
        {
            result = await _versionCheckService.RepairModAutomaticallyAsync(message.ModDirectory);
        }
        else if (preserveModLodUnitIds is not null)
        {
            result = await _versionCheckService.RepairModWithMixedGameReferencesAsync(
                message.ModDirectory,
                preserveModLodUnitIds);
        }
        else
        {
            result = await _versionCheckService.RepairModWithGameReferencesAsync(
                message.ModDirectory,
                lodStrategy ?? AssistedLodStrategy.PreserveMod);
        }

        repairProgress.Visibility = Visibility.Collapsed;
        if (!result.Success)
        {
            repairButton.IsEnabled = true;
            advancedRepairButton.IsEnabled = true;
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = L("VersionCheckDetail.RepairFailed", "Repair failed: {message}")
                    .Replace("{message}", result.ErrorMessage ?? L("Converters.Unknown", "Unknown"))
            });
            return;
        }

        await message.RefreshAsync();
        repairStatus.Text = useGameReferences
            ? usesAutomaticLod
                ? L(
                        "VersionCheckDetail.AutomaticRepairSuccess",
                        "Automatically updated {count} Unit(s): preserved mod LOD for {preserve}, used game LOD for {game}. Original files were kept as backups.")
                    .Replace("{count}", result.AppliedActionCount.ToString())
                    .Replace("{preserve}", (_automaticLodPlan?.AutomaticPreserveUnitCount ?? 0).ToString())
                    .Replace("{game}", (_automaticLodPlan?.AutomaticGameLodUnitCount ?? 0).ToString())
                : L(
                        usesMixedLod
                            ? "VersionCheckDetail.AssistedRepairSuccessMixedLod"
                            : usesGameLod
                                ? "VersionCheckDetail.AssistedRepairSuccessGameLod"
                                : "VersionCheckDetail.AssistedRepairSuccessPreserveLod",
                        usesMixedLod
                            ? "Updated {count} Unit(s) with per-Unit LOD choices. Original files were kept as backups."
                            : usesGameLod
                                ? "Updated {count} Unit(s) with current game LOD data. Original files were kept as backups."
                                : "Updated {count} Unit(s) while preserving mod LOD data. Original files were kept as backups.")
                    .Replace("{count}", result.AppliedActionCount.ToString())
            : L("VersionCheckDetail.RepairSuccess", "Repaired {count} issue(s). Original files were kept as backups.")
                .Replace("{count}", result.AppliedActionCount.ToString());
        repairStatus.Visibility = Visibility.Visible;
        repairButton.Visibility = Visibility.Collapsed;
        advancedRepairButton.Visibility = Visibility.Collapsed;
    }
    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_fullReport);
            copyStatus.Visibility = Visibility.Visible;
            await Task.Delay(1600);
            copyStatus.Visibility = Visibility.Collapsed;
        }
        catch
        {
            copyStatus.Visibility = Visibility.Collapsed;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Overlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    private void Dialog_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void Overlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Focus();
    }

    private void Close()
    {
        Visibility = Visibility.Hidden;
        DataContext = null;
        _currentMessage = null;
        _repairPlan = null;
        _automaticLodPlan = null;
        _preserveLodPlan = null;
        _gameLodPlan = null;
        _useGameReferences = false;
        _fullReport = string.Empty;
    }
}