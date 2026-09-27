using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Tests;

[TestClass]
public sealed class ConflictScanIntegrationTests
{
    [TestMethod]
    public async Task ConflictScanKeepsDeploymentOrderAndPersistsCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-conflicts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var database = new DatabaseService(NullLogger<DatabaseService>.Instance);
        ModService? modService = null;
        try
        {
            var settings = new SettingsService(NullLogger<SettingsService>.Instance,
                Path.Combine(root, "settings.json"), root);
            settings.InitDefault();
            settings.StorageDirectory = root;
            settings.GameDirectory = Path.Combine(root, "game");
            settings.TempDirectory = Path.Combine(root, "temp");
            var first = CreateMod(root, "First", 10800438);
            var second = CreateMod(root, "Second", 1);
            var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
                GetLanguageDirectory());
            var hashes = new ModHashService(NullLogger<ModHashService>.Instance,
                new FileHashRepository(NullLogger<FileHashRepository>.Instance, database),
                database, localization, new BackgroundTaskService(action => action(), () => true));
            modService = new ModService(NullLogger<ModService>.Instance, hashes, localization,
                new GameProcessService(NullLogger<GameProcessService>.Instance));
            modService.Init(settings);
            await modService.HashMigrationTask;
            var versionCheck = new VersionCheckService(
                NullLogger<VersionCheckService>.Instance, settings, localization);
            var conflicts = new ModConflictService(NullLogger<ModConflictService>.Instance,
                modService, versionCheck);

            var ordered = new[] { first, second };
            var result = await conflicts.AnalyzeAsync(ordered);
            Assert.AreEqual(2, result.ScannedModCount);
            Assert.AreEqual(2, result.ScannedPatchCount);
            Assert.AreEqual(2, result.ScannedUnitCount);
            Assert.AreEqual(1, result.Conflicts.Count);
            Assert.IsTrue(result.Conflicts[0].IsDefiniteConflict);
            Assert.AreEqual(second.Manifest.Guid, result.Conflicts[0].Winner.ModGuid);

            var key = conflicts.BuildCacheKey(ordered);
            Assert.AreNotEqual(key, conflicts.BuildCacheKey([second, first]));
            var repository = new ModConflictRepository(
                NullLogger<ModConflictRepository>.Instance, database);
            await repository.SaveAsync(root, key, result);
            var restored = repository.Load(root, key);
            Assert.IsNotNull(restored);
            Assert.AreEqual(second.Manifest.Guid, restored.Conflicts[0].Winner.ModGuid);
            Assert.IsNull(repository.Load(root, "another-profile"));
        }
        finally
        {
            if (modService is not null)
                await modService.HashMigrationTask;
            database.Dispose();
            SqliteConnection.ClearAllPools();
            var target = Path.GetFullPath(root);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            if (!target.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("hd2mm-jalium-conflicts-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test cleanup path.");
            Directory.Delete(target, recursive: true);
        }
    }

    private static ModData CreateMod(string root, string name, uint version)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "Mods", name));
        var manifest = new LegacyModManifest { Guid = Guid.NewGuid(), Name = name,
            Description = string.Empty, Options = [] };
        ModManifest.SaveToFile(manifest, directory);
        var bytes = new byte[72 + 32 + 80 + 0x30];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0), unchecked((int)0xF0000011));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);
        const int entry = 72 + 32;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(entry), 0x123456789ABCDEF);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(entry + 8),
            unchecked((long)16187218042980615487UL));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(entry + 16), bytes.Length - 0x30);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry + 56), 0x30);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), version);
        File.WriteAllBytes(Path.Combine(directory.FullName, "0011223344556677.patch_0"), bytes);
        return new ModData(directory, manifest);
    }

    private static string GetLanguageDirectory([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!);
             current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language");
        throw new DirectoryNotFoundException("Repository language files not found.");
    }
}
