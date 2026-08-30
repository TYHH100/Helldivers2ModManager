using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services;

/// <summary>
/// Profile 保存的唯一调度入口，负责快照编号、防抖、最新状态合并与串行写入。
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class ProfileSaveCoordinator
{
	private static readonly TimeSpan s_debounceDelay = TimeSpan.FromMilliseconds(300);

	private readonly ILogger<ProfileSaveCoordinator> _logger;
	private readonly ProfileService _profileService;
	private readonly ModGroupService _modGroupService;
	private readonly SettingsService _settingsService;
	private readonly Lock _sync = new();

	private long _nextSequence;
	private long _lastQueuedSequence;
	private ProfileSnapshot? _currentSnapshot;
	private ProfileSnapshot? _pendingSnapshot;
	private CancellationTokenSource? _debounceCancellation;
	private Task _saveQueueTail = Task.CompletedTask;

	public ProfileSaveCoordinator(
		ILogger<ProfileSaveCoordinator> logger,
		ProfileService profileService,
		ModGroupService modGroupService,
		SettingsService settingsService)
	{
		_logger = logger;
		_profileService = profileService;
		_modGroupService = modGroupService;
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

		CancellationTokenSource cancellation;
		lock (_sync)
		{
			UpdateCurrentAndPendingLocked(snapshot);
			_debounceCancellation?.Cancel();
			cancellation = new CancellationTokenSource();
			_debounceCancellation = cancellation;
		}

		_ = RunDebouncedSaveAsync(cancellation);
	}

	public Task SaveNowAsync(ProfileSnapshot snapshot)
	{
		if (_settingsService.IsReadonly)
			return Task.CompletedTask;

		lock (_sync)
		{
			UpdateCurrentAndPendingLocked(snapshot);
			_debounceCancellation?.Cancel();
			_debounceCancellation = null;
			var snapshotToSave = _pendingSnapshot;
			_pendingSnapshot = null;
			return snapshotToSave is null ? _saveQueueTail : QueueSaveLocked(snapshotToSave);
		}
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
			_debounceCancellation?.Cancel();
			_debounceCancellation = null;
			if (_pendingSnapshot is not null)
			{
				QueueSaveLocked(_pendingSnapshot);
				_pendingSnapshot = null;
			}
			return _saveQueueTail;
		}
	}

	private void UpdateCurrentAndPendingLocked(ProfileSnapshot snapshot)
	{
		if (_currentSnapshot is null || snapshot.Sequence >= _currentSnapshot.Sequence)
			_currentSnapshot = snapshot;

		var latestSnapshot = _currentSnapshot!;
		if (_pendingSnapshot is null || latestSnapshot.Sequence >= _pendingSnapshot.Sequence)
			_pendingSnapshot = latestSnapshot;
	}

	private async Task RunDebouncedSaveAsync(CancellationTokenSource cancellation)
	{
		try
		{
			await Task.Delay(s_debounceDelay, cancellation.Token).ConfigureAwait(false);
			Task saveTask;
			lock (_sync)
			{
				if (!ReferenceEquals(_debounceCancellation, cancellation))
					return;
				_debounceCancellation = null;
				var snapshot = _pendingSnapshot;
				_pendingSnapshot = null;
				saveTask = snapshot is null ? _saveQueueTail : QueueSaveLocked(snapshot);
			}
			await saveTask.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Automatic profile save failed");
		}
		finally
		{
			cancellation.Dispose();
		}
	}

	private Task QueueSaveLocked(ProfileSnapshot snapshot)
	{
		if (snapshot.Sequence <= _lastQueuedSequence)
			return _saveQueueTail;

		_lastQueuedSequence = snapshot.Sequence;
		_saveQueueTail = PersistAfterPreviousAsync(_saveQueueTail, snapshot);
		return _saveQueueTail;
	}

	private async Task PersistAfterPreviousAsync(Task previousSave, ProfileSnapshot snapshot)
	{
		try
		{
			await previousSave.ConfigureAwait(false);
		}
		catch
		{
		}

		// 默认组以 enabled_mods 为权威来源；成功后再同步默认组状态缓存。
		if (snapshot.IsDefaultGroup)
			await _profileService.SaveSnapshotAsync(_settingsService, snapshot).ConfigureAwait(false);
		await _modGroupService.SaveGroupSnapshotAsync(snapshot).ConfigureAwait(false);
	}
}
