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
    public required IReadOnlySet<long> UnitsMissingGameReference { get; init; }
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

internal sealed class BackupHistoryItemViewData
{
    public required ModBackupEntry Entry { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string Status { get; init; }
    public required Brush StatusBrush { get; init; }
    public bool CanRestore => Entry.CanRestore;
}

/// <summary>按原始补丁文件分组的备份历史（带选项的模组每个选项目录一组）</summary>
internal sealed class BackupHistoryGroupViewData
{
    public required string Title { get; init; }
    public required string EntryCountText { get; init; }
    public required IReadOnlyList<BackupHistoryItemViewData> Entries { get; init; }
}

/// <summary>整模组回滚的时间点选项</summary>
internal sealed class RollbackPointViewData
{
    public required DateTime Time { get; init; }
    public required string DisplayText { get; init; }

    public override string ToString() => DisplayText;
}

internal partial class VersionCheckDetailOverlay : UserControl, IRecipient<VersionCheckDetailMessage>
{
    private const int MaxVisibleIssues = 50;
    private readonly LocalizationService? _localizationService;
    private readonly VersionCheckService? _versionCheckService;
    private readonly RepairDisclaimerService? _repairDisclaimerService;
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
        DataContext = null;
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

        if (Application.Current is App disclaimerApp &&
            disclaimerApp.Host?.Services?.GetService(typeof(RepairDisclaimerService)) is RepairDisclaimerService disclaimerService)
        {
            _repairDisclaimerService = disclaimerService;
        }
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
        rollbackPanel.Visibility = Visibility.Collapsed;
        backupHistoryLoading.Visibility = Visibility.Visible;
        backupOperationStatus.Text = string.Empty;
        cleanBackupsButton.IsEnabled = false;
        backupHistoryExpander.IsExpanded = false;
        _repairControlsBusy = false;
        RefreshRepairControls();
        DataContext = BuildViewData(message);
        Visibility = Visibility.Visible;
        Focus();
        _ = LoadRepairPlanAsync(message);
        _ = LoadBackupHistoryAsync(message);
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
        ResetRepairPlans();
        _backupHistory = new ModBackupHistory();
        backupHistoryItems.ItemsSource = null;
        _repairControlsBusy = false;
        _fullReport = string.Empty;
    }
}
