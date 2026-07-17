using System.Security.Cryptography;
using Helldivers2ModManager.Core.Compatibility;
using Helldivers2ModManager.Core.Operations;

namespace Helldivers2ModManager.Infrastructure.Compatibility;

public sealed class TransactionalBinaryRepairExecutor : IRepairExecutor
{
    private readonly IBackupStore _backupStore;

    public TransactionalBinaryRepairExecutor(IBackupStore backupStore)
    {
        _backupStore = backupStore;
    }

    public async Task<OperationResult> ExecuteAsync(
        RepairPlan plan,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken) =>
        await ExecuteBatchAsync([plan], progress, cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult> ExecuteBatchAsync(
        IReadOnlyList<RepairPlan> plans,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plans);
        if (plans.Count == 0)
            return OperationResult.Failure("Repair.NoPlans");
        var prepared = new List<PreparedRepair>(plans.Count);
        try
        {
            var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = Path.GetFullPath(plan.SourcePath);
                if (!normalizedPaths.Add(sourcePath))
                    return OperationResult.Failure("Repair.DuplicateSource");
                if (!File.Exists(sourcePath))
                    return OperationResult.Failure("Repair.SourceNotFound");

                var sourceHash = Convert.ToHexString(await FileSystemBackupStore.HashAsync(sourcePath, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(sourceHash, plan.ExpectedSourceSha256, StringComparison.OrdinalIgnoreCase))
                    return OperationResult.Failure("Repair.SourceChanged");

                ValidateActions(plan.Actions, new FileInfo(sourcePath).Length);
                var backupPath = await _backupStore.CreateVerifiedBackupAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                var temporaryPath = sourcePath + $".repair-{plan.OperationId:N}.tmp";
                var item = new PreparedRepair(sourcePath, temporaryPath, backupPath);
                prepared.Add(item);
                await CopyWithProgressAsync(sourcePath, temporaryPath, progress, cancellationToken).ConfigureAwait(false);
                await ApplyActionsAsync(temporaryPath, plan.Actions, cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(plan.ExpectedOutputSha256))
                {
                    var outputHash = Convert.ToHexString(await FileSystemBackupStore.HashAsync(temporaryPath, cancellationToken).ConfigureAwait(false));
                    if (!string.Equals(outputHash, plan.ExpectedOutputSha256, StringComparison.OrdinalIgnoreCase))
                        return OperationResult.Failure("Repair.OutputHashMismatch");
                }
            }

            foreach (var item in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Replace(item.TemporaryPath, item.SourcePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                item.Replaced = true;
            }
            return OperationResult.Success();
        }
        catch (OperationCanceledException)
        {
            await RestoreCommittedAsync(prepared).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException)
        {
            await RestoreCommittedAsync(prepared).ConfigureAwait(false);
            return OperationResult.Failure("Repair.ExecutionFailed", ex.Message);
        }
        finally
        {
            foreach (var item in prepared)
                File.Delete(item.TemporaryPath);
        }
    }

    private async Task RestoreCommittedAsync(IEnumerable<PreparedRepair> prepared)
    {
        foreach (var item in prepared.Where(static item => item.Replaced).Reverse())
            await _backupStore.RestoreAsync(item.BackupPath, item.SourcePath, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class PreparedRepair(
        string sourcePath,
        string temporaryPath,
        string backupPath)
    {
        public string SourcePath { get; } = sourcePath;
        public string TemporaryPath { get; } = temporaryPath;
        public string BackupPath { get; } = backupPath;
        public bool Replaced { get; set; }
    }

    private static void ValidateActions(IReadOnlyList<BinaryRepairAction> actions, long fileLength)
    {
        long previousEnd = 0;
        foreach (var action in actions.OrderBy(static action => action.Offset))
        {
            if (action.Offset < previousEnd || action.ExpectedBytes.Length != action.ReplacementBytes.Length ||
                action.Offset < 0 || action.Offset + action.ExpectedBytes.Length > fileLength)
            {
                throw new InvalidDataException("Repair actions overlap, resize the file, or exceed its boundaries.");
            }
            previousEnd = action.Offset + action.ExpectedBytes.Length;
        }
    }

    private static async Task ApplyActionsAsync(
        string path,
        IReadOnlyList<BinaryRepairAction> actions,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.RandomAccess | FileOptions.WriteThrough);
        foreach (var action in actions.OrderBy(static action => action.Offset))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = new byte[action.ExpectedBytes.Length];
            stream.Seek(action.Offset, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(current, cancellationToken).ConfigureAwait(false);
            if (!current.AsSpan().SequenceEqual(action.ExpectedBytes))
                throw new InvalidDataException("Repair target bytes changed after planning.");
            stream.Seek(action.Offset, SeekOrigin.Begin);
            await stream.WriteAsync(action.ReplacementBytes, cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task CopyWithProgressAsync(
        string sourcePath,
        string destinationPath,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0) < 0)
                destination.Seek(read, SeekOrigin.Current);
            else
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            progress?.Report(new OperationProgress("Copy", copied, source.Length, Path.GetFileName(sourcePath)));
        }
        destination.SetLength(source.Length);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }
}
