using System.Security.Cryptography;

namespace Helldivers2ModManager.Services;

/// <summary>Resolved current-game Unit data used by compatibility and repair workflows.</summary>
internal sealed class GameUnitReferenceData
{
    public required long FileId { get; init; }
    public required uint Version { get; init; }
    public required byte[] LodGroupData { get; init; }
    public required uint[] MeshIds { get; init; }
    public uint GpuSize { get; init; }
    public required string PackageName { get; init; }
    public string Signature => $"{Version:X8}:{Convert.ToHexString(SHA256.HashData(LodGroupData))}";
}

internal sealed class GameUnitReferenceLookup
{
    public Dictionary<long, GameUnitReferenceData> References { get; } = [];
    public HashSet<long> MissingUnitIds { get; } = [];
    public HashSet<long> AmbiguousUnitIds { get; } = [];
    public string? ErrorMessage { get; init; }
}
