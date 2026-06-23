using Helldivers2ModManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Helldivers2ModManager.Services;

/// <summary>
/// EnabledData 的 SQLite 仓储，负责将 Mod 启用状态和选项配置持久化到数据库。
/// 每次操作创建独立连接并即用即关，配合 WAL 模式实现真正的并发读写。
/// 写入操作使用 SemaphoreSlim 序列化，确保事务原子性。
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class EnabledDataRepository
{
	private readonly ILogger<EnabledDataRepository> _logger;
	private readonly DatabaseService _databaseService;
	private readonly SemaphoreSlim _writeLock = new(1, 1);

	/// <summary>
	/// JSON 序列化选项 —— 用于将数组字段序列化为 JSON 字符串存储
	/// </summary>
	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		WriteIndented = false,
	};

	public EnabledDataRepository(ILogger<EnabledDataRepository> logger, DatabaseService databaseService)
	{
		_logger = logger;
		_databaseService = databaseService;
	}

	/// <summary>
	/// 批量保存所有 Mod 启用数据到数据库，使用事务确保原子性。
	/// 先清空表中所有记录，再批量插入新数据。
	/// 每次创建独立连接，用完即关。
	/// </summary>
	/// <param name="storageDirectory">存储目录</param>
	/// <param name="enabledDataList">要保存的启用数据集合</param>
	public async Task SaveAllAsync(string storageDirectory, IEnumerable<EnabledData> enabledDataList)
	{
		await _writeLock.WaitAsync();
		try
		{
			using var connection = _databaseService.OpenConnection(storageDirectory);
			using var transaction = connection.BeginTransaction();

			try
			{
				// 先清空表中所有记录
				using (var deleteCmd = connection.CreateCommand())
				{
					deleteCmd.CommandText = "DELETE FROM enabled_mods;";
					deleteCmd.ExecuteNonQuery();
				}

				// 批量插入新数据
				using (var insertCmd = connection.CreateCommand())
				{
					insertCmd.CommandText = @"
						INSERT INTO enabled_mods (Guid, Enabled, Toggled, Selected, TagIds, SortOrder)
						VALUES (@Guid, @Enabled, @Toggled, @Selected, @TagIds, @SortOrder);
					";

					var guidParam = insertCmd.Parameters.Add("@Guid", SqliteType.Text);
					var enabledParam = insertCmd.Parameters.Add("@Enabled", SqliteType.Integer);
					var toggledParam = insertCmd.Parameters.Add("@Toggled", SqliteType.Text);
					var selectedParam = insertCmd.Parameters.Add("@Selected", SqliteType.Text);
					var tagIdsParam = insertCmd.Parameters.Add("@TagIds", SqliteType.Text);
					var sortOrderParam = insertCmd.Parameters.Add("@SortOrder", SqliteType.Integer);

					int index = 0;
					foreach (var data in enabledDataList)
					{
						guidParam.Value = data.Guid.ToString();
						enabledParam.Value = data.Enabled ? 1 : 0;
						toggledParam.Value = JsonSerializer.Serialize(data.Toggled, s_jsonOptions);
						selectedParam.Value = JsonSerializer.Serialize(data.Selected, s_jsonOptions);
						tagIdsParam.Value = data.TagIds is { Count: > 0 }
							? JsonSerializer.Serialize(data.TagIds.Select(id => id.ToString()), s_jsonOptions)
							: DBNull.Value;
						sortOrderParam.Value = index++;

						insertCmd.ExecuteNonQuery();
					}
				}

				transaction.Commit();
				_logger.LogInformation("Saved {Count} mod configs to database", enabledDataList.Count());
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to save enabled data, transaction rolled back");
				transaction.Rollback();
				throw;
			}
		}
		finally
		{
			_writeLock.Release();
		}
	}

	/// <summary>
	/// 从数据库读取所有 Mod 启用数据。每次创建独立连接，用完即关。
	/// </summary>
	/// <param name="storageDirectory">存储目录</param>
	/// <returns>所有已持久化的 EnabledData 记录</returns>
	public List<EnabledData> LoadAll(string storageDirectory)
	{
		using var connection = _databaseService.OpenConnection(storageDirectory);
		var results = new List<EnabledData>();

		using var cmd = connection.CreateCommand();
		cmd.CommandText = "SELECT Guid, Enabled, Toggled, Selected, TagIds FROM enabled_mods ORDER BY SortOrder ASC;";

		using var reader = cmd.ExecuteReader();
		while (reader.Read())
		{
			try
			{
				var guid = Guid.Parse(reader.GetString(0));
				var enabled = reader.GetInt32(1) != 0;
				var toggled = JsonSerializer.Deserialize<bool[]>(reader.GetString(2), s_jsonOptions)
					?? [];
				var selected = JsonSerializer.Deserialize<int[]>(reader.GetString(3), s_jsonOptions)
					?? [];

				List<Guid>? tagIds = null;
				if (!reader.IsDBNull(4))
				{
					try
					{
						var tagIdStrings = JsonSerializer.Deserialize<List<string>>(reader.GetString(4), s_jsonOptions);
						if (tagIdStrings is { Count: > 0 })
						{
							tagIds = [];
							foreach (var tagIdStr in tagIdStrings)
							{
								try
								{
									tagIds.Add(Guid.Parse(tagIdStr));
								}
								catch (Exception ex)
								{
									_logger.LogWarning(ex, "解析 TagId 失败，跳过: {TagIdStr}", tagIdStr);
								}
							}
						}
					}
					catch (Exception ex)
					{
						_logger.LogWarning(ex, "解析 TagIds 失败，默认设为 null");
					}
				}

				results.Add(new EnabledData
				{
					Guid = guid,
					Enabled = enabled,
					Toggled = toggled,
					Selected = selected,
					TagIds = tagIds,
				});
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "解析数据库记录失败，跳过该条记录");
			}
		}

		_logger.LogDebug("Loaded {Count} mod configs from database", results.Count);
		return results;
	}

	/// <summary>
	/// 删除数据库中指定 Guid 的记录。每次创建独立连接，用完即关。
	/// </summary>
	/// <param name="storageDirectory">存储目录</param>
	/// <param name="guids">要删除的 Guid 列表</param>
	public async Task DeleteByGuidsAsync(string storageDirectory, IEnumerable<Guid> guids)
	{
		await _writeLock.WaitAsync();
		try
		{
			using var connection = _databaseService.OpenConnection(storageDirectory);
			using var transaction = connection.BeginTransaction();

			try
			{
				using var cmd = connection.CreateCommand();
				cmd.CommandText = "DELETE FROM enabled_mods WHERE Guid = @Guid;";
				var param = cmd.Parameters.Add("@Guid", SqliteType.Text);

				foreach (var guid in guids)
				{
					param.Value = guid.ToString();
					cmd.ExecuteNonQuery();
				}

				transaction.Commit();
				_logger.LogInformation("Deleted {Count} mod configs from database", guids.Count());
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to delete mod configs, transaction rolled back");
				transaction.Rollback();
				throw;
			}
		}
		finally
		{
			_writeLock.Release();
		}
	}

	/// <summary>
	/// 检查数据库中是否有数据（用于判断是否需要执行迁移）
	/// </summary>
	/// <param name="storageDirectory">存储目录</param>
	public bool HasData(string storageDirectory)
	{
		using var connection = _databaseService.OpenConnection(storageDirectory);
		using var cmd = connection.CreateCommand();
		cmd.CommandText = "SELECT COUNT(*) FROM enabled_mods;";
		var result = cmd.ExecuteScalar();
		return result is long count && count > 0;
	}

	/// <summary>
	/// 获取数据库中已存储的记录数量
	/// </summary>
	public long GetCount(string storageDirectory)
	{
		using var connection = _databaseService.OpenConnection(storageDirectory);
		using var cmd = connection.CreateCommand();
		cmd.CommandText = "SELECT COUNT(*) FROM enabled_mods;";
		var result = cmd.ExecuteScalar();
		return result is long count ? count : 0;
	}
}
