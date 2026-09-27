using System.Text.Json;
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Tests;

[TestClass]
public sealed class DashboardLibraryServiceTests
{
    private string _root = null!;
    private DatabaseService _database = null!;
    private EnabledDataRepository _repository = null!;
    private ModCatalogService _catalog = null!;
    private DashboardLibraryService _library = null!;
    private SettingsService _settings = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-library-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _database = new DatabaseService(NullLogger<DatabaseService>.Instance);
        _repository = new EnabledDataRepository(NullLogger<EnabledDataRepository>.Instance, _database);
        _catalog = new ModCatalogService(NullLogger<ModCatalogService>.Instance);
        _library = new DashboardLibraryService(_catalog, _repository, NullLogger<DashboardLibraryService>.Instance);
        _settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(_root, "settings.json"), _root);
        _settings.InitDefault();
        _settings.StorageDirectory = _root;
    }

    [TestCleanup]
    public void Cleanup()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hd2mm-jalium-library-tests"))
            + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(_root);
        if (!resolved.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its dedicated directory.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(resolved))
                    Directory.Delete(resolved, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50);
            }
        }
    }

    [TestMethod]
    public async Task Catalog_AppliesCurrentPathRulesAndRemovesManifestlessFolders()
    {
        var valid = CreateLegacy("valid", Guid.NewGuid(), options: ["optional-missing"]);
        var duplicate = CreateLegacy("duplicate", valid.Guid);
        var invalid = CreateV1("invalid", Guid.NewGuid(), ["../outside"]);
        var missing = Directory.CreateDirectory(Path.Combine(_root, "Mods", "missing"));
        var broken = Directory.CreateDirectory(Path.Combine(_root, "Mods", "broken"));
        File.WriteAllText(Path.Combine(broken.FullName, "manifest.json"), "{");

        var result = await _catalog.LoadAsync(_root);

        Assert.AreEqual(1, result.Mods.Count);
        Assert.AreEqual(valid.Guid, result.Mods[0].Manifest.Guid);
        Assert.IsTrue(result.Problems.Any(problem =>
            (problem.Directory.Name == duplicate.Directory.Name || problem.Directory.Name == valid.Directory.Name)
            && problem.Kind == ModProblemKind.Duplicate));
        Assert.IsTrue(result.Problems.Any(problem => problem.Directory.Name == invalid.Directory.Name
            && problem.Kind == ModProblemKind.InvalidPath));
        Assert.IsTrue(result.Problems.Any(problem => problem.Directory.Name == missing.Name
            && problem.Kind == ModProblemKind.NoManifestFound));
        Assert.IsTrue(result.Problems.Any(problem => problem.Directory.Name == broken.Name
            && problem.Kind == ModProblemKind.CantParseManifest));
        Assert.IsFalse(Directory.Exists(missing.FullName));
    }

    [TestMethod]
    public async Task Catalog_CacheInvalidatesWhenManifestChanges()
    {
        var mod = CreateLegacy("before", Guid.NewGuid());
        var first = await _catalog.LoadAsync(_root);
        Assert.AreEqual("before", first.Mods.Single().Manifest.Name);
        Assert.IsTrue(File.Exists(Path.Combine(_root, "cache", "manifest_parse_cache.json")));

        ModManifest.SaveToFile(new LegacyModManifest
        {
            Guid = mod.Guid,
            Name = "after-longer-name",
            Description = string.Empty,
        }, mod.Directory);
        var second = await _catalog.LoadAsync(_root);

        Assert.AreEqual("after-longer-name", second.Mods.Single().Manifest.Name);
    }

    [TestMethod]
    public async Task Catalog_StaleMissingManifestCacheDoesNotSkipStartupCleanup()
    {
        var missing = Directory.CreateDirectory(Path.Combine(_root, "Mods", "missing"));
        var cache = new ManifestParseCache(NullLogger.Instance);
        cache.Store(missing.Name, new CachedModEntry
        {
            Fingerprint = ManifestParseCache.ComputeFingerprint(missing),
            FailureKind = (int)ModProblemKind.NoManifestFound,
        });
        cache.Save(_root);

        var result = await _catalog.LoadAsync(_root);

        Assert.IsFalse(Directory.Exists(missing.FullName));
        Assert.IsTrue(result.Problems.Any(problem => problem.Kind == ModProblemKind.NoManifestFound));
    }

    [TestMethod]
    public async Task Library_RestoresSqliteOrderAndEnabledState()
    {
        var first = CreateLegacy("first", Guid.NewGuid());
        var second = CreateLegacy("second", Guid.NewGuid());
        await _repository.SaveAllAsync(_root,
        [
            new EnabledData { Guid = second.Guid, Enabled = false, Toggled = [], Selected = [0] },
            new EnabledData { Guid = first.Guid, Enabled = true, Toggled = [], Selected = [0] },
        ]);

        var result = await _library.LoadAsync(_settings);

        CollectionAssert.AreEqual(new[] { second.Guid, first.Guid },
            result.Mods.Select(mod => mod.Manifest.Guid).ToArray());
        Assert.IsFalse(result.Mods[0].Enabled);
        Assert.AreEqual(0, result.Problems.Count);
    }

    [TestMethod]
    public async Task Library_MigratesLegacyProfileAndBacksUpOriginal()
    {
        var mod = CreateLegacy("legacy", Guid.NewGuid());
        var legacyPath = Path.Combine(_root, "enabled.json");
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new[]
        {
            new { mod.Guid, Enabled = false, Toggled = Array.Empty<bool>(), Selected = new[] { 0 } },
        }));

        var result = await _library.LoadAsync(_settings);

        Assert.IsFalse(result.Mods.Single().Enabled);
        Assert.AreEqual(1, _repository.GetCount(_root));
        Assert.IsFalse(File.Exists(legacyPath));
        Assert.IsTrue(File.Exists(legacyPath + ".bak"));
    }

    [TestMethod]
    public async Task Groups_RestoreSelectedWorkspaceAndPreserveMemberOrder()
    {
        var first = CreateLegacy("first", Guid.NewGuid());
        var second = CreateLegacy("second", Guid.NewGuid());
        var mods = (await _library.LoadAsync(_settings)).Mods;
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            Path.Combine(_root, "Language"));
        var groupsRepository = new ModGroupRepository(NullLogger<ModGroupRepository>.Instance, _database);
        var groups = new ModGroupService(NullLogger<ModGroupService>.Instance, groupsRepository, localization);
        await groups.InitAsync(_settings, mods);

        var custom = await groups.CreateGroupAsync("custom");
        await groups.AddModsToGroupAsync(custom.Id,
            [mods.Single(mod => mod.Manifest.Guid == second.Guid), mods.Single(mod => mod.Manifest.Guid == first.Guid)]);
        await groups.SelectGroupAsync(custom.Id, mods);
        CollectionAssert.AreEqual(new[] { second.Guid, first.Guid },
            groups.FilterMods(mods).Select(mod => mod.Manifest.Guid).ToArray());

        var restarted = new ModGroupService(NullLogger<ModGroupService>.Instance, groupsRepository, localization);
        await restarted.InitAsync(_settings, mods);
        Assert.AreEqual(custom.Id, restarted.SelectedGroup.Id);
        CollectionAssert.AreEqual(new[] { second.Guid, first.Guid },
            restarted.FilterMods(mods).Select(mod => mod.Manifest.Guid).ToArray());
    }

    [TestMethod]
    public async Task Workspace_SearchHidesSeparatorsButSelectAllUsesCurrentCatalog()
    {
        CreateLegacy("宁夏", Guid.NewGuid());
        CreateLegacy("Alpha", Guid.NewGuid());
        _settings.ShowSeparator = true;
        _settings.Separators.Add(new ModSeparator { Name = "section", DisplayIndex = 1 });
        var workspace = await OpenWorkspaceAsync();

        Assert.AreEqual(3, workspace.Rows.Count);
        workspace.SetSearchText("nx");
        Assert.AreEqual(1, workspace.Rows.Count);
        Assert.AreEqual("宁夏", ((ModData)workspace.Rows[0]).Manifest.Name);
        workspace.SelectAll();
        Assert.AreEqual(2, workspace.SelectedGuids.Count);
        Assert.AreEqual(2, workspace.SelectedMods.Count);
        workspace.SetSearchText(string.Empty);
        Assert.AreEqual(3, workspace.Rows.Count);
        Assert.IsInstanceOfType<ModSeparator>(workspace.Rows[1]);
    }

    [TestMethod]
    public async Task Workspace_SelectRangeSupportsReplacementAndAdditiveRanges()
    {
        CreateLegacy("Alpha", Guid.NewGuid());
        CreateLegacy("Bravo", Guid.NewGuid());
        CreateLegacy("Charlie", Guid.NewGuid());
        var workspace = await OpenWorkspaceAsync();
        var visible = workspace.Mods.ToArray();

        workspace.SelectRange(visible[1], visible[2], additive: false);
        CollectionAssert.AreEqual(visible.Skip(1).Take(2).Select(mod => mod.Manifest.Guid).ToArray(),
            workspace.SelectedMods.Select(mod => mod.Manifest.Guid).ToArray());

        workspace.SelectRange(visible[0], visible[0], additive: true);
        CollectionAssert.AreEqual(visible.Select(mod => mod.Manifest.Guid).ToArray(),
            workspace.SelectedMods.Select(mod => mod.Manifest.Guid).ToArray());
    }

    [TestMethod]
    public async Task Workspace_CustomGroupStateDoesNotOverwriteDefaultProfile()
    {
        var first = CreateLegacy("first", Guid.NewGuid());
        CreateLegacy("second", Guid.NewGuid());
        var workspace = await OpenWorkspaceAsync();
        var custom = await workspace.CreateGroupAsync("custom");
        var mod = workspace.Mods.Single(item => item.Manifest.Guid == first.Guid);
        await workspace.AddToGroupAsync(custom.Id, [mod]);
        await workspace.SelectGroupAsync(custom.Id);
        await workspace.SetEnabledAsync(mod, false);

        Assert.IsTrue(_repository.LoadAll(_root).Single(data => data.Guid == first.Guid).Enabled);
        await workspace.SelectGroupAsync(ModGroup.DefaultGroupId);
        Assert.IsTrue(mod.Enabled);
        await workspace.SelectGroupAsync(custom.Id);
        Assert.IsFalse(mod.Enabled);
        Assert.AreEqual(1, workspace.Rows.Count);
        workspace.SetMode(DashboardCatalogMode.Library);
        Assert.AreEqual(2, workspace.Rows.Count);
    }

    [TestMethod]
    public async Task Workspace_MovePersistsDefaultAndCustomOrders()
    {
        var first = CreateLegacy("first", Guid.NewGuid());
        var second = CreateLegacy("second", Guid.NewGuid());
        var third = CreateLegacy("third", Guid.NewGuid());
        var workspace = await OpenWorkspaceAsync();
        var thirdMod = workspace.Mods.Single(mod => mod.Manifest.Guid == third.Guid);
        await workspace.MoveAsync(thirdMod, 0);
        Assert.AreEqual(third.Guid, _repository.LoadAll(_root)[0].Guid);

        var custom = await workspace.CreateGroupAsync("custom");
        await workspace.AddToGroupAsync(custom.Id,
            [workspace.Mods.Single(mod => mod.Manifest.Guid == first.Guid),
             workspace.Mods.Single(mod => mod.Manifest.Guid == second.Guid)]);
        await workspace.SelectGroupAsync(custom.Id);
        await workspace.MoveAsync(workspace.Mods.Single(mod => mod.Manifest.Guid == second.Guid), 0);

        var reloaded = await OpenWorkspaceAsync();
        Assert.AreEqual(custom.Id, reloaded.Groups.SelectedGroup.Id);
        CollectionAssert.AreEqual(new[] { second.Guid, first.Guid },
            reloaded.Rows.OfType<ModData>().Select(mod => mod.Manifest.Guid).ToArray());
    }

    [TestMethod]
    public async Task Workspace_MoveKeepsSeparatorAtItsDisplayPosition()
    {
        var first = CreateLegacy("first", Guid.NewGuid());
        CreateLegacy("second", Guid.NewGuid());
        var third = CreateLegacy("third", Guid.NewGuid());
        _settings.ShowSeparator = true;
        var separator = new ModSeparator { Name = "section", DisplayIndex = 1 };
        _settings.Separators.Add(separator);
        var workspace = await OpenWorkspaceAsync();

        await workspace.MoveToTopAsync(workspace.Mods.Single(mod => mod.Manifest.Guid == third.Guid));

        Assert.AreEqual(2, separator.DisplayIndex);
        Assert.AreEqual(2, workspace.Rows.ToList().IndexOf(separator));
        CollectionAssert.AreEqual(new[] { third.Guid, first.Guid },
            workspace.Rows.OfType<ModData>().Take(2).Select(mod => mod.Manifest.Guid).ToArray());
    }

    [TestMethod]
    public async Task Workspace_FailedCustomMoveRestoresMemoryAndDatabaseOrder()
    {
        var first = CreateLegacy("first", Guid.NewGuid());
        var second = CreateLegacy("second", Guid.NewGuid());
        var workspace = await OpenWorkspaceAsync();
        var custom = await workspace.CreateGroupAsync("custom");
        await workspace.AddToGroupAsync(custom.Id, workspace.Mods);
        await workspace.SelectGroupAsync(custom.Id);

        using (var connection = _database.OpenConnection(_root))
        using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = $"""
                CREATE TRIGGER reject_group_reorder BEFORE INSERT ON mod_groups
                WHEN NEW.Id = '{custom.Id}'
                BEGIN SELECT RAISE(ABORT, 'test write failure'); END;
                """;
            trigger.ExecuteNonQuery();
        }

        try
        {
            var secondMod = workspace.Mods.Single(mod => mod.Manifest.Guid == second.Guid);
            await Assert.ThrowsExceptionAsync<SqliteException>(() => workspace.MoveAsync(secondMod, 0));
            CollectionAssert.AreEqual(new[] { first.Guid, second.Guid },
                workspace.Rows.OfType<ModData>().Select(mod => mod.Manifest.Guid).ToArray());
            var repository = new ModGroupRepository(NullLogger<ModGroupRepository>.Instance, _database);
            CollectionAssert.AreEqual(new[] { first.Guid, second.Guid },
                repository.LoadGroups(_root).Single(group => group.Id == custom.Id).ModGuids.ToArray());
        }
        finally
        {
            using var connection = _database.OpenConnection(_root);
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER reject_group_reorder;";
            command.ExecuteNonQuery();
        }
    }

    [TestMethod]
    public async Task Workspace_GroupSelectionAddsInLibraryAndReplacesInWorkspace()
    {
        var first = CreateLegacy("first", Guid.NewGuid());
        var workspace = await OpenWorkspaceAsync();
        var mod = workspace.Mods.Single(item => item.Manifest.Guid == first.Guid);
        var firstGroup = await workspace.CreateGroupAsync("first group");
        var secondGroup = await workspace.CreateGroupAsync("second group");

        workspace.SetMode(DashboardCatalogMode.Library);
        await workspace.SetModsToGroupsAsync([firstGroup.Id], [mod]);
        await workspace.SetModsToGroupsAsync([secondGroup.Id], [mod]);
        Assert.IsTrue(firstGroup.ModGuids.Contains(first.Guid));
        Assert.IsTrue(secondGroup.ModGuids.Contains(first.Guid));

        workspace.SetMode(DashboardCatalogMode.Workspace);
        await workspace.SetModsToGroupsAsync([secondGroup.Id], [mod]);
        Assert.IsFalse(firstGroup.ModGuids.Contains(first.Guid));
        Assert.IsTrue(secondGroup.ModGuids.Contains(first.Guid));
        var repository = new ModGroupRepository(NullLogger<ModGroupRepository>.Instance, _database);
        var stored = repository.LoadGroups(_root);
        Assert.IsFalse(stored.Single(group => group.Id == firstGroup.Id).ModGuids.Contains(first.Guid));
        Assert.IsTrue(stored.Single(group => group.Id == secondGroup.Id).ModGuids.Contains(first.Guid));
    }

    [TestMethod]
    public async Task Workspace_TagsInCustomGroupPreserveDefaultStateAndOrder()
    {
        var first = CreateLegacy("first", Guid.NewGuid());
        var second = CreateLegacy("second", Guid.NewGuid());
        var workspace = await OpenWorkspaceAsync();
        var custom = await workspace.CreateGroupAsync("custom");
        await workspace.AddToGroupAsync(custom.Id, workspace.Mods);
        await workspace.SelectGroupAsync(custom.Id);
        var target = workspace.Mods.Single(mod => mod.Manifest.Guid == first.Guid);
        await workspace.SetEnabledAsync(target, false);
        var tag = new ModTag("test");
        _settings.Tags.Add(tag);

        await workspace.SetTagsAsync([target], [tag.Id]);

        var saved = _repository.LoadAll(_root);
        CollectionAssert.AreEqual(new[] { first.Guid, second.Guid }, saved.Select(data => data.Guid).ToArray());
        Assert.IsTrue(saved[0].Enabled);
        CollectionAssert.AreEqual(new[] { tag.Id }, saved[0].TagIds);
        await workspace.SelectGroupAsync(ModGroup.DefaultGroupId);
        Assert.IsTrue(target.Enabled);
        CollectionAssert.AreEqual(new[] { tag.Id }, target.TagIds);
    }

    [TestMethod]
    public async Task Workspace_TagsFromLibraryPersistForModsOutsideCurrentGroup()
    {
        var first = CreateLegacy("first", Guid.NewGuid());
        var second = CreateLegacy("second", Guid.NewGuid());
        var workspace = await OpenWorkspaceAsync();
        var custom = await workspace.CreateGroupAsync("custom");
        await workspace.AddToGroupAsync(custom.Id, [workspace.Mods.Single(mod => mod.Manifest.Guid == first.Guid)]);
        await workspace.SelectGroupAsync(custom.Id);
        workspace.SetMode(DashboardCatalogMode.Library);
        var tag = new ModTag("library");
        _settings.Tags.Add(tag);
        var target = workspace.Mods.Single(mod => mod.Manifest.Guid == second.Guid);

        await workspace.SetTagsAsync([target], [tag.Id]);

        CollectionAssert.AreEqual(new[] { tag.Id },
            _repository.LoadAll(_root).Single(data => data.Guid == second.Guid).TagIds);
        var restarted = await OpenWorkspaceAsync();
        CollectionAssert.AreEqual(new[] { tag.Id },
            restarted.Mods.Single(mod => mod.Manifest.Guid == second.Guid).TagIds);
    }

    [TestMethod]
    public async Task Workspace_FailedBatchTagRestoresMemoryAndDatabase()
    {
        CreateLegacy("first", Guid.NewGuid());
        var second = CreateLegacy("second", Guid.NewGuid());
        var workspace = await OpenWorkspaceAsync();
        await workspace.SaveCurrentAsync();
        var tag = new ModTag("test");
        _settings.Tags.Add(tag);
        using (var connection = _database.OpenConnection(_root))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                CREATE TRIGGER reject_tag_update BEFORE UPDATE ON enabled_mods
                WHEN NEW.Guid = '{second.Guid}'
                BEGIN SELECT RAISE(ABORT, 'test write failure'); END;
                """;
            command.ExecuteNonQuery();
        }
        try
        {
            await Assert.ThrowsExceptionAsync<SqliteException>(() =>
                workspace.SetTagsAsync(workspace.Mods, [tag.Id]));
            Assert.IsTrue(workspace.Mods.All(mod => mod.TagIds.Count == 0));
            Assert.IsTrue(_repository.LoadAll(_root).All(data => data.TagIds is null or { Count: 0 }));
        }
        finally
        {
            using var connection = _database.OpenConnection(_root);
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER reject_tag_update;";
            command.ExecuteNonQuery();
        }
    }

    [TestMethod]
    public async Task Workspace_AutoTagUsesManualPairingAndPersistsAcrossReload()
    {
        CreateLegacy("audio", Guid.NewGuid());
        var workspace = await OpenWorkspaceAsync();
        var tag = new ModTag("custom audio");
        _settings.Tags.Add(tag);
        _settings.AutoTagMappings.Add(new AutoTagMapping { Type = ModType.Audio, TagId = tag.Id });
        _settings.EnableAutoTagging = true;
        var mod = workspace.Mods.Single();
        var localization = CreateLocalization();
        var detector = new ModTypeDetectionService(NullLogger<ModTypeDetectionService>.Instance);

        var changed = await workspace.ApplyAutoTagsAsync(detector, localization,
            AudioDetection(mod));

        Assert.AreEqual(1, changed);
        CollectionAssert.AreEqual(new[] { tag.Id }, mod.TagIds.ToArray());
        CollectionAssert.AreEqual(new[] { tag.Id },
            _repository.LoadAll(_root).Single().TagIds!.ToArray());
        var reloaded = await OpenWorkspaceAsync();
        CollectionAssert.AreEqual(new[] { tag.Id }, reloaded.Mods.Single().TagIds.ToArray());
    }

    [TestMethod]
    public async Task Workspace_CancelledAutoTagDoesNotChangeTags()
    {
        CreateLegacy("audio", Guid.NewGuid());
        var workspace = await OpenWorkspaceAsync();
        _settings.EnableAutoTagging = true;
        _settings.AutoTagCreateMissingTags = true;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            workspace.ApplyAutoTagsAsync(
                new ModTypeDetectionService(NullLogger<ModTypeDetectionService>.Instance),
                CreateLocalization(), AudioDetection(workspace.Mods.Single()), cancellation.Token));

        Assert.AreEqual(0, _settings.Tags.Count);
        Assert.AreEqual(0, workspace.Mods.Single().TagIds.Count);
        Assert.AreEqual(0, _repository.LoadAll(_root).Count);
    }

    [TestMethod]
    public async Task Workspace_FailedAutoTagRestoresCreatedTagAndModState()
    {
        CreateLegacy("audio", Guid.NewGuid());
        var workspace = await OpenWorkspaceAsync();
        await workspace.SaveCurrentAsync();
        _settings.EnableAutoTagging = true;
        _settings.AutoTagCreateMissingTags = true;
        using (var connection = _database.OpenConnection(_root))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_auto_tags BEFORE UPDATE ON enabled_mods
                BEGIN SELECT RAISE(ABORT, 'test auto-tag write failure'); END;
                """;
            command.ExecuteNonQuery();
        }
        try
        {
            await Assert.ThrowsExceptionAsync<SqliteException>(() =>
                workspace.ApplyAutoTagsAsync(
                    new ModTypeDetectionService(NullLogger<ModTypeDetectionService>.Instance),
                    CreateLocalization(), AudioDetection(workspace.Mods.Single())));
            Assert.AreEqual(0, _settings.Tags.Count);
            Assert.AreEqual(0, workspace.Mods.Single().TagIds.Count);
            Assert.IsTrue(_repository.LoadAll(_root).Single().TagIds is null or { Count: 0 });
            var restoredSettings = new SettingsService(NullLogger<SettingsService>.Instance,
                Path.Combine(_root, "settings.json"), _root);
            Assert.IsTrue(await restoredSettings.InitAsync());
            Assert.AreEqual(0, restoredSettings.Tags.Count);
        }
        finally
        {
            using var connection = _database.OpenConnection(_root);
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER reject_auto_tags;";
            command.ExecuteNonQuery();
        }
    }

    private LocalizationService CreateLocalization() =>
        new(NullLogger<LocalizationService>.Instance, GetLanguageDirectory());

    private static Dictionary<string, ModTypeDetectionService.ModTypeDetectionResult> AudioDetection(ModData mod) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [mod.Directory.FullName] = new(ModType.Audio, [ModType.Audio],
                new Dictionary<ulong, int>(), [], 1, "test audio"),
        };

    private static string GetLanguageDirectory([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language");
        throw new DirectoryNotFoundException("Repository language files not found.");
    }

    private async Task<DashboardWorkspace> OpenWorkspaceAsync()
    {
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            Path.Combine(_root, "Language"));
        var groupRepository = new ModGroupRepository(NullLogger<ModGroupRepository>.Instance, _database);
        var groups = new ModGroupService(NullLogger<ModGroupService>.Instance, groupRepository, localization);
        return await DashboardWorkspace.OpenAsync(_settings, _library, groups, groupRepository);
    }

    private (DirectoryInfo Directory, Guid Guid) CreateLegacy(string name, Guid guid, string[]? options = null)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "Mods", name));
        ModManifest.SaveToFile(new LegacyModManifest
        {
            Guid = guid,
            Name = name,
            Description = string.Empty,
            Options = options,
        }, directory);
        return (directory, guid);
    }

    private (DirectoryInfo Directory, Guid Guid) CreateV1(string name, Guid guid, string[] includes)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "Mods", name));
        ModManifest.SaveToFile(new V1ModManifest
        {
            Guid = guid,
            Name = name,
            Description = string.Empty,
            Options = [new ModOption { Name = "option", Include = includes }],
        }, directory);
        return (directory, guid);
    }
}
