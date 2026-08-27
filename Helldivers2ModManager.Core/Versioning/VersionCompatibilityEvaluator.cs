using Helldivers2ModManager.Core.GameData;

namespace Helldivers2ModManager.Core.Versioning;

public static class VersionCompatibilityEvaluator
{
    public static uint? MostCommonVersion(IEnumerable<uint> versions)
    {
        var groups = versions.GroupBy(static version => version)
            .Select(static group => (Version: group.Key, Count: group.Count()))
            .ToArray();
        if (groups.Length == 0)
        {
            return null;
        }

        return groups.MaxBy(static item => (item.Count, item.Version)).Version;
    }

    public static ModVersionStatus Evaluate(
        bool hasBlockingIssues,
        IReadOnlyList<uint> comparableVersions,
        uint? referenceVersion,
        IReadOnlyList<uint> fallbackVersions)
    {
        if (hasBlockingIssues)
        {
            return ModVersionStatus.Incompatible;
        }
        if (comparableVersions.Count == 0 || !referenceVersion.HasValue)
        {
            return ModVersionStatus.Unknown;
        }
        return comparableVersions.All(version => version == referenceVersion.Value)
            ? ModVersionStatus.Compatible
            : ModVersionStatus.Incompatible;
    }

    public static uint ReportVersion(
        ModVersionStatus status,
        bool hasBlockingIssues,
        uint? referenceVersion,
        IReadOnlyList<uint> comparableVersions,
        IReadOnlyList<uint> fallbackVersions)
    {
        if (!hasBlockingIssues && comparableVersions.Count == 0 && !referenceVersion.HasValue)
        {
            return 0;
        }
        if (status == ModVersionStatus.Compatible && !referenceVersion.HasValue)
        {
            return MostCommonVersion(fallbackVersions) ?? 0;
        }
        return referenceVersion ?? MostCommonVersion(fallbackVersions) ?? 0;
    }

    public static VersionReferenceResult ResolveBatchReference(
        GameUnitReferenceLookup? lookup,
        IEnumerable<PatchUnitAnalysis> healthyUnits)
    {
        var references = lookup?.References;
        if (lookup is null || !string.IsNullOrEmpty(lookup.ErrorMessage) || references is null || references.Count == 0)
        {
            var versions = healthyUnits.Select(static unit => unit.Version);
            return new(MostCommonVersion(versions), false);
        }

        return new(MostCommonVersion(references.Values.Select(static reference => reference.Version)), true);
    }
}



