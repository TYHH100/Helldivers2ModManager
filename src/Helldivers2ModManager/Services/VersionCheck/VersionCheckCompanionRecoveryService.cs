using Helldivers2ModManager.Models;
using Microsoft.Extensions.Logging;
using System.IO;

using static Helldivers2ModManager.Services.VersionCheckShared;
using static Helldivers2ModManager.Services.VersionCheckFileOps;
using Helldivers2ModManager.Services.Infrastructure;
namespace Helldivers2ModManager.Services;

internal sealed class VersionCheckCompanionRecoveryService
{
    private readonly VersionCheckService _analysis;
    private readonly GameUnitReferenceReader _unitReader;
    private readonly GameCompanionRecoveryReader _recoveryReader;
    private readonly SettingsService _settingsService;
    private readonly ILogger _logger;

    public VersionCheckCompanionRecoveryService(VersionCheckService analysis, GameUnitReferenceReader unitReader, GameCompanionRecoveryReader recoveryReader, SettingsService settingsService, ILogger logger)
    {
        _analysis = analysis;
        _unitReader = unitReader;
        _recoveryReader = recoveryReader;
        _settingsService = settingsService;
        _logger = logger;
    }
    private sealed record PreparedCompanionRecovery(
        CompanionRecoveryItem Item,
        string TemporaryPath);

    public async Task<CompanionRecoveryPlan> CreateCompanionRecoveryPlanAsync(
        DirectoryInfo modDirectory,
        CancellationToken cancellationToken = default)
    {
        var items = new List<CompanionRecoveryItem>();
        if (!modDirectory.Exists)
            return new CompanionRecoveryPlan { Items = items };

        var patchFiles = modDirectory.GetFiles("*", SearchOption.AllDirectories)
            .Where(file => VersionCheckShared.IsMainPatchFile(file.Name))
            .ToArray();
        foreach (var patchFile in patchFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var analysis = await _analysis.AnalyzeSinglePatchFileStructureAsync(patchFile);
            if (analysis.RequiresGpuResources && !analysis.HasGpuResources)
            {
                items.Add(await CreateCompanionRecoveryItemAsync(
                    modDirectory,
                    patchFile,
                    ".gpu_resources",
                    cancellationToken));
            }
            if (analysis.RequiresStream && !analysis.HasStream)
            {
                items.Add(await CreateCompanionRecoveryItemAsync(
                    modDirectory,
                    patchFile,
                    ".stream",
                    cancellationToken));
            }
        }

        return new CompanionRecoveryPlan { Items = items };
    }

    public async Task<CompanionRecoveryResult> RecoverCompanionFilesAsync(
        DirectoryInfo modDirectory,
        CancellationToken cancellationToken = default)
    {
        await VersionCheckShared.RepairGate.WaitAsync(cancellationToken);
        try
        {
            return await RecoverCompanionFilesCoreAsync(modDirectory, cancellationToken);
        }
        finally
        {
            VersionCheckShared.RepairGate.Release();
        }
    }

    /// <summary>
    /// companion 恢复核心（不加全局锁；由入口持锁，或批量修复持锁后并发调用，仅操作该模组目录）。
    /// </summary>
    internal async Task<CompanionRecoveryResult> RecoverCompanionFilesCoreAsync(
        DirectoryInfo modDirectory,
        CancellationToken cancellationToken)
    {
        var prepared = new List<PreparedCompanionRecovery>();
        var committed = new List<string>();
        try
        {
            var plan = await CreateCompanionRecoveryPlanAsync(modDirectory, cancellationToken);
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
                    await VersionCheckFileOps.CopyFileDurablyAsync(item.SourcePath, temporaryPath, cancellationToken);
                }
                else if (item.SourceKind == CompanionRecoverySourceKind.CurrentGameBundles)
                {
                    var recipe = await _recoveryReader.TryBuildGameCompanionRecipeAsync(
                        new FileInfo(item.PatchPath),
                        item.Suffix,
                        includePayloads: true,
                        cancellationToken);
                    if (recipe is null)
                        throw new InvalidDataException("Current game resources no longer provide an exact companion reconstruction.");
                    await GameCompanionRecoveryReader.WriteGameCompanionRecipeAsync(recipe, temporaryPath, cancellationToken);
                }
                else
                {
                    throw new InvalidDataException("The recovery source is not supported.");
                }

                prepared.Add(new PreparedCompanionRecovery(item, temporaryPath));
            }

            foreach (var item in prepared)
            {
                File.Move(item.TemporaryPath, item.Item.CompanionPath);
                committed.Add(item.Item.CompanionPath);
            }

            foreach (var patchPath in prepared
                         .Select(item => item.Item.PatchPath)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var validation = await _analysis.AnalyzeSinglePatchFileStructureAsync(new FileInfo(patchPath));
                if ((validation.RequiresGpuResources && !validation.HasGpuResources) ||
                    (validation.RequiresStream && !validation.HasStream) ||
                    !validation.GpuResourceBoundsValid ||
                    !validation.StreamBoundsValid)
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
                VersionCheckFileOps.TryDeleteFile(path);
            return new CompanionRecoveryResult { ErrorMessage = ex.Message };
        }
        finally
        {
            foreach (var item in prepared)
                VersionCheckFileOps.TryDeleteFile(item.TemporaryPath);
        }
    }

    private async Task<CompanionRecoveryItem> CreateCompanionRecoveryItemAsync(
        DirectoryInfo modDirectory,
        FileInfo patchFile,
        string suffix,
        CancellationToken cancellationToken)
    {
        var companionPath = patchFile.FullName + suffix;
        var exactSource = await FindExactCompanionSourceAsync(
            modDirectory,
            patchFile,
            suffix,
            cancellationToken);
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

        var gameRecipe = await _recoveryReader.TryBuildGameCompanionRecipeAsync(
            patchFile,
            suffix,
            includePayloads: false,
            cancellationToken);
        if (gameRecipe is not null)
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
                SourcePath = gameRecipe.Description,
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

    private async Task<string?> FindExactCompanionSourceAsync(
        DirectoryInfo modDirectory,
        FileInfo patchFile,
        string suffix,
        CancellationToken cancellationToken)
    {
        var patchHash = await VersionCheckFileOps.ComputeSha256Async(patchFile.FullName, cancellationToken);
        foreach (var candidatePath in EnumerateExactPatchCandidates(modDirectory, patchFile))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!string.Equals(
                        patchHash,
                        await VersionCheckFileOps.ComputeSha256Async(candidatePath, cancellationToken),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var sourceCompanion = candidatePath + suffix;
                if (!File.Exists(sourceCompanion))
                    continue;
                var validation = await _analysis.AnalyzeSinglePatchFileStructureAsync(new FileInfo(candidatePath));
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

    private IEnumerable<string> EnumerateExactPatchCandidates(
        DirectoryInfo modDirectory,
        FileInfo patchFile)
    {
        var roots = new List<(string Path, SearchOption SearchOption)>();
        if (_settingsService.Initialized && !string.IsNullOrWhiteSpace(_settingsService.StorageDirectory))
            roots.Add((_settingsService.StorageDirectory, SearchOption.AllDirectories));
        if (modDirectory.Parent is not null)
            roots.Add((modDirectory.Parent.FullName, SearchOption.AllDirectories));
        var gameData = _unitReader.GetConfiguredGameDataDirectory();
        if (gameData is not null)
            roots.Add((gameData.FullName, SearchOption.TopDirectoryOnly));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.DistinctBy(item => Path.GetFullPath(item.Path), StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root.Path))
                continue;
            IEnumerable<string> candidates;
            try
            {
                candidates = Directory.EnumerateFiles(root.Path, patchFile.Name, root.SearchOption);
            }
            catch
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                var fullPath = Path.GetFullPath(candidate);
                if (string.Equals(fullPath, patchFile.FullName, StringComparison.OrdinalIgnoreCase) ||
                    !seen.Add(fullPath))
                {
                    continue;
                }
                yield return fullPath;
            }
        }
    }
}
