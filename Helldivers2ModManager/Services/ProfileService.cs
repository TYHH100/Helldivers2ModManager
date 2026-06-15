using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json;

namespace Helldivers2ModManager.Services;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class ProfileService
{
	private readonly ILogger<ProfileService> _logger;
	private readonly EnabledDataRepository _repository;
	private readonly DatabaseService _databaseService;

	/// <summary>
	/// 缓存最近一次 Dashboard 保存时的 Mod 顺序，供其他页面（如 EditPage）保存时保持正确的排序
	/// </summary>
	private List<Guid>? _lastSavedOrder;

	public ProfileService(
		ILogger<ProfileService> logger,
		EnabledDataRepository repository,
		DatabaseService databaseService)
	{
		_logger = logger;
		_repository = repository;
		_databaseService = databaseService;
	}

	/// <summary>
	/// 从 SQLite 数据库加载 Mod 配置。
	/// 首次运行时若存在旧版 enabled.json 文件且数据库为空，则自动执行数据迁移。
	/// </summary>
	public async Task<IReadOnlyList<ModData>?> LoadAsync(SettingsService settingsService, ModService modService)
	{
		var storageDir = settingsService.StorageDirectory;
		var enabledJsonPath = Path.Combine(storageDir, "enabled.json");

		// 检查是否需要从 JSON 迁移数据（HasData 内部会触发数据库初始化）
		if (File.Exists(enabledJsonPath) && !_repository.HasData(storageDir))
		{
			_logger.LogInformation("Detected legacy enabled.json file, starting migration to SQLite...");
			try
			{
				await MigrateFromJsonAsync(enabledJsonPath, storageDir);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "从 enabled.json 迁移数据失败，将尝试直接使用 JSON 文件");
				// 迁移失败时回退到 JSON 读取方式
				return await LoadFromJsonFallbackAsync(enabledJsonPath, modService, settingsService);
			}
		}

		// 从数据库加载数据
		List<EnabledData> enabledDataList;
		try
		{
			enabledDataList = _repository.LoadAll(storageDir);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "从 SQLite 数据库读取配置失败");
			return null;
		}

		if (enabledDataList.Count == 0)
		{
			_logger.LogInformation("No mod config records in database, aborting initialization");
			return null;
		}

		_logger.LogInformation("Loaded {Count} config records from database", enabledDataList.Count);

		var mods = new List<ModData>(modService.Mods.Count);
		var missingGuids = new List<Guid>();

		foreach (var data in enabledDataList)
		{
			_logger.LogDebug("Processing {}", data);

			var mod = modService.GetModByGuid(data.Guid);
			if (mod is null)
			{
				_logger.LogWarning("{} has no corresponding mod, skipping", data.Guid);
				missingGuids.Add(data.Guid);
				continue;
			}

			mod.ApplyData(data);
			mods.Add(mod);
		}

		if (settingsService.AutoRemoveMissingMods && missingGuids.Count > 0)
		{
			_logger.LogInformation("Auto-removed {Count} missing mod records", missingGuids.Count);
			await _repository.DeleteByGuidsAsync(storageDir, missingGuids);
		}

		var remainder = modService.Mods.Count - enabledDataList.Count;
		if (remainder > 0)
		{
			_logger.LogInformation("{Count} mods were not recorded, added with default config", remainder);
			foreach (var elm in modService.Mods)
				if (!mods.Contains(elm))
					mods.Add(elm);
		}

		return mods.ToArray();
	}

	/// <summary>
	/// 当 SQLite 迁移失败时，回退使用 JSON 文件方式读取
	/// </summary>
	private async Task<IReadOnlyList<ModData>?> LoadFromJsonFallbackAsync(
		string enabledJsonPath, ModService modService, SettingsService settingsService)
	{
		_logger.LogWarning("Using JSON fallback to load config");

		using var stream = File.Open(enabledJsonPath, FileMode.Open, FileAccess.Read, FileShare.Read);
		var doc = await JsonDocument.ParseAsync(stream);
		var root = doc.RootElement;

		if (root.ValueKind != JsonValueKind.Array)
			throw new SerializationException("Expected document root to be of type `array`!");

		var len = root.GetArrayLength();
		var mods = new List<ModData>(modService.Mods.Count);
		var missingGuids = new List<Guid>();

		foreach (var elm in root.EnumerateArray())
		{
			if (elm.ValueKind != JsonValueKind.Object)
				throw new SerializationException("Expected array element to be of type `object`!");

			var data = EnabledData.Deserialize(elm);
			var mod = modService.GetModByGuid(data.Guid);
			if (mod is null)
			{
				_logger.LogWarning("{} has no corresponding mod, skipping", data.Guid);
				missingGuids.Add(data.Guid);
				continue;
			}

			mod.ApplyData(data);
			mods.Add(mod);
		}

		if (settingsService.AutoRemoveMissingMods && missingGuids.Count > 0)
		{
			_logger.LogInformation("Auto-removing {} missing mod entries", missingGuids.Count);
			await RemoveMissingEntriesFromJsonAsync(settingsService, missingGuids, enabledJsonPath);
		}

		var remainder = modService.Mods.Count - len;
		if (remainder > 0)
		{
			foreach (var elm in modService.Mods)
				if (!mods.Contains(elm))
					mods.Add(elm);
		}

		return mods.ToArray();
	}

	/// <summary>
	/// 从 JSON 文件中删除丢失的 Mod 条目（回退模式下的兼容方法）
	/// </summary>
	private static async Task RemoveMissingEntriesFromJsonAsync(
		SettingsService settingsService, List<Guid> missingGuids, string enabledJsonPath)
	{
		using var stream = File.Open(enabledJsonPath, FileMode.Open, FileAccess.Read, FileShare.Read);
		var doc = await JsonDocument.ParseAsync(stream);
		var root = doc.RootElement;

		var remainingEntries = new List<JsonElement>();
		foreach (var elm in root.EnumerateArray())
		{
			if (elm.ValueKind != JsonValueKind.Object)
				continue;

			var guid = Guid.Parse(elm.GetProperty(nameof(EnabledData.Guid)).GetString()!);
			if (!missingGuids.Contains(guid))
			{
				remainingEntries.Add(elm.Clone());
			}
		}

		using var writeStream = File.Open(enabledJsonPath, FileMode.Create, FileAccess.Write, FileShare.Read);
		var writer = new Utf8JsonWriter(writeStream);

		writer.WriteStartArray();
		foreach (var entry in remainingEntries)
		{
			entry.WriteTo(writer);
		}
		writer.WriteEndArray();

		await writer.DisposeAsync();
	}

	/// <summary>
	/// 从旧版 enabled.json 文件迁移数据到 SQLite 数据库。
	/// 迁移成功后备份原 JSON 文件为 .bak。
	/// </summary>
	private async Task MigrateFromJsonAsync(string enabledJsonPath, string storageDir)
	{
		_logger.LogInformation("Starting migration from {JsonPath} to SQLite", enabledJsonPath);

		// 1. 读取 JSON 文件中的所有条目
		List<EnabledData> enabledDataList;
		using (var stream = File.Open(enabledJsonPath, FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			var doc = await JsonDocument.ParseAsync(stream);
			var root = doc.RootElement;

			if (root.ValueKind != JsonValueKind.Array)
				throw new SerializationException("Expected document root to be of type `array`!");

			enabledDataList = [];
			foreach (var elm in root.EnumerateArray())
			{
				if (elm.ValueKind != JsonValueKind.Object)
					throw new SerializationException("Expected array element to be of type `object`!");

				var data = EnabledData.Deserialize(elm, _logger);
				enabledDataList.Add(data);
			}
		}

		_logger.LogInformation("Read {Count} records from JSON file", enabledDataList.Count);

		// 2. 写入 SQLite 数据库
		await _repository.SaveAllAsync(storageDir, enabledDataList);

		// 3. 验证数据完整性 —— 从数据库重新读取并比对数量
		var dbCount = _repository.GetCount(storageDir);
		if (dbCount != enabledDataList.Count)
		{
			throw new InvalidOperationException(
				$"数据迁移验证失败：JSON 中有 {enabledDataList.Count} 条记录，但数据库中有 {dbCount} 条记录");
		}

		// 4. 备份原 JSON 文件
		var backupPath = enabledJsonPath + ".bak";
		try
		{
			File.Move(enabledJsonPath, backupPath);
			_logger.LogInformation("Original enabled.json backed up as enabled.json.bak");
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "备份原 JSON 文件失败，但数据已成功迁移");
		}

		_logger.LogInformation("Migration complete: {Count} records migrated from JSON to SQLite", enabledDataList.Count);
	}

	public IReadOnlyList<ModData> InitDefault(ModService modService)
	{
		_logger.LogInformation("Loading profile default");
		return modService.Mods;
	}

	/// <summary>
	/// 缓存 Dashboard 当前显示的 Mod 顺序，供其他页面在保存时保持正确的排序。
	/// 在 Dashboard 初始化完成后调用一次即可。
	/// </summary>
	public void SetLastSavedOrder(IEnumerable<Guid> order)
	{
		_lastSavedOrder = [.. order];
	}

	/// <summary>
	/// 保存 Mod 配置到 SQLite 数据库。
	/// 如果有缓存顺序（来自 Dashboard 的 SetLastSavedOrder），则按缓存顺序写入，
	/// 确保其他页面（如 EditPage）以不同顺序传入时不会打乱已有排序。
	/// </summary>
	public async Task SaveAsync(SettingsService settingsService, IEnumerable<ModData> mods)
	{
		_logger.LogInformation("Saving profile to SQLite");

		var modsList = mods as IReadOnlyList<ModData> ?? [.. mods];

		// 按缓存顺序重排：确保 Dashboard 设定的顺序始终优先
		if (_lastSavedOrder is { Count: > 0 })
		{
			var orderedMods = _lastSavedOrder
				.Select(g => modsList.FirstOrDefault(m => m.Manifest.Guid == g))
				.Where(static m => m is not null)
				.Cast<ModData>()
				.ToList();
			// 添加不在缓存中的新 Mod
			orderedMods.AddRange(modsList.Where(m => !_lastSavedOrder.Contains(m.Manifest.Guid)));
			modsList = orderedMods;
		}

		var dataList = modsList.Select(static m => m.ToEnabledData()).ToList();
		try
		{
			await _repository.SaveAllAsync(settingsService.StorageDirectory, dataList);
			_lastSavedOrder = modsList.Select(static m => m.Manifest.Guid).ToList();
			_logger.LogInformation("Profile saved to SQLite ({Count} records)", dataList.Count);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "保存 Mod 配置到 SQLite 失败");
			throw;
		}
	}

	/// <summary>
	/// 删除数据库中的单条 Mod 配置记录。
	/// </summary>
	public async Task DeleteEnabledDataAsync(string storageDirectory, Guid guid)
	{
		try
		{
			await _repository.DeleteByGuidsAsync(storageDirectory, [guid]);
			_logger.LogInformation("Deleted mod config {Guid} from database", guid);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to delete mod config from database");
		}
	}

	/// <summary>
	/// 删除数据库中的多条 Mod 配置记录。
	/// </summary>
	public async Task DeleteEnabledDataAsync(string storageDirectory, IEnumerable<Guid> guids)
	{
		try
		{
			var list = guids.ToList();
			await _repository.DeleteByGuidsAsync(storageDirectory, list);
			_logger.LogInformation("Deleted {Count} mod configs from database", list.Count);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to delete mod configs from database");
		}
	}
}
