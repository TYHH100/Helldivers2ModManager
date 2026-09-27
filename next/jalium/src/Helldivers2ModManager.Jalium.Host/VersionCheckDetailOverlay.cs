using System.Text;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

/// <summary>
/// 版本诊断详情覆盖层。这里保留旧 WPF 覆盖层的工作流，但使用原生 Jalium 控件渲染。
/// </summary>
internal sealed class VersionCheckDetailOverlay : Grid, IDisposable
{
    private static readonly Brush ForegroundBrush = Paint(0xF2, 0xF2, 0xF2);
    private static readonly Brush SecondaryBrush = Paint(0xB3, 0xB3, 0xB3);
    private static readonly Brush StrokeBrush = Paint(0x46, 0x46, 0x46);
    private static readonly Brush SurfaceBrush = Paint(0x2E, 0x2E, 0x2E);
    private static readonly Brush DangerBrush = Paint(0xDC, 0x50, 0x37);
    private static readonly Brush SuccessBrush = Paint(0x28, 0xA0, 0x5F);
    private static readonly Brush WarningBrush = Paint(0xDC, 0x9B, 0x2D);
    private static readonly Brush AccentBrush = Paint(0x00, 0x78, 0xD4);

    private readonly LocalizationService _localization;
    private readonly SettingsService _settings;
    private readonly Func<VersionCheckService?> _service;
    private readonly Func<Task> _refreshWorkspace;
    private readonly Func<Window?> _owner;
    private readonly MessageBoxOverlay _messageBoxOverlay;
    private readonly TextBlock _title = new();
    private readonly TextBlock _modName = new();
    private readonly TextBlock _statusIcon = new();
    private readonly TextBlock _statusText = new();
    private readonly TextBlock _summary = new();
    private readonly TextBlock _healthy = new();
    private readonly TextBlock _hiddenIssues = new();
    private readonly TextBlock _copyStatus = new();
    private readonly TextBlock _repairProgress = new();
    private readonly TextBlock _repairStatus = new();
    private readonly TextBlock _backupStatus = new();
    private readonly TextBlock _backupSummary = new();
    private readonly StackPanel _issues = new();
    private readonly StackPanel _backupItems = new();
    private readonly StackPanel _body = new();
    private readonly TextBox _report = new();
    private readonly Button _repair = new();
    private readonly Button _advancedRepair = new();
    private readonly Button _recover = new();
    private readonly Button _cleanBackups = new();
    private readonly ComboBox _rollbackPoint = new();
    private readonly Button _rollback = new();
    private readonly Border _statusCard;
    private readonly Border _issuesCard;
    private readonly Border _backupCard;
    private readonly Border _reportCard;
    private readonly Border _repairArea;
    private readonly Border _noIssues = new();
    private readonly TextBlock _reportToggle = new();
    private ModData? _mod;
    private ModVersionCheckResult? _result;
    private ModDetailedAnalysis? _analysis;
    private ModBackupHistory _history = new();
    private CompanionRecoveryPlan? _companionPlan;
    private ModRepairPlan? _repairPlan;
    private AssistedModRepairPlan? _automaticPlan;
    private AssistedModRepairPlan? _gameLodPlan;
    private AssistedModRepairPlan? _preserveLodPlan;
    private bool _useGameReferences;
    private bool _busy;
    private bool _disposed;
    private int _issueCount;
    private int _generation;

    internal bool IsOpen => Visibility == Visibility.Visible;
    internal int IssueCount => _issueCount;
    internal string ReportText => _report.Text;
    internal IReadOnlyList<string> IssueTitles => BuildIssues().Select(issue => issue.Title).ToArray();

    public VersionCheckDetailOverlay(LocalizationService localization, SettingsService settings,
        Func<VersionCheckService?> service, Func<Task> refreshWorkspace, Func<Window?> owner,
        MessageBoxOverlay messageBoxOverlay)
    {
        _localization = localization;
        _settings = settings;
        _service = service;
        _refreshWorkspace = refreshWorkspace;
        _owner = owner;
        _messageBoxOverlay = messageBoxOverlay;
        Visibility = Visibility.Collapsed;
        Focusable = true;
        Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
        MouseLeftButtonDown += (_, _) => Close();
        KeyDown += OnKeyDown;

        var dialog = new Border
        {
            Width = 820, MaxWidth = 920, MaxHeight = 650, Margin = new Thickness(24),
            Padding = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Background = SurfaceBrush,
            BorderBrush = StrokeBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };
        dialog.MouseLeftButtonDown += (_, e) => e.Handled = true;
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(BuildHeader());
        var scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        dialog.Child = layout;
        Children.Add(dialog);

        _statusCard = BuildStatusCard();
        _issuesCard = BuildIssuesCard();
        _reportCard = BuildReportCard();
        _backupCard = BuildBackupCard();
        _repairArea = BuildRepairArea();
        _body.Margin = new Thickness(0, 0, 8, 0);
        _body.Children.Add(_statusCard);
        _body.Children.Add(BuildStatsCard());
        var attribution = Card(new TextBlock { Text = L("VersionCheckDetail.Attribution", ""), FontSize = 12,
            Foreground = SecondaryBrush, TextWrapping = TextWrapping.Wrap });
        attribution.Margin = new Thickness(0, 0, 0, 16);
        _body.Children.Add(attribution);
        _body.Children.Add(_issuesCard);
        _body.Children.Add(_reportCard);
        _body.Children.Add(_backupCard);
        Grid.SetRow(_repairArea, 2);
        layout.Children.Add(_repairArea);
        _localization.PropertyChanged += OnLocalizationChanged;
        RefreshLabels();
    }

    public void Show(ModData mod, ModVersionCheckResult result, ModDetailedAnalysis? analysis = null)
    {
        if (_disposed)
            return;
        _generation++;
        _mod = mod;
        _result = result;
        _analysis = analysis ?? result.DetailedAnalysis;
        _history = new ModBackupHistory();
        _companionPlan = null;
        _repairPlan = null;
        _automaticPlan = null;
        _gameLodPlan = null;
        _preserveLodPlan = null;
        _useGameReferences = false;
        _busy = false;
        _repair.Visibility = Visibility.Collapsed;
        _advancedRepair.Visibility = Visibility.Collapsed;
        _recover.Visibility = Visibility.Collapsed;
        _repairProgress.Text = string.Empty;
        _repairStatus.Text = string.Empty;
        _copyStatus.Text = string.Empty;
        _backupStatus.Text = string.Empty;
        _history = new ModBackupHistory();
        _backupItems.Children.Clear();
        _backupSummary.Text = string.Empty;
        _cleanBackups.IsEnabled = false;
        _rollbackPoint.ItemsSource = null;
        _rollbackPoint.IsEnabled = false;
        _rollback.IsEnabled = false;
        Render();
        Visibility = Visibility.Visible;
        Focus();
        _ = LoadRepairPlanAsync();
        _ = LoadBackupHistoryAsync();
    }

    public void Close()
    {
        if (Visibility == Visibility.Collapsed)
            return;
        _generation++;
        Visibility = Visibility.Collapsed;
        _mod = null;
        _result = null;
        _analysis = null;
        _busy = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _localization.PropertyChanged -= OnLocalizationChanged;
        KeyDown -= OnKeyDown;
        Close();
    }

    private FrameworkElement BuildHeader()
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var copy = new StackPanel();
        _title.FontSize = 20; _title.FontWeight = FontWeights.SemiBold; _title.Foreground = ForegroundBrush;
        copy.Children.Add(_title);
        _modName.FontSize = 13; _modName.Foreground = SecondaryBrush; _modName.Margin = new Thickness(0, 4, 0, 0);
        _modName.TextTrimming = TextTrimming.CharacterEllipsis; copy.Children.Add(_modName);
        header.Children.Add(copy);
        var close = new Button { Width = 36, Height = 36, Margin = new Thickness(12, -6, -6, 0),
            Content = new TextBlock { Text = "\u00D7", FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = 14, Foreground = ForegroundBrush } };
        close.ToolTip = _localization["MainWindow.Close"];
        close.Click += (_, _) => Close(); Grid.SetColumn(close, 1); header.Children.Add(close);
        return header;
    }

    private Border BuildStatusCard()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _statusIcon.FontFamily = new FontFamily("Segoe UI Symbol");
        _statusIcon.FontSize = 24; Grid.SetColumn(_statusIcon, 0); grid.Children.Add(_statusIcon);
        var copy = new StackPanel();
        _statusText.FontSize = 17; _statusText.FontWeight = FontWeights.SemiBold; copy.Children.Add(_statusText);
        _summary.FontSize = 13; _summary.Foreground = ForegroundBrush; _summary.TextWrapping = TextWrapping.Wrap;
        _summary.Margin = new Thickness(0, 4, 0, 0); copy.Children.Add(_summary); Grid.SetColumn(copy, 1); grid.Children.Add(copy);
        var card = Card(grid); card.Margin = new Thickness(0, 0, 0, 12); return card;
    }

    private Border BuildStatsCard()
    {
        var stats = new Grid();
        for (var i = 0; i < 4; i++) stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (index, label) in new[] { (0, "ArmorPollutionPage.ScannedPatches"), (1, "VersionCheckDetail.Resources"),
            (2, "VersionCheckDetail.Units"), (3, "VersionCheckDetail.Issues") })
        {
            var block = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var value = new TextBlock { Name = $"Stat{index}", FontSize = 20, FontWeight = FontWeights.SemiBold,
                Foreground = index == 3 ? DangerBrush : ForegroundBrush, HorizontalAlignment = HorizontalAlignment.Center };
            block.Children.Add(value);
            block.Children.Add(new TextBlock { Text = L(label, label[(label.LastIndexOf('.') + 1)..]), FontSize = 12,
                Foreground = SecondaryBrush, HorizontalAlignment = HorizontalAlignment.Center });
            Grid.SetColumn(block, index); stats.Children.Add(block);
        }
        var card = Card(stats); card.Margin = new Thickness(0, 0, 0, 16); return card;
    }

    private Border BuildIssuesCard()
    {
        var root = new StackPanel();
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = L("VersionCheckDetail.DetectedIssues", "Detected issues"), FontSize = 15,
            FontWeight = FontWeights.SemiBold, Foreground = ForegroundBrush });
        Grid.SetColumn(_healthy, 1); _healthy.FontSize = 12; _healthy.Foreground = SuccessBrush; heading.Children.Add(_healthy);
        root.Children.Add(heading);
        _noIssues.Padding = new Thickness(16); _noIssues.Margin = new Thickness(0, 0, 0, 12);
        _noIssues.BorderBrush = SuccessBrush; _noIssues.BorderThickness = new Thickness(1);
        var noCopy = new StackPanel { Orientation = Orientation.Horizontal };
        noCopy.Children.Add(new TextBlock { Text = "\uE73E", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 18,
            Foreground = SuccessBrush, Margin = new Thickness(0, 0, 12, 0) });
        noCopy.Children.Add(new TextBlock { Text = L("VersionCheckDetail.NoIssuesDescription", "No blocking issues were detected."),
            Foreground = SecondaryBrush, TextWrapping = TextWrapping.Wrap });
        _noIssues.Child = noCopy; root.Children.Add(_noIssues); root.Children.Add(_issues);
        _hiddenIssues.Foreground = WarningBrush; _hiddenIssues.FontSize = 12;
        _hiddenIssues.TextWrapping = TextWrapping.Wrap; root.Children.Add(_hiddenIssues);
        var card = new Border { Child = root, Padding = new Thickness(0), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 4) };
        return card;
    }

    private Border BuildReportCard()
    {
        _report.IsReadOnly = true; _report.AcceptsReturn = true; _report.TextWrapping = TextWrapping.NoWrap;
        _report.FontFamily = new FontFamily("Cascadia Mono, Consolas"); _report.FontSize = 11; _report.Height = 230;
        _report.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _report.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _report.Margin = new Thickness(0, 10, 0, 0); _report.Visibility = Visibility.Collapsed;
        _reportToggle.Text = L("VersionCheckDetail.TechnicalDetails", "Technical details"); _reportToggle.Foreground = ForegroundBrush;
        var toggle = new Button { Content = _reportToggle, HorizontalContentAlignment = HorizontalAlignment.Left };
        toggle.Click += (_, _) => { _report.Visibility = _report.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; };
        var body = new StackPanel(); body.Children.Add(toggle); body.Children.Add(_report);
        var card = Card(body); card.Margin = new Thickness(0, 8, 0, 0); return card;
    }

    private Border BuildBackupCard()
    {
        var title = new TextBlock { Text = L("VersionCheckBackup.HistoryTitle", "Backup history"), FontSize = 15,
            FontWeight = FontWeights.SemiBold, Foreground = ForegroundBrush };
        var content = new StackPanel(); content.Children.Add(title);
        _backupSummary.FontSize = 12; _backupSummary.Foreground = SecondaryBrush;
        _backupSummary.TextWrapping = TextWrapping.Wrap; _backupSummary.Margin = new Thickness(2, 8, 0, 8);
        content.Children.Add(_backupSummary);
        var rollback = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        rollback.Children.Add(new TextBlock { Text = L("VersionCheckBackup.RollbackTimeLabel", "Time"), Foreground = ForegroundBrush,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) });
        _rollbackPoint.Width = 150; _rollbackPoint.IsEnabled = false; rollback.Children.Add(_rollbackPoint);
        _rollback.Content = L("VersionCheckBackup.RollbackButton", "Roll back whole mod"); _rollback.IsEnabled = false;
        _rollback.Margin = new Thickness(8, 0, 0, 0); _rollback.Click += (_, _) => _ = RollbackAsync(); rollback.Children.Add(_rollback);
        content.Children.Add(rollback); content.Children.Add(_backupItems);
        var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _backupStatus.Foreground = SuccessBrush; _backupStatus.TextWrapping = TextWrapping.Wrap; actions.Children.Add(_backupStatus);
        _cleanBackups.Content = L("VersionCheckBackup.CleanOld", "Clean old backups"); _cleanBackups.IsEnabled = false;
        _cleanBackups.Click += (_, _) => _ = CleanBackupsAsync(); Grid.SetColumn(_cleanBackups, 1); actions.Children.Add(_cleanBackups);
        content.Children.Add(actions);
        var card = Card(content); card.Margin = new Thickness(0, 12, 0, 0); return card;
    }

    private Border BuildRepairArea()
    {
        var root = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        _repairProgress.Foreground = AccentBrush; _repairProgress.FontSize = 12; root.Children.Add(_repairProgress);
        _repairStatus.Foreground = SuccessBrush; _repairStatus.FontSize = 12; _repairStatus.TextWrapping = TextWrapping.Wrap; root.Children.Add(_repairStatus);
        _copyStatus.Foreground = SuccessBrush; _copyStatus.FontSize = 12; root.Children.Add(_copyStatus);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0) };
        _recover.Content = L("VersionCheckRecovery.Button", "Recover companion files"); _recover.Visibility = Visibility.Collapsed;
        _recover.Click += (_, _) => _ = RecoverAsync(); actions.Children.Add(_recover);
        _advancedRepair.Content = L("VersionCheckDetail.AdvancedRepairButton", "Advanced repair"); _advancedRepair.Visibility = Visibility.Collapsed;
        _advancedRepair.Margin = new Thickness(8, 0, 0, 0); _advancedRepair.Click += (_, _) => _ = LoadAdvancedRepairAsync(); actions.Children.Add(_advancedRepair);
        _repair.Content = L("VersionCheckDetail.RepairButton", "Repair"); _repair.Visibility = Visibility.Collapsed;
        _repair.Margin = new Thickness(8, 0, 0, 0); _repair.Click += (_, _) => _ = RepairAsync(); actions.Children.Add(_repair);
        var copy = new Button { Content = L("VersionCheckDetail.CopyReport", "Copy report"), Margin = new Thickness(8, 0, 0, 0) };
        copy.Click += (_, _) => CopyReport(); actions.Children.Add(copy); root.Children.Add(actions);
        var card = new Border { Child = root, Background = Brushes.Transparent, BorderThickness = new Thickness(0) }; return card;
    }

    private void Render()
    {
        if (_mod is null || _result is null) return;
        _title.Text = L("VersionCheckDetail.Title", "Version check details"); _modName.Text = _mod.Manifest.Name;
        var statusColor = _result.Status switch { ModVersionStatus.Compatible => SuccessBrush,
            ModVersionStatus.Incompatible or ModVersionStatus.Error => DangerBrush, _ => WarningBrush };
        _statusIcon.Text = _result.Status switch { ModVersionStatus.Compatible => "\u2713",
            ModVersionStatus.Incompatible or ModVersionStatus.Error => "!", _ => "?" };
        _statusIcon.Foreground = statusColor; _statusText.Foreground = statusColor;
        _statusText.Text = StatusText(_result.Status);
        var truncated = _analysis?.PatchFiles.SelectMany(p => p.UnitDetails).Count(u => u.IsTruncated) ?? 0;
        _summary.Text = truncated > 0 ? L("VersionCheckDetail.SummaryTruncated", "{count} Unit resource(s) are truncated by their TOC size.").Replace("{count}", truncated.ToString())
            : (_analysis?.CorruptedFileCount ?? 0) > 0 ? L("VersionCheckDetail.SummaryCorrupted", "{count} patch file(s) contain structural damage.").Replace("{count}", _analysis!.CorruptedFileCount.ToString())
            : _result.Status == ModVersionStatus.Incompatible ? L("VersionCheckDetail.SummaryVersionMismatch", "One or more Unit versions differ from the reference version.")
            : _result.Status == ModVersionStatus.Compatible ? L("VersionCheckDetail.SummaryHealthy", "No blocking compatibility issues were detected.")
            : L("VersionCheckDetail.SummaryUnknown", "There is not enough Unit version information to confirm compatibility.");
        _report.Text = BuildReport();
        RenderIssues(); RenderStats();
    }

    private void RenderStats()
    {
        var values = new[] { (_analysis?.TotalPatchFiles ?? 0).ToString(), (_analysis?.PatchFiles.Sum(p => p.TotalResources) ?? 0).ToString(),
            (_analysis?.PatchFiles.Sum(p => p.UnitDetails.Count) ?? _result?.PatchUnits.Count ?? 0).ToString(), _issueCount.ToString() };
        var index = 0;
        if (_body.Children[1] is Border statsCard && statsCard.Child is Grid statsGrid)
            foreach (var child in statsGrid.Children.OfType<StackPanel>())
                if (child.Children.OfType<TextBlock>().FirstOrDefault() is { } value) value.Text = values[index++];
    }

    private void RenderIssues()
    {
        _issues.Children.Clear();
        if (_result is null) return;
        var issues = BuildIssues();
        _issueCount = issues.Count;
        foreach (var issue in issues.Take(50))
        {
            var stack = new StackPanel { Margin = new Thickness(8, 10, 14, 10) };
            stack.Children.Add(new TextBlock { Text = issue.Title, FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = issue.Brush, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrWhiteSpace(issue.File)) stack.Children.Add(new TextBlock { Text = issue.File, FontSize = 11,
                Foreground = SecondaryBrush, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock { Text = issue.Description, FontSize = 12, Foreground = SecondaryBrush,
                Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap });
            var card = new Border { Child = stack, BorderBrush = StrokeBrush, BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(0), CornerRadius = new CornerRadius(4) };
            _issues.Children.Add(card);
        }
        var healthyCount = _analysis is null ? 0 : Math.Max(0,
            _analysis.PatchFiles.Sum(p => p.UnitDetails.Count)
            - _analysis.PatchFiles.Sum(p => p.UnitDetails.Count(unit =>
                !unit.UnitDataInBounds || !unit.LODGroupInBounds || !unit.DeclaredSizeMatchesInternal ||
                (unit.LayoutFormatChecked && !unit.LayoutFormatValid))));
        _healthy.Text = healthyCount > 0
            ? L("VersionCheckDetail.HealthyUnits", "{count} Unit(s) passed structural checks")
                .Replace("{count}", healthyCount.ToString())
            : string.Empty;
        _noIssues.Visibility = issues.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _hiddenIssues.Text = issues.Count > 50
            ? L("VersionCheckDetail.HiddenIssues", "{count} more issue(s) are available in technical details.")
                .Replace("{count}", (issues.Count - 50).ToString())
            : string.Empty;
    }

    private List<(string Title, string File, string Description, Brush Brush)> BuildIssues()
    {
        var issues = new List<(string, string, string, Brush)>();
        if (_result is null) return issues;

        void Add(Brush color, string file, string key, string fallback, string? detail, int? index = null) =>
            issues.Add((L(key, fallback).Replace("{index}", index?.ToString() ?? "{index}"), file, detail ?? string.Empty, color));

        var mismatches = _result.PatchUnits
            .Where(unit => _result.GameVersion != 0 && unit.Version != _result.GameVersion &&
                !_result.UnitsMissingGameReference.Contains(unit.FileId))
            .ToArray();
        if (mismatches.Length > 0)
        {
            var versions = string.Join(", ", mismatches.Select(unit => $"0x{unit.Version:X8}").Distinct());
            Add(DangerBrush, string.Empty, "VersionCheckDetail.VersionMismatchTitle", "Unit version mismatch",
                L("VersionCheckDetail.VersionMismatchDescription",
                    "{count} Unit(s) use {versions}; reference is {reference}.")
                    .Replace("{count}", mismatches.Length.ToString())
                    .Replace("{versions}", versions)
                    .Replace("{reference}", $"0x{_result.GameVersion:X8}"));
        }

        foreach (var patch in _analysis?.PatchFiles ?? [])
        {
            var before = issues.Count;
            if (!patch.HeaderValid || !patch.FileEntriesInBounds)
                Add(DangerBrush, patch.FileName, "VersionCheckDetail.HeaderIssueTitle", "Invalid patch header or TOC", patch.Message);
            if (!patch.TypeDistributionValid)
                Add(DangerBrush, patch.FileName, "VersionCheckDetail.TypeTableTitle", "Resource type table is inconsistent",
                    L("VersionCheckDetail.TypeTableDescription", "The type table does not match the {count} actual file entries.")
                        .Replace("{count}", patch.NumFiles.ToString()));
            if (!patch.MainDataBoundsValid)
                Add(DangerBrush, patch.FileName, "VersionCheckDetail.MainBoundsTitle", "Main resource data is out of bounds",
                    L("VersionCheckDetail.MainBoundsDescription", "{count} invalid or overlapping range(s).")
                        .Replace("{count}", patch.MainDataIssueCount.ToString()));
            if (!patch.EntryIndicesValid)
                Add(WarningBrush, patch.FileName, "VersionCheckDetail.EntryIndexTitle", "TOC entry indices are not sequential",
                    L("VersionCheckDetail.EntryIndexDescription", "{count} invalid index value(s).")
                        .Replace("{count}", patch.EntryIndexIssueCount.ToString()));
            if (patch.RequiresGpuResources && !patch.HasGpuResources)
                Add(DangerBrush, patch.FileName, "VersionCheckDetail.MissingGpuTitle", "Required GPU resource file is missing",
                    L("VersionCheckDetail.MissingGpuDescription", "The patch contains non-zero GPU resource references."));
            if (patch.RequiresStream && !patch.HasStream)
                Add(DangerBrush, patch.FileName, "VersionCheckDetail.MissingStreamTitle", "Required stream file is missing",
                    L("VersionCheckDetail.MissingStreamDescription", "The patch contains non-zero stream resource references."));
            if (!patch.GpuResourceBoundsValid || patch.GpuAlignmentIssueCount > 0)
                Add(patch.GpuResourceBoundsValid ? WarningBrush : DangerBrush, patch.FileName,
                    "VersionCheckDetail.GpuIssueTitle", "GPU resource range problem",
                    RangeDescription(patch.GpuResourceIssueCount, patch.GpuAlignmentIssueCount));
            if (!patch.StreamBoundsValid || patch.StreamAlignmentIssueCount > 0)
                Add(patch.StreamBoundsValid ? WarningBrush : DangerBrush, patch.FileName,
                    "VersionCheckDetail.StreamIssueTitle", "stream resource range problem",
                    RangeDescription(patch.StreamIssueCount, patch.StreamAlignmentIssueCount));
            foreach (var unit in patch.UnitDetails)
            {
                if (unit.IsTruncated)
                    Add(DangerBrush, patch.FileName, "VersionCheckDetail.UnitTruncatedTitle", "Unit #{index} data is truncated",
                        L("VersionCheckDetail.UnitTruncatedDescription", "TOC declares {declared} bytes, internal size is {expected}; {difference} bytes are missing. ID {fileId}")
                            .Replace("{declared}", unit.DataSize.ToString())
                            .Replace("{expected}", unit.ExpectedDataSize.ToString())
                            .Replace("{difference}", Math.Max(0, unit.ExpectedDataSize - unit.DataSize).ToString())
                            .Replace("{fileId}", $"0x{unit.FileId:X16}"), unit.EntryIndex);
                else if (!unit.DeclaredSizeMatchesInternal)
                    Add(WarningBrush, patch.FileName, "VersionCheckDetail.UnitSizeMismatchTitle", "Unit #{index} size mismatch",
                        L("VersionCheckDetail.UnitSizeMismatchDescription", "TOC declares {declared} bytes; internal size is {expected}. ID {fileId}")
                            .Replace("{declared}", unit.DataSize.ToString())
                            .Replace("{expected}", unit.ExpectedDataSize.ToString())
                            .Replace("{fileId}", $"0x{unit.FileId:X16}"), unit.EntryIndex);
                if (!unit.UnitDataInBounds)
                    Add(DangerBrush, patch.FileName, "VersionCheckDetail.UnitBoundsTitle", "Unit data exceeds patch bounds", unit.Warning);
                else if (!unit.LODGroupInBounds)
                    Add(DangerBrush, patch.FileName, "VersionCheckDetail.LodBoundsTitle", "Unit LOD data exceeds its declared bounds", unit.Warning);
                if (unit.LayoutFormatChecked && !unit.LayoutFormatValid)
                    Add(DangerBrush, patch.FileName, "VersionCheckDetail.LayoutTitle", "Legacy Unit layout requires repair", unit.Warning);
            }
            if (patch.HealthStatus is PatchHealthStatus.Corrupted or PatchHealthStatus.Warning &&
                issues.Count == before && !string.IsNullOrWhiteSpace(patch.Message))
                Add(patch.HealthStatus == PatchHealthStatus.Corrupted ? DangerBrush : WarningBrush,
                    patch.FileName, "VersionCheckDetail.GenericFileIssueTitle", "Patch file warning", patch.Message);
        }
        return issues;
    }

    private string RangeDescription(int bounds, int alignment) =>
        L("VersionCheckDetail.ResourceRangeDescription", "Out of bounds: {bounds}; misaligned: {alignment}.")
            .Replace("{bounds}", bounds.ToString())
            .Replace("{alignment}", alignment.ToString());

    private async Task LoadRepairPlanAsync()
    {
        var service = _service(); var mod = _mod; if (service is null || mod is null) return;
        var generation = _generation;
        _busy = true; _repairProgress.Text = L("VersionCheckDetail.CheckingRepair", "Checking repair options...");
        try
        {
            var companionPlan = await service.CreateCompanionRecoveryPlanAsync(mod.Directory);
            if (!IsCurrent(mod, generation)) return;
            _companionPlan = companionPlan;
            if (_companionPlan.MissingCount > 0)
            {
                _recover.Visibility = Visibility.Visible; _recover.Content = _companionPlan.CanRecover
                    ? L("VersionCheckRecovery.Button", "Recover {count} companion file(s)").Replace("{count}", _companionPlan.RecoverableCount.ToString())
                    : L("VersionCheckRecovery.Unavailable", "No reliable companion source");
            }
            else
            {
                var repairPlan = await service.CreateRepairPlanAsync(mod.Directory);
                if (!IsCurrent(mod, generation)) return;
                _repairPlan = repairPlan;
                if (_repairPlan.CanRepair)
                {
                    _useGameReferences = false; _repair.Visibility = Visibility.Visible;
                    _repair.Content = L("VersionCheckDetail.RepairButton", "Repair {count} issue(s)").Replace("{count}", _repairPlan.ActionCount.ToString());
                }
                else
                {
                    var automaticPlan = await service.CreateAutomaticAssistedRepairPlanAsync(mod.Directory);
                    if (!IsCurrent(mod, generation)) return;
                    _automaticPlan = automaticPlan;
                    if (_automaticPlan.CanRepair)
                    {
                        _useGameReferences = true; _repair.Visibility = Visibility.Visible; _advancedRepair.Visibility = Visibility.Visible;
                        _repair.Content = L("VersionCheckDetail.AutomaticRepairButton", "Automatically repair {count} Unit(s)").Replace("{count}", _automaticPlan.ActionCount.ToString());
                    }
                }
            }
        }
        catch (Exception ex) { if (IsCurrent(mod, generation)) _repairStatus.Text = L("VersionCheckDetail.RepairPlanFailed", "Failed to load repair options: {message}").Replace("{message}", ex.Message); }
        finally { if (IsCurrent(mod, generation)) { _repairProgress.Text = string.Empty; _busy = false; } }
    }

    private async Task RecoverAsync()
    {
        if (_busy || _mod is not { } mod || _companionPlan?.CanRecover != true) return;
        var generation = _generation;
        if (!await ConfirmRepairAsync() || !IsCurrent(mod, generation) || _service() is not { } service) return;
        if (!await ConfirmAsync(L("VersionCheckRecovery.ConfirmTitle", "Recover missing companion files"),
            L("VersionCheckRecovery.ConfirmMessage", "Recover {count} companion file(s) from verified sources and validate them before replacement?")
                .Replace("{count}", _companionPlan.RecoverableCount.ToString())) || !IsCurrent(mod, generation)) return;
        _busy = true; _repairProgress.Text = L("VersionCheckRecovery.Recovering", "Recovering and validating companion files...");
        try
        {
            var result = await service.RecoverCompanionFilesAsync(mod.Directory);
            if (!IsCurrent(mod, generation)) return;
            _repairStatus.Text = result.Success ? L("VersionCheckRecovery.Success", "Recovered {count} companion file(s).").Replace("{count}", result.RecoveredCount.ToString())
                : L("VersionCheckRecovery.Failed", "Companion recovery failed: {message}").Replace("{message}", result.ErrorMessage ?? L("Converters.Unknown", "Unknown"));
            if (result.Success) await RefreshAfterWriteAsync(mod);
        }
        catch (Exception ex) { if (IsCurrent(mod, generation)) _repairStatus.Text = ex.Message; }
        finally { if (IsCurrent(mod, generation)) { _repairProgress.Text = string.Empty; _busy = false; } }
    }

    private async Task RepairAsync()
    {
        if (_busy || _mod is not { } mod) return;
        var generation = _generation;
        if (!await ConfirmRepairAsync() || !IsCurrent(mod, generation) || _service() is not { } service) return;
        if (_useGameReferences && _automaticPlan is { } automatic)
        {
            if (!await ConfirmAsync(L("VersionCheckDetail.AutomaticRepairTitle", "Automatically repair mod"),
                L("VersionCheckDetail.AutomaticRepairConfirm", "Preserve mod LOD for {preserve} Unit(s), use current game LOD for {game} Unit(s), and validate the result.")
                    .Replace("{preserve}", automatic.AutomaticPreserveUnitCount.ToString()).Replace("{game}", automatic.AutomaticGameLodUnitCount.ToString())) || !IsCurrent(mod, generation)) return;
        }
        else if (_repairPlan is not { CanRepair: true } safePlan) return;
        else if (!await ConfirmAsync(L("VersionCheckDetail.RepairConfirmTitle", "Repair mod"),
            L("VersionCheckDetail.RepairConfirmMessage", "Back up {files} patch file(s), apply {count} verified metadata repair(s), then validate the result?")
                .Replace("{files}", safePlan.FileCount.ToString()).Replace("{count}", safePlan.ActionCount.ToString())) || !IsCurrent(mod, generation)) return;
        _busy = true; _repairProgress.Text = L("VersionCheckDetail.Repairing", "Backing up, repairing, and validating...");
        try
        {
            var result = _useGameReferences ? await service.RepairModAutomaticallyAsync(mod.Directory) : await service.RepairModAsync(mod.Directory);
            if (!IsCurrent(mod, generation)) return;
            _repairStatus.Text = result.Success ? L("VersionCheckDetail.RepairSuccess", "Repaired {count} issue(s). Original files were kept as backups.").Replace("{count}", result.AppliedActionCount.ToString())
                : L("VersionCheckDetail.RepairFailed", "Repair failed: {message}").Replace("{message}", result.ErrorMessage ?? L("Converters.Unknown", "Unknown"));
            if (result.Success) await RefreshAfterWriteAsync(mod);
        }
        catch (Exception ex) { if (IsCurrent(mod, generation)) _repairStatus.Text = ex.Message; }
        finally { if (IsCurrent(mod, generation)) { _repairProgress.Text = string.Empty; _busy = false; } }
    }

    private async Task LoadAdvancedRepairAsync()
    {
        if (_busy) return;
        var service = _service(); var mod = _mod; if (service is null || mod is null) return;
        var generation = _generation;
        if (!await ConfirmRepairAsync() || !IsCurrent(mod, generation)) return;
        _busy = true; _repairProgress.Text = L("VersionCheckDetail.LoadingAdvancedRepair", "Loading advanced LOD strategies...");
        try
        {
            var preserveLodPlan = await service.CreateAssistedRepairPlanAsync(mod.Directory, AssistedLodStrategy.PreserveMod);
            if (!IsCurrent(mod, generation)) return;
            var gameLodPlan = await service.CreateAssistedRepairPlanAsync(mod.Directory, AssistedLodStrategy.UseGameReference);
            if (!IsCurrent(mod, generation)) return;
            _preserveLodPlan = preserveLodPlan;
            _gameLodPlan = gameLodPlan;
            var candidates = _gameLodPlan?.Actions.Where(action => action.LodDataDiffers).Select(action => action.FileId).Distinct().Count() ?? 0;
            var choice = await ChooseLodStrategyAsync(candidates);
            if (choice is null)
                return;
            if (choice.Value.Manual)
            {
                await ShowUnitLodSelectionAsync(service, mod);
                return;
            }
            if (choice.Value.Strategy is not { } strategy) return;
            if (strategy == AssistedLodStrategy.UseGameReference && _gameLodPlan?.CanRepair == true)
                await ExecuteAssistedAsync(service, mod, strategy, null);
            else if (strategy == AssistedLodStrategy.PreserveMod && _preserveLodPlan?.CanRepair == true)
                await ExecuteAssistedAsync(service, mod, strategy, null);
        }
        catch (Exception ex) { if (IsCurrent(mod, generation)) _repairStatus.Text = ex.Message; }
        finally { if (IsCurrent(mod, generation)) { _repairProgress.Text = string.Empty; _busy = false; } }
    }

    private async Task<(bool Manual, AssistedLodStrategy? Strategy)?> ChooseLodStrategyAsync(int selectableCount)
    {
        var choices = new List<(bool Manual, AssistedLodStrategy? Strategy, string Label)>();
        if (selectableCount > 0)
            choices.Add((true, null, L("VersionCheckDetail.LodStrategyPerUnit", "Choose per Unit manually ({count} selectable)")
                .Replace("{count}", selectableCount.ToString())));
        if (_gameLodPlan?.CanRepair == true)
            choices.Add((false, AssistedLodStrategy.UseGameReference,
                L("VersionCheckDetail.LodStrategyGameReference", "Use current game LOD (standard, {count} Unit(s))")
                    .Replace("{count}", _gameLodPlan.ActionCount.ToString())));
        if (_preserveLodPlan?.CanRepair == true)
            choices.Add((false, AssistedLodStrategy.PreserveMod,
                L("VersionCheckDetail.LodStrategyPreserveMod", "Preserve mod LOD (custom models, {count} Unit(s))")
                    .Replace("{count}", _preserveLodPlan.ActionCount.ToString())));
        if (choices.Count == 0 || _messageBoxOverlay is null)
            return null;
        var selected = await _messageBoxOverlay.ChooseOneAsync(
            L("VersionCheckDetail.LodStrategyTitle", "Choose Unit LOD strategy"),
            L("VersionCheckDetail.LodStrategyMessage", "Choose how Unit LOD data should be repaired."),
            choices.Select(choice => choice.Label).ToArray());
        return selected is { } index && index < choices.Count
            ? (choices[index].Manual, choices[index].Strategy) : null;
    }

    private async Task ShowUnitLodSelectionAsync(VersionCheckService service, ModData mod)
    {
        var actions = _gameLodPlan?.Actions.Where(action => action.LodDataDiffers).GroupBy(action => action.FileId)
            .Select(group =>
            {
                var first = group.First();
                var title = string.IsNullOrWhiteSpace(first.FriendlyName)
                    ? L("VersionCheckDetail.UnitSelectionUnnamed", "Unit {id}")
                        .Replace("{id}", $"0x{(ulong)first.FileId:X16}")
                    : first.FriendlyName;
                var lodSizes = string.Join(", ", group.Select(action =>
                    $"{action.CurrentLodSize}->{action.ReferenceLodSize}").Distinct(StringComparer.Ordinal));
                var detail = L("VersionCheckDetail.UnitSelectionDescription",
                        "{occurrences} patch entry(s) | LOD {lodSizes} | ID {id}")
                    .Replace("{occurrences}", group.Count().ToString())
                    .Replace("{lodSizes}", lodSizes)
                    .Replace("{id}", $"0x{(ulong)first.FileId:X16}");
                return (Id: first.FileId, Option: new MessageBoxSelectionOption(title, Detail: detail));
            })
            .OrderBy(item => item.Option.Text, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id).ToArray() ?? [];
        if (actions.Length == 0 || _messageBoxOverlay is null)
            return;
        var generation = _generation;
        var selectedIndices = await _messageBoxOverlay.SelectManyAsync(
            L("VersionCheckDetail.UnitSelectionTitle", "Choose Units that keep mod LOD"),
            L("VersionCheckDetail.UnitSelectionMessage",
                "Checked Units preserve the mod LOD; unchecked Units use current game LOD. Equal LOD sizes can still contain incompatible data. If the mod was already converted entirely to game LOD, restore its HD2MM backup before using this screen."),
            actions.Select(item => item.Option).ToArray());
        if (selectedIndices is null || !IsCurrent(mod, generation))
            return;
        var preserve = selectedIndices.Select(index => actions[index].Id).ToHashSet();
        await ExecuteAssistedAsync(service, mod, AssistedLodStrategy.UseGameReference, preserve);
    }

    private async Task ExecuteAssistedAsync(VersionCheckService service, ModData mod, AssistedLodStrategy strategy, IReadOnlySet<long>? preserve)
    {
        if (!ReferenceEquals(_mod, mod) || !IsOpen) return;
        var generation = _generation;
        var result = preserve is not null ? await service.RepairModWithMixedGameReferencesAsync(mod.Directory, preserve)
            : await service.RepairModWithGameReferencesAsync(mod.Directory, strategy);
        if (!IsCurrent(mod, generation)) return;
        _repairStatus.Text = result.Success ? L("VersionCheckDetail.AssistedRepairSuccessGameLod", "Updated {count} Unit(s). Original files were kept as backups.").Replace("{count}", result.AppliedActionCount.ToString())
            : result.ErrorMessage ?? L("Converters.Unknown", "Unknown");
        if (result.Success) await RefreshAfterWriteAsync(mod);
    }

    private async Task RefreshAfterWriteAsync(ModData mod)
    {
        await _refreshWorkspace();
        if (!ReferenceEquals(_mod, mod) || _service() is not { } service) return;
        var refreshed = await service.CheckSingleModAsync(mod, _result?.GameVersion, includeDetailedAnalysis: true);
        if (!ReferenceEquals(_mod, mod) || refreshed is null) return;
        _result = refreshed;
        _analysis = refreshed.DetailedAnalysis;
        Render();
        _repairPlan = null; _automaticPlan = null; _companionPlan = null;
        _repair.Visibility = Visibility.Collapsed; _advancedRepair.Visibility = Visibility.Collapsed;
        _recover.Visibility = Visibility.Collapsed;
        await LoadRepairPlanAsync();
        await LoadBackupHistoryAsync();
    }

    private async Task LoadBackupHistoryAsync()
    {
        var service = _service(); var mod = _mod; if (service is null || mod is null) return;
        var generation = _generation;
        try
        {
            var history = await service.GetBackupHistoryAsync(mod.Directory);
            if (!IsCurrent(mod, generation)) return;
            _history = history;
            _backupItems.Children.Clear();
            _backupSummary.Text = _history.Entries.Count == 0
                ? L("VersionCheckBackup.None", "No HD2MM repair backups were found for this mod.")
                : L("VersionCheckBackup.Summary", "{count} backup(s), {restorable} restorable, {invalid} invalid.")
                    .Replace("{count}", _history.Entries.Count.ToString())
                    .Replace("{restorable}", _history.RestorableCount.ToString())
                    .Replace("{invalid}", _history.InvalidCount.ToString());
            foreach (var group in _history.Entries.GroupBy(e => e.OriginalPath, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                _backupItems.Children.Add(BuildBackupGroup(group));
            _cleanBackups.IsEnabled = _history.Entries.GroupBy(e => e.OriginalPath, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 3);
            var points = _history.Entries.Select(e => new DateTime(e.CreatedLocal.Year, e.CreatedLocal.Month, e.CreatedLocal.Day, e.CreatedLocal.Hour, e.CreatedLocal.Minute, 0)).Distinct().OrderByDescending(x => x).ToArray();
            _rollbackPoint.ItemsSource = points;
            _rollbackPoint.SelectedIndex = points.Length > 0 ? 0 : -1;
            _rollbackPoint.IsEnabled = points.Length > 0; _rollback.IsEnabled = points.Length > 0;
        }
        catch (Exception ex) { if (IsCurrent(mod, generation)) _backupStatus.Text = ex.Message; }
    }

    private bool IsCurrent(ModData mod, int generation) =>
        !_disposed && IsOpen && ReferenceEquals(_mod, mod) && _generation == generation;

    private FrameworkElement BuildBackupGroup(IEnumerable<ModBackupEntry> entries)
    {
        var group = entries.OrderByDescending(e => e.CreatedLocal).ToArray();
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = Path.GetFileName(group[0].OriginalPath), FontSize = 13,
            FontWeight = FontWeights.SemiBold, Foreground = ForegroundBrush, Margin = new Thickness(0, 8, 0, 6) });
        foreach (var entry in group)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var detail = new StackPanel(); detail.Children.Add(new TextBlock { Text = $"{entry.CreatedLocal:yyyy-MM-dd HH:mm:ss} | {entry.BackupSize:N0} B | {entry.RepairKind}",
                FontSize = 11, Foreground = SecondaryBrush, TextWrapping = TextWrapping.Wrap });
            detail.Children.Add(new TextBlock { Text = entry.CanRestore ? L("VersionCheckBackup.Ready", "Ready to restore.") : entry.ValidationMessage,
                FontSize = 11, Foreground = entry.CanRestore ? SuccessBrush : DangerBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            row.Children.Add(detail);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var restore = new Button { Content = "\uE777", Width = 34, Height = 34, IsEnabled = entry.CanRestore,
                ToolTip = L("VersionCheckBackup.Restore", "Restore") }; restore.Click += (_, _) => _ = RestoreAsync(entry); buttons.Children.Add(restore);
            var delete = new Button { Content = "\uE74D", Width = 34, Height = 34, Margin = new Thickness(4, 0, 0, 0),
                ToolTip = L("VersionCheckBackup.Delete", "Delete") }; delete.Click += (_, _) => _ = DeleteAsync(entry); buttons.Children.Add(delete);
            Grid.SetColumn(buttons, 1); row.Children.Add(buttons); panel.Children.Add(Card(row));
        }
        return panel;
    }

    private async Task RestoreAsync(ModBackupEntry entry)
    {
        if (_busy || !entry.CanRestore || _mod is not { } mod) return;
        var generation = _generation;
        var confirmation = L("VersionCheckBackup.RestoreConfirm", "Restore {file} from {time}? HD2MM will snapshot the current patch first, replace it atomically, and verify the SHA-256.")
            .Replace("{file}", entry.OriginalFileName)
            .Replace("{time}", entry.CreatedLocal.ToString("yyyy-MM-dd HH:mm:ss"));
        if (!await ConfirmAsync(L("VersionCheckBackup.RestoreTitle", "Restore patch backup"), confirmation) || !IsCurrent(mod, generation)) return;
        var service = _service(); if (service is null) return;
        _busy = true;
        try
        {
            var result = await service.RestoreBackupAsync(mod.Directory, entry.BackupPath);
            if (!IsCurrent(mod, generation)) return;
            _backupStatus.Text = result.Success
                ? L("VersionCheckBackup.RestoreSuccess", "Backup restored. A snapshot of the replaced patch was retained.")
                : L("VersionCheckBackup.RestoreFailed", "Backup restore failed: {message}")
                    .Replace("{message}", result.ErrorMessage ?? L("Converters.Unknown", "Unknown"));
            if (result.Success) await RefreshAfterWriteAsync(mod);
        }
        catch (Exception ex)
        {
            if (IsCurrent(mod, generation)) _backupStatus.Text = L("VersionCheckBackup.RestoreFailed", "Backup restore failed: {message}").Replace("{message}", ex.Message);
        }
        finally { if (IsCurrent(mod, generation)) _busy = false; }
    }

    private async Task DeleteAsync(ModBackupEntry entry)
    {
        if (_busy || _mod is not { } mod) return;
        var generation = _generation;
        var confirmation = L("VersionCheckBackup.DeleteConfirm", "Delete the backup of {file} from {time}? The final restorable backup for each patch is always protected.")
            .Replace("{file}", entry.OriginalFileName)
            .Replace("{time}", entry.CreatedLocal.ToString("yyyy-MM-dd HH:mm:ss"));
        if (!await ConfirmAsync(L("VersionCheckBackup.DeleteTitle", "Delete backup"), confirmation) || !IsCurrent(mod, generation)) return;
        var service = _service(); if (service is null) return;
        _busy = true;
        try
        {
            var result = await service.DeleteBackupAsync(mod.Directory, entry.BackupPath);
            if (!IsCurrent(mod, generation)) return;
            _backupStatus.Text = result.Success ? L("VersionCheckBackup.DeleteSuccess", "Backup deleted.") : result.ErrorMessage ?? string.Empty;
            await LoadBackupHistoryAsync();
        }
        catch (Exception ex) { if (IsCurrent(mod, generation)) _backupStatus.Text = ex.Message; }
        finally { if (IsCurrent(mod, generation)) _busy = false; }
    }

    private async Task CleanBackupsAsync()
    {
        if (_busy || _mod is not { } mod) return;
        var generation = _generation;
        if (!await ConfirmAsync(L("VersionCheckBackup.CleanOld", "Clean old backups"),
            L("VersionCheckBackup.CleanConfirm", "Keep the newest three backups for each patch and delete older entries? At least one restorable backup is always retained.")) || !IsCurrent(mod, generation)) return;
        var service = _service(); if (service is null) return;
        _busy = true;
        try
        {
            var result = await service.CleanOldBackupsAsync(mod.Directory, 3);
            if (!IsCurrent(mod, generation)) return;
            _backupStatus.Text = result.Success
                ? L("VersionCheckBackup.CleanSuccess", "Deleted {count} old backup(s).").Replace("{count}", result.DeletedCount.ToString())
                : result.ErrorMessage ?? string.Empty;
            await LoadBackupHistoryAsync();
        }
        catch (Exception ex) { if (IsCurrent(mod, generation)) _backupStatus.Text = ex.Message; }
        finally { if (IsCurrent(mod, generation)) _busy = false; }
    }

    private async Task RollbackAsync()
    {
        if (_busy) return;
        if (_rollbackPoint.SelectedItem is not DateTime point || _mod is not { } mod) return;
        var pendingCount = CountPendingRollback(_history, point);
        if (pendingCount == 0)
        {
            _backupStatus.Text = L("VersionCheckBackup.RollbackNothing", "Nothing to roll back at this time point.");
            return;
        }
        var generation = _generation;
        var confirmation = L("VersionCheckBackup.RollbackConfirm", "Roll the whole mod (including all option folders) back to {time}? {count} file(s) will be restored, and the current files will be backed up first.")
            .Replace("{time}", point.ToString("yyyy-MM-dd HH:mm"))
            .Replace("{count}", pendingCount.ToString());
        if (!await ConfirmAsync(L("VersionCheckBackup.RollbackButton", "Roll back whole mod"), confirmation) || !IsCurrent(mod, generation)) return;
        var service = _service(); if (service is null) return;
        _busy = true;
        try
        {
            var result = await service.RollbackModToAsync(mod.Directory, RollbackCutoff(point));
            if (!IsCurrent(mod, generation)) return;
            _backupStatus.Text = result.Success || result.FailedItems.Count > 0
                ? L("VersionCheckBackup.RollbackResult", "Rolled back to {time}: {restored} restored, {skipped} skipped, {failed} failed.")
                    .Replace("{time}", point.ToString("yyyy-MM-dd HH:mm"))
                    .Replace("{restored}", result.RestoredCount.ToString())
                    .Replace("{skipped}", result.SkippedCount.ToString())
                    .Replace("{failed}", result.FailedItems.Count.ToString())
                : L("VersionCheckBackup.RestoreFailed", "Backup restore failed: {message}")
                    .Replace("{message}", result.ErrorMessage ?? L("Converters.Unknown", "Unknown"));
            if (result.RestoredCount > 0) await RefreshAfterWriteAsync(mod);
            else await LoadBackupHistoryAsync();
        }
        catch (Exception ex)
        {
            if (IsCurrent(mod, generation)) _backupStatus.Text = L("VersionCheckBackup.RestoreFailed", "Backup restore failed: {message}").Replace("{message}", ex.Message);
        }
        finally { if (IsCurrent(mod, generation)) _busy = false; }
    }

    internal static int CountPendingRollback(ModBackupHistory history, DateTime point) =>
        history.Entries.GroupBy(entry => entry.OriginalPath, StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Where(entry => entry.CreatedLocal <= RollbackCutoff(point))
                .OrderByDescending(entry => entry.CreatedLocal).FirstOrDefault() is { CanRestore: true, CurrentMatchesBackup: false });

    internal static DateTime RollbackCutoff(DateTime selectedMinute) => selectedMinute.AddMinutes(1).AddTicks(-1);

    private async Task<bool> ConfirmRepairAsync()
    {
        if (_settings.RepairDisclaimerAccepted) return true;
        var accepted = await ConfirmAsync(L("VersionCheckDisclaimer.Title", "Mod repair risk and usage notice"), L("VersionCheckDisclaimer.Message", "Repair can change resource structures and may cause crashes. Keep an original backup."));
        if (!accepted) return false;
        try { _settings.RepairDisclaimerAccepted = true; await _settings.SaveAsync(); return true; } catch (Exception ex) { _repairStatus.Text = ex.Message; return false; }
    }

    private Task<bool> ConfirmAsync(string title, string message)
        => _messageBoxOverlay.ConfirmAsync(title, message);

    private void CopyReport()
    {
        try { NativeClipboard.SetText(_report.Text); _copyStatus.Text = L("VersionCheckDetail.Copied", "Copied"); } catch (Exception ex) { _copyStatus.Text = ex.Message; }
    }

    private string BuildReport()
    {
        if (_mod is null || _result is null) return string.Empty;
        var info = new CompatibleCheckInfo { ModName = _mod.Manifest.Name, VersionStatus = _result.Status, GameUnitVersion = _result.GameVersion,
            LastChecked = _result.LastChecked, PatchUnits = _result.PatchUnits.ToList(), ErrorMessage = _result.ErrorMessage, DetailedAnalysis = _analysis };
        return info.ToString();
    }

    private void RefreshLabels()
    {
        _title.Text = L("VersionCheckDetail.Title", "Version check details"); _reportToggle.Text = L("VersionCheckDetail.TechnicalDetails", "Technical details");
        _cleanBackups.Content = L("VersionCheckBackup.CleanOld", "Clean old backups");
    }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshLabels();
    private void OnKeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }
    private string StatusText(ModVersionStatus status) => status switch { ModVersionStatus.Compatible => L("Converters.Compatible", "Compatible"), ModVersionStatus.Incompatible => L("Converters.Incompatible", "Incompatible"), ModVersionStatus.Error => L("VersionCheck.CheckFailed", "Check failed"), _ => L("Converters.UnableToConfirm", "Unable to confirm") };
    private string L(string key, string fallback) => _localization.Get(key, fallback);
    private static Border Card(UIElement child) => new() { Child = child, Padding = new Thickness(16), Background = SurfaceBrush, BorderBrush = StrokeBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
    private static Brush Paint(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
}
