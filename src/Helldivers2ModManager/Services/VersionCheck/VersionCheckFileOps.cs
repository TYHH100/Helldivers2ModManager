using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using Helldivers2ModManager.Models;

namespace Helldivers2ModManager.Services;

/// <summary>
/// VersionCheck 子服务共享的备份文件操作（原分散在修复与备份 partial 中）。
/// </summary>
internal static class VersionCheckFileOps
{
private static readonly JsonSerializerOptions s_backupJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

public static string CreateBackupPath(FileInfo patchFile, string stamp)
    {
        var backupName = patchFile.Name.Replace(
            ".patch_",
            ".patch-backup_",
            StringComparison.OrdinalIgnoreCase);
        var candidate = Path.Combine(
            patchFile.DirectoryName!,
            $"{backupName}.{stamp}.hd2mm-backup");
        var suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(
                patchFile.DirectoryName!,
                $"{backupName}.{stamp}-{suffix++}.hd2mm-backup");
        }
        return candidate;
    }

public static async Task<string> ComputeSha256Async(
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

public static async Task CopyFileDurablyAsync(
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

public static void TryDeleteFile(string path)
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

public static async Task TryWriteBackupMetadataAsync(
        DirectoryInfo modDirectory,
        string backupPath,
        string repairedPath,
        ModBackupRepairKind repairKind,
        int actionCount,
        ILogger logger,
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
            logger.LogWarning(ex, "Failed to write backup metadata for {Backup}", backupPath);
        }
    }

public static async Task<ModBackupMetadata?> ReadBackupMetadataAsync(
        string backupPath,
        CancellationToken cancellationToken)
    {
        var metadataPath = backupPath + ".json";
        if (!File.Exists(metadataPath))
            return null;
        await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<ModBackupMetadata>(stream, s_backupJsonOptions, cancellationToken);
    }
}
