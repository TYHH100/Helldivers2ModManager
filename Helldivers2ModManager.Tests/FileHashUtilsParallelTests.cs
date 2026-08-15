using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// 并行哈希计算的行为等价性测试：结果与逐文件串行计算一致，
/// 数据库缓存命中/失效路径在并行实现下保持正确。
/// </summary>
[TestClass]
public sealed class FileHashUtilsParallelTests
{
    [TestMethod]
    public async Task ComputeDirectoryHashesAsync_Parallel_MatchesSerialPerFileHashes()
    {
        var root = CreateTempDirectory();
        try
        {
            var modDir = Path.Combine(root, "mod");
            Directory.CreateDirectory(Path.Combine(modDir, "sub"));
            File.WriteAllText(Path.Combine(modDir, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(modDir, "sub", "b.bin"), new string('x', 100_000));
            File.WriteAllText(Path.Combine(modDir, "c.patch_0"), "patch-data");

            var directory = new DirectoryInfo(modDir);
            var hashes = await FileHashUtils.ComputeDirectoryHashesAsync(directory);

            Assert.AreEqual(3, hashes.Count);
            foreach (var file in directory.GetFiles("*", SearchOption.AllDirectories))
            {
                var relativePath = file.FullName
                    .Substring(directory.FullName.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
                var expected = await FileHashUtils.ComputeFileHashAsync(file);
                Assert.AreEqual(expected, hashes[relativePath], $"Hash mismatch for {relativePath}");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task ComputeDirectoryHashesWithCacheAsync_CacheHitAndInvalidation_WorkUnderParallelPath()
    {
        var root = CreateTempDirectory();
        try
        {
            var storageDir = Path.Combine(root, "storage");
            Directory.CreateDirectory(storageDir);
            var modDir = Path.Combine(root, "mod");
            Directory.CreateDirectory(modDir);
            File.WriteAllText(Path.Combine(modDir, "a.txt"), "data1");

            var repository = new FileHashRepository(
                NullLogger<FileHashRepository>.Instance,
                new DatabaseService(NullLogger<DatabaseService>.Instance));
            var directory = new DirectoryInfo(modDir);
            var guid = Guid.NewGuid();

            // 首次：计算并写入缓存
            var first = await FileHashUtils.ComputeDirectoryHashesWithCacheAsync(directory, guid, repository, storageDir);
            Assert.AreEqual(1, first.Count);

            // 第二次：全部命中缓存（结果一致）
            var second = await FileHashUtils.ComputeDirectoryHashesWithCacheAsync(directory, guid, repository, storageDir);
            Assert.AreEqual(first["a.txt"], second["a.txt"]);

            // 修改文件（大小/时间变化）→ 缓存失效并重新计算
            File.WriteAllText(Path.Combine(modDir, "a.txt"), "data2-modified");
            var third = await FileHashUtils.ComputeDirectoryHashesWithCacheAsync(directory, guid, repository, storageDir);
            Assert.AreNotEqual(first["a.txt"], third["a.txt"]);
        }
        finally
        {
            // SQLite 连接池会持有数据库文件句柄，先清空池再删除临时目录
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hd2mm_hash_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
