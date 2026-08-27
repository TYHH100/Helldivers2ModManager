using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Core.Profiles;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;

namespace Helldivers2ModManager.Adapters;

internal sealed class LegacyProfileSaveAdapter(
    Services.ProfileService profileService,
    Services.ModGroupService modGroupService,
    SettingsService settingsService)
{
    public async Task SaveAsync(Core.Persistence.ProfileSnapshot snapshot)
    {
        var mods = snapshot.Mods.OrderBy(static state => state.SortOrder).ToArray();
        if (snapshot.IsDefault)
        {
            await profileService.SaveSnapshotAsync(settingsService, [.. mods.Select(ToEnabledData)]).ConfigureAwait(false);
        }

		await modGroupService.SaveGroupSnapshotAsync(
			snapshot.Groups[0].Id,
			[.. mods.Select(state => ToGroupedData(state, snapshot.Groups[0].Id))]).ConfigureAwait(false);
    }

    private static EnabledData ToEnabledData(ProfileModState state)
    {
        var runtime = ProfileStateService.DeserializeRuntimeState(state.StateJson);
        return new EnabledData
        {
            Guid = state.ModGuid,
            Enabled = state.Enabled,
            Toggled = [.. runtime.EnabledOptions],
            Selected = [.. runtime.SelectedOptions],
            TagIds = runtime.TagIds is { } tagIds ? [.. tagIds] : [],
        };
    }

	private static GroupedEnabledData ToGroupedData(ProfileModState state, Guid fallbackGroupId)
    {
        var runtime = ProfileStateService.DeserializeRuntimeState(state.StateJson);
        return new GroupedEnabledData
        {
			GroupId = state.GroupId ?? fallbackGroupId,
            Guid = state.ModGuid,
            Enabled = state.Enabled,
            Toggled = [.. runtime.EnabledOptions],
            Selected = [.. runtime.SelectedOptions],
            SortOrder = state.SortOrder,
        };
    }
}
