using Helldivers2ModManager.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Helldivers2ModManager.Services;

internal sealed partial class VersionCheckService
{
    private static readonly Regex s_backupNamePattern = new(
        @"^(?<base>.+)\.patch-backup_(?<index>[^.]+)\.(?<stamp>\d{8}-\d{6})(?:-(?<sequence>\d+))?\.hd2mm-backup$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex s_legacyBackupNamePattern = new(
        @"^(?<base>.+)\.patch_(?<index>[^.]+)\.(?<stamp>\d{8}-\d{6})(?:-(?<sequence>\d+))?\.hd2mm-backup$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions s_backupJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<ModBackupHistory> GetBackupHistoryAsync(
        DirectoryInfo modDirectory,
        CancellationToken cancellationToken = default)
    {
        if (_coreBackupService is not null)
        {
            var detailed = await _coreBackupService.GetDetailedHistoryAsync(modDirectory, cancellationToken).ConfigureAwait(false);
            return new ModBackupHistory
            {
                Entries = detailed.Entries.Select(ToLegacyEntry).ToList()
            };
        }

        if (!modDirectory.Exists)
            return new ModBackupHistory();

        var entries = new List<ModBackupEntry>();
        foreach (var backupFile in modDirectory
                     .GetFiles("*.hd2mm-backup", SearchOption.AllDirectories)
                     .OrderByDescending(file => file.LastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(await ReadBackupEntryAsync(modDirectory, backupFile, cancellationToken));
        }

        return new ModBackupHistory
        {
            Entries = entries
                .OrderByDescending(entry => entry.CreatedLocal)
                .ThenBy(entry => entry.OriginalPath, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    public async Task<ModBackupOperationResult> RestoreBackupAsync(
        DirectoryInfo modDirectory,
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        if (_coreBackupService is not null)
        {
            await _repairSemaphore.WaitAsync(cancellationToken);
            try
            {
                return await RestoreDetailedAsync(_coreBackupService.RestoreSelectedAsync(modDirectory, backupPath, cancellationToken)).ConfigureAwait(false);
            }
            finally
            {
                _repairSemaphore.Release();
            }
        }

        await _repairSemaphore.WaitAsync(cancellationToken);
        try
        {
            var history = await GetBackupHistoryAsync(modDirectory, cancellationToken);
            var entry = history.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.BackupPath, Path.GetFullPath(backupPath), StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return new ModBackupOperationResult { ErrorMessage = "The selected backup is not part of this mod." };
            if (!entry.CanRestore)
                return new ModBackupOperationResult { ErrorMessage = entry.ValidationMessage };

            return await RestoreBackupCoreAsync(modDirectory, entry, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore backup {Backup} in {ModDirectory}", backupPath, modDirectory.FullName);
            return new ModBackupOperationResult { ErrorMessage = ex.Message };
        }
        finally
        {
            _repairSemaphore.Release();
        }
    }

    /// <summary>
    /// 将整个模组（含所有选项/子选项目录下的补丁文件）回滚到指定时间点：
    /// 对每个原始文件选择创建时间不晚于目标时间的最新备份进行恢复。
    /// 当前状态已与目标备份一致的文件跳过；没有不晚于目标时间的备份时保持现状。
    /// </summary>
    /// <param name="modDirectory">模组目录</param>
    /// <param name="targetLocal">目标时间点（本地时间，精度到分钟）</param>
    /// <returns>汇总结果（恢复数、跳过数、失败明细）</returns>
    public async Task<ModBackupOperationResult> RollbackModToAsync(
        DirectoryInfo modDirectory,
        DateTime targetLocal,
        CancellationToken cancellationToken = default)
    {
        if (_coreBackupService is not null)
        {
            await _repairSemaphore.WaitAsync(cancellationToken);
            try
            {
                return await RestoreDetailedAsync(_coreBackupService.RollbackToAsync(modDirectory, targetLocal, cancellationToken)).ConfigureAwait(false);
            }
            finally
            {
                _repairSemaphore.Release();
            }
        }

        await _repairSemaphore.WaitAsync(cancellationToken);
        try
        {
            var history = await GetBackupHistoryAsync(modDirectory, cancellationToken);
            var restoredCount = 0;
            var skippedCount = 0;
            var failedItems = new List<string>();

            foreach (var group in history.Entries
                         .GroupBy(entry => entry.OriginalPath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var targetEntry = group
                    .Where(entry => entry.CreatedLocal <= targetLocal)
                    .OrderByDescending(entry => entry.CreatedLocal)
                    .FirstOrDefault();
                if (targetEntry is null)
                {
                    // 该文件在目标时间点没有备份，当前状态即目标状态
                    skippedCount++;
                    continue;
                }

                if (!targetEntry.CanRestore)
                {
                    failedItems.Add($"{targetEntry.OriginalFileName} ({targetEntry.ValidationMessage})");
                    continue;
                }

                if (targetEntry.CurrentMatchesBackup)
                {
                    skippedCount++;
                    continue;
                }

                var single = await RestoreBackupCoreAsync(modDirectory, targetEntry, cancellationToken);
                if (single.Success)
                    restoredCount++;
                else
                    failedItems.Add($"{targetEntry.OriginalFileName} ({single.ErrorMessage})");
            }

            return new ModBackupOperationResult
            {
                Success = failedItems.Count == 0,
                RestoredCount = restoredCount,
                SkippedCount = skippedCount,
                FailedItems = failedItems
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to roll back mod {ModDirectory} to {Time}", modDirectory.FullName, targetLocal);
            return new ModBackupOperationResult { ErrorMessage = ex.Message };
        }
        finally
        {
            _repairSemaphore.Release();
        }
    }

    /// <summary>
    /// 恢复单个备份的核心逻辑（调用方需持有 <see cref="_repairSemaphore"/>）。
    /// 先校验备份结构，再原子替换当前文件，最后校验哈希并保留回滚快照。
    /// </summary>
    private async Task<ModBackupOperationResult> RestoreBackupCoreAsync(
        DirectoryInfo modDirectory,
        ModBackupEntry entry,
        CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        string? rollbackPath = null;
        string? originalPath = null;
        var originalExisted = false;
        var committed = false;
        try
        {
            originalPath = entry.OriginalPath;
            var originalFile = new FileInfo(originalPath);
            Directory.CreateDirectory(originalFile.DirectoryName!);
            temporaryPath = Path.Combine(
                originalFile.DirectoryName!,
                "." + originalFile.Name + ".hd2mm-restore-" + Guid.NewGuid().ToString("N") + ".tmp");
            await CopyFileDurablyAsync(entry.BackupPath, temporaryPath, cancellationToken);

            var stagedAnalysis = await AnalyzeSinglePatchFileStructureAsync(
                new FileInfo(temporaryPath),
                originalFile);
            if (!await IsBackupRestorableAsync(new FileInfo(temporaryPath), originalFile, stagedAnalysis))
                throw new InvalidDataException("The selected backup failed structural validation before restore.");

            originalExisted = File.Exists(originalPath);
            if (originalExisted)
            {
                rollbackPath = CreateBackupPath(originalFile, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                File.Replace(temporaryPath, originalPath, rollbackPath, true);
            }
            else
            {
                File.Move(temporaryPath, originalPath);
            }
            committed = true;

            var restoredHash = await ComputeSha256Async(originalPath, cancellationToken);
            if (!string.Equals(restoredHash, entry.BackupSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The restored file hash does not match the selected backup.");

            if (rollbackPath is not null)
            {
                await TryWriteBackupMetadataAsync(
                    modDirectory,
                    rollbackPath,
                    originalPath,
                    ModBackupRepairKind.PreRestore,
                    0,
                    entry.BackupFileName,
                    cancellationToken);
            }

            return new ModBackupOperationResult
            {
                Success = true,
                RestoredPath = originalPath,
                RollbackBackupPath = rollbackPath
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore backup {Backup} in {ModDirectory}", entry.BackupPath, modDirectory.FullName);
            if (committed && originalPath is not null)
            {
                try
                {
                    if (originalExisted && rollbackPath is not null && File.Exists(rollbackPath))
                        File.Copy(rollbackPath, originalPath, true);
                    else if (!originalExisted && File.Exists(originalPath))
                        File.Delete(originalPath);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogCritical(rollbackException, "Failed to roll back backup restore for {Patch}", originalPath);
                }
            }
            return new ModBackupOperationResult { ErrorMessage = ex.Message };
        }
        finally
        {
            if (temporaryPath is not null)
                TryDeleteFile(temporaryPath);
        }
    }

    public async Task<ModBackupOperationResult> DeleteBackupAsync(
        DirectoryInfo modDirectory,
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        if (_coreBackupService is not null)
        {
            await _repairSemaphore.WaitAsync(cancellationToken);
            try
            {
                return await RestoreDetailedAsync(_coreBackupService.DeleteValidatedAsync(modDirectory, backupPath, cancellationToken)).ConfigureAwait(false);
            }
            finally
            {
                _repairSemaphore.Release();
            }
        }

        await _repairSemaphore.WaitAsync(cancellationToken);
        try
        {
            var history = await GetBackupHistoryAsync(modDirectory, cancellationToken);
            var fullPath = Path.GetFullPath(backupPath);
            var entry = history.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.BackupPath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return new ModBackupOperationResult { ErrorMessage = "The selected backup is not part of this mod." };

            var sameFileBackups = history.Entries
                .Where(candidate => string.Equals(candidate.OriginalPath, entry.OriginalPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sameFileBackups.Count <= 1)
                return new ModBackupOperationResult { ErrorMessage = "The last backup for a patch cannot be deleted." };

            var remainingRestorable = sameFileBackups.Count(candidate =>
                !string.Equals(candidate.BackupPath, entry.BackupPath, StringComparison.OrdinalIgnoreCase) &&
                candidate.CanRestore);
            if (entry.CanRestore && remainingRestorable == 0)
                return new ModBackupOperationResult { ErrorMessage = "The last restorable backup for a patch cannot be deleted." };

            DeleteBackupFiles(entry);
            return new ModBackupOperationResult { Success = true, DeletedCount = 1 };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete backup {Backup}", backupPath);
            return new ModBackupOperationResult { ErrorMessage = ex.Message };
        }
        finally
        {
            _repairSemaphore.Release();
        }
    }

    public async Task<ModBackupOperationResult> CleanOldBackupsAsync(
        DirectoryInfo modDirectory,
        int keepPerPatch = 3,
        CancellationToken cancellationToken = default)
    {
        if (_coreBackupService is not null)
        {
            await _repairSemaphore.WaitAsync(cancellationToken);
            try
            {
                return await RestoreDetailedAsync(_coreBackupService.CleanValidatedOldAsync(modDirectory, keepPerPatch, cancellationToken)).ConfigureAwait(false);
            }
            finally
            {
                _repairSemaphore.Release();
            }
        }

        keepPerPatch = Math.Max(1, keepPerPatch);
        await _repairSemaphore.WaitAsync(cancellationToken);
        try
        {
            var history = await GetBackupHistoryAsync(modDirectory, cancellationToken);
            var deleted = 0;
            foreach (var group in history.Entries.GroupBy(
                         entry => entry.OriginalPath,
                         StringComparer.OrdinalIgnoreCase))
            {
                var ordered = group.OrderByDescending(entry => entry.CreatedLocal).ToList();
                var keep = ordered.Take(keepPerPatch).ToHashSet();
                if (!keep.Any(entry => entry.CanRestore))
                {
                    var newestRestorable = ordered.FirstOrDefault(entry => entry.CanRestore);
                    if (newestRestorable is not null)
                        keep.Add(newestRestorable);
                }

                foreach (var candidate in ordered.Where(entry => !keep.Contains(entry)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DeleteBackupFiles(candidate);
                    deleted++;
                }
            }

            return new ModBackupOperationResult { Success = true, DeletedCount = deleted };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clean old backups in {ModDirectory}", modDirectory.FullName);
            return new ModBackupOperationResult { ErrorMessage = ex.Message };
        }
        finally
        {
            _repairSemaphore.Release();
        }
    }

    private static ModBackupHistory ToLegacyHistory(Core.Repair.DetailedBackupHistory history)
    {
        return new ModBackupHistory
        {
            Entries = history.Entries.Select(ToLegacyEntry).ToList()
        };
    }

    private static ModBackupEntry ToLegacyEntry(Core.Repair.ValidatedBackupEntry entry)
    {
        return new ModBackupEntry
        {
            BackupPath = entry.BackupPath,
            OriginalPath = entry.OriginalPath,
            CreatedLocal = entry.CreatedLocal,
            BackupSize = entry.BackupSize,
            BackupSha256 = entry.BackupSha256,
            CurrentSha256 = entry.CurrentSha256,
            RepairKind = ToLegacyKind(entry.RepairKind),
            ActionCount = entry.ActionCount,
            HasMetadata = entry.HasMetadata,
            MetadataMatchesFile = entry.MetadataMatchesFile,
            CurrentExists = entry.CurrentExists,
            CurrentMatchesBackup = entry.CurrentMatchesBackup,
            CanRestore = entry.CanRestore,
            HealthStatus = (PatchHealthStatus)entry.HealthStatus,
            ValidationMessage = entry.ValidationMessage
        };
    }

    private static ModBackupRepairKind ToLegacyKind(Core.Repair.ModBackupRepairKind kind)
    {
        return kind switch
        {
            Core.Repair.ModBackupRepairKind.SafeMetadata => ModBackupRepairKind.SafeMetadata,
            Core.Repair.ModBackupRepairKind.PreRestore => ModBackupRepairKind.PreRestore,
            Core.Repair.ModBackupRepairKind.PreserveModLod => ModBackupRepairKind.PreserveModLod,
            Core.Repair.ModBackupRepairKind.UseGameLod => ModBackupRepairKind.UseGameLod,
            Core.Repair.ModBackupRepairKind.MixedLod => ModBackupRepairKind.MixedLod,
            Core.Repair.ModBackupRepairKind.AutomaticLod => ModBackupRepairKind.AutomaticLod,
            _ => ModBackupRepairKind.Unknown
        };
    }

    private static async Task<ModBackupOperationResult> RestoreDetailedAsync(Task<Core.Repair.DetailedBackupOperationResult> coreTask)
    {
        var result = await coreTask.ConfigureAwait(false);
        return new ModBackupOperationResult
        {
            Success = result.Success,
            ErrorMessage = result.ErrorMessage,
            RestoredPath = result.RestoredPath,
            RollbackBackupPath = result.RollbackBackupPath,
            DeletedCount = result.DeletedCount,
            RestoredCount = result.RestoredCount,
            SkippedCount = result.SkippedCount,
            FailedItems = result.FailedItems?.ToList() ?? []
        };
    }

    private async Task<ModBackupEntry> ReadBackupEntryAsync(
        DirectoryInfo modDirectory,
        FileInfo backupFile,
        CancellationToken cancellationToken)
    {
        var originalPath = string.Empty;
        try
        {
            var metadata = await ReadBackupMetadataAsync(backupFile.FullName, cancellationToken);
            var match = s_backupNamePattern.Match(backupFile.Name);
            if (!match.Success)
                match = s_legacyBackupNamePattern.Match(backupFile.Name);

            if (!TryGetOriginalBackupFileName(backupFile, match, metadata, out var originalName))
                return InvalidBackupEntry(backupFile, "The backup file name is not recognized.");
            originalPath = ResolveOriginalPath(modDirectory, backupFile, originalName, metadata);
            if (!IsPathInside(modDirectory.FullName, originalPath))
                return InvalidBackupEntry(backupFile, "The backup maps outside the mod directory.", originalPath);

            var backupHash = await ComputeSha256Async(backupFile.FullName, cancellationToken);
            var metadataMatches = metadata is null ||
                (string.Equals(metadata.OriginalFileName, originalName, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(metadata.BackupSha256, backupHash, StringComparison.OrdinalIgnoreCase));
            var currentExists = File.Exists(originalPath);
            var currentHash = currentExists
                ? await ComputeSha256Async(originalPath, cancellationToken)
                : string.Empty;
            var analysis = await AnalyzeSinglePatchFileStructureAsync(
                backupFile,
                new FileInfo(originalPath));
            var structurallyRestorable = await IsBackupRestorableAsync(
                backupFile, new FileInfo(originalPath), analysis);
            var validationMessage = metadataMatches
                ? structurallyRestorable
                    ? analysis.Message ?? string.Empty
                    : analysis.Message ?? "The backup does not contain a structurally readable patch."
                : "Backup metadata does not match the backup file.";

            var createdLocal = metadata is not null && metadata.CreatedUtc != default
                ? metadata.CreatedUtc.ToLocalTime()
                : match.Success
                    ? ParseBackupTimestamp(match, backupFile.LastWriteTime)
                    : backupFile.LastWriteTime;
            return new ModBackupEntry
            {
                BackupPath = backupFile.FullName,
                OriginalPath = originalPath,
                CreatedLocal = createdLocal,
                BackupSize = backupFile.Length,
                BackupSha256 = backupHash,
                CurrentSha256 = currentHash,
                RepairKind = metadata?.RepairKind ?? ModBackupRepairKind.Unknown,
                ActionCount = metadata?.ActionCount ?? 0,
                HasMetadata = metadata is not null,
                MetadataMatchesFile = metadataMatches,
                CurrentExists = currentExists,
                CurrentMatchesBackup = currentExists && string.Equals(backupHash, currentHash, StringComparison.OrdinalIgnoreCase),
                CanRestore = metadataMatches && structurallyRestorable,
                HealthStatus = analysis.HealthStatus,
                ValidationMessage = validationMessage
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inspect backup {Backup}", backupFile.FullName);
            return InvalidBackupEntry(backupFile, ex.Message, originalPath);
        }
    }

    private static bool TryGetOriginalBackupFileName(
        FileInfo backupFile,
        Match match,
        ModBackupMetadata? metadata,
        out string originalName)
    {
        if (match.Success)
        {
            originalName = match.Groups["base"].Value + ".patch_" + match.Groups["index"].Value;
            return IsSafePatchFileName(originalName);
        }

        if (metadata is not null && IsSafePatchFileName(metadata.OriginalFileName))
        {
            originalName = metadata.OriginalFileName;
            return true;
        }

        originalName = string.Empty;
        return false;
    }

    private static bool IsSafePatchFileName(string fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName) &&
               Path.GetFileName(fileName) == fileName &&
               fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
               IsMainPatchFile(fileName);
    }

    private static string ResolveOriginalPath(
        DirectoryInfo modDirectory,
        FileInfo backupFile,
        string originalName,
        ModBackupMetadata? metadata)
    {
        if (metadata is not null && !string.IsNullOrWhiteSpace(metadata.OriginalRelativePath))
        {
            var candidate = Path.GetFullPath(Path.Combine(modDirectory.FullName, metadata.OriginalRelativePath));
            if (IsPathInside(modDirectory.FullName, candidate) &&
                string.Equals(Path.GetFileName(candidate), originalName, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return Path.GetFullPath(Path.Combine(backupFile.DirectoryName!, originalName));
    }

    private async Task TryWriteBackupMetadataAsync(
        DirectoryInfo modDirectory,
        string backupPath,
        string repairedPath,
        ModBackupRepairKind repairKind,
        int actionCount,
        string? sourceBackupFileName = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = new ModBackupMetadata
            {
                CreatedUtc = DateTime.UtcNow,
                OriginalFileName = Path.GetFileName(repairedPath),
                OriginalRelativePath = Path.GetRelativePath(modDirectory.FullName, repairedPath),
                RepairKind = repairKind,
                ActionCount = actionCount,
                BackupSha256 = await ComputeSha256Async(backupPath, cancellationToken),
                RepairedSha256 = await ComputeSha256Async(repairedPath, cancellationToken),
                SourceBackupFileName = sourceBackupFileName
            };
            var metadataPath = backupPath + ".json";
            var temporaryPath = metadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(metadata, s_backupJsonOptions),
                cancellationToken);
            File.Move(temporaryPath, metadataPath, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write backup metadata for {Backup}", backupPath);
        }
    }

    private static async Task<ModBackupMetadata?> ReadBackupMetadataAsync(
        string backupPath,
        CancellationToken cancellationToken)
    {
        var metadataPath = backupPath + ".json";
        if (!File.Exists(metadataPath))
            return null;
        await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<ModBackupMetadata>(stream, s_backupJsonOptions, cancellationToken);
    }

    private async Task<bool> IsBackupRestorableAsync(
        FileInfo backupFile,
        FileInfo companionSource,
        PatchFileAnalysis analysis)
    {
        if (IsBackupStructurallyRestorable(analysis))
            return true;

        var actions = new List<PatchRepairAction>();
        var blockers = new List<string>();
        await InspectPatchForRepairsAsync(backupFile, actions, blockers, companionSource);
        return actions.Count > 0 && blockers.Count == 0;
    }

    private static bool IsBackupStructurallyRestorable(PatchFileAnalysis analysis)
    {
        return analysis.HeaderValid &&
               analysis.FileEntriesInBounds &&
               analysis.MainDataBoundsValid &&
               (!analysis.RequiresGpuResources || analysis.HasGpuResources) &&
               (!analysis.RequiresStream || analysis.HasStream) &&
               analysis.GpuResourceBoundsValid &&
               analysis.StreamBoundsValid;
    }

    private static ModBackupEntry InvalidBackupEntry(
        FileInfo backupFile,
        string message,
        string originalPath = "")
    {
        return new ModBackupEntry
        {
            BackupPath = backupFile.FullName,
            OriginalPath = originalPath,
            CreatedLocal = backupFile.LastWriteTime,
            BackupSize = backupFile.Exists ? backupFile.Length : 0,
            BackupSha256 = string.Empty,
            CanRestore = false,
            HealthStatus = PatchHealthStatus.Corrupted,
            MetadataMatchesFile = false,
            ValidationMessage = message
        };
    }

    private static DateTime ParseBackupTimestamp(Match match, DateTime fallback)
    {
        return DateTime.TryParseExact(
            match.Groups["stamp"].Value,
            "yyyyMMdd-HHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var parsed)
            ? parsed
            : fallback;
    }

    private static bool IsPathInside(string rootPath, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(candidatePath);
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task CopyFileDurablyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        destination.Flush(true);
    }

    private static void DeleteBackupFiles(ModBackupEntry entry)
    {
        File.Delete(entry.BackupPath);
        if (File.Exists(entry.MetadataPath))
            File.Delete(entry.MetadataPath);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
