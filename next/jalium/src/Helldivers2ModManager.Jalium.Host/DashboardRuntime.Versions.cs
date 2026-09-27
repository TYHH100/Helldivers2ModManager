using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class DashboardRuntime
{
    private IReadOnlyDictionary<Guid, ModVersionCheckResult> _versionResults =
        new Dictionary<Guid, ModVersionCheckResult>();
    private CancellationTokenSource? _versionCancellation;
    private CancellationTokenSource? _autoVersionCancellation;
    private IReadOnlyDictionary<Guid, DateTime> _knownVersionTimes = new Dictionary<Guid, DateTime>();
    private bool _versionAutoPending;
    private bool _versionStopped;
    private Task _versionIdleTask = Task.CompletedTask;

    private void AttachVersionWorkspace()
    {
        _versionService ??= new VersionCheckService(
            NullLogger<VersionCheckService>.Instance, settings, localization);
        _versionRepository ??= new VersionCheckRepository(
            NullLogger<VersionCheckRepository>.Instance, database);
        try
        {
            var cached = _versionRepository.LoadAll(settings.StorageDirectory);
            _knownVersionTimes = cached.ToDictionary(entry => entry.Key,
                entry => entry.Value.ModLastWriteTimeUtc);
            _versionResults = workspace.Mods
                .Where(mod => cached.ContainsKey(mod.Manifest.Guid))
                .ToDictionary(mod => mod.Manifest.Guid, mod =>
                {
                    var entry = cached[mod.Manifest.Guid];
                    return new ModVersionCheckResult
                    {
                        Status = entry.Status,
                        GameVersion = entry.GameVersion,
                        LastChecked = entry.LastChecked,
                    };
                });
            _dashboard?.SetVersionResults(_versionResults);
        }
        catch (Exception ex)
        {
            _versionResults = new Dictionary<Guid, ModVersionCheckResult>();
            _dashboard?.SetVersionResults(_versionResults);
            _toastOverlay?.Show(localization["Toast.VersionCheckTitle"], ex.Message, isError: true);
        }
    }

    private void RequestAutomaticVersionCheck()
    {
        if (_versionStopped || _batchRepairActive || !settings.AutoCheckVersionOnStartup || _versionService is null
            || workspace.Mods.Count == 0)
            return;
        if (_versionCancellation is not null)
        {
            _versionAutoPending = true;
            return;
        }
        _ = CheckVersionChangesAsync();
    }

    internal async Task CheckVersionChangesAsync()
    {
        _autoVersionCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _autoVersionCancellation = cancellation;
        var targetWorkspace = workspace;
        var mods = targetWorkspace.Mods.ToArray();
        var knownTimes = _knownVersionTimes;
        try
        {
            var timestamps = await Task.Run(() => mods.ToDictionary(
                mod => mod.Manifest.Guid, mod => GetModLastWriteTime(mod.Directory)),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_versionStopped || !ReferenceEquals(targetWorkspace, workspace))
                return;
            var exe = Path.Combine(settings.GameDirectory, "bin", "helldivers2.exe");
            var previousExeTime = _versionRepository!.GetGameExeLastWriteTime(settings.StorageDirectory);
            var gameChanged = File.Exists(exe) && previousExeTime != DateTime.MinValue
                && File.GetLastWriteTimeUtc(exe) != previousExeTime;
            var changed = mods.Where(mod => !knownTimes.TryGetValue(mod.Manifest.Guid, out var previous)
                || previous != timestamps[mod.Manifest.Guid]).ToArray();
            if (gameChanged || changed.Length == mods.Length)
                await ScanVersionsAsync();
            else if (changed.Length > 0)
                await ScanVersionsAsync(changed);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_versionStopped)
                _toastOverlay?.Show(localization["Toast.VersionCheckTitle"], ex.Message, isError: true);
        }
        finally
        {
            if (ReferenceEquals(_autoVersionCancellation, cancellation))
                _autoVersionCancellation = null;
        }
    }

    private async Task ScanVersionsAsync(IReadOnlyList<ModData>? changedMods = null,
        bool afterRepair = false)
    {
        if ((_batchRepairActive && !afterRepair) || _versionService is null
            || _versionRepository is null || _tasks is null
            || _versionCancellation is not null)
            return;
        if (!settings.Initialized || string.IsNullOrWhiteSpace(settings.StorageDirectory))
        {
            ReportError(new InvalidOperationException(localization["DashboardPage.DeployNoGameDir"]));
            return;
        }

        var targetWorkspace = workspace;
        var mods = targetWorkspace.Mods.ToArray();
        using var cancellation = new CancellationTokenSource();
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _versionIdleTask = idle.Task;
        _versionCancellation = cancellation;
        _dashboard?.SetVersionScanning(true);
        try
        {
            var existing = _versionResults;
            var results = await _tasks.RunAsync(
                localization["Toast.VersionCheckTitle"], localization["VersionCheck.ScanningMods"],
                async (_, token) =>
                {
                    if (changedMods is null)
                        return await _versionService.CheckAllModsAsync(mods);
                    var updated = new Dictionary<Guid, ModVersionCheckResult>(existing);
                    foreach (var mod in changedMods)
                    {
                        token.ThrowIfCancellationRequested();
                        var fallback = existing.GetValueOrDefault(mod.Manifest.Guid)?.GameVersion;
                        var result = await _versionService.CheckSingleModAsync(mod,
                            fallback is > 0 ? fallback : null);
                        if (result is not null)
                            updated[mod.Manifest.Guid] = result;
                    }
                    return updated;
                },
                localization["VersionCheck.ScanningMods"], cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(targetWorkspace, workspace))
                return;

            var timestamps = await Task.Run(() => mods.ToDictionary(
                mod => mod.Manifest.Guid, mod => GetModLastWriteTime(mod.Directory)),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!settings.IsReadonly)
            {
                await _versionRepository.SaveAllAsync(settings.StorageDirectory,
                    results.ToDictionary(pair => pair.Key, pair =>
                        (pair.Value.Status, pair.Value.GameVersion, pair.Value.LastChecked,
                            timestamps[pair.Key])));
                var exe = Path.Combine(settings.GameDirectory, "bin", "helldivers2.exe");
                if (File.Exists(exe))
                    await _versionRepository.UpdateGameExeLastWriteTimeAsync(
                        settings.StorageDirectory, File.GetLastWriteTimeUtc(exe));
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(targetWorkspace, workspace))
                return;
            _versionResults = results;
            _knownVersionTimes = timestamps;
            _dashboard?.SetVersionResults(results);
            var incompatible = results.Values.Count(result => result.Status == ModVersionStatus.Incompatible);
            var compatible = results.Values.Count(result => result.Status == ModVersionStatus.Compatible);
            var message = incompatible > 0
                ? localization["VersionCheck.IncompatibleFoundMsg"]
                    .Replace("{IncompatibleModCount}", incompatible.ToString())
                : compatible > 0
                    ? localization["VersionCheck.AllCompatible"]
                        .Replace("{CompatibleModCount}", compatible.ToString())
                    : localization["VersionCheck.NoneCheckable"];
            _toastOverlay?.Show(localization["Toast.VersionCheckTitle"], message);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _toastOverlay?.Show(localization["Toast.VersionCheckTitle"],
                localization["VersionCheck.CheckFailed"] + "\n" + ex.Message, isError: true);
        }
        finally
        {
            if (ReferenceEquals(_versionCancellation, cancellation))
                _versionCancellation = null;
            _dashboard?.SetVersionScanning(false);
            if (_versionAutoPending && !_versionStopped)
            {
                _versionAutoPending = false;
                RequestAutomaticVersionCheck();
            }
            idle.TrySetResult();
        }
    }

    private async Task OpenVersionDetailAsync(ModData? mod)
    {
        if (mod is null || _versionService is null || _tasks is null)
            return;
        using var cancellation = new CancellationTokenSource();
        try
        {
            var reference = _versionResults.GetValueOrDefault(mod.Manifest.Guid)?.GameVersion;
            var result = await _tasks.RunAsync(
                localization["Toast.VersionCheckTitle"], mod.Manifest.Name,
                (_, _) => _versionService.CheckSingleModAsync(mod, reference is > 0 ? reference : null,
                    includeDetailedAnalysis: true),
                mod.Manifest.Name, cancellation.Token);
            if (cancellation.IsCancellationRequested || result is null)
                return;
            _versionOverlay?.Show(mod, result, result.DetailedAnalysis);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested)
                _toastOverlay?.Show(localization["Toast.VersionCheckTitle"], ex.Message, isError: true);
        }
    }

    private static DateTime GetModLastWriteTime(DirectoryInfo directory)
    {
        var latest = directory.Exists ? directory.LastWriteTimeUtc : DateTime.MinValue;
        try
        {
            foreach (var file in directory.EnumerateFiles("*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }))
                if (file.LastWriteTimeUtc > latest)
                    latest = file.LastWriteTimeUtc;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return latest;
    }

    private void CancelVersionScan()
    {
        _versionCancellation?.Cancel();
        _autoVersionCancellation?.Cancel();
    }

    private void StopVersionScanning()
    {
        _versionStopped = true;
        _versionAutoPending = false;
        CancelVersionScan();
    }
}
