using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class DashboardRuntime
{
    private readonly Dictionary<string, ModConflictAnalysisResult> _conflictCache = new(StringComparer.Ordinal);
    private ModService? _conflictModService;
    private ModConflictService? _conflictService;
    private ModConflictRepository? _conflictRepository;
    private ModConflictAnalysisResult? _appliedConflictResult;
    private string? _appliedConflictKey;
    private CancellationTokenSource? _conflictCancellation;
    private bool _conflictScanInProgress;
    private bool _conflictScanPending;
    private bool _conflictStopped;
    private Task _conflictIdleTask = Task.CompletedTask;

    internal Task ConflictHashMigrationTask => _conflictModService?.HashMigrationTask ?? Task.CompletedTask;

    private void AttachConflictWorkspace()
    {
        _conflictCancellation?.Cancel();
        _conflictCache.Clear();
        _appliedConflictKey = null;
        _appliedConflictResult = null;
        _conflictModService = CreateModService();
        var versionCheck = new VersionCheckService(
            NullLogger<VersionCheckService>.Instance, settings, localization);
        _conflictService = new ModConflictService(
            NullLogger<ModConflictService>.Instance, _conflictModService, versionCheck);
        _conflictRepository = new ModConflictRepository(
            NullLogger<ModConflictRepository>.Instance, database);
        workspace.ProfileChanged += OnConflictProfileChanged;
        RestoreCachedConflictResult();
    }

    private void OnConflictProfileChanged(object? sender, EventArgs eventArgs)
        => RequestAutomaticConflictScan();

    private ModData[] GetConflictDeploymentMods()
        => DeploymentOrderHelper.BuildDeploymentMods(workspace.CaptureProfileSnapshot(),
            settings.UseDeploymentOrder, settings.DeploymentOrderGuids, settings.DeployBottomToTop);

    private string CurrentConflictKey()
        => _conflictService!.BuildCacheKey(GetConflictDeploymentMods());

    private ModConflictAnalysisResult? LoadConflictResult(string key)
    {
        if (_conflictCache.TryGetValue(key, out var cached))
            return cached;
        if (string.IsNullOrEmpty(settings.StorageDirectory))
            return null;
        var result = _conflictRepository!.Load(settings.StorageDirectory, key);
        if (result is not null)
            _conflictCache[key] = result;
        return result;
    }

    private bool RestoreCachedConflictResult()
    {
        if (_conflictService is null)
            return false;
        var key = CurrentConflictKey();
        var cached = LoadConflictResult(key);
        if (cached is null)
            return false;
        ApplyConflictResult(key, cached);
        return true;
    }

    private void RequestAutomaticConflictScan()
    {
        if (_conflictStopped || _batchRepairActive || _conflictService is null || _tasks is null || _layout is null)
            return;
        if (!HasConfiguredConflictPaths())
        {
            _dashboard?.SetConflictResult(null);
            return;
        }
        try
        {
            var key = CurrentConflictKey();
            if (string.Equals(_appliedConflictKey, key, StringComparison.Ordinal)
                && _appliedConflictResult is not null)
                return;
            if (LoadConflictResult(key) is { } cached)
            {
                ApplyConflictResult(key, cached);
                return;
            }
            _dashboard?.SetConflictResult(null);
            if (_conflictScanInProgress)
            {
                _conflictScanPending = true;
                return;
            }
            _ = ScanConflictsAsync(showReport: false);
        }
        catch (Exception ex)
        {
            _toastOverlay?.Show(localization["BackgroundTasksPage.TaskTypeConflictScan"], ex.Message,
                isError: true);
        }
    }

    private async Task ScanConflictsAsync(bool showReport)
    {
        if (_conflictStopped || _batchRepairActive || _conflictService is null || _conflictModService is null || _tasks is null)
            return;
        if (!HasConfiguredConflictPaths())
        {
            if (showReport)
                ReportError(new InvalidOperationException(localization["DashboardPage.DeployNoGameDir"]));
            return;
        }
        if (_conflictScanInProgress)
        {
            _conflictScanPending = true;
            return;
        }

        var targetWorkspace = workspace;
        var deploymentMods = GetConflictDeploymentMods();
        var key = _conflictService.BuildCacheKey(deploymentMods);
        var scanService = _conflictService;
        var modService = _conflictModService;
        var repository = _conflictRepository!;
        var storageDirectory = settings.StorageDirectory;
        using var cancellation = new CancellationTokenSource();
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _conflictIdleTask = idle.Task;
        _conflictCancellation = cancellation;
        _conflictScanInProgress = true;
        _dashboard?.SetConflictResult(null);
        _dashboard?.SetConflictScanning(true);
        var scanningText = localization["DashboardPage.ConflictScanning"];
        try
        {
            var result = await _tasks.RunAsync(
                localization["BackgroundTasksPage.TaskTypeConflictScan"], scanningText,
                async (_, token) =>
                {
                    if (!modService.Initialized)
                        modService.Init(settings);
                    token.ThrowIfCancellationRequested();
                    return await scanService.AnalyzeAsync(deploymentMods, token);
                }, scanningText, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _conflictCache[key] = result;
            if (!settings.IsReadonly && !string.IsNullOrEmpty(storageDirectory))
                await repository.SaveAsync(storageDirectory, key, result);
            cancellation.Token.ThrowIfCancellationRequested();
            if (ReferenceEquals(targetWorkspace, workspace)
                && string.Equals(key, CurrentConflictKey(), StringComparison.Ordinal))
            {
                ApplyConflictResult(key, result);
                if (showReport)
                {
                    var visibleCount = result.Conflicts.Count(static conflict =>
                        !string.IsNullOrWhiteSpace(conflict.FriendlyName));
                    var message = visibleCount > 0
                        ? localization["Toast.ConflictScanSummary"]
                            .Replace("{mods}", result.ScannedModCount.ToString())
                            .Replace("{conflicts}", visibleCount.ToString())
                        : localization["Toast.ConflictScanClean"]
                            .Replace("{mods}", result.ScannedModCount.ToString());
                    _toastOverlay?.Show(localization["BackgroundTasksPage.TaskTypeConflictScan"], message);
                }
            }
            else
            {
                _conflictScanPending = true;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (showReport)
                ReportError(new InvalidOperationException(
                    localization["DashboardPage.ConflictScanFailed"] + "\n" + ex.Message, ex));
        }
        finally
        {
            if (ReferenceEquals(_conflictCancellation, cancellation))
                _conflictCancellation = null;
            _conflictScanInProgress = false;
            if (!_conflictStopped)
                _dashboard?.SetConflictScanning(false);
            if (_conflictScanPending)
            {
                _conflictScanPending = false;
                RequestAutomaticConflictScan();
            }
            idle.TrySetResult();
        }
    }

    private void ApplyConflictResult(string key, ModConflictAnalysisResult result)
    {
        _appliedConflictKey = key;
        _appliedConflictResult = result;
        _dashboard?.SetConflictResult(result);
    }

    private void OpenConflictDetail(ModData? mod)
    {
        if (mod is null || _appliedConflictResult is null)
            return;
        var conflicts = _appliedConflictResult.Conflicts
            .Where(conflict => conflict.Participants.Any(participant =>
                participant.ModGuid == mod.Manifest.Guid)).ToArray();
        _conflictOverlay?.Show(mod.Manifest.Name, conflicts);
    }

    private void CancelConflictScan() => _conflictCancellation?.Cancel();

    private void StopConflictScanning()
    {
        _conflictStopped = true;
        _conflictScanPending = false;
        CancelConflictScan();
    }

    private bool HasConfiguredConflictPaths()
        => settings.Initialized
           && !string.IsNullOrWhiteSpace(settings.StorageDirectory)
           && !string.IsNullOrWhiteSpace(settings.TempDirectory)
           && !string.IsNullOrWhiteSpace(settings.GameDirectory)
           && Directory.Exists(Path.Combine(settings.GameDirectory, "data"));
}
