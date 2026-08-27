using Helldivers2ModManager.Core.Preview;
namespace Helldivers2ModManager.Core.Repair;

public enum AssistedLodStrategy
{
    PreserveMod,
    UseGameReference,
}

public sealed record AssistedUnitRepairAction(
    string PatchFilePath,
    int EntryIndex,
    long FileId,
    uint CurrentVersion,
    uint ReferenceVersion,
    uint CurrentLodSize,
    uint ReferenceLodSize,
    uint CurrentGpuSize,
    uint ReferenceGpuSize,
    bool MeshIdsDiffer,
    string CurrentMeshSignature,
    bool StrongCustomModelSignal,
    ModelPreviewBodyShape BodyShape,
    ModelPreviewCustomizationSlot CustomizationSlot,
    AssistedLodStrategy LodStrategy,
    bool LodDataDiffers)
{
    public static AssistedUnitRepairAction Empty { get; } = new(string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, false, string.Empty, false, default, default, default, false);
}

public sealed record AssistedModRepairPlan(
    IReadOnlyList<AssistedUnitRepairAction> Actions,
    IReadOnlyList<AssistedMaterialRepairAction> MaterialActions,
    IReadOnlyList<string> BlockingReasons,
    int MatchedReferenceCount = 0,
    int MissingReferenceCount = 0,
    bool IsAutomatic = false,
    int AutomaticStrongCustomCount = 0,
    int AutomaticPreserveUnitCount = 0,
    int AutomaticGameLodUnitCount = 0)
{
    public bool CanRepair => Actions.Count > 0 && BlockingReasons.Count == 0;
}

public static class AssistedRepairRules
{
    public const double WholePatchCustomDensity = 0.5;
    public const double MeshGpuExpansionRatio = 6.0;
    public const long LargeCustomGpuBytes = 5 * 1024 * 1024;
    public const double LargeCustomGpuExpansionRatio = 8.0;
    public const uint LegacyCharacterReferenceVersion = 0xA4CD36;

    public static bool IsStrongCustomModel(bool meshIdsDiffer, uint currentGpuSize, uint referenceGpuSize)
    {
        var ratio = referenceGpuSize > 0 ? currentGpuSize / (double)referenceGpuSize : currentGpuSize > 0 ? double.PositiveInfinity : 1.0;
        return (meshIdsDiffer && ratio >= MeshGpuExpansionRatio) ||
               (currentGpuSize >= LargeCustomGpuBytes && ratio >= LargeCustomGpuExpansionRatio);
    }

    public static bool RequiresCurrentGameLodForLegacyMaterial(
        uint currentVersion,
        uint referenceVersion,
        ReadOnlySpan<byte> unitData,
        IReadOnlySet<long> legacyCharacterMaterialIds)
    {
        if (currentVersion != 1 || referenceVersion != LegacyCharacterReferenceVersion || legacyCharacterMaterialIds.Count == 0 || unitData.Length < sizeof(ulong))
        {
            return false;
        }
        for (var offset = 0; offset <= unitData.Length - sizeof(ulong); offset++)
        {
            var resourceId = unchecked((long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(unitData.Slice(offset, sizeof(ulong))));
            if (legacyCharacterMaterialIds.Contains(resourceId)) return true;
        }
        return false;
    }

    public static bool RequiresCurrentGameLodForLegacyPack(
        bool patchHasLegacyCharacterMaterial,
        uint currentVersion,
        uint referenceVersion) =>
        patchHasLegacyCharacterMaterial && currentVersion == 1 && referenceVersion == LegacyCharacterReferenceVersion;

    public static (IReadOnlySet<long> Preserve, IReadOnlySet<long> StrongCustom, IReadOnlySet<long> Automatic) ClassifyAutomaticLodActions(
        IEnumerable<AssistedUnitRepairAction> actions)
    {
        var preserve = new HashSet<long>();
        var strong = new HashSet<long>();
        var automatic = new HashSet<long>();
        foreach (var group in actions.Where(action => action.LodDataDiffers).GroupBy(action => action.PatchFilePath, StringComparer.OrdinalIgnoreCase))
        {
            var units = group.GroupBy(action => action.FileId).Select(units =>
            {
                var list = units.ToArray();
                return (
                    FileId: list[0].FileId,
                    Strong: list.Any(item => item.StrongCustomModelSignal),
                    MeshDiffers: list.Any(item => item.MeshIdsDiffer),
                    Signature: list.Select(item => item.CurrentMeshSignature).FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? string.Empty,
                    Slot: list.Select(item => item.CustomizationSlot).First());
            }).ToArray();
            foreach (var unit in units)
            {
                automatic.Add(unit.FileId);
                if (unit.Strong) strong.Add(unit.FileId);
            }
            var wholeStrong = units.Count(unit => unit.Strong) / (double)units.Length >= WholePatchCustomDensity;
            var wholeMesh = units.Count(unit => unit.MeshDiffers) / (double)units.Length >= WholePatchCustomDensity;
            var signatures = units.Where(unit => (unit.Strong || unit.MeshDiffers) && unit.Signature.Length > 0).Select(unit => unit.Signature).ToHashSet(StringComparer.Ordinal);
            var slots = units.Where(unit => unit.Strong && unit.Slot != ModelPreviewCustomizationSlot.Unknown).Select(unit => unit.Slot).ToHashSet();
            foreach (var unit in units.Where(unit => unit.Strong || unit.MeshDiffers || wholeStrong || wholeMesh || signatures.Contains(unit.Signature) || slots.Contains(unit.Slot)))
                preserve.Add(unit.FileId);
        }
        return (preserve, strong, automatic);
    }
}



