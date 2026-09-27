using System.IO.Compression;
using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpSevenZip;

namespace Helldivers2ModManager.Jalium.Tests;

[TestClass]
public sealed class ModImportIntegrationTests
{
    private string _root = null!;
    private DatabaseService _database = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _database = new DatabaseService(NullLogger<DatabaseService>.Instance);
        SharpSevenZipBase.SetLibraryPath(Path.Combine(AppContext.BaseDirectory, "7z.dll"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hd2mm-jalium-import-tests"))
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
    public async Task ArchiveImportUsesOriginalServiceAndCleansTemporaryFiles()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source"));
        var guid = Guid.NewGuid();
        File.WriteAllText(Path.Combine(source.FullName, "sample.patch_0"), "fixture");
        ModManifest.SaveToFile(new LegacyModManifest
        {
            Guid = guid,
            Name = "Imported",
            Description = string.Empty,
            Options = ["sample.patch_0"],
        }, source);
        var archive = Path.Combine(_root, "mod.ZIP");
        ZipFile.CreateFromDirectory(source.FullName, archive);

        var settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(_root, "settings.json"), _root);
        settings.InitDefault();
        settings.GameDirectory = Path.Combine(_root, "game");
        settings.StorageDirectory = Path.Combine(_root, "storage");
        settings.TempDirectory = Path.Combine(_root, "temp");
        settings.AutoAddImportedModsToActiveProfile = true;
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            GetLanguageDirectory());
        var repository = new EnabledDataRepository(NullLogger<EnabledDataRepository>.Instance, _database);
        var library = new DashboardLibraryService(
            new ModCatalogService(NullLogger<ModCatalogService>.Instance), repository,
            NullLogger<DashboardLibraryService>.Instance);
        var groupRepository = new ModGroupRepository(NullLogger<ModGroupRepository>.Instance, _database);
        var groups = new ModGroupService(NullLogger<ModGroupService>.Instance, groupRepository, localization);
        var workspace = await DashboardWorkspace.OpenAsync(settings, library, groups, groupRepository);
        var fileHashes = new FileHashRepository(NullLogger<FileHashRepository>.Instance, _database);
        var tasks = new BackgroundTaskService(action => action(), () => true);
        var hashes = new ModHashService(NullLogger<ModHashService>.Instance,
            fileHashes, _database, localization, tasks);
        var service = new ModService(NullLogger<ModService>.Instance, hashes, localization,
            new GameProcessService(NullLogger<GameProcessService>.Instance));
        Assert.AreEqual(0, service.Init(settings).Count(problem => problem.IsError));

        var result = await new ModImportWorkflow(service).ImportAsync(
            [Path.Combine(_root, "missing.zip"), archive],
            _ => Task.FromResult<string?>(null), () => Task.FromResult(true));

        Assert.AreEqual(1, result.Succeeded);
        Assert.AreEqual(1, result.Failed);
        Assert.IsTrue(result.Problems.Any(problem => problem.IsError));
        CollectionAssert.AreEqual(new[] { guid }, result.AddedGuids.ToArray());
        Assert.AreEqual(guid, service.Mods.Single().Manifest.Guid);
        Assert.IsTrue(File.Exists(Path.Combine(settings.StorageDirectory, "Mods", "Imported", "sample.patch_0")));
        Assert.AreEqual(0, Directory.GetDirectories(settings.TempDirectory).Length);

        await workspace.AddToGroupAsync(workspace.Groups.SelectedGroup.Id,
            result.AddedGuids.Select(service.GetModByGuid).OfType<ModData>().ToArray());
        await workspace.RefreshAsync(persistImportedDefaults: true);
        Assert.AreEqual(guid, workspace.Mods.Single().Manifest.Guid);
        Assert.IsTrue(workspace.Groups.SelectedGroup.ModGuids.Contains(guid));
        Assert.AreEqual(guid, repository.LoadAll(settings.StorageDirectory).Single().Guid);
        var reloaded = await DashboardWorkspace.OpenAsync(settings, library,
            new ModGroupService(NullLogger<ModGroupService>.Instance, groupRepository, localization),
            groupRepository);
        Assert.AreEqual(guid, reloaded.Rows.OfType<ModData>().Single().Manifest.Guid);
    }

    private static string GetLanguageDirectory([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language");
        throw new DirectoryNotFoundException("Repository language files not found.");
    }
}
