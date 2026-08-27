using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services;

/// <summary>
/// Mod 链接（作者主页 / 发布页）的 SQLite 仓储。
/// 每个 Mod 只保存一条链接，仅存于管理器数据库（mod_links 表），
/// 不写入模组档案（manifest JSON），因此不受模组更新/清单保存影响。
/// 写入操作使用 SemaphoreSlim 序列化，配合 WAL 模式实现真正的并发读写。
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class ModLinkRepository
{
	private readonly ILogger<ModLinkRepository> _logger;
	private readonly DatabaseService _databaseService;
	private readonly SemaphoreSlim _writeLock = new(1, 1);

	public ModLinkRepository(ILogger<ModLinkRepository> logger, DatabaseService databaseService)
	{
		_logger = logger;
		_databaseService = databaseService;
	}

	/// <summary>
	/// 读取指定 Mod 的链接；未设置或为空时返回 null。
	/// </summary>
	public string? GetLink(string storageDirectory, Guid guid)
	{
		try
		{
			using var connection = _databaseService.OpenConnection(storageDirectory);
			using var cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT LinkUrl FROM mod_links WHERE ModGuid = @Guid;";
			cmd.Parameters.AddWithValue("@Guid", guid.ToString());
			var result = cmd.ExecuteScalar() as string;
			return string.IsNullOrWhiteSpace(result) ? null : result!.Trim();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "读取 Mod {Guid} 的链接失败", guid);
			return null;
		}
	}

	/// <summary>
	/// 保存指定 Mod 的链接。链接为空或空白时删除该记录。
	/// </summary>
	public async Task SaveLinkAsync(string storageDirectory, Guid guid, string? link)
	{
		await _writeLock.WaitAsync().ConfigureAwait(false);
		try
		{
			using var connection = _databaseService.OpenConnection(storageDirectory);

			var trimmed = string.IsNullOrWhiteSpace(link) ? null : link.Trim();
			using var cmd = connection.CreateCommand();
			if (string.IsNullOrEmpty(trimmed))
			{
				cmd.CommandText = "DELETE FROM mod_links WHERE ModGuid = @Guid;";
				cmd.Parameters.AddWithValue("@Guid", guid.ToString());
				cmd.ExecuteNonQuery();
				_logger.LogDebug("Cleared link for mod {Guid}", guid);
			}
			else
			{
				cmd.CommandText = @"
					INSERT INTO mod_links (ModGuid, LinkUrl) VALUES (@Guid, @LinkUrl)
					ON CONFLICT(ModGuid) DO UPDATE SET LinkUrl = excluded.LinkUrl;";
				cmd.Parameters.AddWithValue("@Guid", guid.ToString());
				cmd.Parameters.AddWithValue("@LinkUrl", trimmed);
				cmd.ExecuteNonQuery();
				_logger.LogDebug("Saved link for mod {Guid}", guid);
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "保存 Mod {Guid} 的链接失败", guid);
			throw;
		}
		finally
		{
			_writeLock.Release();
		}
	}

	/// <summary>
	/// 批量删除指定 Mod 的链接记录（模组被删除时清理）。
	/// </summary>
	public async Task DeleteByGuidsAsync(string storageDirectory, IEnumerable<Guid> guids)
	{
		await _writeLock.WaitAsync().ConfigureAwait(false);
		try
		{
			using var connection = _databaseService.OpenConnection(storageDirectory);
			using var transaction = connection.BeginTransaction();
			using var cmd = connection.CreateCommand();
			cmd.CommandText = "DELETE FROM mod_links WHERE ModGuid = @Guid;";
			var param = cmd.Parameters.Add("@Guid", SqliteType.Text);

			foreach (var guid in guids)
			{
				param.Value = guid.ToString();
				cmd.ExecuteNonQuery();
			}

			transaction.Commit();
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "批量删除 Mod 链接失败");
			throw;
		}
		finally
		{
			_writeLock.Release();
		}
	}
}
