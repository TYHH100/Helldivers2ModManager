using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Helldivers2ModManager.Core.PatchKit;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Core.Repair;

public sealed class BackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly string[] BackupSuffixes = [".hd2mm-backup", ".hd2mm-backup.json"];
    private static readonly Regex CurrentBackupNamePattern = new(
        @"^(?<base>.+)\.patch-backup_(?<index>[^.]+)\.(?<stamp>\d{8}-\d{6})(?:-(?<sequence>\d+))?\.hd2mm-backup$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex LegacyBackupNamePattern = new(
        @"^(?<base>.+)\.patch_(?<index>[^.]+)\.(?<stamp>\d{8}-\d{6})(?:-(?<sequence>\d+))?\.hd2mm-backup$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly PatchStructureAnalyzer _analyzer;
    private readonly MetadataRepairService _metadataRepairService;
    private readonly ILogger<BackupService> _logger;

    public BackupService()
        : this(new PatchStructureAnalyzer(), new MetadataRepairService(new PatchStructureAnalyzer()))
    {
    }

    public BackupService(
        PatchStructureAnalyzer analyzer,
        MetadataRepairService metadataRepairService,
        ILogger<BackupService>? logger = null)
    {
        _analyzer = analyzer;
        _metadataRepairService = metadataRepairService;
        _logger = logger ?? NullLogger<BackupService>.Instance;
    }

    public static async Task<bool> TryWriteMetadataAsync(
        DirectoryInfo modDirectory,
        string backupPath,
        string repairedPath,
        string originalPath,
        ModBackupRepairKind repairKind,
        int actionCount,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = new BackupMetadata(
                1,
                DateTime.UtcNow,
                Path.GetFileName(originalPath),
                Path.GetRelativePath(modDirectory.FullName, Path.GetFullPath(originalPath)),
                repairKind,
                actionCount,
                await ComputeSha256Async(backupPath, cancellationToken),
                await ComputeSha256Async(repairedPath, cancellationToken));
            var metadataPath = backupPath + ".json";
            var temporaryPath = metadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(metadata, JsonOptions), cancellationToken);
            File.Move(temporaryPath, metadataPath, true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<ModBackupHistory> GetHistoryAsync(DirectoryInfo modDirectory, CancellationToken cancellationToken = default)
    {
        var backups = new List<BackupMetadata>();
        foreach (var backup in modDirectory.EnumerateFiles("*.hd2mm-backup", SearchOption.AllDirectories))
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<BackupMetadata>(await File.ReadAllTextAsync(backup.FullName + ".json", cancellationToken), JsonOptions);
                if (metadata is not null) backups.Add(metadata with { BackupPath = backup.FullName });
            }
            catch (Exception) when (backup.Exists is false || !File.Exists(backup.FullName + ".json"))
            {
            }
            catch (Exception)
            {
            }
        }
        return new(modDirectory.FullName, backups.OrderByDescending(item => item.CreatedUtc).ToArray());
    }

    public async Task<DetailedBackupHistory> GetDetailedHistoryAsync(
        DirectoryInfo modDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!modDirectory.Exists)
            return new([]);

        var entries = new List<ValidatedBackupEntry>();
        foreach (var backupFile in modDirectory
                     .EnumerateFiles("*.hd2mm-backup", SearchOption.AllDirectories)
                     .OrderByDescending(file => file.LastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(await ReadValidatedEntryAsync(modDirectory, backupFile, cancellationToken).ConfigureAwait(false));
        }

        return new(entries
            .OrderByDescending(entry => entry.CreatedLocal)
            .ThenBy(entry => entry.OriginalPath, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    public async Task<DetailedBackupOperationResult> RestoreSelectedAsync(
        DirectoryInfo modDirectory,
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        var history = await GetDetailedHistoryAsync(modDirectory, cancellationToken).ConfigureAwait(false);
        var fullPath = Path.GetFullPath(backupPath);
        var entry = history.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.BackupPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return DetailedBackupOperationResult.Failed("The selected backup is not part of this mod.");
        if (!entry.CanRestore)
            return DetailedBackupOperationResult.Failed(entry.ValidationMessage);

        return await RestoreEntryAsync(modDirectory, entry, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DetailedBackupOperationResult> RollbackToAsync(
        DirectoryInfo modDirectory,
        DateTime targetLocal,
        CancellationToken cancellationToken = default)
    {
        var history = await GetDetailedHistoryAsync(modDirectory, cancellationToken).ConfigureAwait(false);
        var restoredCount = 0;
        var skippedCount = 0;
        var failedItems = new List<string>();

        foreach (var group in history.Entries.GroupBy(entry => entry.OriginalPath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetEntry = group
                .Where(entry => entry.CreatedLocal <= targetLocal)
                .OrderByDescending(entry => entry.CreatedLocal)
                .FirstOrDefault();
            if (targetEntry is null)
            {
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

            var result = await RestoreEntryAsync(modDirectory, targetEntry, cancellationToken).ConfigureAwait(false);
            if (result.Success) restoredCount++;
            else failedItems.Add($"{targetEntry.OriginalFileName} ({result.ErrorMessage})");
        }

        return new(true,
            RestoredCount: restoredCount,
            SkippedCount: skippedCount,
            FailedItems: failedItems);
    }

    public async Task<DetailedBackupOperationResult> DeleteValidatedAsync(
        DirectoryInfo modDirectory,
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        var history = await GetDetailedHistoryAsync(modDirectory, cancellationToken).ConfigureAwait(false);
        var fullPath = Path.GetFullPath(backupPath);
        var entry = history.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.BackupPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return DetailedBackupOperationResult.Failed("The selected backup is not part of this mod.");

        var sameFileBackups = history.Entries
            .Where(candidate => string.Equals(candidate.OriginalPath, entry.OriginalPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sameFileBackups.Count <= 1)
            return DetailedBackupOperationResult.Failed("The last backup for a patch cannot be deleted.");
        var remainingRestorable = sameFileBackups.Count(candidate =>
            !string.Equals(candidate.BackupPath, entry.BackupPath, StringComparison.OrdinalIgnoreCase) &&
            candidate.CanRestore);
        if (entry.CanRestore && remainingRestorable == 0)
            return DetailedBackupOperationResult.Failed("The last restorable backup for a patch cannot be deleted.");

        DeleteBackupFiles(entry);
        return new(true, DeletedCount: 1);
    }

    public async Task<DetailedBackupOperationResult> CleanValidatedOldAsync(
        DirectoryInfo modDirectory,
        int keepPerPatch,
        CancellationToken cancellationToken = default)
    {
        keepPerPatch = Math.Max(1, keepPerPatch);
        var history = await GetDetailedHistoryAsync(modDirectory, cancellationToken).ConfigureAwait(false);
        var deleted = 0;
        foreach (var group in history.Entries.GroupBy(entry => entry.OriginalPath, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderByDescending(entry => entry.CreatedLocal).ToList();
            var keep = ordered.Take(keepPerPatch).ToHashSet();
            if (!keep.Any(entry => entry.CanRestore))
            {
                var newestRestorable = ordered.FirstOrDefault(entry => entry.CanRestore);
                if (newestRestorable is not null) keep.Add(newestRestorable);
            }

            foreach (var candidate in ordered.Where(entry => !keep.Contains(entry)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeleteBackupFiles(candidate);
                deleted++;
            }
        }

        return new(true, DeletedCount: deleted);
    }

    private async Task<ValidatedBackupEntry> ReadValidatedEntryAsync(
        DirectoryInfo modDirectory,
        FileInfo backupFile,
        CancellationToken cancellationToken)
    {
        return await ReadValidatedEntryCoreAsync(modDirectory, backupFile, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ValidatedBackupEntry> ReadValidatedEntryCoreAsync(
        DirectoryInfo modDirectory,
        FileInfo backupFile,
        CancellationToken cancellationToken)
    {
        var originalPath = string.Empty;
        try
        {
            var metadata = await ReadMetadataAsync(backupFile.FullName, cancellationToken).ConfigureAwait(false);
            var match = CurrentBackupNamePattern.Match(backupFile.Name);
            if (!match.Success) match = LegacyBackupNamePattern.Match(backupFile.Name);
            if (!TryGetOriginalBackupFileName(backupFile, match, metadata, out var originalName))
                return InvalidEntry(backupFile, "The backup file name is not recognized.");

            originalPath = ResolveOriginalPath(modDirectory, backupFile, originalName, metadata);
            if (!IsPathInside(modDirectory.FullName, originalPath))
                return InvalidEntry(backupFile, "The backup maps outside the mod directory.", originalPath);

            var backupHash = await ComputeSha256Async(backupFile.FullName, cancellationToken).ConfigureAwait(false);
            var metadataMatches = metadata is null ||
                (string.Equals(metadata.OriginalFileName, originalName, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(metadata.BackupSha256, backupHash, StringComparison.OrdinalIgnoreCase));
            var currentExists = File.Exists(originalPath);
            var currentHash = currentExists
                ? await ComputeSha256Async(originalPath, cancellationToken).ConfigureAwait(false)
                : string.Empty;

            Core.Versioning.PatchFileAnalysis analysis;
            try
            {
                analysis = await _analyzer.AnalyzeTemporaryFileAsync(
                    backupFile,
                    new FileInfo(originalPath),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return InvalidEntry(backupFile, exception.Message, originalPath);
            }

            var structurallyRestorable = await IsRestorableAsync(
                backupFile,
                new FileInfo(originalPath),
                analysis,
                cancellationToken).ConfigureAwait(false);
            var validationMessage = metadataMatches
                ? structurallyRestorable
                    ? analysis.HealthStatus == Core.Versioning.PatchHealthStatus.Healthy ? string.Empty : "The backup has warnings but is structurally usable."
                    : "The backup does not contain a structurally readable patch."
                : "Backup metadata does not match the backup file.";
            var createdLocal = metadata is not null && metadata.CreatedUtc != default
                ? metadata.CreatedUtc.ToLocalTime()
                : match.Success ? ParseTimestamp(match, backupFile.LastWriteTime) : backupFile.LastWriteTime;

            return new(
                backupFile.FullName,
                originalPath,
                createdLocal,
                backupFile.Length,
                backupHash,
                currentHash,
                metadata?.RepairKind ?? ModBackupRepairKind.Unknown,
                metadata?.ActionCount ?? 0,
                metadata is not null,
                metadataMatches,
                currentExists,
                currentExists && string.Equals(backupHash, currentHash, StringComparison.OrdinalIgnoreCase),
                metadataMatches && structurallyRestorable,
                analysis.HealthStatus,
                validationMessage);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to inspect backup {Backup}", backupFile.FullName);
            return InvalidEntry(backupFile, exception.Message, originalPath);
        }
    }

    private async Task<bool> IsRestorableAsync(
        FileSystemInfo backupFile,
        FileSystemInfo companionSource,
        Core.Versioning.PatchFileAnalysis analysis,
        CancellationToken cancellationToken)
    {
        if (analysis.HeaderValid &&
            analysis.FileEntriesInBounds &&
            analysis.MainDataBoundsValid &&
            (!analysis.RequiresGpuResources || analysis.HasGpuResources) &&
            (!analysis.RequiresStream || analysis.HasStream) &&
            analysis.GpuResourceBoundsValid &&
            analysis.StreamBoundsValid)
            return true;

        var directory = Path.GetDirectoryName(backupFile.FullName);
        if (directory is null || !Directory.Exists(directory)) return false;
        var plan = await _metadataRepairService.CreatePlanAsync(new DirectoryInfo(directory), cancellationToken).ConfigureAwait(false);
        return plan.Actions.Any(action =>
            string.Equals(action.PatchFilePath, backupFile.FullName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<DetailedBackupOperationResult> RestoreEntryAsync(
        DirectoryInfo modDirectory,
        ValidatedBackupEntry entry,
        CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        string? rollbackPath = null;
        var originalExisted = false;
        var committed = false;
        try
        {
            var originalFile = new FileInfo(entry.OriginalPath);
            Directory.CreateDirectory(originalFile.DirectoryName!);
            temporaryPath = Path.Combine(
                originalFile.DirectoryName!,
                "." + originalFile.Name + ".hd2mm-restore-" + Guid.NewGuid().ToString("N") + ".tmp");
            await CopyDurablyAsync(entry.BackupPath, temporaryPath, cancellationToken).ConfigureAwait(false);

            var stagedAnalysis = await _analyzer.AnalyzeTemporaryFileAsync(
                new FileInfo(temporaryPath),
                originalFile,
                cancellationToken).ConfigureAwait(false);
            if (!await IsRestorableAsync(
                    new FileInfo(temporaryPath),
                    originalFile,
                    stagedAnalysis,
                    cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The selected backup failed structural validation before restore.");

            originalExisted = originalFile.Exists;
            if (originalExisted)
            {
                rollbackPath = CreateBackupPath(originalFile, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                File.Replace(temporaryPath, originalFile.FullName, rollbackPath, true);
            }
            else
            {
                File.Move(temporaryPath, originalFile.FullName);
            }
            committed = true;

            var restoredHash = await ComputeSha256Async(originalFile.FullName, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(restoredHash, entry.BackupSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The restored file hash does not match the selected backup.");
            if (rollbackPath is not null)
            {
                await TryWriteMetadataAsync(
                    modDirectory,
                    rollbackPath,
                    originalFile.FullName,
                    originalFile.FullName,
                    ModBackupRepairKind.PreRestore,
                    0,
                    cancellationToken).ConfigureAwait(false);
            }

            return new(true, RestoredPath: originalFile.FullName, RollbackBackupPath: rollbackPath);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to restore backup {Backup}", entry.BackupPath);
            if (committed)
            {
                try
                {
                    if (originalExisted && rollbackPath is not null && File.Exists(rollbackPath))
                        File.Copy(rollbackPath, entry.OriginalPath, true);
                    else if (!originalExisted && File.Exists(entry.OriginalPath))
                        File.Delete(entry.OriginalPath);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogCritical(rollbackException, "Failed to roll back restore for {Patch}", entry.OriginalPath);
                }
            }

            return DetailedBackupOperationResult.Failed(exception.Message);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch { }
            }
        }
    }

    private static async Task<BackupMetadata?> ReadMetadataAsync(string backupPath, CancellationToken cancellationToken)
    {
        var metadataPath = backupPath + ".json";
        if (!File.Exists(metadataPath)) return null;
        var json = await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<BackupMetadata>(json, JsonOptions);
    }

    private static bool TryGetOriginalBackupFileName(
        FileSystemInfo backupFile,
        Match match,
        BackupMetadata? metadata,
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
               PatchFileRules.IsMainPatchFile(fileName);
    }

    private static string ResolveOriginalPath(
        DirectoryInfo modDirectory,
        FileInfo backupFile,
        string originalName,
        BackupMetadata? metadata)
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

    private static ValidatedBackupEntry InvalidEntry(
        FileSystemInfo backupFile,
        string message,
        string originalPath = "")
    {
        return new(
            backupFile.FullName,
            originalPath,
            backupFile.LastWriteTime,
            backupFile is FileInfo validFile && validFile.Exists ? validFile.Length : 0,
            string.Empty,
            string.Empty,
            ModBackupRepairKind.Unknown,
            0,
            false,
            false,
            false,
            false,
            false,
            Core.Versioning.PatchHealthStatus.Corrupted,
            message);
    }

    private static DateTime ParseTimestamp(Match match, DateTime fallback)
    {
        return DateTime.TryParseExact(
            match.Groups["stamp"].Value,
            "yyyyMMdd-HHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var parsed) ? parsed : fallback;
    }

    private static async Task CopyDurablyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(true);
    }

    private static void DeleteBackupFiles(ValidatedBackupEntry entry)
    {
        if (File.Exists(entry.BackupPath)) File.Delete(entry.BackupPath);
        if (File.Exists(entry.MetadataPath)) File.Delete(entry.MetadataPath);
    }

    public async Task<ModBackupOperationResult> RestoreLatestAsync(DirectoryInfo modDirectory, string originalPath, CancellationToken cancellationToken = default)
    {
        var history = await GetHistoryAsync(modDirectory, cancellationToken);
        var fullPath = Path.GetFullPath(originalPath);
        if (!IsPathInside(modDirectory.FullName, fullPath)) return ModBackupOperationResult.Failed("OriginalPathOutsideMod");
        var relativePath = Path.GetRelativePath(modDirectory.FullName, fullPath);
        var backup = history.Backups.FirstOrDefault(item => string.Equals(item.OriginalRelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
        if (backup is null || !File.Exists(backup.BackupPath)) return ModBackupOperationResult.Failed("BackupNotFound");
        if (!string.Equals(await ComputeSha256Async(backup.BackupPath, cancellationToken), backup.BackupSha256, StringComparison.OrdinalIgnoreCase))
            return ModBackupOperationResult.Failed("BackupHashMismatch");
        var original = new FileInfo(Path.Combine(modDirectory.FullName, backup.OriginalRelativePath));
        Directory.CreateDirectory(original.DirectoryName!);
        var temporary = Path.Combine(original.DirectoryName!, "." + original.Name + ".hd2mm-restore-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.Copy(backup.BackupPath, temporary, true);
        string? rollbackPath = null;
        try
        {
            if (original.Exists)
            {
                rollbackPath = CreateBackupPath(original, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                File.Replace(temporary, original.FullName, rollbackPath, true);
            }
            else File.Move(temporary, original.FullName);
            if (!string.Equals(await ComputeSha256Async(original.FullName, cancellationToken), backup.BackupSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Restored file hash mismatch.");
            if (rollbackPath is not null)
                await TryWriteMetadataAsync(modDirectory, rollbackPath, original.FullName, original.FullName, ModBackupRepairKind.PreRestore, 0, cancellationToken);
            return new(true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task<ModBackupOperationResult> DeleteAsync(DirectoryInfo modDirectory, string backupPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupPath)) return Task.FromResult(ModBackupOperationResult.Failed("BackupNotFound"));
        var fullBackupPath = Path.GetFullPath(backupPath);
        if (Directory.GetParent(fullBackupPath) is not { } parent || !IsPathInside(modDirectory.FullName, parent.FullName))
            return Task.FromResult(ModBackupOperationResult.Failed("BackupOutsideMod"));
        File.Delete(backupPath);
        if (File.Exists(backupPath + ".json")) File.Delete(backupPath + ".json");
        return Task.FromResult(new ModBackupOperationResult(true));
    }

    public async Task<ModBackupOperationResult> CleanOldAsync(DirectoryInfo modDirectory, int keep, CancellationToken cancellationToken = default)
    {
        if (keep < 1) return ModBackupOperationResult.Failed("KeepCountMustBePositive");
        var history = await GetHistoryAsync(modDirectory, cancellationToken);
        foreach (var group in history.Backups.GroupBy(item => item.OriginalRelativePath, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var backup in group.OrderByDescending(item => item.CreatedUtc).Skip(keep))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DeleteAsync(modDirectory, backup.BackupPath, cancellationToken);
            }
        }
        return new(true);
    }

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var hash = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(await hash.ComputeHashAsync(stream, cancellationToken));
    }

    private static bool IsPathInside(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fullRoot, fullPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateBackupPath(FileInfo original, string stamp)
    {
        var name = original.Name.Replace(".patch_", ".patch-backup_", StringComparison.OrdinalIgnoreCase);
        var candidate = Path.Combine(original.DirectoryName!, $"{name}.{stamp}.hd2mm-backup");
        for (var suffix = 1; File.Exists(candidate); suffix++)
            candidate = Path.Combine(original.DirectoryName!, $"{name}.{stamp}-{suffix}.hd2mm-backup");
        return candidate;
    }
}

