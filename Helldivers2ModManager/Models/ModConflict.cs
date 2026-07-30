namespace Helldivers2ModManager.Models;

/// <summary>
/// 一个模组在当前选项配置下提供的 Unit 资源。
/// </summary>
internal sealed class ModConflictParticipant
{
    public required Guid ModGuid { get; init; }
    public required string ModName { get; init; }
    public required string PatchFileName { get; init; }
    public required long UnitId { get; init; }
    public required uint Version { get; init; }
    public required int DataSize { get; init; }
    public required uint GpuSize { get; init; }
    public required int DeploymentOrder { get; init; }
}

/// <summary>
/// 多个已启用模组同时提供同一个 Unit 时的覆盖记录。
/// </summary>
internal sealed class ModConflictRecord
{
    public required long UnitId { get; init; }
    public string FriendlyName { get; init; } = string.Empty;
    public string OriginalName { get; init; } = string.Empty;
    public required IReadOnlyList<ModConflictParticipant> Participants { get; init; }

    /// <summary>
    /// 版本、主数据大小或 GPU 数据大小不一致时，可以确认不是同一份资源；否则仍标记为潜在覆盖。
    /// </summary>
    public bool IsDefiniteConflict => Participants
        .Select(static p => (p.Version, p.DataSize, p.GpuSize))
        .Distinct()
        .Count() > 1;

    /// <summary>
    /// 按部署顺序最后写入的模组是当前预期生效者。
    /// </summary>
    public ModConflictParticipant Winner => Participants
        .OrderBy(static p => p.DeploymentOrder)
        .ThenBy(static p => p.ModName, StringComparer.OrdinalIgnoreCase)
        .Last();
}

internal sealed class ModConflictAnalysisResult
{
    public int ScannedModCount { get; init; }
    public int ScannedPatchCount { get; init; }
    public int ScannedUnitCount { get; init; }
    public IReadOnlyList<ModConflictRecord> Conflicts { get; init; } = [];

    public int DefiniteConflictCount => Conflicts.Count(static c => c.IsDefiniteConflict);
    public bool HasConflicts => Conflicts.Count > 0;
}
