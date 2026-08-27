using System.Security.Cryptography;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Mods;

[TestClass]
public sealed class FileHashServiceTests
{
    [TestMethod]
    public async Task ComputeDirectoryHashesAsync_ShouldMatchSingleFilesAndReportProgress()
    {
        var root = CreateTempDirectory();
        try
        {
            var modDirectory = Directory.CreateDirectory(Path.Combine(root, "mod"));
            Directory.CreateDirectory(Path.Combine(modDirectory.FullName, "sub"));
            await File.WriteAllTextAsync(Path.Combine(modDirectory.FullName, "a.txt"), "hello");
            await File.WriteAllTextAsync(Path.Combine(modDirectory.FullName, "sub", "b.bin"), new string('x', 100_000));

            var progressReports = new List<DirectoryHashProgress>();
            using var database = new Database(Path.Combine(root, $"hash-{Guid.NewGuid():N}.db"));
            await database.InitializeAsync();
            var service = new FileHashService(new FileHashRepository(database));
            var hashes = await service.ComputeDirectoryHashesAsync(
                modDirectory,
                new Progress<DirectoryHashProgress>(progressReports.Add));

            Assert.AreEqual(2, hashes.Count);
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData("hello"u8)), hashes["a.txt"]);
            var expected = Convert.ToHexStringLower(SHA256.HashData(new string('x', 100_000).Select(static character => (byte)character).ToArray()));
            Assert.AreEqual(expected, hashes["sub/b.bin"]);
            Assert.IsTrue(progressReports.Count > 0);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CachedHashes_ShouldHitInvalidateAndRespectSaveMode()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hd2mm-{Guid.NewGuid():N}.db");
        var root = CreateTempDirectory();
        try
        {
            using var database = new Database(databasePath);
            await database.InitializeAsync();
            var repository = new FileHashRepository(database);
            var service = new FileHashService(repository);
            var modDirectory = Directory.CreateDirectory(Path.Combine(root, "mod"));
            var file = Path.Combine(modDirectory.FullName, "a.txt");
            await File.WriteAllTextAsync(file, "first");
            var guid = Guid.NewGuid();

            var first = await service.ComputeDirectoryHashesWithCacheAsync(modDirectory, guid, true);
            var second = await service.ComputeDirectoryHashesWithCacheAsync(modDirectory, guid, false);
            Assert.AreEqual(first["a.txt"], second["a.txt"]);

            await repository.ReplaceForModAsync(guid, []);
            var readOnly = await service.ComputeDirectoryHashesWithCacheAsync(modDirectory, guid, false);
            Assert.AreEqual(0, (await repository.LoadForModAsync(guid)).Count);
            await File.WriteAllTextAsync(file, "second");
            var third = await service.ComputeDirectoryHashesWithCacheAsync(modDirectory, guid, true);
            Assert.AreNotEqual(first["a.txt"], third["a.txt"]);
            Assert.AreEqual(third["a.txt"], (await repository.LoadForModAsync(guid)).Single().FileHash);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [TestMethod]
    public void CompareHashes_ShouldClassifyChangedNewDeletedAndUnchanged()
    {
        var comparison = FileHashService.CompareHashes(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["same.txt"] = "one",
                ["changed.txt"] = "old",
                ["deleted.txt"] = "gone",
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["same.txt"] = "ONE",
                ["changed.txt"] = "new",
                ["new.txt"] = "added",
            });

        Assert.AreEqual(2, comparison.ChangedFiles.Count);
        CollectionAssert.Contains(comparison.ChangedFiles.ToList(), "changed.txt");
        CollectionAssert.Contains(comparison.ChangedFiles.ToList(), "new.txt");
        Assert.AreEqual("deleted.txt", comparison.DeletedFiles.Single());
        Assert.AreEqual(1, comparison.UnchangedCount);
        Assert.AreEqual(3, comparison.TotalNewFiles);
        Assert.AreEqual(3, comparison.TotalCurrentFiles);
    }

    [TestMethod]
    public async Task ComputeDirectoryHashesAsync_ShouldHonorCanceledToken()
    {
        var root = CreateTempDirectory();
        try
        {
            var repositoryPath = Path.Combine(root, "hash.db");
            using var database = new Database(repositoryPath);
            await database.InitializeAsync();
            var service = new FileHashService(new FileHashRepository(database));
            using var cancellationTokenSource = new CancellationTokenSource();
            await cancellationTokenSource.CancelAsync();
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => service.ComputeDirectoryHashesAsync(new DirectoryInfo(root), cancellationToken: cancellationTokenSource.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hd2mm-hash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
