using Helldivers2ModManager.Core.Operations;

namespace Helldivers2ModManager.Core.Mods;

public enum ModImportConflict
{
    None,
    SameGuidUpdate,
    NameConflict
}

public sealed record ModImportPlan(
    Guid OperationId,
    Guid ModId,
    string ModName,
    string SourceDirectory,
    string ModsRoot,
    string DestinationDirectory,
    string StagingDirectory,
    string BackupDirectory,
    string JournalPath,
    ModImportConflict Conflict,
    Guid? ExistingModId);

public sealed record ModImportCommitResult(
    string DestinationDirectory,
    string? RollbackDirectory,
    bool UpdatedExistingMod);

public interface IModImportService
{
    Task<OperationResult<ModImportPlan>> PlanImportAsync(
        Guid modId,
        string modName,
        string sourceDirectory,
        string modsRoot,
        CancellationToken cancellationToken);

    Task<OperationResult<ModImportCommitResult>> CommitImportAsync(
        ModImportPlan plan,
        bool updateConfirmed,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    Task CompleteImportAsync(ModImportPlan plan, bool commit, CancellationToken cancellationToken);

    Task RecoverInterruptedImportsAsync(string modsRoot, CancellationToken cancellationToken);
}

public interface IModDeploymentService
{
    Task<OperationResult> DeployAsync(IReadOnlyList<Guid> modIds, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
}

public interface IModRemovalService
{
    Task<OperationResult> RemoveAsync(Guid modId, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
}

public interface IModExportService
{
    Task<OperationResult> ExportAsync(Guid modId, string destinationPath, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
}
