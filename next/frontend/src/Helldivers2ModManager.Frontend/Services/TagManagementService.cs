using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Core.Profiles;

namespace Helldivers2ModManager.Frontend.Services;

public sealed class TagManagementService(
    ApplicationSettingsService settings,
    EnabledStateRepository enabledStates)
{
    public IReadOnlyList<TagSetting> LoadTags() => [.. settings.Current.Tags];

    public async Task<TagSetting> AddAsync(string name, string color, CancellationToken cancellationToken = default)
    {
        var cleanName = name.Trim();
        if (cleanName.Length == 0)
        {
            throw new InvalidOperationException("Tag name cannot be empty.");
        }

        if (settings.Current.Tags.Any(tag => tag.Name.Equals(cleanName, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new InvalidOperationException($"Tag \"{cleanName}\" already exists.");
        }

        var tag = new TagSetting(Guid.NewGuid(), cleanName, color);
        settings.Current.Tags = new List<TagSetting>(settings.Current.Tags) { tag };
        await settings.SaveAsync(settings.Current, cancellationToken).ConfigureAwait(false);
        return tag;
    }

    public async Task SaveAsync(IReadOnlyList<TagSetting> tags, CancellationToken cancellationToken = default)
    {
        settings.Current.Tags = new List<TagSetting>(tags);
        await settings.SaveAsync(settings.Current, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid tagId, CancellationToken cancellationToken = default)
    {
        settings.Current.Tags = settings.Current.Tags.Where(tag => tag.Id != tagId).ToList();
        await settings.SaveAsync(settings.Current, cancellationToken).ConfigureAwait(false);

        var records = await enabledStates.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var changed = false;
        var updated = new List<EnabledStateRecord>();
        foreach (var record in records)
        {
            var runtime = ProfileStateService.DeserializeRuntimeState(record.StateJson);
            var tagIds = runtime.TagIds?.ToList() ?? [];
            if (tagIds.Remove(tagId))
            {
                changed = true;
                updated.Add(record with
                {
                    StateJson = ProfileStateService.SerializeRuntimeState(runtime with { TagIds = tagIds }),
                });
            }
            else
            {
                updated.Add(record);
            }
        }

        if (changed)
        {
            await enabledStates.ReplaceAllAsync(updated, cancellationToken).ConfigureAwait(false);
        }
    }
}
