namespace Helldivers2ModManager.Core.GameData;

public sealed record GameUnitReference(
    long FileId,
    uint Version,
    byte[] LodGroupData,
    uint[] MeshIds,
    ulong GpuSize,
    string PackageName)
{
    public string Signature => $"{Version:X8}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(LodGroupData))}";
}

public sealed record GameUnitReferenceLookup(
    IReadOnlyDictionary<long, GameUnitReference> References,
    IReadOnlyDictionary<long, IReadOnlyList<string>> PackageNames,
    IReadOnlySet<long> MissingUnitIds,
    IReadOnlySet<long> AmbiguousUnitIds,
    string? ErrorMessage)
{
    public static GameUnitReferenceLookup Empty { get; } = new(
        new Dictionary<long, GameUnitReference>(),
        new Dictionary<long, IReadOnlyList<string>>(),
        new HashSet<long>(),
        new HashSet<long>(),
        null);
}

public sealed record GameArchiveIndexStatistics(int BundleCount, int PackageCount, int UnitIdCount);