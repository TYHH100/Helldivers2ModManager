using Helldivers2ModManager.Core.Persistence;

namespace Helldivers2ModManager.Core.Profiles;

public sealed class ModGroupService(GroupRepository repository)
{
    public async Task<ProfileGroupRecord> CreateAsync(Guid profileId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var groups = await repository.LoadForProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        var group = new ProfileGroupRecord(Guid.NewGuid(), name.Trim(), groups.Count, DateTimeOffset.UtcNow);
        await repository.SaveAsync(profileId, group, cancellationToken).ConfigureAwait(false);
        return group;
    }

    public Task RenameAsync(Guid profileId, ProfileGroupRecord group, string name, CancellationToken cancellationToken = default) =>
        repository.SaveAsync(profileId, group with { Name = name.Trim() }, cancellationToken);

    public Task DeleteAsync(Guid profileId, Guid groupId, CancellationToken cancellationToken = default) =>
        repository.DeleteAsync(profileId, groupId, cancellationToken);

    public Task SaveMembersAsync(Guid profileId, Guid groupId, IReadOnlyList<ProfileModState> states, CancellationToken cancellationToken = default) =>
        repository.SaveStatesAsync(profileId, groupId, states, cancellationToken);
}
