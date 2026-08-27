namespace Helldivers2ModManager.Core.Persistence;

public sealed record ProfileGroupRecord(
    Guid Id,
    string Name,
    int DisplayIndex,
    DateTimeOffset CreatedAtUtc);

public sealed record ProfileModState(
    Guid ModGuid,
    bool Enabled,
    Guid? GroupId,
    int SortOrder,
    string StateJson);

public sealed record ProfileSnapshot(
    Guid Id,
    string Name,
    bool IsDefault,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<ProfileGroupRecord> Groups,
    IReadOnlyList<ProfileModState> Mods);

public sealed record FileHashRecord(
    Guid ModGuid,
    string FilePath,
    string FileHash,
    long FileSize,
    DateTimeOffset LastModifiedUtc);

public sealed record VersionResultRecord(
    Guid ModGuid,
    int Status,
    string ResultJson,
    DateTimeOffset CheckedAtUtc,
    DateTimeOffset? ModLastWriteTimeUtc);

public sealed record JsonCacheRecord(
    string Category,
    string Key,
    string ResultJson,
    DateTimeOffset UpdatedAtUtc);
