using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Tests;

[TestClass]
public sealed class CreateModEditorTests
{
    private string _root = null!;
    private DatabaseService _database = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-create-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _database = new DatabaseService(NullLogger<DatabaseService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hd2mm-jalium-create-tests"))
            + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(_root);
        if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its dedicated directory.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(resolved))
                    Directory.Delete(resolved, recursive: true);
                var parent = Path.GetDirectoryName(resolved)!;
                if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                    Directory.Delete(parent);
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
    public async Task CreatesV1WithNestedOptionsAndExternalIconWithoutChangingSource()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source"));
        var model = Directory.CreateDirectory(Path.Combine(source.FullName, "Models", "Armor"));
        var variant = Directory.CreateDirectory(Path.Combine(model.FullName, "Variant"));
        File.WriteAllText(Path.Combine(variant.FullName, "0123456789abcdef.patch_0"), "patch");
        var externalIcon = Path.Combine(_root, "icon.png");
        File.WriteAllBytes(externalIcon, [137, 80, 78, 71]);
        var settings = CreateSettings();
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            GetLanguageDirectory());
        var editor = new CreateModEditor(settings, localization);
        editor.SetSourceDirectory(source.FullName);
        editor.SetIconFromFile(externalIcon);

        Assert.AreEqual("source", editor.Name);
        Assert.AreEqual(1, editor.Options.Count);
        Assert.AreEqual("Models\\Armor", editor.Options[0].IncludePaths);
        Assert.AreEqual("Models\\Armor\\Variant", editor.Options[0].SubOptions.Single().IncludePaths);

        var service = CreateService(settings, localization);
        await service.HashMigrationTask;
        Assert.IsFalse((await editor.CreateAsync(service)).Any(problem => problem.IsError));
        var created = service.Mods.Single();
        var manifest = created.Manifest as V1ModManifest;
        Assert.IsNotNull(manifest);
        Assert.AreEqual("icon.png", manifest.IconPath);
        Assert.AreEqual("Models\\Armor", manifest.Options!.Single().Include!.Single());
        Assert.AreEqual("Models\\Armor\\Variant",
            manifest.Options!.Single().SubOptions!.Single().Include.Single());
        Assert.IsTrue(File.Exists(Path.Combine(created.Directory.FullName, "icon.png")));
        Assert.IsFalse(File.Exists(Path.Combine(source.FullName, "icon.png")));
        Assert.IsFalse(Directory.Exists(Path.Combine(settings.TempDirectory, "JaliumCreate")));
    }

    [TestMethod]
    public async Task LegacyUsesDirectoryNamesAndDoesNotPersistSubOptions()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "legacy-source"));
        Directory.CreateDirectory(Path.Combine(source.FullName, "Choice"));
        var settings = CreateSettings();
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            GetLanguageDirectory());
        var editor = new CreateModEditor(settings, localization);
        editor.SetSourceDirectory(source.FullName);
        editor.IsV1Manifest = false;
        editor.Options.Single().SubOptions.Add(new CreateSubOptionDraft
            { Name = "ignored", IncludePaths = "Choice" });

        var service = CreateService(settings, localization);
        await service.HashMigrationTask;
        Assert.IsFalse((await editor.CreateAsync(service)).Any(problem => problem.IsError));
        var manifest = service.Mods.Single().Manifest as LegacyModManifest;
        Assert.IsNotNull(manifest);
        CollectionAssert.AreEqual(new[] { "Choice" }, manifest.Options!.ToArray());
    }

    [TestMethod]
    public async Task CreatedModRemainsInDefaultProfileAfterRefreshAndReopen()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "new-mod"));
        File.WriteAllText(Path.Combine(source.FullName, "0123456789abcdef.patch_0"), "patch");
        var settings = CreateSettings();
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            GetLanguageDirectory());
        var groupsRepository = new ModGroupRepository(NullLogger<ModGroupRepository>.Instance, _database);
        var library = new DashboardLibraryService(
            new ModCatalogService(NullLogger<ModCatalogService>.Instance),
            new EnabledDataRepository(NullLogger<EnabledDataRepository>.Instance, _database),
            NullLogger<DashboardLibraryService>.Instance);
        var groups = new ModGroupService(NullLogger<ModGroupService>.Instance,
            groupsRepository, localization);
        var workspace = await DashboardWorkspace.OpenAsync(settings, library, groups, groupsRepository);
        var editor = new CreateModEditor(settings, localization);
        editor.SetSourceDirectory(source.FullName);
        var service = CreateService(settings, localization);
        await service.HashMigrationTask;

        Assert.AreEqual(0, (await editor.CreateAsync(service)).Length);
        var added = service.Mods.Single();
        await workspace.AddToGroupAsync(groups.Groups.Single(group => group.IsDefault).Id, [added]);
        await workspace.RefreshAsync(persistImportedDefaults: true);
        Assert.AreEqual(added.Manifest.Guid, workspace.Rows.OfType<ModData>().Single().Manifest.Guid);

        var reopenedGroups = new ModGroupService(NullLogger<ModGroupService>.Instance,
            groupsRepository, localization);
        var reopened = await DashboardWorkspace.OpenAsync(settings, library, reopenedGroups,
            groupsRepository);
        Assert.AreEqual(added.Manifest.Guid, reopened.Rows.OfType<ModData>().Single().Manifest.Guid);
    }

    private SettingsService CreateSettings()
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(_root, "settings.json"), _root);
        settings.InitDefault();
        settings.GameDirectory = Path.Combine(_root, "game");
        settings.StorageDirectory = Path.Combine(_root, "storage");
        settings.TempDirectory = Path.Combine(_root, "temp");
        return settings;
    }

    private ModService CreateService(SettingsService settings, LocalizationService localization)
    {
        var hashes = new ModHashService(NullLogger<ModHashService>.Instance,
            new FileHashRepository(NullLogger<FileHashRepository>.Instance, _database),
            _database, localization, new BackgroundTaskService(action => action(), () => true));
        var service = new ModService(NullLogger<ModService>.Instance, hashes, localization,
            new GameProcessService(NullLogger<GameProcessService>.Instance));
        service.Init(settings);
        return service;
    }

    private static string GetLanguageDirectory([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language");
        throw new DirectoryNotFoundException("Repository language files not found.");
    }
}
