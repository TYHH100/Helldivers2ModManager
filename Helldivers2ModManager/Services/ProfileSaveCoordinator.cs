using Helldivers2ModManager.Models;
using Helldivers2ModManager.Core.Profiles;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Services;

/// <summary>
/// Profile 保存的唯一调度入口，负责快照编号、防抖、最新状态合并与串行写入。
/// </summary>
internal sealed class ProfileSaveCoordinator
{
	private readonly global::Helldivers2ModManager.Core.Profiles.ProfileSaveCoordinator _coreCoordinator;
	private readonly SettingsService _settingsService;
	private readonly Lock _sync = new();

	private long _nextSequence;
	private ProfileSnapshot? _currentSnapshot;
	private ProfileSnapshot? _pendingSnapshot;

	public ProfileSaveCoordinator(
		global::Helldivers2ModManager.Core.Profiles.ProfileSaveCoordinator coreCoordinator,
		SettingsService settingsService)
	{
		_coreCoordinator = coreCoordinator;
		_settingsService = settingsService;
	}

	public ProfileSnapshot Capture(
		IEnumerable<ModData> mods,
		IEnumerable<Guid> order,
		Guid groupId,
		bool isDefaultGroup)
	{
		var snapshot = ProfileSnapshot.Capture(
			Interlocked.Increment(ref _nextSequence),
			groupId,
			isDefaultGroup,
			mods,
			order);

		lock (_sync)
		{
			if (_currentSnapshot is null || snapshot.Sequence >= _currentSnapshot.Sequence)
				_currentSnapshot = snapshot;
		}

		_coreCoordinator.Capture(CreateRequest(snapshot));
		return snapshot;
	}

	public IReadOnlyList<Guid>? GetCurrentOrder()
	{
		lock (_sync)
			return _currentSnapshot?.Order.ToArray();
	}

	public void RequestSave(ProfileSnapshot snapshot)
	{
		if (_settingsService.IsReadonly)
			return;

		UpdateCurrentAndPendingLocked(snapshot);
		_coreCoordinator.RequestSave(CreateRequest(snapshot));
	}

	public Task SaveNowAsync(ProfileSnapshot snapshot)
	{
		if (_settingsService.IsReadonly)
			return Task.CompletedTask;

		UpdateCurrentAndPendingLocked(snapshot);
		return _coreCoordinator.SaveNowAsync(CreateRequest(snapshot));
	}

	public Task SaveCurrentAsync(IEnumerable<ModData> mods)
	{
		ProfileSnapshot? context;
		lock (_sync)
			context = _currentSnapshot;

		if (context is null)
			return Task.CompletedTask;

		var sourceMods = mods.ToDictionary(static mod => mod.Manifest.Guid);
		var contextMods = context.Order
			.Where(sourceMods.ContainsKey)
			.Select(guid => sourceMods[guid])
			.ToArray();
		var snapshot = Capture(contextMods, context.Order, context.GroupId, context.IsDefaultGroup);
		return SaveNowAsync(snapshot);
	}

	public Task FlushAsync()
	{
		if (_settingsService.IsReadonly)
			return Task.CompletedTask;

		lock (_sync)
			{
				return _coreCoordinator.FlushAsync();
			}
	}

	private void UpdateCurrentAndPendingLocked(ProfileSnapshot snapshot)
	{
		lock (_sync)
		{
			var currentSnapshot = _currentSnapshot;
			if (currentSnapshot is null || snapshot.Sequence >= currentSnapshot.Sequence)
				currentSnapshot = snapshot;

			_currentSnapshot = currentSnapshot;
			if (_pendingSnapshot is null || currentSnapshot.Sequence >= _pendingSnapshot.Sequence)
				_pendingSnapshot = currentSnapshot;
		}
	}

	private static ProfileCaptureRequest CreateRequest(ProfileSnapshot snapshot)
	{
		return new(
			snapshot.GroupId,
			snapshot.IsDefaultGroup,
			[.. snapshot.Mods.Select(static mod =>
			{
				var data = mod.ToEnabledData();
				return new ProfileModCapture(
					data.Guid,
					data.Enabled,
					new ModRuntimeState(
						[.. data.Toggled],
						[.. data.Selected],
						data.TagIds is { Count: > 0 } tagIds ? [.. tagIds] : null));
			})]);
	}
}
