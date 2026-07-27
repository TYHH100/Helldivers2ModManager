namespace Helldivers2ModManager.Models;

/// <summary>
/// 一个模组替换的护甲与游戏内复用相同 Unit 部件的其他护甲之间的关系。
/// </summary>
internal sealed class ArmorReuseRecord
{
    public required Guid ModGuid { get; init; }
    public required string ModName { get; init; }
    public required string SourceArmorId { get; init; }
    public required string SourceArmorName { get; init; }
    public required IReadOnlyList<ArmorReuseTarget> ReusedBy { get; init; }
    public required int SharedUnitCount { get; init; }
}

internal sealed class ArmorReuseTarget
{
    public required string ArmorId { get; init; }
    public required string ArmorName { get; init; }
}

internal sealed class ArmorReuseAnalysisResult
{
    public int ScannedModCount { get; init; }
    public int ScannedPatchCount { get; init; }
    public int ScannedUnitCount { get; init; }
    public IReadOnlyList<ArmorReuseRecord> Records { get; init; } = [];

    public int AffectedModCount => Records.Select(static record => record.ModGuid).Distinct().Count();
}
