using Helldivers2ModManager.Core.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Persistence;

[TestClass]
public sealed class PersistenceTests
{
    [TestMethod]
    public async Task Database_ShouldCreateWalSchema()
    {
        var path = CreateTempDatabasePath();
        try
        {
            using var database = new Database(path);
            await database.InitializeAsync();
            var results = await database.ExecuteAsync(async connection =>
            {
                var values = new Dictionary<string, long>();
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA user_version;";
                    var reader = await command.ExecuteReaderAsync();
                    Assert.IsTrue(await reader.ReadAsync());
                    values["userVersion"] = reader.GetInt64(0);
                    values["integrity"] = 1;
                    await reader.DisposeAsync();
                }

                await using var tables = connection.CreateCommand();
                tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('profiles','mod_groups','mod_states','file_hashes','version_results','json_cache','preferences')";
                values["tables"] = (long)(await tables.ExecuteScalarAsync())!;
                return values;
            });

            Assert.AreEqual(2L, results["userVersion"]);
            Assert.AreEqual(1L, results["integrity"]);
            Assert.AreEqual(7L, results["tables"]);
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    public async Task Preferences_And_BootConfiguration_ShouldRoundTrip()
    {
        var path = CreateTempDatabasePath();
        var bootPath = Path.Combine(Path.GetTempPath(), "hd2mm-boot-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var database = new Database(path);
            var repository = new PreferenceRepository(database);
            var modId = Guid.NewGuid();
            var settings = new AppSettings
            {
                StorageDirectory = @"D:\Mods",
                OptionOrders = new Dictionary<Guid, int[]> { [modId] = [2, 0, 1] },
                Tags = [new TagSetting(Guid.NewGuid(), "Armor", "#FFFFFFFF")],
            };
            await repository.SetAppSettingsAsync("settings", settings);
            var loaded = await repository.GetAppSettingsAsync("settings");
            Assert.IsNotNull(loaded);
            Assert.AreEqual(settings.StorageDirectory, loaded.StorageDirectory);
            Assert.AreEqual(modId, loaded.OptionOrders.Single().Key);
            CollectionAssert.AreEqual(new[] { 2, 0, 1 }, loaded.OptionOrders[modId]);
            Assert.AreEqual("Armor", loaded.Tags[0].Name);

            BootConfigurationStore.Save(new BootConfiguration { StorageDirectory = @"D:\Mods" }, bootPath);
            Assert.AreEqual(@"D:\Mods", BootConfigurationStore.Load(bootPath)!.StorageDirectory);

            await File.WriteAllTextAsync(bootPath, "{ invalid");
            await Assert.ThrowsExceptionAsync<System.Text.Json.JsonException>(() => BootConfigurationStore.LoadAsync(bootPath));
        }
        finally
        {
            DeleteDatabase(path);
            if (File.Exists(bootPath)) File.Delete(bootPath);
        }
    }

    [TestMethod]
    public async Task Repositories_ShouldPersistDomainRecords()
    {
        var path = CreateTempDatabasePath();
        try
        {
            using var database = new Database(path);
            var profiles = new ProfileRepository(database);
            var profileId = Guid.NewGuid(); var groupId = Guid.NewGuid(); var modId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            await profiles.SaveAsync(new ProfileSnapshot(profileId, "Test", true, now, now,
                [new ProfileGroupRecord(groupId, "Group A", 2, now)],
                [new ProfileModState(modId, true, groupId, 7, """{"selected":[1]}""")]));

            var profile = await profiles.LoadAsync(profileId);
            Assert.IsNotNull(profile);
            Assert.AreEqual("Group A", profile.Groups[0].Name);
            Assert.AreEqual(7, profile.Mods[0].SortOrder);
            Assert.AreEqual(groupId, profile.Mods[0].GroupId);
            Assert.AreEqual("""{"selected":[1]}""", profile.Mods[0].StateJson);
            Assert.AreEqual(profileId, (await profiles.LoadDefaultAsync())!.Id);

            var hashes = new FileHashRepository(database);
            await hashes.ReplaceForModAsync(modId, [new FileHashRecord(modId, @"mods\a.patch_0", "sha256", 128, now)]);
            Assert.AreEqual("sha256", (await hashes.LoadForModAsync(modId)).Single().FileHash);

            var versions = new VersionResultRepository(database);
            await versions.SaveAsync(new VersionResultRecord(modId, 2, """{"ok":true}""", now, now));
            Assert.AreEqual(2, (await versions.LoadAllAsync()).Single().Status);

            var cache = new JsonCacheRepository(database);
            await cache.SetAsync("conflicts", "cache-key", """{"items":[]}""");
            Assert.AreEqual("""{"items":[]}""", await cache.GetAsync("conflicts", "cache-key"));
            await cache.DeleteCategoryAsync("conflicts");
            Assert.IsNull(await cache.GetAsync("conflicts", "cache-key"));
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    public void MissingBootConfiguration_ShouldNotTouchLegacySettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2mm-boot-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var legacyPath = Path.Combine(root, "settings.json");
            const string legacyJson = "{\"legacy\":true}";
            File.WriteAllText(legacyPath, legacyJson);
            Assert.IsNull(BootConfigurationStore.Load(Path.Combine(root, "boot.json")));
            Assert.AreEqual(legacyJson, File.ReadAllText(legacyPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateTempDatabasePath() =>
        Path.Combine(Path.GetTempPath(), "hd2mm-persistence-" + Guid.NewGuid().ToString("N") + ".db");

    private static void DeleteDatabase(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(file)) File.Delete(file);
    }
}
