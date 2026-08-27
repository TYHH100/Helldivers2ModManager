using Helldivers2ModManager.Models;

namespace Helldivers2ModManager.Adapters;

internal static class ConflictAnalysisMapper
{
    public static ModConflictAnalysisResult Map(Core.Analysis.ConflictAnalysisResult result) => new()
    {
        ScannedModCount = result.ScannedModCount,
        ScannedPatchCount = result.ScannedPatchCount,
        ScannedUnitCount = result.ScannedUnitCount,
        Conflicts = result.Conflicts.Select(MapRecord).ToArray(),
    };

    private static ModConflictRecord MapRecord(Core.Analysis.ConflictRecord record) => new()
    {
        UnitId = record.UnitId,
        FriendlyName = record.FriendlyName,
        OriginalName = record.OriginalName,
        Participants = record.Participants.Select(MapParticipant).ToArray(),
    };

    private static ModConflictParticipant MapParticipant(Core.Analysis.ConflictParticipant participant) => new()
    {
        ModGuid = participant.ModId,
        ModName = participant.ModName,
        PatchFileName = participant.PatchFileName,
        UnitId = participant.UnitId,
        Version = participant.Version,
        DataSize = checked((int)participant.DataSize),
        GpuSize = participant.GpuSize,
        DeploymentOrder = participant.DeploymentOrder,
    };
}