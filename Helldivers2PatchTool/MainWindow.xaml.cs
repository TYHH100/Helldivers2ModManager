using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Helldivers2PatchTool;

public sealed record PatchResultRow(
    string Path,
    string Health,
    int Entries,
    int Units,
    string Details,
    string DiagnosticReport);

public partial class MainWindow : Window
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly SettingsService _settingsService;
    private readonly VersionCheckService _versionCheckService;
    private DirectoryInfo? _targetDirectory;

    public ObservableCollection<PatchResultRow> Results { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        _settingsService = new SettingsService(_loggerFactory.CreateLogger<SettingsService>());
        _settingsService.InitDefault();
        var localizationService = new LocalizationService(_loggerFactory.CreateLogger<LocalizationService>());
        _versionCheckService = new VersionCheckService(
            _loggerFactory.CreateLogger<VersionCheckService>(),
            _settingsService,
            localizationService);

        Loaded += async (_, _) => await DetectGameDirectoryAsync(showNotFoundMessage: false);
    }

    protected override void OnClosed(EventArgs e)
    {
        _loggerFactory.Dispose();
        base.OnClosed(e);
    }

    private void BrowseTarget_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder("选择包含 .patch_* 文件的 Mod 目录");
        if (path is not null)
            targetPathBox.Text = path;
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder("选择 Helldivers 2 游戏目录（其中应包含 data 文件夹）");
        if (path is not null)
        {
            gamePathBox.Text = path;
            UpdateGameDirectoryHint(path, detectedAutomatically: false);
        }
    }

    private async void AutoDetectGame_Click(object sender, RoutedEventArgs e) =>
        await DetectGameDirectoryAsync(showNotFoundMessage: true);

    private static string? PickFolder(string description)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    private async Task DetectGameDirectoryAsync(bool showNotFoundMessage)
    {
        gamePathHint.Text = "正在扫描 Steam 库与常见安装位置…";
        var path = await Task.Run(FindGameDirectory);
        if (path is not null)
        {
            gamePathBox.Text = path;
            UpdateGameDirectoryHint(path, detectedAutomatically: true);
            if (showNotFoundMessage)
                statusText.Text = "已自动检测到 Helldivers 2 游戏目录，可用于 Unit 智能修复。";
            return;
        }

        gamePathHint.Text = "未自动检测到游戏；智能修复前请手动选择。";
        if (showNotFoundMessage)
            statusText.Text = "未找到 Helldivers 2。请确认 Steam 已安装游戏，或使用“手动选择”。";
    }

    private void UpdateGameDirectoryHint(string path, bool detectedAutomatically)
    {
        if (IsValidGameDirectory(path))
        {
            gamePathHint.Text = detectedAutomatically
                ? "✓ 已自动检测到有效游戏目录，可直接用于 Unit 智能修复。"
                : "✓ 游戏目录有效，可直接用于 Unit 智能修复。";
        }
        else
        {
            gamePathHint.Text = "所选目录不是有效的 Helldivers 2 游戏根目录。";
        }
    }

    private static string? FindGameDirectory()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var steamPath in GetSteamInstallPaths())
        {
            foreach (var library in GetSteamLibraries(steamPath))
            {
                candidates.Add(Path.Combine(library, "steamapps", "common", "Helldivers 2"));
                var manifestPath = Path.Combine(library, "steamapps", "appmanifest_553850.acf");
                var installDirectory = ReadSteamInstallDirectory(manifestPath);
                if (!string.IsNullOrWhiteSpace(installDirectory))
                    candidates.Add(Path.Combine(library, "steamapps", "common", installDirectory));
            }
        }

        foreach (var drive in Environment.GetLogicalDrives())
        {
            candidates.Add(Path.Combine(drive, "Steam", "steamapps", "common", "Helldivers 2"));
            candidates.Add(Path.Combine(drive, "SteamLibrary", "steamapps", "common", "Helldivers 2"));
        }

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Helldivers 2"));
        return candidates.FirstOrDefault(IsValidGameDirectory);
    }

    private static IEnumerable<string> GetSteamInstallPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, keyPath, valueName) in new[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (Registry.LocalMachine, @"Software\Valve\Steam", "InstallPath"),
            (Registry.LocalMachine, @"Software\Wow6432Node\Valve\Steam", "InstallPath")
        })
        {
            try
            {
                using var key = hive.OpenSubKey(keyPath);
                if (key?.GetValue(valueName) is string path && Directory.Exists(path))
                    paths.Add(path);
            }
            catch (UnauthorizedAccessException)
            {
                // Registry access is optional; fall back to common paths below.
            }
        }

        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        return paths.Where(Directory.Exists);
    }

    private static IEnumerable<string> GetSteamLibraries(string steamPath)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamPath };
        var libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(libraryFile))
            {
                var content = File.ReadAllText(libraryFile);
                foreach (Match match in Regex.Matches(content, @"""path""\s*""([^""]+)""", RegexOptions.IgnoreCase))
                {
                    var libraryPath = match.Groups[1].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(libraryPath))
                        libraries.Add(libraryPath);
                }
            }
        }
        catch (IOException)
        {
            // Steam may be updating the library file; the root library remains available.
        }

        return libraries;
    }

    private static string? ReadSteamInstallDirectory(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath))
                return null;

            var content = File.ReadAllText(manifestPath);
            var match = Regex.Match(content, @"""installdir""\s*""([^""]+)""", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool IsValidGameDirectory(string path) =>
        Directory.Exists(path) &&
        File.Exists(Path.Combine(path, "bin", "helldivers2.exe")) &&
        File.Exists(Path.Combine(path, "data", "bundles.nxa"));

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPrepareTargetDirectory())
            return;

        await AnalyzeAsync();
    }

    private void ResultsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (resultsGrid.SelectedItem is not PatchResultRow row)
            return;

        diagnosticTitle.Text = "详细诊断 · " + row.Path;
        diagnosticText.Text = row.DiagnosticReport;
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPrepareTargetDirectory())
            return;

        SetBusy(true, "正在生成修复计划…");
        try
        {
            var analysis = await _versionCheckService.AnalyzePatchDirectoryAsync(_targetDirectory!);
            if (!analysis.PatchFiles.Any(patch => patch.UnitDetails.Count > 0))
            {
                statusText.Text = "已跳过：自动修复目前仅支持包含 Unit 资源的模型模组；音频及其他非 Unit 资源不会创建备份或修改文件。";
                return;
            }

            if (!TryConfigureGameDirectory())
                return;

            var companionPlan = await _versionCheckService.CreateCompanionRecoveryPlanAsync(_targetDirectory!);
            if (companionPlan.MissingCount > 0 && !companionPlan.CanRecover)
            {
                statusText.Text = "缺失的 companion 文件无法安全恢复：" + BuildCompanionBlockMessage(companionPlan);
                return;
            }

            var safePlan = await _versionCheckService.CreateRepairPlanAsync(_targetDirectory!);
            if (safePlan.BlockingReasons.Count > 0)
            {
                statusText.Text = "元数据修复无法执行：" + string.Join("；", safePlan.BlockingReasons);
                return;
            }

            var assistedPlan = safePlan.ActionCount == 0 && companionPlan.MissingCount == 0
                ? await _versionCheckService.CreateAutomaticAssistedRepairPlanAsync(_targetDirectory!)
                : null;
            if (assistedPlan is { BlockingReasons.Count: > 0 })
            {
                statusText.Text = "Unit 智能修复无法执行：" + string.Join("；", assistedPlan.BlockingReasons);
                return;
            }

            if (companionPlan.MissingCount == 0 && safePlan.ActionCount == 0 && assistedPlan is { CanRepair: false })
            {
                statusText.Text = "没有检测到可安全自动修复的问题。";
                return;
            }

            var confirmation = System.Windows.MessageBox.Show(
                BuildRepairConfirmation(companionPlan, safePlan, assistedPlan),
                "确认一键修复",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
                return;

            var recoveredCount = 0;
            if (companionPlan.MissingCount > 0)
            {
                var recoveryResult = await _versionCheckService.RecoverCompanionFilesAsync(_targetDirectory!);
                if (!recoveryResult.Success)
                    throw new InvalidDataException("companion 恢复失败：" + recoveryResult.ErrorMessage);
                recoveredCount = recoveryResult.RecoveredCount;
            }

            var metadataActionCount = 0;
            var backupCount = 0;
            var refreshedSafePlan = await _versionCheckService.CreateRepairPlanAsync(_targetDirectory!);
            if (refreshedSafePlan.ActionCount > 0)
            {
                if (!refreshedSafePlan.CanRepair)
                    throw new InvalidDataException(string.Join(Environment.NewLine, refreshedSafePlan.BlockingReasons));

                var metadataResult = await _versionCheckService.RepairModAsync(_targetDirectory!);
                if (!metadataResult.Success)
                    throw new InvalidDataException("元数据修复失败：" + metadataResult.ErrorMessage);
                metadataActionCount = metadataResult.AppliedActionCount;
                backupCount += metadataResult.BackupPaths.Count;
            }

            var refreshedAssistedPlan = await _versionCheckService.CreateAutomaticAssistedRepairPlanAsync(_targetDirectory!);
            if (refreshedAssistedPlan.BlockingReasons.Count > 0)
                throw new InvalidDataException("Unit 智能修复无法执行：" + string.Join("；", refreshedAssistedPlan.BlockingReasons));

            var assistedActionCount = 0;
            if (refreshedAssistedPlan.CanRepair)
            {
                var assistedResult = await _versionCheckService.RepairModAutomaticallyAsync(_targetDirectory!);
                if (!assistedResult.Success)
                    throw new InvalidDataException("Unit 智能修复失败：" + assistedResult.ErrorMessage);
                assistedActionCount = assistedResult.AppliedActionCount;
                backupCount += assistedResult.BackupPaths.Count;
            }

            var successMessage = $"一键修复完成：恢复 {recoveredCount} 个 companion 文件，应用 {metadataActionCount} 项元数据修复和 {assistedActionCount} 项 Unit/材质修复，创建 {backupCount} 份补丁备份。";
            await AnalyzeAsync();
            statusText.Text = successMessage;
        }
        catch (Exception ex)
        {
            statusText.Text = "修复失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private bool TryConfigureGameDirectory()
    {
        var path = gamePathBox.Text.Trim();
        if (!IsValidGameDirectory(path))
        {
            statusText.Text = "一键修复需要有效的 Helldivers 2 游戏目录，以便恢复 companion 文件并执行 Unit/材质智能修复。";
            return false;
        }

        _settingsService.GameDirectory = path;
        return true;
    }

    private static string BuildRepairConfirmation(
        CompanionRecoveryPlan companionPlan,
        ModRepairPlan safePlan,
        AssistedModRepairPlan? assistedPlan)
    {
        var steps = new List<string>();
        if (companionPlan.MissingCount > 0)
            steps.Add($"恢复 {companionPlan.RecoverableCount} 个缺失的 companion 文件");
        if (safePlan.ActionCount > 0)
            steps.Add($"修复 {safePlan.FileCount} 个补丁中的 {safePlan.ActionCount} 项元数据");
        if (assistedPlan is { CanRepair: true })
            steps.Add($"参考当前游戏修复 {assistedPlan.FileCount} 个补丁中的 {assistedPlan.ActionCount} 个 Unit/材质项");
        if (assistedPlan is null)
            steps.Add("在前置修复后重新生成 Unit/材质智能修复计划");

        return "将按管理器同样的安全顺序执行：\n\n"
            + string.Join("\n", steps.Select((step, index) => $"{index + 1}. {step}"))
            + "\n\n补丁写入前会创建备份，且每个阶段完成后都会重新检查。是否继续？";
    }

    private static string BuildCompanionBlockMessage(CompanionRecoveryPlan plan)
    {
        var reasons = plan.Items
            .Where(item => item.IsMissing && !item.CanRecover)
            .Select(item => $"{Path.GetFileName(item.CompanionPath)}：{item.Reason}");
        return string.Join("；", reasons);
    }

    private bool TryPrepareTargetDirectory()
    {
        var path = targetPathBox.Text.Trim();
        if (!Directory.Exists(path))
        {
            statusText.Text = "请选择存在的 Mod 或补丁目录。";
            return false;
        }

        _targetDirectory = new DirectoryInfo(path);
        return true;
    }

    private async Task AnalyzeAsync()
    {
        SetBusy(true, "正在检查补丁结构和伴生文件…");
        try
        {
            var analysis = await _versionCheckService.AnalyzePatchDirectoryAsync(_targetDirectory!);
            Results.Clear();
            emptyResultsPanel.Visibility = Visibility.Collapsed;
            foreach (var patch in analysis.PatchFiles.OrderBy(p => p.FileName, StringComparer.OrdinalIgnoreCase))
            {
                Results.Add(new PatchResultRow(
                    patch.FileName,
                    GetHealthText(patch.HealthStatus),
                    patch.NumFiles,
                    patch.UnitDetails.Count,
                    BuildDetails(patch),
                    BuildDiagnosticReport(patch)));
            }

            if (Results.Count > 0)
                resultsGrid.SelectedIndex = 0;

            statusText.Text = analysis.TotalPatchFiles == 0
                ? "未找到 .patch_* 主补丁文件。"
                : $"检测完成：{analysis.TotalPatchFiles} 个补丁，正常 {analysis.HealthyFileCount} 个，警告 {analysis.WarningFileCount} 个，异常 {analysis.CorruptedFileCount} 个。";
        }
        catch (Exception ex)
        {
            Results.Clear();
            emptyResultsPanel.Visibility = Visibility.Visible;
            diagnosticTitle.Text = "详细诊断";
            diagnosticText.Text = "检测未完成，因此没有可显示的补丁诊断。\n\n原因：" + ex.Message;
            statusText.Text = "检测失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private static string GetHealthText(PatchHealthStatus status) => status switch
    {
        PatchHealthStatus.Healthy => "正常",
        PatchHealthStatus.Warning => "警告",
        PatchHealthStatus.Corrupted => "异常",
        PatchHealthStatus.NoUnitResources => "无 Unit 资源",
        _ => status.ToString()
    };

    private static string BuildDetails(PatchFileAnalysis patch)
    {
        var messages = new List<string>();
        if (!patch.HeaderValid || !patch.FileEntriesInBounds)
            messages.Add("TOC 无效");
        if (!patch.TypeDistributionValid)
            messages.Add("类型表不一致");
        if (!patch.MainDataBoundsValid)
            messages.Add("主数据越界");
        if (!patch.EntryIndicesValid)
            messages.Add($"{patch.EntryIndexIssueCount} 个 TOC 索引异常");
        if (patch.RequiresGpuResources && !patch.HasGpuResources)
            messages.Add("缺少 GPU 伴生文件");
        if (patch.RequiresStream && !patch.HasStream)
            messages.Add("缺少 stream 伴生文件");
        if (!patch.GpuResourceBoundsValid || patch.GpuAlignmentIssueCount > 0)
            messages.Add("GPU 引用范围/对齐异常");
        if (!patch.StreamBoundsValid || patch.StreamAlignmentIssueCount > 0)
            messages.Add("stream 引用范围/对齐异常");
        if (patch.UnitDetails.Any(unit => unit.IsTruncated || !unit.UnitDataInBounds || !unit.LODGroupInBounds))
            messages.Add("Unit 数据异常");
        if (patch.UnitDetails.Any(unit => !unit.DeclaredSizeMatchesInternal ||
                                          (unit.LayoutFormatChecked && !unit.LayoutFormatValid)))
            messages.Add("Unit 内部结构警告");
        if (patch.UnitDetails.Any(unit => unit.GpuStructureChecked && !unit.GpuStructureValid))
            messages.Add("GPU Stream 布局或缓冲区异常");
        return messages.Count == 0 ? "结构检查通过" : string.Join("；", messages);
    }

    private static string BuildDiagnosticReport(PatchFileAnalysis patch)
    {
        var lines = new List<string>
        {
            $"状态：{GetHealthText(patch.HealthStatus)}",
            $"文件表：{patch.NumFiles} 个条目；类型表：{patch.NumTypes} 种类型，声明资源总数 {patch.TotalResources}。"
        };

        if (patch.ResourceTypes.Count > 0)
        {
            var types = string.Join("，", patch.ResourceTypes.Select(type =>
                $"0x{unchecked((ulong)type.TypeId):X16} × {type.ResourceCount}"));
            lines.Add("类型表声明：" + types);
        }

        var issues = new List<string>();
        if (!patch.HeaderValid || !patch.FileEntriesInBounds)
            issues.Add("补丁头或 TOC 文件条目无效/越界。" + WithMessage(patch.Message));
        if (!patch.TypeDistributionValid)
            issues.Add($"资源类型表与实际文件条目不一致：类型表声明 {patch.TotalResources} 个资源，文件表包含 {patch.NumFiles} 个条目（发现 {patch.TypeDistributionIssueCount} 项不一致）。");
        if (!patch.MainDataBoundsValid)
            issues.Add($"主资源数据范围异常：发现 {patch.MainDataIssueCount} 个越界或重叠范围。");
        if (!patch.EntryIndicesValid)
            issues.Add($"TOC 条目索引不连续：发现 {patch.EntryIndexIssueCount} 个非 1..N 的索引值。");
        if (patch.RequiresGpuResources && !patch.HasGpuResources)
            issues.Add("缺少必需的 .gpu_resources 伴生文件：至少一个条目引用了 GPU 数据。");
        if (patch.RequiresStream && !patch.HasStream)
            issues.Add("缺少必需的 .stream 伴生文件：至少一个条目引用了 stream 数据。");
        if (!patch.GpuResourceBoundsValid || patch.GpuAlignmentIssueCount > 0)
            issues.Add($"GPU 资源引用异常：越界 {patch.GpuResourceIssueCount} 项，非 64 字节对齐 {patch.GpuAlignmentIssueCount} 项。");
        if (!patch.StreamBoundsValid || patch.StreamAlignmentIssueCount > 0)
            issues.Add($"stream 资源引用异常：越界 {patch.StreamIssueCount} 项，非 64 字节对齐 {patch.StreamAlignmentIssueCount} 项。");

        foreach (var unit in patch.UnitDetails)
        {
            var identifier = $"Unit #{unit.EntryIndex}（ID 0x{unchecked((ulong)unit.FileId):X16}）";
            if (unit.IsTruncated)
                issues.Add($"{identifier} 被截断：TOC 声明 {unit.DataSize} 字节，Unit 内部需要 {unit.ExpectedDataSize} 字节，缺少 {Math.Max(0, unit.ExpectedDataSize - unit.DataSize)} 字节。" + WithMessage(unit.Warning));
            else if (!unit.DeclaredSizeMatchesInternal)
                issues.Add($"{identifier} 大小不一致：TOC 声明 {unit.DataSize} 字节，Unit 内部记录 {unit.ExpectedDataSize} 字节。" + WithMessage(unit.Warning));

            if (!unit.UnitDataInBounds)
                issues.Add($"{identifier} 的 Unit 数据范围超出主补丁文件边界。" + WithMessage(unit.Warning));
            else if (!unit.LODGroupInBounds)
                issues.Add($"{identifier} 的 LOD 数据范围超出 Unit 声明边界。" + WithMessage(unit.Warning));

            if (unit.LayoutFormatChecked && !unit.LayoutFormatValid)
                issues.Add($"{identifier} 的旧版 Layout 格式异常：检测到 {unit.LayoutFormatIssueCount} 个无效 item_format。" + WithMessage(unit.Warning));

            if (unit.GpuStructureChecked && !unit.GpuStructureValid)
                issues.Add($"{identifier} 的 GPU Stream 结构异常：{unit.GpuStructureIssueCount} 个布局或缓冲区问题。" + WithMessage(unit.Warning));
            else if (unit.GpuStructureChecked && unit.UnknownGpuComponentCount > 0)
                issues.Add($"{identifier} 包含 {unit.UnknownGpuComponentCount} 个未知 GPU 顶点组件，未按损坏处理。" + WithMessage(unit.Warning));
        }

        if (issues.Count == 0)
        {
            lines.Add("\n✓ 未发现结构、伴生文件、资源边界或 Unit 内部结构问题。");
        }
        else
        {
            lines.Add("\n检测到的问题：");
            lines.AddRange(issues.Select(issue => "• " + issue));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string WithMessage(string? message) =>
        string.IsNullOrWhiteSpace(message) ? string.Empty : " 原始解析信息：" + message;

    private void SetBusy(bool isBusy, string? message)
    {
        analyzeButton.IsEnabled = !isBusy;
        repairButton.IsEnabled = !isBusy;
        if (message is not null)
        {
            statusText.Text = message;
            statusDot.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(76, 169, 255));
        }
    }
}
