namespace Helldivers2ModManager.Core.Compatibility;

public sealed class VersionCheckCoordinator(
    IPatchScanner patchScanner,
    IGameReferenceProvider gameReferenceProvider,
    ICompatibilityEvaluator compatibilityEvaluator) : IVersionCheckCoordinator
{
    public async Task<CompatibilityResult> CheckAsync(
        string patchPath,
        string gameDataDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patchPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var scan = await patchScanner.ScanAsync(patchPath, cancellationToken).ConfigureAwait(false);
        var unitIds = scan.Units
            .Select(static unit => unit.FileId)
            .Distinct()
            .ToArray();
        var references = await gameReferenceProvider
            .GetReferencesAsync(gameDataDirectory, unitIds, cancellationToken)
            .ConfigureAwait(false);

        return compatibilityEvaluator.Evaluate(scan, references);
    }
}
