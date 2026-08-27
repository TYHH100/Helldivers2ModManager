namespace Helldivers2ModManager.Core.Nexus;

public sealed record NexusMod(
    string Id,
    string GameScopedId,
    string Name,
    string? Summary,
    string? Author,
    bool? AdultContent,
    int? Endorsements,
    int? Downloads);

public sealed record NexusFile(
    string Id,
    string? Name,
    string? Version,
    NexusFileCategory? Category,
    long? SizeBytes,
    bool? IsPrimary,
    NexusUpdateGroupPosition? UpdateGroupVersion);

public sealed record NexusUpdateGroup(string Id, string Name, bool? IsActive);

public sealed record NexusUpdateGroupVersion(
    string Id,
    string Position,
    NexusFile? File,
    NexusUpdateGroupPosition? UpdateGroupVersion);

public sealed record NexusTrendingMod(
    string Name,
    string Author,
    string? Summary,
    string? PictureUrl,
    string ModPageUrl);

public sealed record NexusUpdateInfo(
    bool HasUpdate,
    string CurrentVersion,
    string? LatestVersion,
    NexusFile? LatestFile);

public enum NexusFileCategory
{
    Unknown,
    Main,
    Update,
    Optional,
    Old,
    Miscellaneous,
    Deleted,
    Archived
}

public sealed record NexusUpdateGroupPosition(string Position);
