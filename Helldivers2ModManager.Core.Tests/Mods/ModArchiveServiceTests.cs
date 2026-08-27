using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Mods;

[TestClass]
public sealed class ModArchiveServiceTests
{
    [TestMethod]
    public async Task ExportAndImportZip_ShouldExcludeBackupsAndPreserveManifest()
    {
        var root = CreateTempDirectory();
        try
        {
            var storage = Directory.CreateDirectory(Path.Combine(root, "storage"));
            var modDirectory = CreateMod(storage, "Source Mod");
            await File.WriteAllTextAsync(Path.Combine(modDirectory.FullName, "keep.txt"), "keep");
            await File.WriteAllTextAsync(Path.Combine(modDirectory.FullName, "old.hd2mm-backup"), "backup");
            var archivePath = Path.Combine(root, "source.zip");
            using var database = CreateDatabase(root);
            var hashes = new FileHashRepository(database);
            var directories = new ModDirectoryService(new FileHashService(hashes), hashes, NullLogger<ModDirectoryService>.Instance);
            var service = new ModArchiveService(directories, NullLogger<ModArchiveService>.Instance);

            await service.ExportAsync(modDirectory, archivePath, ArchiveExportFormat.Zip);
            var import = await service.ImportArchiveAsync(
                new FileInfo(archivePath),
                new DirectoryInfo(Path.Combine(root, "destination")),
                new DirectoryInfo(Path.Combine(root, "temp")));

            Assert.IsTrue(File.Exists(archivePath));
            Assert.AreEqual(1, import.ImportedMods.Count);
            Assert.AreEqual(0, import.Problems.Count);
            var imported = import.ImportedMods[0].Directory;
            Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Combine(imported.FullName, "keep.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(imported.FullName, "old.hd2mm-backup")));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "temp", Path.GetFileNameWithoutExtension(archivePath) + "_0")));

            await File.WriteAllTextAsync(Path.Combine(imported.FullName, "keep.txt"), "changed");
            var replacement = await service.ImportArchiveAsync(
                new FileInfo(archivePath),
                new DirectoryInfo(Path.Combine(root, "destination")),
                new DirectoryInfo(Path.Combine(root, "temp")),
                deleteExistingToRecycleBin: false);
            Assert.AreEqual(1, replacement.ImportedMods.Count);
            Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Combine(replacement.ImportedMods[0].Directory.FullName, "keep.txt")));

            var duplicate = await service.ImportArchiveAsync(
                new FileInfo(archivePath),
                new DirectoryInfo(Path.Combine(root, "destination")),
                new DirectoryInfo(Path.Combine(root, "temp")));
            Assert.AreEqual(0, duplicate.ImportedMods.Count);
            Assert.AreEqual(ArchiveImportProblemKind.Duplicate, duplicate.Problems.Single().Kind);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ImportNestedArchive_ShouldImportInnerMods()
    {
        var root = CreateTempDirectory();
        try
        {
            var storage = Directory.CreateDirectory(Path.Combine(root, "storage"));
            var innerDirectory = Directory.CreateDirectory(Path.Combine(root, "inner"));
            await File.WriteAllTextAsync(Path.Combine(innerDirectory.FullName, "manifest.json"), """{"Version":1,"Guid":"00000000-0000-0000-0000-000000000002","Name":"Inner","Description":"Nested"}""");
            await File.WriteAllTextAsync(Path.Combine(innerDirectory.FullName, "content.txt"), "inner content");
            var nestedPath = Path.Combine(root, "nested.zip");
            CreateZip([(Path.GetFileName(innerDirectory.FullName) + "/manifest.json", await File.ReadAllBytesAsync(Path.Combine(innerDirectory.FullName, "manifest.json"))), (Path.GetFileName(innerDirectory.FullName) + "/content.txt", "inner content"u8.ToArray())], nestedPath);

            var outerPath = Path.Combine(root, "outer.zip");
            CreateZip([("packages/nested.zip", await File.ReadAllBytesAsync(nestedPath))], outerPath);
            using var database = CreateDatabase(root);
            var hashes = new FileHashRepository(database);
            var directories = new ModDirectoryService(new FileHashService(hashes), hashes, NullLogger<ModDirectoryService>.Instance);
            var service = new ModArchiveService(directories, NullLogger<ModArchiveService>.Instance);
            var result = await service.ImportArchiveAsync(
                new FileInfo(outerPath),
                storage,
                new DirectoryInfo(Path.Combine(root, "temp")));

            Assert.AreEqual(1, result.ImportedMods.Count);
            Assert.AreEqual("Inner", result.ImportedMods[0].Manifest.Name);
            Assert.AreEqual("inner content", await File.ReadAllTextAsync(Path.Combine(result.ImportedMods[0].Directory.FullName, "content.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task PrepareUpdateSource_ShouldCleanExtractFlattenAndExposeManifest()
    {
        var root = CreateTempDirectory();
        try
        {
            var destination = Directory.CreateDirectory(Path.Combine(root, "source"));
            await File.WriteAllTextAsync(Path.Combine(destination.FullName, "stale.txt"), "stale");
            var manifestPath = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, """{"Version":1,"Guid":"00000000-0000-0000-0000-000000000003","Name":"Update","Description":""}""");
            var archivePath = Path.Combine(root, "update.zip");
            CreateZip(
            [
                ("root/manifest.json", await File.ReadAllBytesAsync(manifestPath)),
                ("root/data.txt", "updated"u8.ToArray()),
            ],
            archivePath);
            using var database = CreateDatabase(root);
            var hashes = new FileHashRepository(database);
            var directories = new ModDirectoryService(new FileHashService(hashes), hashes, NullLogger<ModDirectoryService>.Instance);
            var service = new ModArchiveService(directories, NullLogger<ModArchiveService>.Instance);

            await service.PrepareUpdateSourceAsync(new FileInfo(archivePath), destination);

            Assert.IsTrue(File.Exists(Path.Combine(destination.FullName, "manifest.json")));
            Assert.AreEqual("updated", await File.ReadAllTextAsync(Path.Combine(destination.FullName, "data.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(destination.FullName, "stale.txt")));
            Assert.IsFalse(Directory.Exists(Path.Combine(destination.FullName, "root")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ExportSevenZipFast_ShouldCreateNonEmptyArchive()
    {
        var root = CreateTempDirectory();
        try
        {
            var storage = Directory.CreateDirectory(Path.Combine(root, "storage"));
            var modDirectory = CreateMod(storage, "Seven Zip Mod");
            await File.WriteAllTextAsync(Path.Combine(modDirectory.FullName, "data.txt"), new string('a', 10_000));
            var archivePath = Path.Combine(root, "data.7z");
            using var database = CreateDatabase(root);
            var hashes = new FileHashRepository(database);
            var directories = new ModDirectoryService(new FileHashService(hashes), hashes, NullLogger<ModDirectoryService>.Instance);
            var archiveService = new ModArchiveService(directories, NullLogger<ModArchiveService>.Instance);
            await archiveService.ExportAsync(modDirectory, archivePath, ArchiveExportFormat.SevenZipFast);

            Assert.IsTrue(new FileInfo(archivePath).Length > 0);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CalculateExportSize_ShouldSkipArchivesAndBackups()
    {
        var root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "model.patch_0"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "old.zip"), [4]);
            File.WriteAllBytes(Path.Combine(root, "patch.hd2mm-backup"), [5]);
            Assert.AreEqual(3, ModArchiveService.CalculateExportSize(new DirectoryInfo(root)));
        }
        finally { Directory.Delete(root, true); }
    }

    private static Database CreateDatabase(string root)
    {
        var database = new Database(Path.Combine(root, $"archive-{Guid.NewGuid():N}.db"));
        database.InitializeAsync().GetAwaiter().GetResult();
        return database;
    }

    private static DirectoryInfo CreateMod(DirectoryInfo storage, string name)
    {
        var directory = Directory.CreateDirectory(Path.Combine(storage.FullName, "Mods", name));
        var manifest = new LegacyModManifest
        {
            Guid = Guid.NewGuid(),
            Name = name,
            Description = "Test mod",
        };
        ModManifest.SaveToFile(manifest, directory);
        return directory;
    }

    private static void CreateZip(IReadOnlyList<(string Name, byte[] Content)> entries, string path)
    {
        using var stream = new FileStream(path, FileMode.Create);
        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            var zipEntry = archive.CreateEntry(entry.Name);
            using var entryStream = zipEntry.Open();
            entryStream.Write(entry.Content);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hd2mm-archive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public TestContext TestContext { get; set; } = null!;
}
