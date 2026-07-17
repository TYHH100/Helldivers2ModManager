using Helldivers2ModManager.Core.Operations;

namespace Helldivers2ModManager.Core.Compatibility;

public enum ReferenceSource
{
    Unavailable,
    CurrentGameFiles,
    FingerprintMatchedCache
}

public enum CompatibilityState
{
    Unknown,
    Compatible,
    Incompatible
}

public sealed record GameUnitReference(long FileId, uint Version, string ContentFingerprint);

public sealed record GameReferenceSnapshot(
    ReferenceSource Source,
    string? GameDataFingerprint,
    IReadOnlyDictionary<long, GameUnitReference> Units,
    string? ErrorCode = null);

public sealed record PatchUnitObservation(long FileId, uint Version, string PatchPath, long DataOffset, int DataSize);

public sealed record PatchScanResult(
    IReadOnlyList<PatchUnitObservation> Units,
    IReadOnlyList<string> StructuralIssues);

public sealed record CompatibilityResult(
    CompatibilityState State,
    ReferenceSource ReferenceSource,
    string? GameDataFingerprint,
    IReadOnlyList<string> StructuralIssues,
    IReadOnlyList<string> VersionIssues,
    double Confidence,
    IReadOnlyList<PatchUnitObservation>? Observations = null,
    IReadOnlyDictionary<long, uint>? ReferenceVersions = null);

public interface IGameReferenceProvider
{
    Task<GameReferenceSnapshot> GetReferencesAsync(
        string gameDataDirectory,
        IReadOnlyCollection<long> unitIds,
        CancellationToken cancellationToken);
}

public interface IPatchScanner
{
    Task<PatchScanResult> ScanAsync(string patchPath, CancellationToken cancellationToken);
}

public interface ICompatibilityEvaluator
{
    CompatibilityResult Evaluate(PatchScanResult scan, GameReferenceSnapshot reference);
}

public sealed record BinaryRepairAction(long Offset, byte[] ExpectedBytes, byte[] ReplacementBytes);

public sealed record RepairPlan(
    Guid OperationId,
    string SourcePath,
    string ExpectedSourceSha256,
    IReadOnlyList<BinaryRepairAction> Actions,
    string? ExpectedOutputSha256 = null);

public interface IRepairPlanner
{
    Task<OperationResult<RepairPlan>> PlanAsync(string patchPath, CancellationToken cancellationToken);
}

public interface IRepairExecutor
{
    Task<OperationResult> ExecuteAsync(
        RepairPlan plan,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<OperationResult> ExecuteBatchAsync(
        IReadOnlyList<RepairPlan> plans,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IBackupStore
{
    Task<string> CreateVerifiedBackupAsync(string sourcePath, CancellationToken cancellationToken);

    Task RestoreAsync(string backupPath, string destinationPath, CancellationToken cancellationToken);
}

public interface ICompanionRecoveryService
{
    Task<OperationResult<int>> RecoverAsync(string modDirectory, CancellationToken cancellationToken);
}

public interface IVersionCheckCoordinator
{
    Task<CompatibilityResult> CheckAsync(
        string patchPath,
        string gameDataDirectory,
        CancellationToken cancellationToken);
}
