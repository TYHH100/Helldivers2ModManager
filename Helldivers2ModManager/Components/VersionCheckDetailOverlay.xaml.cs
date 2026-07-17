using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Extensions;
using Helldivers2ModManager.Core.UI;
using Helldivers2ModManager.Core.Compatibility;
using Helldivers2ModManager.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging;

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
    public bool IsUiTestSample { get; init; }
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

internal sealed class BackupHistoryItemViewData
{
    public required ModBackupEntry Entry { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string Status { get; init; }
    public required Brush StatusBrush { get; init; }
    public bool CanRestore => Entry.CanRestore;
}

internal partial class VersionCheckDetailOverlay : UserControl, IRecipient<VersionCheckDetailMessage>
{
    private static WeakReference<VersionCheckDetailOverlay>? s_current;
    private const int MaxVisibleIssues = 50;
    private LocalizationService? _localizationService;
    private VersionCheckService? _versionCheckService;
    private RepairDisclaimerService? _repairDisclaimerService;
    private IDialogService? _dialogService;
    private IClipboardService? _clipboardService;
    private IRepairPlanner? _repairPlanner;
    private IRepairExecutor? _repairExecutor;
    private ICompanionRecoveryService? _companionRecoveryService;
    private ILogger<VersionCheckDetailOverlay>? _logger;
    private VersionCheckDetailMessage? _currentMessage;
    private ModRepairPlan? _repairPlan;
    private AssistedModRepairPlan? _automaticLodPlan;
    private AssistedModRepairPlan? _preserveLodPlan;
    private AssistedModRepairPlan? _gameLodPlan;
    private ModBackupHistory _backupHistory = new();
    private CompanionRecoveryPlan? _companionRecoveryPlan;
    private bool _useGameReferences;
    private bool _repairControlsBusy;
    private string _fullReport = string.Empty;

    public VersionCheckDetailOverlay()
    {
        InitializeComponent();
        s_current = new WeakReference<VersionCheckDetailOverlay>(this);
        WeakReferenceMessenger.Default.Register<VersionCheckDetailMessage>(this);
    }

    internal static VersionCheckDetailOverlay? Current =>
        s_current is not null && s_current.TryGetTarget(out var current) ? current : null;

    internal void Configure(
        LocalizationService localizationService,
        VersionCheckService versionCheckService,
        RepairDisclaimerService repairDisclaimerService,
        IDialogService dialogService,
        IRepairPlanner repairPlanner,
        IRepairExecutor repairExecutor,
        ICompanionRecoveryService companionRecoveryService,
        IClipboardService clipboardService,
        ILogger<VersionCheckDetailOverlay> logger)
    {
        _localizationService = localizationService;
        _versionCheckService = versionCheckService;
        _repairDisclaimerService = repairDisclaimerService;
        _dialogService = dialogService;
        _repairPlanner = repairPlanner;
        _repairExecutor = repairExecutor;
        _companionRecoveryService = companionRecoveryService;
        _clipboardService = clipboardService;
        _logger = logger;
    }

    public void Receive(VersionCheckDetailMessage message)
    {
        _currentMessage = message;
        ResetRepairPlans();
        _backupHistory = new ModBackupHistory();
        _companionRecoveryPlan = null;
        _useGameReferences = false;
        _fullReport = message.FullReport;
        copyStatus.Visibility = Visibility.Collapsed;
        repairStatus.Visibility = Visibility.Collapsed;
        repairProgress.Visibility = Visibility.Collapsed;
        backupHistoryItems.ItemsSource = null;
        backupHistorySummary.Text = string.Empty;
        backupHistoryLoading.Visibility = Visibility.Visible;
        backupOperationStatus.Text = string.Empty;
        cleanBackupsButton.IsEnabled = false;
        backupHistoryExpander.IsExpanded = false;
        _repairControlsBusy = false;
        RefreshRepairControls();
        DataContext = BuildViewData(message);
        Visibility = Visibility.Visible;
        Focus();
        if (message.IsUiTestSample)
        {
            backupHistoryLoading.Visibility = Visibility.Collapsed;
            backupHistorySummary.Text = L(
                "VersionCheckBackup.None",
                "No HD2MM repair backups were found for this mod.");
            backupHistoryExpander.IsExpanded = true;
            technicalDetailsExpander.IsExpanded = true;
            return;
        }
        LoadRepairPlanAsync(message).Observe(
            ex => _logger?.LogError(ex, "Failed to supervise repair plan loading"));
        LoadBackupHistoryAsync(message).Observe(
            ex => _logger?.LogError(ex, "Failed to supervise backup history loading"));
    }

    internal void ShowUiTestSample()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("HD2MM_RUN_UI_TESTS"), "1", StringComparison.Ordinal))
            throw new InvalidOperationException("UI test samples are only available in UI test mode.");

        var fileName = "very_long_localization_layout_sample_filename_0123456789abcdef.patch_999";
        var unitDetails = new List<UnitResourceDetail>
        {
            new()
            {
                FileName = fileName,
                EntryIndex = 1,
                FileId = 0x123456789,
                Version = 0xA4CD20,
                DataSize = 128,
                EndingOffset = 512,
                ExpectedDataSize = 520,
                DeclaredSizeMatchesInternal = false,
                IsTruncated = true,
                UnitDataInBounds = false,
                LODGroupInBounds = false,
                LayoutFormatChecked = true,
                LayoutFormatValid = false,
                LayoutFormatIssueCount = 3,
                Warning = "The declared Unit data range exceeds the patch boundary and contains several legacy layout entries."
            },
            new()
            {
                FileName = fileName,
                EntryIndex = 2,
                FileId = 0x223456789,
                Version = 0xA4CD21,
                DataSize = 256,
                EndingOffset = 400,
                ExpectedDataSize = 408,
                DeclaredSizeMatchesInternal = false,
                UnitDataInBounds = true,
                LODGroupInBounds = false,
                Warning = "The LOD group extends beyond the Unit data declared by the table of contents."
            }
        };
        var patch = new PatchFileAnalysis
        {
            FileName = fileName,
            FileSize = 4_294_967_296,
            HealthStatus = PatchHealthStatus.Corrupted,
            NumTypes = 4,
            NumFiles = 18,
            TotalResources = 16,
            TypeDistributionValid = false,
            TypeDistributionIssueCount = 2,
            HeaderValid = false,
            FileEntriesInBounds = false,
            MainDataBoundsValid = false,
            MainDataIssueCount = 3,
            EntryIndicesValid = false,
            EntryIndexIssueCount = 2,
            RequiresGpuResources = true,
            HasGpuResources = false,
            RequiresStream = true,
            HasStream = false,
            GpuResourceBoundsValid = false,
            GpuResourceIssueCount = 4,
            GpuAlignmentIssueCount = 2,
            StreamBoundsValid = false,
            StreamIssueCount = 3,
            StreamAlignmentIssueCount = 1,
            UnitDetails = unitDetails,
            Message = "The patch header, resource table, and companion-file ranges contain multiple structural problems."
        };
        var analysis = new ModDetailedAnalysis
        {
            PatchFiles = [patch],
            HasStructuralIssues = true,
            HasCompanionFileIssues = true,
            HasUnitStructuralIssues = true,
            HasGpuResourceIssues = true,
            HasStreamResourceIssues = true,
            TotalPatchFiles = 1,
            FilesWithUnits = 1,
            WarningFileCount = 0,
            CorruptedFileCount = 1
        };
        var directory = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "TestStorage", "Mods", "UiTestMod"));
        Receive(new VersionCheckDetailMessage
        {
            ModName = "A deliberately very long mod name used to verify localized version detail layout",
            Status = ModVersionStatus.Incompatible,
            GameVersion = 0xA4CD36,
            LastChecked = DateTime.UtcNow,
            PatchUnits =
            [
                new PatchUnitInfo { FileName = fileName, FileId = 0x123456789, Version = 0xA4CD20, DataSize = 128 },
                new PatchUnitInfo { FileName = fileName, FileId = 0x223456789, Version = 0xA4CD21, DataSize = 256 }
            ],
            Analysis = analysis,
            FullReport = "Compatibility reference: current game files\nConfidence: high\nStructural issues: multiple\n" +
                         "This intentionally long technical report verifies horizontal scrolling without clipping localized status text.",
            ModDirectory = directory,
            RefreshAsync = static () => Task.CompletedTask,
            IsUiTestSample = true
        });
    }

    private async Task LoadRepairPlanAsync(VersionCheckDetailMessage message)
    {
        if (_versionCheckService is null)
            return;

        ResetRepairPlans();
        SetRepairControlsBusy(true);
        repairProgress.Text = L("VersionCheckDetail.CheckingRepair", "Checking repair options...");
        repairProgress.Visibility = Visibility.Visible;
        try
        {
            var recoveryPlan = await _versionCheckService.CreateCompanionRecoveryPlanAsync(
                message.ModDirectory);
            if (!ReferenceEquals(_currentMessage, message))
                return;
            _companionRecoveryPlan = recoveryPlan;
            if (recoveryPlan.MissingCount > 0)
            {
                recoveryButtonText.Text = recoveryPlan.CanRecover
                    ? F("VersionCheckRecovery.Button", "Recover {count} companion file(s)", new { count = recoveryPlan.RecoverableCount })
                    : L("VersionCheckRecovery.Unavailable", "No reliable companion source");
                return;
            }

            var plan = await _versionCheckService.CreateRepairPlanAsync(message.ModDirectory);
            if (!ReferenceEquals(_currentMessage, message))
                return;

            _repairPlan = plan;
            if (plan.CanRepair)
            {
                _useGameReferences = false;
                repairButtonText.Text = F("VersionCheckDetail.RepairButton", "Repair {count} issue(s)", new { count = plan.ActionCount });
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
                repairButtonText.Text = F("VersionCheckDetail.AutomaticRepairButton", "Automatically repair {count} Unit(s)", new { count = automaticLodPlan.ActionCount });
            }
        }
        catch (Exception ex)
        {
            repairStatus.Text = F(
                "VersionCheckDetail.RepairPlanFailed",
                "Failed to load repair options: {message}",
                new { message = ex.Message });
            repairStatus.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_currentMessage, message))
            {
                repairProgress.Visibility = Visibility.Collapsed;
                SetRepairControlsBusy(false);
            }
        }
    }


    private async Task LoadBackupHistoryAsync(VersionCheckDetailMessage message)
    {
        if (_versionCheckService is null)
            return;

        backupHistoryLoading.Text = L("VersionCheckBackup.Loading", "Loading backup history...");
        backupHistoryLoading.Visibility = Visibility.Visible;
        try
        {
            var history = await _versionCheckService.GetBackupHistoryAsync(message.ModDirectory);
            if (!ReferenceEquals(_currentMessage, message))
                return;

            _backupHistory = history;
            backupHistoryItems.ItemsSource = history.Entries
                .Select(BuildBackupHistoryItem)
                .ToList();
            backupHistorySummary.Text = history.Entries.Count == 0
                ? L("VersionCheckBackup.None", "No HD2MM repair backups were found for this mod.")
                : F(
                    "VersionCheckBackup.Summary",
                    "{count} backup(s), {restorable} restorable, {invalid} invalid.",
                    new
                    {
                        count = history.Entries.Count,
                        restorable = history.RestorableCount,
                        invalid = history.InvalidCount
                    });
            cleanBackupsButton.IsEnabled = history.Entries
                .GroupBy(entry => entry.OriginalPath, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() > 3);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_currentMessage, message))
            {
                backupHistorySummary.Text = F(
                    "VersionCheckBackup.LoadFailed",
                    "Failed to load backup history: {message}",
                    new { message = ex.Message });
            }
        }
        finally
        {
            if (ReferenceEquals(_currentMessage, message))
                backupHistoryLoading.Visibility = Visibility.Collapsed;
        }
    }

    private BackupHistoryItemViewData BuildBackupHistoryItem(ModBackupEntry entry)
    {
        var status = !entry.CanRestore
            ? F("VersionCheckBackup.Invalid", "Cannot restore: {message}", new { message = entry.ValidationMessage })
            : entry.CurrentMatchesBackup
                ? L("VersionCheckBackup.MatchesCurrent", "Current patch already matches this backup.")
                : !entry.CurrentExists
                    ? L("VersionCheckBackup.CurrentMissing", "Current patch is missing; restore will recreate it.")
                    : L("VersionCheckBackup.Ready", "Ready to restore. The current patch will be backed up first.");
        var statusBrush = !entry.CanRestore
            ? GetBrush("DangerBrush", Colors.IndianRed)
            : entry.CurrentMatchesBackup
                ? GetBrush("WarningBrush", Colors.Goldenrod)
                : GetBrush("SuccessBrush", Colors.ForestGreen);
        var detail = F(
            "VersionCheckBackup.ItemDetail",
            "{time} | {kind} | {actions} action(s) | {size} | SHA-256 {hash}",
            new
            {
                time = entry.CreatedLocal.ToString("yyyy-MM-dd HH:mm:ss"),
                kind = GetBackupKindLabel(entry.RepairKind),
                actions = entry.ActionCount,
                size = FormatFileSize(entry.BackupSize),
                hash = entry.BackupSha256.Length >= 12 ? entry.BackupSha256[..12] : "-"
            });
        return new BackupHistoryItemViewData
        {
            Entry = entry,
            Title = entry.OriginalFileName,
            Detail = detail,
            Status = status,
            StatusBrush = statusBrush
        };
    }

    private string GetBackupKindLabel(ModBackupRepairKind kind)
    {
        return kind switch
        {
            ModBackupRepairKind.SafeMetadata => L("VersionCheckBackup.KindMetadata", "Safe metadata repair"),
            ModBackupRepairKind.AutomaticLod => L("VersionCheckBackup.KindAutomatic", "Automatic LOD repair"),
            ModBackupRepairKind.PreserveModLod => L("VersionCheckBackup.KindPreserve", "Preserve mod LOD"),
            ModBackupRepairKind.UseGameLod => L("VersionCheckBackup.KindGame", "Use game LOD"),
            ModBackupRepairKind.MixedLod => L("VersionCheckBackup.KindMixed", "Mixed per-Unit LOD"),
            ModBackupRepairKind.PreRestore => L("VersionCheckBackup.KindPreRestore", "Snapshot before restore"),
            _ => L("VersionCheckBackup.KindLegacy", "Legacy backup")
        };
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024d:F1} KiB";
        return $"{bytes / (1024d * 1024d):F1} MiB";
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
            summary = F("VersionCheckDetail.SummaryTruncated", "{count} Unit resource(s) are truncated by their TOC size.", new { count = truncatedCount });
        }
        else if (message.Analysis.CorruptedFileCount > 0)
        {
            summary = F("VersionCheckDetail.SummaryCorrupted", "{count} patch file(s) contain structural damage.", new { count = message.Analysis.CorruptedFileCount });
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
                ? F("VersionCheckDetail.HealthyUnits", "{count} Unit(s) passed structural checks", new { count = healthyUnits })
                : string.Empty,
            VisibleIssues = issues.Take(MaxVisibleIssues).ToList(),
            HiddenIssueSummary = hiddenCount > 0
                ? F("VersionCheckDetail.HiddenIssues", "{count} more issue(s) are available in technical details.", new { count = hiddenCount })
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
                Description = F(
                    "VersionCheckDetail.VersionMismatchDescription",
                    "{count} Unit(s) use {versions}; reference is {reference}.",
                    new { count = versionMismatches.Count, versions, reference = $"0x{message.GameVersion:X8}" })
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
                        Title = F("VersionCheckDetail.UnitTruncatedTitle", "Unit #{index} data is truncated", new { index = unit.EntryIndex }),
                        FileName = patch.FileName,
                        Description = F(
                            "VersionCheckDetail.UnitTruncatedDescription",
                            "TOC declares {declared} bytes, internal size is {expected}; {difference} bytes are missing. ID {fileId}",
                            new
                            {
                                declared = unit.DataSize,
                                expected = unit.ExpectedDataSize,
                                difference = Math.Max(0, unit.ExpectedDataSize - unit.DataSize),
                                fileId = $"0x{unit.FileId:X16}"
                            })
                    });
                }
                else if (!unit.DeclaredSizeMatchesInternal)
                {
                    issues.Add(new VersionCheckDiagnosticIssue
                    {
                        Icon = "\uE7BA",
                        Brush = warning,
                        Title = F("VersionCheckDetail.UnitSizeMismatchTitle", "Unit #{index} size mismatch", new { index = unit.EntryIndex }),
                        FileName = patch.FileName,
                        Description = F(
                            "VersionCheckDetail.UnitSizeMismatchDescription",
                            "TOC declares {declared} bytes; internal size is {expected}. ID {fileId}",
                            new { declared = unit.DataSize, expected = unit.ExpectedDataSize, fileId = $"0x{unit.FileId:X16}" })
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
            AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.TypeTableTitle", "Resource type table is inconsistent", F("VersionCheckDetail.TypeTableDescription", "The type table does not match the {count} actual file entries.", new { count = patch.NumFiles }));
        if (!patch.MainDataBoundsValid)
            AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.MainBoundsTitle", "Main resource data is out of bounds", F("VersionCheckDetail.MainBoundsDescription", "{count} invalid or overlapping range(s).", new { count = patch.MainDataIssueCount }));
        if (!patch.EntryIndicesValid)
            AddSimpleIssue(issues, warning, patch.FileName, "VersionCheckDetail.EntryIndexTitle", "TOC entry indices are not sequential", F("VersionCheckDetail.EntryIndexDescription", "{count} invalid index value(s).", new { count = patch.EntryIndexIssueCount }));
        if (patch.RequiresGpuResources && !patch.HasGpuResources)
            AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.MissingGpuTitle", "Required GPU resource file is missing", L("VersionCheckDetail.MissingGpuDescription", "The patch contains non-zero GPU resource references."));
        if (patch.RequiresStream && !patch.HasStream)
            AddSimpleIssue(issues, danger, patch.FileName, "VersionCheckDetail.MissingStreamTitle", "Required stream file is missing", L("VersionCheckDetail.MissingStreamDescription", "The patch contains non-zero stream resource references."));
        if (!patch.GpuResourceBoundsValid || patch.GpuAlignmentIssueCount > 0)
            AddSimpleIssue(issues, patch.GpuResourceBoundsValid ? warning : danger, patch.FileName, "VersionCheckDetail.GpuIssueTitle", "GPU resource range problem", F("VersionCheckDetail.ResourceRangeDescription", "Out of bounds: {bounds}; misaligned: {alignment}.", new { bounds = patch.GpuResourceIssueCount, alignment = patch.GpuAlignmentIssueCount }));
        if (!patch.StreamBoundsValid || patch.StreamAlignmentIssueCount > 0)
            AddSimpleIssue(issues, patch.StreamBoundsValid ? warning : danger, patch.FileName, "VersionCheckDetail.StreamIssueTitle", "stream resource range problem", F("VersionCheckDetail.ResourceRangeDescription", "Out of bounds: {bounds}; misaligned: {alignment}.", new { bounds = patch.StreamIssueCount, alignment = patch.StreamAlignmentIssueCount }));
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

    private string F(string key, string fallback, object arguments)
    {
        if (_localizationService is null)
            return fallback;
        return _localizationService.Format(key, arguments);
    }

    private Task ShowErrorAsync(string message)
    {
        if (_dialogService is null)
            return Task.CompletedTask;

        return _dialogService.ShowMessageAsync(
            new MessageDialogRequest(
                L("MessageBox.Error", "Error"),
                message,
                MessageDialogSeverity.Error),
            CancellationToken.None);
    }

    private async Task<bool> EnsureRepairDisclaimerAcceptedAsync()
    {
        if (_repairDisclaimerService is not null)
            return await _repairDisclaimerService.EnsureAcceptedAsync(CancellationToken.None);

        await ShowErrorAsync(L(
            "VersionCheckDisclaimer.SettingsUnavailable",
            "Repair settings are unavailable. Restart HD2MM and try again."));
        return false;
    }

    private void ResetRepairPlans()
    {
        _repairPlan = null;
        _automaticLodPlan = null;
        _preserveLodPlan = null;
        _gameLodPlan = null;
        _companionRecoveryPlan = null;
        _useGameReferences = false;
    }

    private void SetRepairControlsBusy(bool busy)
    {
        _repairControlsBusy = busy;
        RefreshRepairControls();
    }

    private void RefreshRepairControls()
    {
        var recoveryVisible = _companionRecoveryPlan?.MissingCount > 0;
        var safeRepairVisible = !recoveryVisible && _repairPlan?.CanRepair == true;
        var automaticRepairVisible = !recoveryVisible && !safeRepairVisible &&
            _automaticLodPlan?.CanRepair == true;
        var repairVisible = safeRepairVisible || automaticRepairVisible;

        recoveryButton.Visibility = recoveryVisible ? Visibility.Visible : Visibility.Collapsed;
        recoveryButton.IsEnabled = !_repairControlsBusy &&
            _companionRecoveryPlan?.CanRecover == true;
        repairButton.Visibility = repairVisible ? Visibility.Visible : Visibility.Collapsed;
        repairButton.IsEnabled = !_repairControlsBusy && repairVisible;
        advancedRepairButton.Visibility = automaticRepairVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        advancedRepairButton.IsEnabled = !_repairControlsBusy && automaticRepairVisible;
        backupHistoryExpander.IsEnabled = !_repairControlsBusy;
    }

    private static Brush GetBrush(string resourceKey, Color fallback)
    {
        return Application.Current.TryFindResource(resourceKey) as Brush ?? new SolidColorBrush(fallback);
    }


    private async void RecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_companionRecoveryPlan?.CanRecover != true)
            return;

        if (!await EnsureRepairDisclaimerAcceptedAsync())
            return;

        if (_dialogService is null || !await _dialogService.ShowAsync(
            new DialogRequest(
                L("VersionCheckRecovery.ConfirmTitle", "Recover missing companion files"),
                F(
                    "VersionCheckRecovery.ConfirmMessage",
                    "HD2MM will recover {count} companion file(s) from byte-verified sources, stage them in temporary files, and commit only after bounds validation. Continue?",
                    new { count = _companionRecoveryPlan.RecoverableCount })),
            CancellationToken.None))
            return;

        await ExecuteCompanionRecoveryAsync();
    }

    private async Task ExecuteCompanionRecoveryAsync()
    {
        if (_companionRecoveryService is null || _currentMessage is null ||
            _companionRecoveryPlan?.CanRecover != true)
        {
            return;
        }

        var message = _currentMessage;
        SetRepairControlsBusy(true);
        repairProgress.Text = L(
            "VersionCheckRecovery.Recovering",
            "Recovering and validating companion files...");
        repairProgress.Visibility = Visibility.Visible;
        try
        {
            var result = await _companionRecoveryService.RecoverAsync(
                message.ModDirectory.FullName,
                CancellationToken.None);
            if (!result.IsSuccess)
            {
                await ShowErrorAsync(F("VersionCheckRecovery.Failed", "Companion recovery failed: {message}", new { message = result.ErrorMessage ?? result.ErrorCode ?? L("Converters.Unknown", "Unknown") }));
                return;
            }

            repairStatus.Text = F(
                "VersionCheckRecovery.Success",
                "Recovered {count} companion file(s).",
                new { count = result.Value });
            repairStatus.Visibility = Visibility.Visible;
            await message.RefreshAsync();
            if (ReferenceEquals(_currentMessage, message))
                await LoadRepairPlanAsync(message);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(F("VersionCheckRecovery.Failed", "Companion recovery failed: {message}", new { message = ex.Message }));
        }
        finally
        {
            if (ReferenceEquals(_currentMessage, message))
            {
                repairProgress.Visibility = Visibility.Collapsed;
                SetRepairControlsBusy(false);
            }
        }
    }
    private async void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMessage is null)
            return;

        if (!await EnsureRepairDisclaimerAcceptedAsync())
            return;

        if (_useGameReferences)
        {
            await ShowAutomaticRepairConfirmation();
            return;
        }

        var fileCount = _repairPlan?.FileCount ?? 0;
        var actionCount = _repairPlan?.ActionCount ?? 0;
        if (actionCount == 0)
            return;

        if (_dialogService is null || !await _dialogService.ShowAsync(
            new DialogRequest(
                L("VersionCheckDetail.RepairConfirmTitle", "Repair mod"),
                F(
                    "VersionCheckDetail.RepairConfirmMessage",
                    "HD2MM will back up {files} patch file(s), apply {count} verified metadata repair(s), and validate the result before replacing the originals.",
                    new { files = fileCount, count = actionCount })),
            CancellationToken.None))
            return;

        await ExecuteRepairAsync(false, null, null);
    }

    private async Task ShowAutomaticRepairConfirmation()
    {
        if (_automaticLodPlan?.CanRepair != true)
            return;

        var plan = _automaticLodPlan;
        if (_dialogService is null || !await _dialogService.ShowAsync(
            new DialogRequest(
                L("VersionCheckDetail.AutomaticRepairTitle", "Automatically repair mod"),
                F(
                    "VersionCheckDetail.AutomaticRepairConfirm",
                    "HD2MM analyzed the Unit mesh and GPU structure. It will preserve mod LOD for {preserve} Unit(s), use current game LOD for {game} Unit(s), back up the patch files, and validate the rebuilt result before replacement.",
                    new { preserve = plan.AutomaticPreserveUnitCount, game = plan.AutomaticGameLodUnitCount })),
            CancellationToken.None))
            return;

        await ExecuteRepairAsync(true, null, null);
    }

    private async void AdvancedRepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (_versionCheckService is null || _currentMessage is null)
            return;

        if (!await EnsureRepairDisclaimerAcceptedAsync())
            return;

        var message = _currentMessage;
        SetRepairControlsBusy(true);
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

            await ShowLodStrategySelectionAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(F("VersionCheckDetail.AdvancedRepairFailed", "Failed to load advanced repair strategies: {message}", new { message = ex.Message }));
        }
        finally
        {
            if (ReferenceEquals(_currentMessage, message))
            {
                repairProgress.Visibility = Visibility.Collapsed;
                SetRepairControlsBusy(false);
            }
        }
    }

    private async Task ShowLodStrategySelectionAsync()
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
                F("VersionCheckDetail.LodStrategyPerUnit", "Choose per Unit manually ({count} selectable)", new { count = selectableUnitCount })));
        }
        if (_gameLodPlan?.CanRepair == true)
        {
            options.Add(new LodStrategyOption(
                AssistedLodStrategy.UseGameReference,
                F("VersionCheckDetail.LodStrategyGameReference", "Use current game LOD (standard, {count} Unit(s))", new { count = _gameLodPlan.ActionCount })));
        }
        if (_preserveLodPlan?.CanRepair == true)
        {
            options.Add(new LodStrategyOption(
                AssistedLodStrategy.PreserveMod,
                F("VersionCheckDetail.LodStrategyPreserveMod", "Preserve mod LOD (custom models, {count} Unit(s))", new { count = _preserveLodPlan.ActionCount })));
        }
        if (options.Count == 0)
            return;

        if (_dialogService is null)
            return;

        var labels = options.Select(static option => option.Label).ToArray();
        var selected = await _dialogService.SelectAsync(
            new SelectionDialogRequest(
                L("VersionCheckDetail.LodStrategyTitle", "Choose Unit LOD strategy"),
                L(
                    "VersionCheckDetail.LodStrategyMessage",
                    "LOD compatibility can differ between Units in the same patch. Per-Unit selection is the safest option. Game LOD can prevent preview crashes but may hide custom models; preserving mod LOD keeps custom models but an obsolete group can crash the preview. Every repair creates a backup."),
                labels),
            CancellationToken.None);
        if (selected is null)
            return;

        var option = options.FirstOrDefault(item => string.Equals(item.Label, selected, StringComparison.Ordinal));
        if (option?.Strategy is AssistedLodStrategy strategy)
            await ExecuteRepairAsync(true, strategy, null);
        else if (option is not null)
            await ShowUnitLodSelectionAsync();
    }

    private async Task ShowUnitLodSelectionAsync()
    {
        var candidates = _gameLodPlan?.Actions
            .Where(action => action.LodDataDiffers)
            .GroupBy(action => action.FileId)
            .Select(group =>
            {
                var first = group.First();
                var title = string.IsNullOrWhiteSpace(first.FriendlyName)
                    ? F("VersionCheckDetail.UnitSelectionUnnamed", "Unit {id}", new { id = $"0x{(ulong)first.FileId:X16}" })
                    : first.FriendlyName;
                var lodSizes = string.Join(", ", group
                    .Select(action => $"{action.CurrentLodSize}->{action.ReferenceLodSize}")
                    .Distinct(StringComparer.Ordinal));
                var description = F(
                    "VersionCheckDetail.UnitSelectionDescription",
                    "{occurrences} patch entry(s) | LOD {lodSizes} | ID {id}",
                    new
                    {
                        occurrences = group.Count(),
                        lodSizes,
                        id = $"0x{(ulong)first.FileId:X16}"
                    });
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

        if (_dialogService is null)
            return;

        var selected = await _dialogService.SelectManyAsync(
            new ChecklistDialogRequest(
                L("VersionCheckDetail.UnitSelectionTitle", "Choose Units that keep mod LOD"),
                L(
                    "VersionCheckDetail.UnitSelectionMessage",
                    "Checked Units preserve the mod LOD; unchecked Units use current game LOD. Equal LOD sizes can still contain incompatible data. If the mod was already converted entirely to game LOD, restore its HD2MM backup before using this screen."),
                candidates.Select(item => new ChecklistDialogOption(
                    item.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    item.Title,
                    item.Description,
                    item.IsSelected)).ToArray()),
            CancellationToken.None);
        if (selected is null)
            return;

        var preserveIds = selected
            .Select(static value => long.TryParse(value, out var id) ? (long?)id : null)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToHashSet();
        await ExecuteRepairAsync(true, null, preserveIds);
    }
    private async Task ExecuteRepairAsync(
        bool useGameReferences,
        AssistedLodStrategy? lodStrategy,
        IReadOnlySet<long>? preserveModLodUnitIds)
    {
        if (_versionCheckService is null || _currentMessage is null)
            return;

        var message = _currentMessage;
        SetRepairControlsBusy(true);
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

        try
        {
            ModRepairResult result;
            if (!useGameReferences)
            {
                result = await ExecuteSafeMetadataRepairAsync(message);
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

            if (!result.Success)
            {
                await ShowErrorAsync(F("VersionCheckDetail.RepairFailed", "Repair failed: {message}", new { message = result.ErrorMessage ?? L("Converters.Unknown", "Unknown") }));
                return;
            }

            await message.RefreshAsync();
            repairStatus.Text = useGameReferences
                ? usesAutomaticLod
                    ? F(
                        "VersionCheckDetail.AutomaticRepairSuccess",
                        "Automatically updated {count} Unit(s): preserved mod LOD for {preserve}, used game LOD for {game}. Original files were kept as backups.",
                        new
                        {
                            count = result.AppliedActionCount,
                            preserve = _automaticLodPlan?.AutomaticPreserveUnitCount ?? 0,
                            game = _automaticLodPlan?.AutomaticGameLodUnitCount ?? 0
                        })
                    : F(
                        usesMixedLod
                            ? "VersionCheckDetail.AssistedRepairSuccessMixedLod"
                            : usesGameLod
                                ? "VersionCheckDetail.AssistedRepairSuccessGameLod"
                                : "VersionCheckDetail.AssistedRepairSuccessPreserveLod",
                        usesMixedLod
                            ? "Updated {count} Unit(s) with per-Unit LOD choices. Original files were kept as backups."
                            : usesGameLod
                                ? "Updated {count} Unit(s) with current game LOD data. Original files were kept as backups."
                                : "Updated {count} Unit(s) while preserving mod LOD data. Original files were kept as backups.",
                        new { count = result.AppliedActionCount })
                : F("VersionCheckDetail.RepairSuccess", "Repaired {count} issue(s). Original files were kept as backups.", new { count = result.AppliedActionCount });
            repairStatus.Visibility = Visibility.Visible;
            ResetRepairPlans();
            if (ReferenceEquals(_currentMessage, message))
                await LoadBackupHistoryAsync(message);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(F("VersionCheckDetail.RepairFailed", "Repair failed: {message}", new { message = ex.Message }));
        }
        finally
        {
            if (ReferenceEquals(_currentMessage, message))
            {
                repairProgress.Visibility = Visibility.Collapsed;
                SetRepairControlsBusy(false);
            }
        }
    }

    private async Task<ModRepairResult> ExecuteSafeMetadataRepairAsync(VersionCheckDetailMessage message)
    {
        if (_repairPlanner is null || _repairExecutor is null)
            return new ModRepairResult
            {
                ErrorMessage = L("VersionCheckRepair.ServicesUnavailable", "The transactional repair services are unavailable.")
            };

        var patchPaths = _repairPlan?.Actions
            .Select(static action => action.PatchFilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        if (patchPaths.Length == 0)
            return new ModRepairResult { ErrorMessage = L("VersionCheckRepair.NothingToRepair", "Nothing to repair.") };

        var plans = new List<RepairPlan>(patchPaths.Length);
        foreach (var patchPath in patchPaths)
        {
            var planning = await _repairPlanner.PlanAsync(patchPath, CancellationToken.None);
            if (!planning.IsSuccess || planning.Value is null)
            {
                return new ModRepairResult
                {
                    ErrorMessage = planning.ErrorMessage ?? planning.ErrorCode ?? "Repair planning failed."
                };
            }
            plans.Add(planning.Value);
        }

        var execution = await _repairExecutor.ExecuteBatchAsync(plans, null, CancellationToken.None);
        return execution.IsSuccess
            ? new ModRepairResult
            {
                Success = true,
                AppliedActionCount = plans.Sum(static plan => plan.Actions.Count)
            }
            : new ModRepairResult
            {
                ErrorMessage = execution.ErrorMessage ?? execution.ErrorCode
            };
    }

    private async void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMessage is null ||
            (sender as FrameworkElement)?.Tag is not ModBackupEntry entry ||
            !entry.CanRestore)
        {
            return;
        }

        if (_dialogService is null || !await _dialogService.ShowAsync(
            new DialogRequest(
                L("VersionCheckBackup.RestoreTitle", "Restore patch backup"),
                F(
                    "VersionCheckBackup.RestoreConfirm",
                    "Restore {file} from {time}? HD2MM will snapshot the current patch first, replace it atomically, and verify the restored SHA-256.",
                    new { file = entry.OriginalFileName, time = entry.CreatedLocal.ToString("g") })),
            CancellationToken.None))
            return;

        await ExecuteBackupRestoreAsync(entry);
    }

    private async Task ExecuteBackupRestoreAsync(ModBackupEntry entry)
    {
        if (_versionCheckService is null || _currentMessage is null)
            return;

        var message = _currentMessage;
        SetRepairControlsBusy(true);
        backupOperationStatus.Text = L("VersionCheckBackup.Restoring", "Restoring and validating backup...");
        backupOperationStatus.Foreground = GetBrush("SystemAccentBrush", Colors.DodgerBlue);
        try
        {
            var result = await _versionCheckService.RestoreBackupAsync(
                message.ModDirectory,
                entry.BackupPath);
            if (!result.Success)
            {
                await ShowErrorAsync(F("VersionCheckBackup.RestoreFailed", "Backup restore failed: {message}", new { message = result.ErrorMessage ?? L("Converters.Unknown", "Unknown") }));
                return;
            }

            backupOperationStatus.Text = L(
                "VersionCheckBackup.RestoreSuccess",
                "Backup restored. A snapshot of the replaced patch was retained.");
            backupOperationStatus.Foreground = GetBrush("SuccessBrush", Colors.ForestGreen);
            await message.RefreshAsync();
            if (ReferenceEquals(_currentMessage, message))
            {
                await LoadBackupHistoryAsync(message);
                await LoadRepairPlanAsync(message);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(F("VersionCheckBackup.RestoreFailed", "Backup restore failed: {message}", new { message = ex.Message }));
        }
        finally
        {
            if (ReferenceEquals(_currentMessage, message))
                SetRepairControlsBusy(false);
        }
    }

    private async void DeleteBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMessage is null || (sender as FrameworkElement)?.Tag is not ModBackupEntry entry)
            return;

        if (_dialogService is null || !await _dialogService.ShowAsync(
            new DialogRequest(
                L("VersionCheckBackup.DeleteTitle", "Delete backup"),
                F(
                    "VersionCheckBackup.DeleteConfirm",
                    "Delete the backup of {file} from {time}? The final restorable backup for a patch is always protected.",
                    new { file = entry.OriginalFileName, time = entry.CreatedLocal.ToString("g") })),
            CancellationToken.None))
            return;

        await ExecuteBackupDeleteAsync(entry);
    }

    private async Task ExecuteBackupDeleteAsync(ModBackupEntry entry)
    {
        if (_versionCheckService is null || _currentMessage is null)
            return;

        var message = _currentMessage;
        backupHistoryExpander.IsEnabled = false;
        try
        {
            var result = await _versionCheckService.DeleteBackupAsync(
                message.ModDirectory,
                entry.BackupPath);
            if (!result.Success)
            {
                await ShowErrorAsync(F("VersionCheckBackup.DeleteFailed", "Backup deletion failed: {message}", new { message = result.ErrorMessage ?? L("Converters.Unknown", "Unknown") }));
                return;
            }

            backupOperationStatus.Text = L("VersionCheckBackup.DeleteSuccess", "Backup deleted.");
            backupOperationStatus.Foreground = GetBrush("SuccessBrush", Colors.ForestGreen);
            await LoadBackupHistoryAsync(message);
        }
        finally
        {
            if (ReferenceEquals(_currentMessage, message))
                backupHistoryExpander.IsEnabled = true;
        }
    }

    private async void CleanBackupsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMessage is null)
            return;

        if (_dialogService is null || !await _dialogService.ShowAsync(
            new DialogRequest(
                L("VersionCheckBackup.CleanTitle", "Clean old backups"),
                L(
                "VersionCheckBackup.CleanConfirm",
                "Keep the newest three backups for each patch and delete older entries? At least one restorable backup is always retained.")),
            CancellationToken.None))
            return;

        await ExecuteBackupCleanupAsync();
    }

    private async Task ExecuteBackupCleanupAsync()
    {
        if (_versionCheckService is null || _currentMessage is null)
            return;

        var message = _currentMessage;
        backupHistoryExpander.IsEnabled = false;
        try
        {
            var result = await _versionCheckService.CleanOldBackupsAsync(message.ModDirectory, 3);
            if (!result.Success)
            {
                await ShowErrorAsync(F("VersionCheckBackup.CleanFailed", "Backup cleanup failed: {message}", new { message = result.ErrorMessage ?? L("Converters.Unknown", "Unknown") }));
                return;
            }

            backupOperationStatus.Text = F(
                "VersionCheckBackup.CleanSuccess",
                "Deleted {count} old backup(s).",
                new { count = result.DeletedCount });
            backupOperationStatus.Foreground = GetBrush("SuccessBrush", Colors.ForestGreen);
            await LoadBackupHistoryAsync(message);
        }
        finally
        {
            if (ReferenceEquals(_currentMessage, message))
                backupHistoryExpander.IsEnabled = true;
        }
    }
    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_clipboardService is null)
                return;
            await _clipboardService.SetTextAsync(_fullReport, CancellationToken.None);
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
        ResetRepairPlans();
        _backupHistory = new ModBackupHistory();
        backupHistoryItems.ItemsSource = null;
        _repairControlsBusy = false;
        _fullReport = string.Empty;
    }
}
