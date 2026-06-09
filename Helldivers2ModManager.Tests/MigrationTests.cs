using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// 数据迁移测试 —— 验证从旧版 enabled.json 到 SQLite 的迁移流程
/// </summary>
[TestClass]
public sealed class MigrationTests : IDisposable
{
	private readonly string _testStorageDir;
	private readonly ILogger<DatabaseService> _dbLogger;
	private readonly ILogger<EnabledDataRepository> _repoLogger;
	private DatabaseService? _dbService;
	private EnabledDataRepository? _repository;

	public MigrationTests()
	{
		_testStorageDir = Path.Combine(Path.GetTempPath(), $"hd2mm_migration_{Guid.NewGuid():N}");
		_dbLogger = LoggerFactory.Create(b => b.AddDebug().SetMinimumLevel(LogLevel.Debug))
			.CreateLogger<DatabaseService>();
		_repoLogger = LoggerFactory.Create(b => b.AddDebug().SetMinimumLevel(LogLevel.Debug))
			.CreateLogger<EnabledDataRepository>();
	}

	[TestInitialize]
	public void Setup()
	{
		if (Directory.Exists(_testStorageDir))
			Directory.Delete(_testStorageDir, true);
		Directory.CreateDirectory(_testStorageDir);

		_dbService = new DatabaseService(_dbLogger);
		_repository = new EnabledDataRepository(_repoLogger, _dbService);
	}

	[TestCleanup]
	public void Cleanup()
	{
		_repository = null;
		_dbService?.Dispose();
		_dbService = null;

		try { if (Directory.Exists(_testStorageDir)) Directory.Delete(_testStorageDir, true); }
		catch { }
	}

	public void Dispose() => _dbService?.Dispose();

	/// <summary>
	/// 验证 JSON 数据可以正确写入 SQLite 并读取
	/// </summary>
	[TestMethod]
	public void JsonToSqlite_DataIntegrity_Maintained()
	{
		// Arrange: 模拟用户已有的 JSON 数据
		var originalData = new List<EnabledData>
		{
			new()
			{
				Guid = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890"),
				Enabled = true,
				Toggled = [true, false, true, false],
				Selected = [0, 0, 1, 2],
				GroupId = null,
				TagIds = null,
			},
			new()
			{
				Guid = Guid.Parse("b2c3d4e5-f6a7-8901-bcde-f12345678901"),
				Enabled = false,
				Toggled = [true, true],
				Selected = [2, 0],
				GroupId = Guid.Parse("c3d4e5f6-a7b8-9012-cdef-123456789012"),
				TagIds = [Guid.Parse("d4e5f6a7-b8c9-0123-defa-234567890123")],
			},
		};

		// 1. 序列化为 JSON（模拟原 enabled.json 内容）
		var jsonBytes = SerializeToJson(originalData);
		var jsonPath = Path.Combine(_testStorageDir, "enabled.json");
		File.WriteAllBytes(jsonPath, jsonBytes);

		// 2. 从 JSON 读取并写入 SQLite（模拟迁移）
		var deserialized = DeserializeFromJson(jsonPath);
		_repository!.SaveAllAsync(_testStorageDir, deserialized).GetAwaiter().GetResult();

		// 3. 从 SQLite 读取并验证
		var fromDb = _repository.LoadAll(_testStorageDir);
		Assert.HasCount(originalData.Count, fromDb, "记录数应一致");

		for (int i = 0; i < originalData.Count; i++)
		{
			Assert.AreEqual(originalData[i].Guid, fromDb[i].Guid);
			Assert.AreEqual(originalData[i].Enabled, fromDb[i].Enabled);
			CollectionAssert.AreEqual(originalData[i].Toggled, fromDb[i].Toggled);
			CollectionAssert.AreEqual(originalData[i].Selected, fromDb[i].Selected);
			Assert.AreEqual(originalData[i].GroupId, fromDb[i].GroupId);
			if (originalData[i].TagIds is null)
				Assert.IsNull(fromDb[i].TagIds);
			else
				CollectionAssert.AreEqual(originalData[i].TagIds!, fromDb[i].TagIds!);
		}
	}

	/// <summary>
	/// 验证迁移后的备份机制 —— 原 JSON 文件在验证通过后应被备份
	/// </summary>
	[TestMethod]
	public void Migration_BacksUp_JsonFile()
	{
		// Arrange: 创建 enabled.json
		var data = new List<EnabledData>
		{
			new()
			{
				Guid = Guid.NewGuid(),
				Enabled = true,
				Toggled = [true],
				Selected = [0],
			}
		};
		var jsonPath = Path.Combine(_testStorageDir, "enabled.json");
		File.WriteAllBytes(jsonPath, SerializeToJson(data));

		// Act: 迁移并备份
		var deserialized = DeserializeFromJson(jsonPath);
		_repository!.SaveAllAsync(_testStorageDir, deserialized).GetAwaiter().GetResult();

		// 验证数据一致性后备份
		var dbCount = _repository.GetCount(_testStorageDir);
		Assert.AreEqual(data.Count, (int)dbCount, "数据应完整迁移");

		var backupPath = jsonPath + ".bak";
		File.Move(jsonPath, backupPath);

		// Assert
		Assert.IsTrue(File.Exists(backupPath), "备份文件应存在");
		Assert.IsFalse(File.Exists(jsonPath), "原 JSON 文件应已移动");
	}

	/// <summary>
	/// 验证空数组和 null 字段的正确序列化/反序列化
	/// </summary>
	[TestMethod]
	public void EmptyArrays_And_NullFields_AreHandledCorrectly()
	{
		// Arrange
		var data = new List<EnabledData>
		{
			new()
			{
				Guid = Guid.NewGuid(),
				Enabled = true,
				Toggled = [],
				Selected = [],
				GroupId = null,
				TagIds = null,
			},
		};

		// Act
		_repository!.SaveAllAsync(_testStorageDir, data).GetAwaiter().GetResult();
		var loaded = _repository.LoadAll(_testStorageDir);

		// Assert
		Assert.HasCount(1, loaded);
#pragma warning disable MSTEST0037 // bool[] 和 int[] 不适用 Assert.IsEmpty
		Assert.AreEqual(0, loaded[0].Toggled.Length, "空 Toggled 数组应保留");
		Assert.AreEqual(0, loaded[0].Selected.Length, "空 Selected 数组应保留");
#pragma warning restore MSTEST0037
		Assert.IsNull(loaded[0].GroupId, "null GroupId 应保留");
		Assert.IsNull(loaded[0].TagIds, "null TagIds 应保留");
	}

	private static byte[] SerializeToJson(List<EnabledData> data)
	{
		using var ms = new MemoryStream();
		using var writer = new Utf8JsonWriter(ms);
		writer.WriteStartArray();
		foreach (var item in data)
			item.Serialize(writer);
		writer.WriteEndArray();
		writer.Flush();
		return ms.ToArray();
	}

	private static List<EnabledData> DeserializeFromJson(string jsonPath)
	{
		using var stream = File.OpenRead(jsonPath);
		var doc = JsonDocument.ParseAsync(stream).GetAwaiter().GetResult();
		var root = doc.RootElement;
		var results = new List<EnabledData>();
		foreach (var elm in root.EnumerateArray())
			results.Add(EnabledData.Deserialize(elm));
		return results;
	}
}
