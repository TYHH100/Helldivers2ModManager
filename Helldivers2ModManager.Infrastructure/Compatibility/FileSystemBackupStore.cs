using System.Security.Cryptography;
using Helldivers2ModManager.Core.Compatibility;

namespace Helldivers2ModManager.Infrastructure.Compatibility;

public sealed class FileSystemBackupStore : IBackupStore
{
    public async Task<string> CreateVerifiedBackupAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(sourcePath);
        var backupPath = CreateBackupPath(source);
        await CopyAsync(source, backupPath, overwrite: false, cancellationToken).ConfigureAwait(false);
        var sourceHash = await HashAsync(source, cancellationToken).ConfigureAwait(false);
        var backupHash = await HashAsync(backupPath, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(sourceHash, backupHash))
        {
            File.Delete(backupPath);
            throw new IOException("Backup verification failed.");
        }
        return backupPath;
    }

    private static string CreateBackupPath(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        var fileName = Path.GetFileName(sourcePath);
        var patchMarker = fileName.LastIndexOf(".patch_", StringComparison.OrdinalIgnoreCase);
        if (patchMarker < 1 || patchMarker + ".patch_".Length >= fileName.Length)
        {
            var backupDirectory = Path.Combine(directory, ".hd2mm-backups");
            Directory.CreateDirectory(backupDirectory);
            return Path.Combine(
                backupDirectory,
                $"{fileName}.{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.{Guid.NewGuid():N}.bak");
        }

        var baseName = fileName[..patchMarker];
        var patchIndex = fileName[(patchMarker + ".patch_".Length)..];
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        for (var sequence = 0; sequence < 10_000; sequence++)
        {
            var suffix = sequence == 0 ? string.Empty : $"-{sequence}";
            var candidate = Path.Combine(
                directory,
                $"{baseName}.patch-backup_{patchIndex}.{timestamp}{suffix}.hd2mm-backup");
            if (!File.Exists(candidate))
                return candidate;
        }

        throw new IOException("Unable to allocate a unique patch backup path.");
    }

    public async Task RestoreAsync(string backupPath, string destinationPath, CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(destinationPath);
        var temporaryPath = destination + $".restore-{Guid.NewGuid():N}.tmp";
        try
        {
            await CopyAsync(backupPath, temporaryPath, overwrite: false, cancellationToken).ConfigureAwait(false);
            if (File.Exists(destination))
                File.Replace(temporaryPath, destination, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, destination);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal static async Task<byte[]> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task CopyAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0) < 0)
                destination.Seek(read, SeekOrigin.Current);
            else
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        destination.SetLength(source.Length);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }
}
