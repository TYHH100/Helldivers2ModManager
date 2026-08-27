using Helldivers2ModManager.Core.Mods;

namespace Helldivers2ModManager.Core.Analysis;

public sealed record ConflictParticipant(
    Guid ModId,
    string ModName,
    string PatchFileName,
    long UnitId,
    uint Version,
    uint DataSize,
    uint GpuSize,
    int DeploymentOrder);

public sealed record ConflictRecord(
    long UnitId,
    string FriendlyName,
    IReadOnlyList<ConflictParticipant> Participants,
    string OriginalName = "")
{
    public bool IsDefiniteConflict => Participants.Select(static participant => (participant.Version, participant.DataSize, participant.GpuSize)).Distinct().Count() > 1;
    public ConflictParticipant Winner => Participants.OrderBy(static participant => participant.DeploymentOrder).ThenBy(static participant => participant.ModName, StringComparer.OrdinalIgnoreCase).Last();
}

public sealed record ConflictAnalysisResult(
    int ScannedModCount,
    int ScannedPatchCount,
    int ScannedUnitCount,
    IReadOnlyList<ConflictRecord> Conflicts)
{
    public int DefiniteConflictCount => Conflicts.Count(record => record.IsDefiniteConflict);
    public bool HasConflicts => Conflicts.Count > 0;
}

public sealed record ArmorReuseTarget(string ArmorId, string ArmorName);

public sealed record ArmorReuseRecord(
    Guid ModId,
    string ModName,
    string SourceArmorId,
    string SourceArmorName,
    IReadOnlyList<ArmorReuseTarget> ReusedBy,
    int SharedUnitCount);

public sealed record ArmorReuseAnalysisResult(
    int ScannedModCount,
    int ScannedPatchCount,
    int ScannedUnitCount,
    IReadOnlyList<ArmorReuseRecord> Records)
{
    public int AffectedModCount => Records.Select(record => record.ModId).Distinct().Count();
}

public sealed record AnalysisMod(
    Guid Id,
    string Name,
    bool Enabled,
    int DeploymentOrder,
    DirectoryInfo Directory,
    IModManifest? Manifest = null,
    string Version = "",
    IReadOnlyList<bool>? EnabledOptions = null,
    IReadOnlyList<int>? SelectedOptions = null);
