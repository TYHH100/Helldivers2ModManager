using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// EnabledDataRepository 的单元测试，验证基于 SQLite 的 CRUD 操作和线程安全
/// </summary>
[TestClass]
public sealed class EnabledDataRepositoryTests : IDisposable
{
	private readonly string _testStorageDir;
	private readonly ILogger<DatabaseService> _dbLogger;
	private readonly ILogger<EnabledDataRepository> _repoLogger;
	private DatabaseService? _dbService;
	private EnabledDataRepository? _repository;

	public EnabledDataRepositoryTests()
	{
		_testStorageDir = Path.Combine(Path.GetTempPath(), $"hd2mm_test_{Guid.NewGuid():N}");
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
		catch { /* 忽略清理异常 */ }
	}

	public void Dispose()
	{
		_dbService?.Dispose();
	}

	[TestMethod]
	public void DatabaseService_Initialize_CreatesDbFile_And_WalFiles()
	{
		// Act: 初始化数据库连接
		using var connection = _dbService!.OpenConnection(_testStorageDir);

		// Assert: 数据库文件应该被创建
		var dbPath = DatabaseService.GetDatabasePath(_testStorageDir);
		Assert.IsTrue(File.Exists(dbPath), $"数据库文件应存在: {dbPath}");

		// WAL 模式下会生成 WAL 和 SHM 文件
		var walPath = dbPath + "-wal";
		var shmPath = dbPath + "-shm";
		Assert.IsTrue(File.Exists(walPath), $"WAL 文件应存在: {walPath}");
		Assert.IsTrue(File.Exists(shmPath), $"SHM 文件应存在: {shmPath}");

		Assert.IsTrue(_dbService.IsInitialized, "数据库应已初始化");
	}

	[TestMethod]
	public void SaveAll_And_LoadAll_Should_ReturnSameData()
	{
		// Arrange: 准备测试数据
		var testData = new List<EnabledData>
		{
			new()
			{
				Guid = Guid.NewGuid(),
				Enabled = true,
				Toggled = [true, false, true],
				Selected = [0, 1, 0],
				GroupId = null,
				TagIds = null,
			},
			new()
			{
				Guid = Guid.NewGuid(),
				Enabled = false,
				Toggled = [false, false],
				Selected = [2],
				GroupId = Guid.NewGuid(),
				TagIds = [Guid.NewGuid(), Guid.NewGuid()],
			},
		};

		// Act: 保存
		_repository!.SaveAllAsync(_testStorageDir, testData).GetAwaiter().GetResult();

		// Assert: 读取并验证
		var loaded = _repository.LoadAll(_testStorageDir);
		Assert.HasCount(testData.Count, loaded, "记录数量应一致");

		for (int i = 0; i < testData.Count; i++)
		{
			Assert.AreEqual(testData[i].Guid, loaded[i].Guid);
			Assert.AreEqual(testData[i].Enabled, loaded[i].Enabled);
			CollectionAssert.AreEqual(testData[i].Toggled, loaded[i].Toggled);
			CollectionAssert.AreEqual(testData[i].Selected, loaded[i].Selected);
			Assert.AreEqual(testData[i].GroupId, loaded[i].GroupId);

			if (testData[i].TagIds is null)
				Assert.IsNull(loaded[i].TagIds);
			else
				CollectionAssert.AreEqual(testData[i].TagIds!, loaded[i].TagIds!);
		}
	}

	[TestMethod]
	public void SaveAll_Should_OverwriteExistingData()
	{
		// Arrange: 先保存一批数据
		var firstBatch = new List<EnabledData>
		{
			new()
			{
				Guid = Guid.NewGuid(),
				Enabled = true,
				Toggled = [true],
				Selected = [0],
			},
		};
		_repository!.SaveAllAsync(_testStorageDir, firstBatch).GetAwaiter().GetResult();

		// Act: 保存第二批数据（应覆盖）
		var secondBatch = new List<EnabledData>
		{
			new()
			{
				Guid = Guid.NewGuid(),
				Enabled = false,
				Toggled = [false, true],
				Selected = [1, 2],
			},
			new()
			{
				Guid = Guid.NewGuid(),
				Enabled = true,
				Toggled = [true],
				Selected = [0],
			},
		};
		_repository.SaveAllAsync(_testStorageDir, secondBatch).GetAwaiter().GetResult();

		// Assert: 应只有第二批数据
		var loaded = _repository.LoadAll(_testStorageDir);
		Assert.HasCount(2, loaded, "应为第二批的 2 条记录");
	}

	[TestMethod]
	public void HasData_ShouldReturnFalse_WhenEmpty()
	{
		_dbService!.OpenConnection(_testStorageDir).Dispose();
		Assert.IsFalse(_repository!.HasData(_testStorageDir), "空数据库不应有数据");
	}

	[TestMethod]
	public void HasData_ShouldReturnTrue_AfterSave()
	{
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
		_repository!.SaveAllAsync(_testStorageDir, data).GetAwaiter().GetResult();
		Assert.IsTrue(_repository.HasData(_testStorageDir), "保存后应有数据");
	}

	[TestMethod]
	public void DeleteByGuids_ShouldRemoveSpecifiedRecords()
	{
		// Arrange
		var guidToDelete = Guid.NewGuid();
		var guidToKeep = Guid.NewGuid();
		var data = new List<EnabledData>
		{
			new() { Guid = guidToDelete, Enabled = true, Toggled = [true], Selected = [0] },
			new() { Guid = guidToKeep, Enabled = true, Toggled = [false], Selected = [1] },
		};
		_repository!.SaveAllAsync(_testStorageDir, data).GetAwaiter().GetResult();

		// Act
		_repository.DeleteByGuidsAsync(_testStorageDir, [guidToDelete]).GetAwaiter().GetResult();

		// Assert
		var loaded = _repository.LoadAll(_testStorageDir);
		Assert.HasCount(1, loaded);
		Assert.AreEqual(guidToKeep, loaded[0].Guid);
	}

	[TestMethod]
	public void ConcurrentWrites_ShouldBeThreadSafe()
	{
		// Arrange: 使用 Task.WhenAll 模拟并发写入
		const int threadCount = 5;
		var tasks = new Task[threadCount];
		var exceptions = new List<Exception>();

		for (int i = 0; i < threadCount; i++)
		{
			var idx = i;
			tasks[i] = Task.Run(async () =>
			{
				try
				{
					var data = new List<EnabledData>
					{
						new()
						{
							Guid = Guid.NewGuid(),
							Enabled = true,
							Toggled = [true, false],
							Selected = [idx],
						}
					};
					await _repository!.SaveAllAsync(_testStorageDir, data);
				}
				catch (Exception ex)
				{
					lock (exceptions)
						exceptions.Add(ex);
				}
			});
		}

		// Act & Assert: 不应抛出异常
		Task.WhenAll(tasks).GetAwaiter().GetResult();
		Assert.IsTrue(exceptions is { Count: 0 }, $"并发写入应无异常，但捕获到 {exceptions.Count} 个异常");

		// 最终数据库应有数据
		Assert.IsTrue(_repository!.HasData(_testStorageDir), "并发写入后数据库应有数据");
	}

	[TestMethod]
	public void LargeDataSet_ShouldNotTimeout()
	{
		// Arrange: 生成大量测试数据
		const int count = 1000;
		var largeDataSet = new List<EnabledData>(count);
		for (int i = 0; i < count; i++)
		{
			largeDataSet.Add(new EnabledData
			{
				Guid = Guid.NewGuid(),
				Enabled = i % 2 == 0,
				Toggled = Enumerable.Repeat(true, 5).ToArray(),
				Selected = Enumerable.Range(0, 5).Select(x => x % 3).ToArray(),
				GroupId = i % 3 == 0 ? Guid.NewGuid() : null,
				TagIds = i % 4 == 0 ? [Guid.NewGuid()] : null,
			});
		}

		// Act: 测量写入性能
		var sw = Stopwatch.StartNew();
		_repository!.SaveAllAsync(_testStorageDir, largeDataSet).GetAwaiter().GetResult();
		sw.Stop();

		// Assert
#pragma warning disable MSTEST0037 // 对于 IsLessThan: long/int 比较不适用 Assert.IsLessThan
		Assert.IsTrue(sw.ElapsedMilliseconds < 30000, $"写入 {count} 条记录应在 30 秒内完成，实际耗时: {sw.ElapsedMilliseconds}ms");
#pragma warning restore MSTEST0037

		var loaded = _repository.LoadAll(_testStorageDir);
		Assert.HasCount(count, loaded, $"应加载全部 {count} 条记录");
	}
}
