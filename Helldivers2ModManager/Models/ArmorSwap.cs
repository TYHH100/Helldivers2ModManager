namespace Helldivers2ModManager.Models;

/// <summary>来源模组 A 的完整分析结果（合并视图 + 护甲组 + 选项目录视图）。</summary>
internal sealed class ArmorSwapSourceAnalysis
{
    public required ModData Mod { get; init; }
    public required IReadOnlyList<ArmorSwapSourceGroup> Groups { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>来源全部 patch 的材质条目合并视图（FileId → 位置，部署顺序后覆盖先）。</summary>
    internal required IReadOnlyDictionary<ulong, (string PatchPath, ulong MainOffset, uint MainSize)> MaterialEntries { get; init; }

    /// <summary>来源全部 patch 的纹理条目合并视图（FileId → 位置）。</summary>
    internal required IReadOnlyDictionary<ulong, (string PatchPath, ulong MainOffset, uint MainSize, ulong GpuOffset, uint GpuSize, ulong StreamOffset, uint StreamSize)> TextureEntries { get; init; }

    /// <summary>按目录（选项/子选项目录或根目录）的视图，产物模组保留同样目录结构。</summary>
    internal required IReadOnlyList<ArmorSwapDirectoryView> DirectoryViews { get; init; }
}

/// <summary>来源模组中一个含 patch 的目录（选项 Include 目录、子选项目录或根目录）。</summary>
internal sealed class ArmorSwapDirectoryView
{
    /// <summary>相对模组根目录的路径（"" = 根目录），产物沿用相同结构。</summary>
    public required string RelativeDirectory { get; init; }

    public required IReadOnlyList<ArmorSwapUnitStructure> Units { get; init; }

    internal required IReadOnlyDictionary<ulong, (string PatchPath, ulong MainOffset, uint MainSize)> MaterialEntries { get; init; }

    internal required IReadOnlyDictionary<ulong, (string PatchPath, ulong MainOffset, uint MainSize, ulong GpuOffset, uint GpuSize, ulong StreamOffset, uint StreamSize)> TextureEntries { get; init; }

    /// <summary>该目录第一个 patch 的前 72 字节（产物 header 模板）。</summary>
    internal required byte[] TemplateHeader { get; init; }
}

/// <summary>来源模组 A 中的一个护甲外观组（模组可能同时替换多个护甲）。</summary>
internal sealed class ArmorSwapSourceGroup
{
    public required string ArmorId { get; init; }
    public required string ArmorName { get; init; }

    /// <summary>该组全部 Unit（合并视图，已按部署顺序覆盖合并）。</summary>
    public required IReadOnlyList<ArmorSwapUnitStructure> Units { get; init; }

    /// <summary>有槽位元数据的 Unit（按 (Slot, BodyShape) 可配对）。</summary>
    public IReadOnlyList<ArmorSwapUnitStructure> SlotUnits =>
        Units.Where(static unit => unit.Slot != ModelPreviewCustomizationSlot.Unknown).ToArray();

    /// <summary>无槽位元数据的 Unit（旧结构护甲部件，通常为头盔）。</summary>
    public IReadOnlyList<ArmorSwapUnitStructure> UnclassifiedUnits =>
        Units.Where(static unit => !unit.HasCustomizationInfo).ToArray();

    public string DisplayName => string.IsNullOrWhiteSpace(ArmorName) ? $"0x{ArmorId}" : ArmorName;
}

/// <summary>目标护甲骨架中的一个游戏包（body 包/变体包/头盔包）及其 Unit FileId 列表。</summary>
internal sealed record ArmorSwapTargetPackage(string PackageId, IReadOnlyList<long> UnitIds);

/// <summary>游戏目标护甲 B 的骨架视图。</summary>
internal sealed class ArmorSwapTargetArmor
{
    public required string ArmorId { get; init; }
    public required string ArmorName { get; init; }

    /// <summary>去重后的全部骨架 Unit（共享 Unit 只保留首次出现）。</summary>
    public required IReadOnlyList<ArmorSwapUnitStructure> Units { get; init; }

    /// <summary>
    /// 逐包 Unit 归属（按加载顺序，选中包最前）。变体包之间共享大量 Unit，
    /// 配对按包逐组分配时必须看到每包的完整槽位组（含共享 Unit），
    /// 才能保证每个变体都拿到完整的一套层。
    /// </summary>
    public required IReadOnlyList<ArmorSwapTargetPackage> Packages { get; init; }

    /// <summary>来自头盔包（SDK Helmet 表关联）的 Unit——旧结构护甲的头盔无槽位元数据，但它是头盔。</summary>
    public IReadOnlyList<ArmorSwapUnitStructure> HelmetUnits =>
        Units.Where(static unit => unit.IsFromHelmetPackage).ToArray();

    public IReadOnlyList<ArmorSwapUnitStructure> SlotUnits =>
        Units.Where(static unit => unit.Slot != ModelPreviewCustomizationSlot.Unknown).ToArray();

    public IReadOnlyList<ArmorSwapUnitStructure> UnclassifiedUnits =>
        Units.Where(static unit => !unit.HasCustomizationInfo).ToArray();

    public string DisplayName => string.IsNullOrWhiteSpace(ArmorName) ? $"0x{ArmorId}" : ArmorName;
}

/// <summary>一次换甲生成的结果。</summary>
internal sealed class ArmorSwapResult
{
    public required string ModName { get; init; }
    public required string ModDirectory { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>换甲兼容性/移植问题（错误阻断，警告跳过该项）。</summary>
internal sealed class ArmorSwapIssue
{
    public required bool IsError { get; init; }
    public required string Message { get; init; }
}
