using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Mods;

[TestClass]
public sealed class ModDirectoryServiceTests
{
    [TestMethod]
    public async Task ImportDirectory_ShouldCopyFilesAndInferManifestWithoutChangingSource()
    {
        var root = CreateTempDirectory();
        try
        {
            var storage = Directory.CreateDirectory(Path.Combine(root, "storage"));
            var source = Directory.CreateDirectory(Path.Combine(root, "source", "Armor Pack"));
            Directory.CreateDirectory(Path.Combine(source.FullName, "Option A"));
            await File.WriteAllTextAsync(Path.Combine(source.FullName, "data.patch_0"), "patch");
            await File.WriteAllTextAsync(Path.Combine(source.FullName, "icon.png"), "image");

            using var database = CreateDatabase(storage);
            var hashes = new FileHashRepository(database);
            var service = new ModDirectoryService(new FileHashService(hashes), hashes, NullLogger<ModDirectoryService>.Instance);
            var result = await service.ImportDirectoryAsync(source, storage);
            var imported = result.Value!;
            var manifest = ModManifest.DeserializeFromDirectory(imported.Directory);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual("Armor Pack", manifest.Name);
            Assert.AreEqual("icon.png", manifest.IconPath);
            Assert.AreEqual("Option A", ((LegacyModManifest)manifest).Options!.Single());
            Assert.AreEqual("patch", await File.ReadAllTextAsync(Path.Combine(imported.Directory.FullName, "data.patch_0")));
            Assert.IsFalse(File.Exists(Path.Combine(source.FullName, "manifest.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task DiscoverMods_ShouldReturnValidModsAndProblems()
    {
        var root = CreateTempDirectory();
        try
        {
            var storage = Directory.CreateDirectory(Path.Combine(root, "storage"));
            var valid = Directory.CreateDirectory(Path.Combine(storage.FullName, "Mods", "Valid"));
            await File.WriteAllTextAsync(Path.Combine(valid.FullName, "manifest.json"), """{"Version":1,"Guid":"00000000-0000-0000-0000-000000000001","Name":"Valid","Description":""}""");
            Directory.CreateDirectory(Path.Combine(storage.FullName, "Mods", "Invalid"));
            using var database = CreateDatabase(storage);
            var hashes = new FileHashRepository(database);
            var service = new ModDirectoryService(new FileHashService(hashes), hashes, NullLogger<ModDirectoryService>.Instance);

            var discovery = service.DiscoverMods(storage);
            Assert.AreEqual(1, discovery.Mods.Count);
            Assert.AreEqual("Valid", discovery.Mods[0].Manifest.Name);
            Assert.AreEqual(1, discovery.Problems.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task UpdateFromDirectory_ShouldApplyChangedNewAndDeletedFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var storage = Directory.CreateDirectory(Path.Combine(root, "storage"));
            var current = Directory.CreateDirectory(Path.Combine(storage.FullName, "Mods", "Mod"));
            var source = Directory.CreateDirectory(Path.Combine(root, "update"));
            await File.WriteAllTextAsync(Path.Combine(current.FullName, "same.txt"), "same");
            await File.WriteAllTextAsync(Path.Combine(current.FullName, "old.txt"), "delete me");
            await File.WriteAllTextAsync(Path.Combine(source.FullName, "same.txt"), "same");
            await File.WriteAllTextAsync(Path.Combine(source.FullName, "changed.txt"), "new content");
            Directory.CreateDirectory(Path.Combine(source.FullName, "nested"));
            await File.WriteAllTextAsync(Path.Combine(source.FullName, "nested", "new.txt"), "nested");

            var manifest = new LegacyModManifest
            {
                Guid = Guid.NewGuid(),
                Name = "Mod",
                Description = "Original",
            };
            ModManifest.SaveToFile(manifest, current);
            using var database = CreateDatabase(storage);
            var hashes = new FileHashRepository(database);
            var service = new ModDirectoryService(new FileHashService(hashes), hashes, NullLogger<ModDirectoryService>.Instance);
            var result = await service.UpdateFromDirectoryAsync(current, source, manifest, manifest.Guid, true);

            Assert.IsTrue(result.Succeeded);
            Assert.IsTrue(result.Value!.Comparison.ChangedFiles.Contains("changed.txt"));
            Assert.IsTrue(result.Value.Comparison.DeletedFiles.Contains("old.txt"));
            Assert.AreEqual("new content", await File.ReadAllTextAsync(Path.Combine(current.FullName, "changed.txt")));
            Assert.AreEqual("nested", await File.ReadAllTextAsync(Path.Combine(current.FullName, "nested", "new.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(current.FullName, "old.txt")));
            Assert.AreEqual(manifest.Guid, ModManifest.DeserializeFromDirectory(current).Guid);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task UpdateFromDirectory_ShouldReportStageAndFileProgress()
    {
        var root = CreateTempDirectory();
        try
        {
            var storage = Directory.CreateDirectory(Path.Combine(root, "storage"));
            var current = Directory.CreateDirectory(Path.Combine(storage.FullName, "Mods", "Mod"));
            var source = Directory.CreateDirectory(Path.Combine(root, "update"));
            await File.WriteAllTextAsync(Path.Combine(current.FullName, "same.txt"), "same");
            await File.WriteAllTextAsync(Path.Combine(source.FullName, "same.txt"), "same");
            await File.WriteAllTextAsync(Path.Combine(source.FullName, "new.txt"), "new");
            var manifest = new LegacyModManifest { Guid = Guid.NewGuid(), Name = "Mod", Description = string.Empty };
            ModManifest.SaveToFile(manifest, current);
            using var database = CreateDatabase(storage);
            var hashes = new FileHashRepository(database);
            var service = new ModDirectoryService(new FileHashService(hashes), hashes, NullLogger<ModDirectoryService>.Instance);
            var updates = new List<ModUpdateProgress>();
            var progress = new Progress<ModUpdateProgress>(updates.Add);

            await service.UpdateFromDirectoryAsync(
                current,
                source,
                manifest,
                manifest.Guid,
                true,
                progress: progress);

            Assert.IsTrue(updates.Any(static item => item.Stage == ModUpdateStage.HashingCurrent));
            Assert.IsTrue(updates.Any(static item => item.Stage == ModUpdateStage.HashingNew));
            Assert.IsTrue(updates.Any(static item => item.Stage == ModUpdateStage.Comparing));
            var fileUpdates = updates.Where(static item => item.Stage == ModUpdateStage.Updating && item.CurrentFile is not null).ToArray();
            Assert.AreEqual(1, fileUpdates.Single().ProcessedCount);
            Assert.AreEqual(1, fileUpdates.Single().TotalCount);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task DeletePermanent_ShouldRemoveDirectoryAndCache()
    {
        var root = CreateTempDirectory();
        try
        {
            var storage = Directory.CreateDirectory(Path.Combine(root, "storage"));
            var mod = Directory.CreateDirectory(Path.Combine(storage.FullName, "Mods", "Mod"));
            await File.WriteAllTextAsync(Path.Combine(mod.FullName, "file.txt"), "content");
            var guid = Guid.NewGuid();
            using var database = new Database(Path.Combine(root, "database.db"));
            await database.InitializeAsync();
            var hashes = new FileHashRepository(database);
            await hashes.ReplaceForModAsync(guid, [new(guid, "file.txt", "hash", 7, DateTimeOffset.UtcNow)]);
            var service = new ModDirectoryService(new FileHashService(hashes), hashes, NullLogger<ModDirectoryService>.Instance);

            var result = await service.DeleteAsync(mod, storage, guid, false);

            Assert.IsTrue(result.Succeeded);
            Assert.IsFalse(Directory.Exists(mod.FullName));
            Assert.AreEqual(0, (await hashes.LoadForModAsync(guid)).Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task DeleteToRecycleDelegate_ShouldMoveDirectoryAndDeleteCache()
    {
        var root = CreateTempDirectory();
        try
        {
            var storage = Directory.CreateDirectory(Path.Combine(root, "storage"));
            var mod = Directory.CreateDirectory(Path.Combine(storage.FullName, "Mods", "Mod"));
            await File.WriteAllTextAsync(Path.Combine(mod.FullName, "file.txt"), "content");
            var guid = Guid.NewGuid();
            using var database = new Database(Path.Combine(root, "database.db"));
            await database.InitializeAsync();
            var hashes = new FileHashRepository(database);
            await hashes.ReplaceForModAsync(guid, [new(guid, "file.txt", "hash", 7, DateTimeOffset.UtcNow)]);
            var service = new ModDirectoryService(new FileHashService(hashes), hashes, NullLogger<ModDirectoryService>.Instance);
            var recycleRoot = Directory.CreateDirectory(Path.Combine(root, "recycle"));

            var result = await service.DeleteAsync(
                mod,
                storage,
                guid,
                true,
                (path, _) =>
                {
                    Directory.Move(path, Path.Combine(recycleRoot.FullName, "Mod"));
                    return Task.CompletedTask;
                });

            Assert.IsTrue(result.Succeeded);
            Assert.IsFalse(Directory.Exists(mod.FullName));
            Assert.IsTrue(File.Exists(Path.Combine(recycleRoot.FullName, "Mod", "file.txt")));
            Assert.AreEqual(0, (await hashes.LoadForModAsync(guid)).Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void PatchFileRules_ShouldMatchLegacyCompanionNames()
    {
        Assert.IsTrue(PatchFileRules.IsPatchFile("0123456789abcdef.patch_12"));
        Assert.IsTrue(PatchFileRules.IsPatchFile("0123456789abcdef.patch_12.stream"));
        Assert.IsTrue(PatchFileRules.TryParse("0123456789abcdef.patch_12.gpu_resources", out var parsed));
        Assert.AreEqual(PatchFileKind.GpuResources, parsed.Kind);
        Assert.AreEqual(12, parsed.Index);
        Assert.IsFalse(PatchFileRules.IsPatchFile("UPPER.patch_0"));
    }

    private static Database CreateDatabase(DirectoryInfo storage)
    {
        var database = new Database(Path.Combine(storage.FullName, $"hd2mm-{Guid.NewGuid():N}.db"));
        database.InitializeAsync().GetAwaiter().GetResult();
        return database;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hd2mm-mods-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
