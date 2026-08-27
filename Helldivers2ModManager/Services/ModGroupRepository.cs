using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Core.Profiles;
using Helldivers2ModManager.Models;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace Helldivers2ModManager.Services;

internal sealed class ModGroupRepository(
	ILogger<ModGroupRepository> logger,
	ProfileRepository profileRepository,
	GroupRepository groupRepository)
{
	public async Task<List<ModGroup>> LoadGroupsAsync(CancellationToken cancellationToken = default)
	{
		var profileId = await GetDefaultProfileIdAsync(cancellationToken).ConfigureAwait(false);
		var groups = await groupRepository.LoadForProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
		var result = new List<ModGroup>(groups.Count);

		foreach (var group in groups)
		{
			try
			{
				var memberIds = await groupRepository
					.LoadMemberIdsAsync(profileId, group.Id, cancellationToken)
					.ConfigureAwait(false);
				result.Add(new ModGroup
				{
					Id = group.Id,
					Name = group.Name,
					DisplayIndex = group.DisplayIndex,
					CreatedAtUtc = DateTime.SpecifyKind(group.CreatedAtUtc.UtcDateTime, DateTimeKind.Utc),
					ModGuids = new ObservableCollection<Guid>(memberIds.Distinct()),
				});
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "解析分组记录失败，跳过该条记录");
			}
		}

		return result;
	}

	public async Task SaveGroupsAsync(IEnumerable<ModGroup> groups, CancellationToken cancellationToken = default)
	{
		var profileId = await GetDefaultProfileIdAsync(cancellationToken).ConfigureAwait(false);
		var groupList = groups.ToList();
		for (var index = 0; index < groupList.Count; index++)
		{
			groupList[index].DisplayIndex = index;
		}

		await groupRepository.ReplaceForProfileAsync(
			profileId,
			groupList.Select(static group => new ProfileGroupRecord(
				group.Id,
				group.Name,
				group.DisplayIndex,
				new DateTimeOffset(group.CreatedAtUtc))),
			cancellationToken).ConfigureAwait(false);
	}

	public async Task<List<GroupedEnabledData>> LoadStatesAsync(Guid groupId, CancellationToken cancellationToken = default)
	{
		if (groupId == ModGroup.DefaultGroupId)
			return [];

		var profileId = await GetDefaultProfileIdAsync(cancellationToken).ConfigureAwait(false);
		var records = await groupRepository.LoadMembersAsync(profileId, groupId, cancellationToken).ConfigureAwait(false);
		var states = new List<GroupedEnabledData>(records.Count);

		foreach (var record in records)
		{
			try
			{
				var runtime = ProfileStateService.DeserializeRuntimeState(record.StateJson);
				states.Add(new GroupedEnabledData
				{
					GroupId = groupId,
					Guid = record.ModGuid,
					Enabled = record.Enabled,
					Toggled = [.. runtime.EnabledOptions],
					Selected = [.. runtime.SelectedOptions],
					SortOrder = record.SortOrder,
				});
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "解析分组状态记录失败，跳过该条记录");
			}
		}

		return states;
	}

	public async Task SaveStatesAsync(
		Guid groupId,
		IEnumerable<GroupedEnabledData> states,
		CancellationToken cancellationToken = default)
	{
		if (groupId == ModGroup.DefaultGroupId)
			return;

		var profileId = await GetDefaultProfileIdAsync(cancellationToken).ConfigureAwait(false);
		await groupRepository.ReplaceMembersAsync(
			profileId,
			groupId,
			[.. states.Select(static state => new EnabledStateRecord(
				state.Guid,
				state.Enabled,
				state.SortOrder,
				ProfileStateService.SerializeRuntimeState(new ModRuntimeState(
					state.Toggled,
					state.Selected))))],
			cancellationToken).ConfigureAwait(false);
	}

	public async Task DeleteGroupAsync(Guid groupId, CancellationToken cancellationToken = default)
	{
		if (groupId == ModGroup.DefaultGroupId)
			return;

		var profileId = await GetDefaultProfileIdAsync(cancellationToken).ConfigureAwait(false);
		await groupRepository.DeleteAsync(profileId, groupId, cancellationToken).ConfigureAwait(false);
	}

	public async Task DeleteStatesByGuidsAsync(IEnumerable<Guid> guids, CancellationToken cancellationToken = default)
	{
		var profileId = await GetDefaultProfileIdAsync(cancellationToken).ConfigureAwait(false);
		await groupRepository.DeleteMembersForModsAsync(profileId, guids, cancellationToken).ConfigureAwait(false);
	}

	private async Task<Guid> GetDefaultProfileIdAsync(CancellationToken cancellationToken)
	{
		var profile = await profileRepository.GetOrCreateDefaultAsync(cancellationToken).ConfigureAwait(false);
		return profile.Id;
	}
}
