namespace Helldivers2ModManager.Core.Compatibility;

public sealed class CompatibilityEvaluator : ICompatibilityEvaluator
{
    public CompatibilityResult Evaluate(PatchScanResult scan, GameReferenceSnapshot reference)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(reference);

        if (scan.StructuralIssues.Count > 0)
        {
            return new CompatibilityResult(
                CompatibilityState.Incompatible,
                reference.Source,
                reference.GameDataFingerprint,
                scan.StructuralIssues,
                [],
                1,
                scan.Units,
                ToReferenceVersions(reference));
        }

        if (reference.Source == ReferenceSource.Unavailable || string.IsNullOrWhiteSpace(reference.GameDataFingerprint))
        {
            return new CompatibilityResult(
                CompatibilityState.Unknown,
                reference.Source,
                reference.GameDataFingerprint,
                [],
                [],
                0,
                scan.Units,
                ToReferenceVersions(reference));
        }

        var versionIssues = new List<string>();
        var matched = 0;
        foreach (var observation in scan.Units)
        {
            if (!reference.Units.TryGetValue(observation.FileId, out var expected))
                continue;
            matched++;
            if (observation.Version != expected.Version)
                versionIssues.Add($"Unit {observation.FileId} expected 0x{expected.Version:X8} but found 0x{observation.Version:X8}.");
        }

        if (scan.Units.Count == 0 || matched != scan.Units.Count)
        {
            return new CompatibilityResult(
                CompatibilityState.Unknown,
                reference.Source,
                reference.GameDataFingerprint,
                [],
                versionIssues,
                scan.Units.Count == 0 ? 0 : matched / (double)scan.Units.Count,
                scan.Units,
                ToReferenceVersions(reference));
        }

        return new CompatibilityResult(
            versionIssues.Count == 0 ? CompatibilityState.Compatible : CompatibilityState.Incompatible,
            reference.Source,
            reference.GameDataFingerprint,
            [],
            versionIssues,
            1,
            scan.Units,
            ToReferenceVersions(reference));
    }

    private static Dictionary<long, uint> ToReferenceVersions(GameReferenceSnapshot reference) =>
        reference.Units.ToDictionary(static pair => pair.Key, static pair => pair.Value.Version);
}
