using System.Text.Json.Serialization;

namespace Helldivers2ModManager.Core.Mods;

public enum ManifestVersion
{
    Legacy = -1,
    V1 = 1,
    V2 = 2,
}

public interface IModManifest
{
    ManifestVersion Version { get; }

    Guid Guid { get; }

    string Name { get; }

    string Description { get; }

    string? IconPath { get; }
}

public sealed record LegacyModManifest : IModManifest
{
    [JsonIgnore]
    public ManifestVersion Version => ManifestVersion.Legacy;

    public required Guid Guid { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IconPath { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Options { get; init; }
}

public sealed record NexusManifestData(int ModId, string Version);

public sealed record ModSubOption(
    string Name,
    string Description,
    IReadOnlyList<string> Include,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Image);

public sealed record ModOption(
    string Name,
    string Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Include,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Image,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ModSubOption>? SubOptions);

public sealed record V1ModManifest : IModManifest
{
    public ManifestVersion Version => ManifestVersion.V1;

    public required Guid Guid { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IconPath { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ModOption>? Options { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NexusManifestData? NexusData { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LegacyModManifest))]
[JsonSerializable(typeof(V1ModManifest))]
internal sealed partial class ManifestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
