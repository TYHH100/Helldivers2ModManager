using Helldivers2ModManager.Models;
using Microsoft.Extensions.Logging;
using System.IO;

namespace Helldivers2ModManager.Services;

/// <summary>
/// Coordinates companion-file recovery without coupling the workflow to the
/// version-check service. Resource reconstruction remains supplied by the
/// legacy reader through a narrow delegate until that reader is extracted.
/// </summary>
internal sealed class CompanionRecoveryCoordinator
{
    private sealed record PreparedRecovery(CompanionRecoveryItem Item, string TemporaryPath);

    private readonly ILogger _logger;
    private readonly SettingsService _settingsService;
    private readonly Func<DirectoryInfo?> _getGameDataDirectory;
    private readonly Func<FileInfo, Task<PatchFileAnalysis>> _analyzePatch;
    private readonly Func<FileInfo, string, CancellationToken, Task<bool>> _canBuildGameCompanion;
    private readonly Func<FileInfo, string, string, CancellationToken, Task<bool>> _writeGameCompanion;
    private readonly SemaphoreSlim _recoverySemaphore = new(1, 1);

    public CompanionRecoveryCoordinator(
        ILogger logger,
        SettingsService settingsService,
        Func<DirectoryInfo?> getGameDataDirectory,
        Func<FileInfo, Task<PatchFileAnalysis>> analyzePatch,
        Func<FileInfo, string, CancellationToken, Task<bool>> canBuildGameCompanion,
        Func<FileInfo, string, string, CancellationToken, Task<bool>> writeGameCompanion)
    {
        _logger = logger;
        _settingsService = settingsService;
        _getGameDataDirectory = getGameDataDirectory;
        _analyzePatch = analyzePatch;
        _canBuildGameCompanion = canBuildGameCompanion;
        _writeGameCompanion = writeGameCompanion;
    }

    public async Task<CompanionRecoveryPlan> CreatePlanAsync(
        DirectoryInfo modDirectory,
        CancellationToken cancellationToken = default)
    {
        var items = new List<CompanionRecoveryItem>();
        if (!modDirectory.Exists)
            return new CompanionRecoveryPlan { Items = items };

        var patchFiles = modDirectory.GetFiles("*", SearchOption.AllDirectories)
            .Where(file => IsMainPatchFile(file.Name))
            .ToArray();
        foreach (var patchFile in patchFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var analysis = await _analyzePatch(patchFile);
            if (analysis.RequiresGpuResources && !analysis.HasGpuResources)
            {
                items.Add(await CreateItemAsync(modDirectory, patchFile, ".gpu_resources", cancellationToken));
            }
            if (analysis.RequiresStream && !analysis.HasStream)
            {
                items.Add(await CreateItemAsync(modDirectory, patchFile, ".stream", cancellationToken));
            }
        }

        return new CompanionRecoveryPlan { Items = items };
    }

    public async Task<CompanionRecoveryResult> RecoverAsync(
        DirectoryInfo modDirectory,
        CancellationToken cancellationToken = default)
    {
        await _recoverySemaphore.WaitAsync(cancellationToken);
        var prepared = new List<PreparedRecovery>();
        var committed = new List<string>();
        try
        {
            var plan = await CreatePlanAsync(modDirectory, cancellationToken);
            if (!plan.CanRecover)
            {
                var reasons = plan.Items
                    .Where(item => item.IsMissing && !item.CanRecover)
                    .Select(item => $"{Path.GetFileName(item.CompanionPath)}: {item.Reason}")
                    .ToList();
                return new CompanionRecoveryResult
                {
                    ErrorMessage = reasons.Count > 0
                        ? string.Join(Environment.NewLine, reasons)
                        : "No missing companion files can be recovered."
                };
            }

            foreach (var item in plan.Items.Where(candidate => candidate.CanRecover))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(item.CompanionPath))
                    throw new IOException($"The companion file appeared after planning: {item.CompanionPath}");

                var temporaryPath = Path.Combine(
                    Path.GetDirectoryName(item.CompanionPath)!,
                    "." + Path.GetFileName(item.CompanionPath) + ".hd2mm-recover-" +
                    Guid.NewGuid().ToString("N") + ".tmp");

                if (item.SourceKind == CompanionRecoverySourceKind.ExactPatchCopy)
                {
                    if (string.IsNullOrWhiteSpace(item.SourcePath) || !File.Exists(item.SourcePath))
                        throw new FileNotFoundException("The exact companion source is no longer available.", item.SourcePath);
                    await ModBackupService.CopyFileDurablyAsync(item.SourcePath, temporaryPath, cancellationToken);
                }
                else if (item.SourceKind == CompanionRecoverySourceKind.CurrentGameBundles)
                {
                    if (!await _writeGameCompanion(
                            new FileInfo(item.PatchPath), item.Suffix, temporaryPath, cancellationToken))
                    {
                        throw new InvalidDataException("Current game resources no longer provide an exact companion reconstruction.");
                    }
                }
                else
                {
                    throw new InvalidDataException("The recovery source is not supported.");
                }

                prepared.Add(new PreparedRecovery(item, temporaryPath));
            }

            foreach (var item in prepared)
            {
                File.Move(item.TemporaryPath, item.Item.CompanionPath);
                committed.Add(item.Item.CompanionPath);
            }

            foreach (var patchPath in prepared.Select(item => item.Item.PatchPath)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var validation = await _analyzePatch(new FileInfo(patchPath));
                if ((validation.RequiresGpuResources && !validation.HasGpuResources) ||
                    (validation.RequiresStream && !validation.HasStream) ||
                    !validation.GpuResourceBoundsValid || !validation.StreamBoundsValid)
                {
                    throw new InvalidDataException($"Recovered companion validation failed for {Path.GetFileName(patchPath)}.");
                }
            }

            return new CompanionRecoveryResult
            {
                Success = true,
                RecoveredCount = committed.Count,
                RecoveredPaths = committed.ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover companion files in {ModDirectory}", modDirectory.FullName);
            foreach (var path in committed.AsEnumerable().Reverse())
                ModBackupService.TryDeleteFile(path);
            return new CompanionRecoveryResult { ErrorMessage = ex.Message };
        }
        finally
        {
            foreach (var item in prepared)
                ModBackupService.TryDeleteFile(item.TemporaryPath);
            _recoverySemaphore.Release();
        }
    }

    private async Task<CompanionRecoveryItem> CreateItemAsync(
        DirectoryInfo modDirectory,
        FileInfo patchFile,
        string suffix,
        CancellationToken cancellationToken)
    {
        var companionPath = patchFile.FullName + suffix;
        var exactSource = await FindExactSourceAsync(modDirectory, patchFile, suffix, cancellationToken);
        if (exactSource is not null)
        {
            return new CompanionRecoveryItem
            {
                PatchPath = patchFile.FullName,
                CompanionPath = companionPath,
                Suffix = suffix,
                IsRequired = true,
                IsMissing = true,
                CanRecover = true,
                SourceKind = CompanionRecoverySourceKind.ExactPatchCopy,
                SourcePath = exactSource,
                Reason = "An exact complete patch copy is available."
            };
        }

        if (await _canBuildGameCompanion(patchFile, suffix, cancellationToken))
        {
            return new CompanionRecoveryItem
            {
                PatchPath = patchFile.FullName,
                CompanionPath = companionPath,
                Suffix = suffix,
                IsRequired = true,
                IsMissing = true,
                CanRecover = true,
                SourceKind = CompanionRecoverySourceKind.CurrentGameBundles,
                SourcePath = "Current game bundles",
                Reason = "Every companion segment has an exact current-game resource match."
            };
        }

        return new CompanionRecoveryItem
        {
            PatchPath = patchFile.FullName,
            CompanionPath = companionPath,
            Suffix = suffix,
            IsRequired = true,
            IsMissing = true,
            CanRecover = false,
            SourceKind = CompanionRecoverySourceKind.None,
            Reason = "No byte-compatible complete copy or current-game resource source was found."
        };
    }

    private async Task<string?> FindExactSourceAsync(
        DirectoryInfo modDirectory,
        FileInfo patchFile,
        string suffix,
        CancellationToken cancellationToken)
    {
        var patchHash = await ModBackupService.ComputeSha256Async(patchFile.FullName, cancellationToken);
        foreach (var candidatePath in EnumerateCandidates(modDirectory, patchFile))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!string.Equals(patchHash,
                        await ModBackupService.ComputeSha256Async(candidatePath, cancellationToken),
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                var sourceCompanion = candidatePath + suffix;
                if (!File.Exists(sourceCompanion))
                    continue;
                var validation = await _analyzePatch(new FileInfo(candidatePath));
                var valid = suffix.Equals(".gpu_resources", StringComparison.OrdinalIgnoreCase)
                    ? validation.HasGpuResources && validation.GpuResourceBoundsValid
                    : validation.HasStream && validation.StreamBoundsValid;
                if (valid)
                    return sourceCompanion;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Rejected companion source candidate {Candidate}", candidatePath);
            }
        }
        return null;
    }

    private IEnumerable<string> EnumerateCandidates(DirectoryInfo modDirectory, FileInfo patchFile)
    {
        var roots = new List<(string Path, SearchOption SearchOption)>();
        if (_settingsService.Initialized && !string.IsNullOrWhiteSpace(_settingsService.StorageDirectory))
            roots.Add((_settingsService.StorageDirectory, SearchOption.AllDirectories));
        if (modDirectory.Parent is not null)
            roots.Add((modDirectory.Parent.FullName, SearchOption.AllDirectories));
        var gameData = _getGameDataDirectory();
        if (gameData is not null)
            roots.Add((gameData.FullName, SearchOption.TopDirectoryOnly));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.DistinctBy(item => Path.GetFullPath(item.Path), StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root.Path))
                continue;
            IEnumerable<string> candidates;
            try { candidates = Directory.EnumerateFiles(root.Path, patchFile.Name, root.SearchOption); }
            catch { continue; }
            foreach (var candidate in candidates)
            {
                var fullPath = Path.GetFullPath(candidate);
                if (string.Equals(fullPath, patchFile.FullName, StringComparison.OrdinalIgnoreCase) || !seen.Add(fullPath))
                    continue;
                yield return fullPath;
            }
        }
    }

    private static bool IsMainPatchFile(string fileName) =>
        fileName.EndsWith(".patch", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".patch_0", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("data", StringComparison.OrdinalIgnoreCase);
}
