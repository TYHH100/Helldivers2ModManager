using System.Text.Json;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Operations;
using Helldivers2ModManager.Core.Security;

namespace Helldivers2ModManager.Infrastructure.Mods;

public sealed class TransactionalModImportService : IModImportService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private readonly ISafePathPolicy _safePathPolicy;
    private readonly IModImportFaultInjector? _faultInjector;

    public TransactionalModImportService(ISafePathPolicy safePathPolicy)
        : this(safePathPolicy, null)
    {
    }

    internal TransactionalModImportService(
        ISafePathPolicy safePathPolicy,
        IModImportFaultInjector? faultInjector)
    {
        _safePathPolicy = safePathPolicy;
        _faultInjector = faultInjector;
    }

    public Task<OperationResult<ModImportPlan>> PlanImportAsync(
        Guid modId,
        string modName,
        string sourceDirectory,
        string modsRoot,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => PlanImport(modId, modName, sourceDirectory, modsRoot, cancellationToken), cancellationToken);
    }

    public async Task<OperationResult<ModImportCommitResult>> CommitImportAsync(
        ModImportPlan plan,
        bool updateConfirmed,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Conflict == ModImportConflict.NameConflict)
            return OperationResult.Failure<ModImportCommitResult>("Import.NameConflict");
        if (plan.Conflict == ModImportConflict.SameGuidUpdate && !updateConfirmed)
            return OperationResult.Failure<ModImportCommitResult>("Import.UpdateConfirmationRequired");

        try
        {
            ValidatePlanPaths(plan);
            var transactionRoot = Path.GetDirectoryName(plan.JournalPath)!;
            Directory.CreateDirectory(transactionRoot);
            await WriteJournalAsync(plan, ImportPhase.Planned, cancellationToken).ConfigureAwait(false);
            await NotifyPhaseAsync(ImportPhase.Planned, cancellationToken).ConfigureAwait(false);
            await CopyDirectoryAsync(plan.SourceDirectory, plan.StagingDirectory, progress, cancellationToken)
                .ConfigureAwait(false);
            await WriteJournalAsync(plan, ImportPhase.Staged, cancellationToken).ConfigureAwait(false);
            await NotifyPhaseAsync(ImportPhase.Staged, cancellationToken).ConfigureAwait(false);

            if (Directory.Exists(plan.DestinationDirectory))
            {
                Directory.Move(plan.DestinationDirectory, plan.BackupDirectory);
                await WriteJournalAsync(plan, ImportPhase.OldMovedToBackup, cancellationToken).ConfigureAwait(false);
                await NotifyPhaseAsync(ImportPhase.OldMovedToBackup, cancellationToken).ConfigureAwait(false);
            }

            Directory.Move(plan.StagingDirectory, plan.DestinationDirectory);
            await WriteJournalAsync(plan, ImportPhase.NewActivated, cancellationToken).ConfigureAwait(false);
            await NotifyPhaseAsync(ImportPhase.NewActivated, cancellationToken).ConfigureAwait(false);
            return OperationResult.Success(new ModImportCommitResult(
                plan.DestinationDirectory,
                Directory.Exists(plan.BackupDirectory) ? plan.BackupDirectory : null,
                plan.Conflict == ModImportConflict.SameGuidUpdate));
        }
        catch (OperationCanceledException)
        {
            await RollbackAsync(plan, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await RollbackAsync(plan, CancellationToken.None).ConfigureAwait(false);
            return OperationResult.Failure<ModImportCommitResult>("Import.CommitFailed", ex.Message);
        }
    }

    private Task NotifyPhaseAsync(ImportPhase phase, CancellationToken cancellationToken) =>
        _faultInjector?.OnPhaseCompletedAsync(phase.ToString(), cancellationToken) ?? Task.CompletedTask;

    public async Task CompleteImportAsync(ModImportPlan plan, bool commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlanPaths(plan);
        if (!commit)
        {
            await RollbackAsync(plan, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteJournalAsync(plan, ImportPhase.Finalized, cancellationToken).ConfigureAwait(false);
        if (Directory.Exists(plan.BackupDirectory))
            Directory.Delete(plan.BackupDirectory, recursive: true);
        DeleteTransactionRoot(plan);
    }

    public async Task RecoverInterruptedImportsAsync(string modsRoot, CancellationToken cancellationToken)
    {
        var transactionDirectory = Path.Combine(Path.GetFullPath(modsRoot), ".transactions");
        if (!Directory.Exists(transactionDirectory))
            return;

        var journalPaths = Directory.EnumerateFiles(transactionDirectory, "journal.json", SearchOption.AllDirectories).ToArray();
        foreach (var journalPath in journalPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImportJournal? journal;
            try
            {
                await using var stream = File.OpenRead(journalPath);
                journal = await JsonSerializer.DeserializeAsync<ImportJournal>(stream, s_jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                continue;
            }

            if (journal?.Plan is null || !string.Equals(Path.GetFullPath(journal.Plan.ModsRoot), Path.GetFullPath(modsRoot), StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                ValidatePlanPaths(journal.Plan);
                if (journal.Phase != ImportPhase.Finalized)
                    await RollbackAsync(journal.Plan, cancellationToken).ConfigureAwait(false);
                else
                    DeleteTransactionRoot(journal.Plan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Preserve uncertain state for manual recovery instead of deleting user data.
            }
        }
    }

    private OperationResult<ModImportPlan> PlanImport(
        Guid modId,
        string modName,
        string sourceDirectory,
        string modsRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(sourceDirectory))
                return OperationResult.Failure<ModImportPlan>("Import.SourceNotFound");
            if (string.IsNullOrWhiteSpace(modName) || !string.Equals(Path.GetFileName(modName), modName, StringComparison.Ordinal))
                return OperationResult.Failure<ModImportPlan>("Import.InvalidName");

            Directory.CreateDirectory(modsRoot);
            var operationId = Guid.NewGuid();
            var destination = _safePathPolicy.ResolveUnderRoot(modsRoot, modName);
            string? sameGuidDirectory = null;
            Guid? destinationModId = null;
            foreach (var directory in Directory.EnumerateDirectories(modsRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.GetFileName(directory).StartsWith('.'))
                    continue;
                var existingId = TryReadManifestGuid(directory);
                if (string.Equals(directory, destination, StringComparison.OrdinalIgnoreCase))
                    destinationModId = existingId;
                if (existingId == modId)
                    sameGuidDirectory = directory;
            }

            var conflict = ModImportConflict.None;
            Guid? existingModId = null;
            if (sameGuidDirectory is not null)
            {
                conflict = ModImportConflict.SameGuidUpdate;
                existingModId = modId;
                destination = sameGuidDirectory;
            }
            else if (Directory.Exists(destination))
            {
                conflict = ModImportConflict.NameConflict;
                existingModId = destinationModId;
            }

            var transactionRelative = Path.Combine(".transactions", operationId.ToString("N"));
            var transactionRoot = _safePathPolicy.ResolveUnderRoot(modsRoot, transactionRelative);
            var plan = new ModImportPlan(
                operationId,
                modId,
                modName,
                Path.GetFullPath(sourceDirectory),
                Path.GetFullPath(modsRoot),
                destination,
                Path.Combine(transactionRoot, "new"),
                Path.Combine(transactionRoot, "old"),
                Path.Combine(transactionRoot, "journal.json"),
                conflict,
                existingModId);
            return OperationResult.Success(plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return OperationResult.Failure<ModImportPlan>("Import.InvalidPlan", ex.Message);
        }
    }

    private void ValidatePlanPaths(ModImportPlan plan)
    {
        if (!_safePathPolicy.IsUnderRoot(plan.ModsRoot, plan.DestinationDirectory) ||
            !_safePathPolicy.IsUnderRoot(plan.ModsRoot, plan.StagingDirectory) ||
            !_safePathPolicy.IsUnderRoot(plan.ModsRoot, plan.BackupDirectory) ||
            !_safePathPolicy.IsUnderRoot(plan.ModsRoot, plan.JournalPath))
        {
            throw new InvalidDataException("Import plan paths are outside the mod root.");
        }
    }

    private async Task CopyDirectoryAsync(
        string sourceRoot,
        string destinationRoot,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).ToArray();
        Directory.CreateDirectory(destinationRoot);
        long completed = 0;
        foreach (var sourceFile in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(sourceFile) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Symbolic links are not allowed in imported mods.");
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var destination = _safePathPolicy.ResolveUnderRoot(destinationRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            completed++;
            progress?.Report(new OperationProgress("Stage", completed, files.Length, relativePath));
        }
    }

    private async Task RollbackAsync(ModImportPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePlanPaths(plan);
        var phase = await ReadJournalPhaseAsync(plan.JournalPath, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The import journal is missing or invalid; automatic rollback was stopped to preserve user data.");
        if (phase >= ImportPhase.NewActivated && Directory.Exists(plan.DestinationDirectory))
            Directory.Delete(plan.DestinationDirectory, recursive: true);

        if (Directory.Exists(plan.BackupDirectory) && !Directory.Exists(plan.DestinationDirectory))
            Directory.Move(plan.BackupDirectory, plan.DestinationDirectory);
        if (Directory.Exists(plan.StagingDirectory))
            Directory.Delete(plan.StagingDirectory, recursive: true);
        DeleteTransactionRoot(plan);
        await Task.CompletedTask;
    }

    private static async Task<ImportPhase?> ReadJournalPhaseAsync(
        string journalPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(journalPath))
            return null;
        try
        {
            await using var stream = File.OpenRead(journalPath);
            var journal = await JsonSerializer.DeserializeAsync<ImportJournal>(
                stream,
                s_jsonOptions,
                cancellationToken).ConfigureAwait(false);
            return journal?.Phase;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Guid? TryReadManifestGuid(string directory)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath))
            return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "Guid", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(property.Value.GetString(), out var id))
                {
                    return id;
                }
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static async Task WriteJournalAsync(
        ModImportPlan plan,
        ImportPhase phase,
        CancellationToken cancellationToken)
    {
        var journal = new ImportJournal(plan, phase, DateTimeOffset.UtcNow);
        var temporaryPath = plan.JournalPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, journal, s_jsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(plan.JournalPath))
            File.Replace(temporaryPath, plan.JournalPath, destinationBackupFileName: null);
        else
            File.Move(temporaryPath, plan.JournalPath);
    }

    private static void DeleteTransactionRoot(ModImportPlan plan)
    {
        var transactionRoot = Path.GetDirectoryName(plan.JournalPath)!;
        if (Directory.Exists(transactionRoot))
            Directory.Delete(transactionRoot, recursive: true);
    }

    private enum ImportPhase
    {
        Planned,
        Staged,
        OldMovedToBackup,
        NewActivated,
        Finalized
    }

    private sealed record ImportJournal(ModImportPlan Plan, ImportPhase Phase, DateTimeOffset UpdatedAtUtc);
}

internal interface IModImportFaultInjector
{
    Task OnPhaseCompletedAsync(string phase, CancellationToken cancellationToken);
}
