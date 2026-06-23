using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 哈希迁移进度信息，用于向 UI 报告后台哈希计算的状态
/// </summary>
public sealed class HashMigrationProgress
{
	/// <summary>是否正在进行迁移</summary>
	public bool IsMigrating { get; init; }

	/// <summary>当前正在处理的模组名称</summary>
	public string? CurrentModName { get; init; }

	/// <summary>已完成的模组数</summary>
	public int CompletedCount { get; init; }

	/// <summary>模组总数</summary>
	public int TotalCount { get; init; }

	/// <summary>计算失败的模组数</summary>
	public int FailedCount { get; init; }

	/// <summary>当前状态描述文本</summary>
	public string? Message { get; init; }

	/// <summary>是否迁移已完成</summary>
	public bool IsCompleted => TotalCount > 0 && CompletedCount + FailedCount >= TotalCount;
}

/// <summary>
/// 模组文件哈希管理服务，负责哈希值的自动计算、数据库持久化及生命周期管理。
/// 
/// 核心职责：
/// - 模组添加时自动计算并存储文件哈希
/// - 模组更新时自动替换旧哈希记录
/// - 模组删除时自动清理对应的哈希记录
/// - 版本升级迁移：为所有现有模组计算并存储哈希值
/// 
/// 使用后台线程执行计算任务，避免阻塞 UI 线程；通过 SemaphoreSlim 限制并发数，
/// 防止大量模组同时计算哈希导致系统性能下降。
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class ModHashService
{
	/// <summary>
	/// SQLite user_version 值，用于标记数据库迁移状态。
	/// 0 = 初始状态 / 未执行哈希迁移
	/// 1 = 已完成哈希迁移（所有现有模组的哈希值已存储）
	/// </summary>
	private const int HashMigrationVersion = 1;

	/// <summary>
	/// 后台哈希计算的最大并发数，防止同时计算大量文件的哈希导致磁盘 IO 饱和
	/// </summary>
	private const int MaxConcurrentComputations = 2;

	private readonly ILogger<ModHashService> _logger;
	private readonly FileHashRepository _fileHashRepository;
	private readonly DatabaseService _databaseService;
	private SettingsService? _settingsService;

	/// <summary>
	/// 控制后台哈希计算并发数的信号量
	/// </summary>
	private readonly SemaphoreSlim _computationSemaphore = new(MaxConcurrentComputations, MaxConcurrentComputations);

	/// <summary>
	/// 跟踪正在执行哈希计算的任务，防止同一模组的重复计算
	/// </summary>
	private readonly ConcurrentDictionary<Guid, Task> _activeComputations = new();

	/// <summary>
	/// 哈希迁移进度变化事件，在后台迁移过程中定期触发，供 UI 层订阅显示进度。
	/// 回调在后台线程中执行，订阅者需自行 Dispatch 到 UI 线程。
	/// </summary>
	public event Action<HashMigrationProgress>? MigrationProgressChanged;

	public ModHashService(
		ILogger<ModHashService> logger,
		FileHashRepository fileHashRepository,
		DatabaseService databaseService)
	{
		_logger = logger;
		_fileHashRepository = fileHashRepository;
		_databaseService = databaseService;
	}

	/// <summary>
	/// 初始化服务，注入延迟绑定的 SettingsService
	/// </summary>
	public void Init(SettingsService settingsService)
	{
		_settingsService = settingsService;
	}

	/// <summary>
	/// 为单个模组的所有文件计算 SHA-256 哈希值并存储到数据库。
	/// 
	/// 使用 ComputeDirectoryHashesWithCacheAsync，自动利用现有缓存跳过未变化文件的计算。
	/// 完成后将新计算的哈希值持久化到 file_hashes 表中。
	/// 
	/// 此方法异步执行但不等待完成（fire-and-forget），通过 _activeComputations 字典去重，
	/// 确保同一模组不会同时有多个计算任务在运行。
	/// </summary>
	/// <param name="mod">要计算哈希的模组数据</param>
	public void ComputeAndStoreForModAsync(ModData mod)
	{
		if (_settingsService is null)
		{
			_logger.LogWarning("ModHashService not initialized, skipping hash computation for mod \"{Name}\"", mod.Manifest.Name);
			return;
		}

		// 去重：如果同一模组已有正在执行的计算任务，则跳过
		if (_activeComputations.ContainsKey(mod.Manifest.Guid))
		{
			_logger.LogDebug("Hash computation already in progress for mod \"{Name}\", skipping duplicate request", mod.Manifest.Name);
			return;
		}

		// 启动后台任务，不阻塞调用方
		var task = Task.Run(async () =>
		{
			await _computationSemaphore.WaitAsync();
			try
			{
				_logger.LogInformation("Computing file hashes for mod \"{Name}\" ({Guid})", mod.Manifest.Name, mod.Manifest.Guid);

				// 使用带缓存的哈希计算，自动利用已有缓存并更新变更部分
				await FileHashUtils.ComputeDirectoryHashesWithCacheAsync(
					mod.Directory,
					mod.Manifest.Guid,
					_fileHashRepository,
					_settingsService.StorageDirectory);

				_logger.LogInformation("File hashes computed and stored for mod \"{Name}\"", mod.Manifest.Name);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to compute file hashes for mod \"{Name}\" ({Guid})", mod.Manifest.Name, mod.Manifest.Guid);
			}
			finally
			{
				_computationSemaphore.Release();
				_activeComputations.TryRemove(mod.Manifest.Guid, out _);
			}
		});

		_activeComputations[mod.Manifest.Guid] = task;
	}

	/// <summary>
	/// 模组更新后重新计算哈希值。
	/// 先删除旧的哈希记录，再为当前文件重新计算并存储。
	/// </summary>
	/// <param name="mod">已更新的模组数据</param>
	public async Task RecomputeForUpdatedModAsync(ModData mod)
	{
		if (_settingsService is null)
		{
			_logger.LogWarning("ModHashService not initialized, skipping hash recomputation for mod \"{Name}\"", mod.Manifest.Name);
			return;
		}

		// 先取消正在进行的旧任务（如果有）
		if (_activeComputations.TryRemove(mod.Manifest.Guid, out var existingTask))
		{
			try
			{
				_logger.LogDebug("Cancelling existing hash computation for mod \"{Name}\" before recomputation", mod.Manifest.Name);
				await existingTask;
			}
			catch
			{
				// 忽略旧任务的异常
			}
		}

		await _computationSemaphore.WaitAsync();
		try
		{
			_logger.LogInformation("Deleting old hash records and recomputing for updated mod \"{Name}\"", mod.Manifest.Name);

			// 删除旧哈希记录
			await _fileHashRepository.DeleteForModAsync(_settingsService.StorageDirectory, mod.Manifest.Guid);

			// 重新计算并存储新哈希值（不使用缓存，因为文件已全部更新）
			await FileHashUtils.ComputeDirectoryHashesWithCacheAsync(
				mod.Directory,
				mod.Manifest.Guid,
				_fileHashRepository,
				_settingsService.StorageDirectory);

			_logger.LogInformation("File hashes recomputed and stored for updated mod \"{Name}\"", mod.Manifest.Name);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to recompute file hashes for updated mod \"{Name}\"", mod.Manifest.Name);
		}
		finally
		{
			_computationSemaphore.Release();
		}
	}

	/// <summary>
	/// 删除指定模组的文件哈希缓存记录
	/// </summary>
	/// <param name="mod">要清理哈希记录的模组</param>
	public async Task DeleteForModAsync(ModData mod)
	{
		if (_settingsService is null)
		{
			_logger.LogWarning("ModHashService not initialized, skipping hash deletion for mod \"{Name}\"", mod.Manifest.Name);
			return;
		}

		// 等待该模组的后台哈希计算任务完成，释放文件句柄
		// 避免后续删除模组目录时因文件被占用而提示"文件正在使用"
		if (_activeComputations.TryRemove(mod.Manifest.Guid, out var existingTask))
		{
			_logger.LogDebug("Waiting for ongoing hash computation to complete before deleting records for mod \"{Name}\"", mod.Manifest.Name);
			try
			{
				await existingTask;
			}
			catch
			{
				// 忽略计算任务中的异常（已在 ComputeAndStoreForModAsync 中记录）
			}
		}

		try
		{
			await _fileHashRepository.DeleteForModAsync(_settingsService.StorageDirectory, mod.Manifest.Guid);
			_logger.LogDebug("Hash records deleted for mod \"{Name}\"", mod.Manifest.Name);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to delete hash records for mod \"{Name}\"", mod.Manifest.Name);
		}
	}

	/// <summary>
	/// 版本升级迁移：为所有现有模组计算并存储 SHA-256 文件哈希值。
	/// 
	/// 通过 SQLite PRAGMA user_version 跟踪迁移状态：
	/// - 检查当前 user_version，若小于 HashMigrationVersion(1)，则执行迁移
	/// - 迁移完成后设置 user_version = HashMigrationVersion
	/// 
	/// 迁移过程使用信号量控制并发数，避免大量计算导致磁盘 IO 饱和。
	/// 单个模组计算失败不会影响其他模组的迁移。
	/// </summary>
	/// <param name="mods">所有现有的模组列表</param>
	public async Task MigrateExistingModsAsync(IEnumerable<ModData> mods)
	{
		if (_settingsService is null)
		{
			_logger.LogWarning("ModHashService not initialized, skipping hash migration");
			return;
		}

		var storageDir = _settingsService.StorageDirectory;
		var currentVersion = GetMigrationVersion(storageDir);

		if (currentVersion >= HashMigrationVersion)
		{
			_logger.LogDebug("Hash migration already completed (version {Version}), skipping", currentVersion);
			return;
		}

		// 收集所有模组为列表
		var modList = mods.ToList();
		if (modList.Count == 0)
		{
			_logger.LogInformation("No mods to migrate, setting migration version to {Version}", HashMigrationVersion);
			SetMigrationVersion(storageDir, HashMigrationVersion);
			return;
		}

		_logger.LogInformation(
			"Starting hash migration from version {CurrentVersion} to {TargetVersion} for {Count} mods",
			currentVersion, HashMigrationVersion, modList.Count);

		// 通知 UI：迁移开始
		MigrationProgressChanged?.Invoke(new HashMigrationProgress
		{
			IsMigrating = true,
			TotalCount = modList.Count,
			Message = $"正在计算 {modList.Count} 个模组的文件指纹..."
		});

		var migratedCount = 0;
		var failedCount = 0;

		foreach (var mod in modList)
		{
			// 每处理一个模组，通知 UI 更新进度
			MigrationProgressChanged?.Invoke(new HashMigrationProgress
			{
				IsMigrating = true,
				CurrentModName = mod.Manifest.Name,
				CompletedCount = migratedCount + failedCount,
				TotalCount = modList.Count,
				FailedCount = failedCount,
				Message = $"正在计算文件指纹 ({migratedCount + failedCount + 1}/{modList.Count}): {mod.Manifest.Name}"
			});

			// 注册到 _activeComputations，确保 DeleteForModAsync 能等待迁移完成后再删除目录
			// 避免迁移读取大文件时因文件被占用而导致删除失败
			var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			_activeComputations[mod.Manifest.Guid] = tcs.Task;

			try
			{
				await _computationSemaphore.WaitAsync();
				try
				{
					_logger.LogDebug("Migrating hashes for mod \"{Name}\" ({Index}/{Total})",
						mod.Manifest.Name, migratedCount + failedCount + 1, modList.Count);

					await FileHashUtils.ComputeDirectoryHashesWithCacheAsync(
						mod.Directory,
						mod.Manifest.Guid,
						_fileHashRepository,
						storageDir);

					migratedCount++;
				}
				catch (Exception ex)
				{
					failedCount++;
					_logger.LogError(ex, "Failed to migrate hashes for mod \"{Name}\" ({Guid}), continuing with remaining mods",
						mod.Manifest.Name, mod.Manifest.Guid);
				}
				finally
				{
					_computationSemaphore.Release();
				}
			}
			finally
			{
				// 通知等待者：该模组的哈希计算已全部完成，文件句柄已释放
				tcs.TrySetResult();
				_activeComputations.TryRemove(mod.Manifest.Guid, out _);
			}
		}

		// 迁移完成后更新版本号
		SetMigrationVersion(storageDir, HashMigrationVersion);

		// 通知 UI：迁移完成
		MigrationProgressChanged?.Invoke(new HashMigrationProgress
		{
			IsMigrating = false,
			CompletedCount = migratedCount,
			TotalCount = modList.Count,
			FailedCount = failedCount,
			Message = failedCount > 0
				? $"文件指纹计算完成: {migratedCount} 成功, {failedCount} 失败"
				: $"文件指纹计算完成: {migratedCount} 个模组已就绪"
		});

		_logger.LogInformation(
			"Hash migration completed: {Migrated} mods migrated, {Failed} mods failed, migration version set to {Version}",
			migratedCount, failedCount, HashMigrationVersion);
	}

	/// <summary>
	/// 获取数据库迁移版本号
	/// </summary>
	private int GetMigrationVersion(string storageDirectory)
	{
		try
		{
			using var connection = _databaseService.OpenConnection(storageDirectory);
			using var cmd = connection.CreateCommand();
			cmd.CommandText = "PRAGMA user_version;";
			var result = cmd.ExecuteScalar();
			return Convert.ToInt32(result);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to read migration version, assuming 0");
			return 0;
		}
	}

	/// <summary>
	/// 设置数据库迁移版本号
	/// </summary>
	private void SetMigrationVersion(string storageDirectory, int version)
	{
		try
		{
			using var connection = _databaseService.OpenConnection(storageDirectory);
			using var cmd = connection.CreateCommand();
			cmd.CommandText = $"PRAGMA user_version = {version};";
			cmd.ExecuteNonQuery();
			_logger.LogDebug("Migration version set to {Version}", version);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to set migration version to {Version}", version);
		}
	}

	/// <summary>
	/// 强制重新计算所有模组的文件哈希值（由用户在设置页面手动触发）。
	/// 先重置迁移版本号，再执行完整的哈希迁移流程。
	/// 迁移进度通过 <see cref="MigrationProgressChanged"/> 事件报告。
	/// </summary>
	/// <param name="mods">所有现有模组列表</param>
	public async Task ForceRecomputeAllAsync(IEnumerable<ModData> mods)
	{
		if (_settingsService is null)
		{
			_logger.LogWarning("ModHashService not initialized, skipping force recompute");
			return;
		}

		var storageDir = _settingsService.StorageDirectory;
		_logger.LogInformation("Force recomputing all file hashes (resetting migration version)");

		// 先将迁移版本号重置为 0，使 MigrateExistingModsAsync 重新执行完整的哈希计算流程
		SetMigrationVersion(storageDir, 0);

		await MigrateExistingModsAsync(mods);
	}
}
