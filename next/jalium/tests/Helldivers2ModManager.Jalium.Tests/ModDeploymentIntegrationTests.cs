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
public sealed class ModDeploymentIntegrationTests
{
    private string _root = null!;
    private DatabaseService _database = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-deploy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _database = new DatabaseService(NullLogger<DatabaseService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hd2mm-jalium-deploy-tests"))
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
    public async Task DeployUsesSelectedProfileOrderAndPurgeKeepsUnrelatedFiles()
    {
        const string patchName = "0123456789abcdef.patch_0";
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(_root, "settings.json"), _root);
        settings.InitDefault();
        settings.GameDirectory = Path.Combine(_root, "game");
        settings.StorageDirectory = Path.Combine(_root, "storage");
        settings.TempDirectory = Path.Combine(_root, "temp");
        settings.UseDeploymentOrder = true;
        settings.DeploymentOrderGuids.AddRange([secondId, firstId]);
        var data = Directory.CreateDirectory(Path.Combine(settings.GameDirectory, "data"));
        File.WriteAllText(Path.Combine(data.FullName, "keep.txt"), "untouched");
        AddFixtureMod(settings.StorageDirectory, "First", firstId, patchName, "first");
        AddFixtureMod(settings.StorageDirectory, "Second", secondId, patchName, "second");

        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            GetLanguageDirectory());
        var repository = new EnabledDataRepository(NullLogger<EnabledDataRepository>.Instance, _database);
        var library = new DashboardLibraryService(
            new ModCatalogService(NullLogger<ModCatalogService>.Instance), repository,
            NullLogger<DashboardLibraryService>.Instance);
        var groupRepository = new ModGroupRepository(NullLogger<ModGroupRepository>.Instance, _database);
        var groups = new ModGroupService(NullLogger<ModGroupService>.Instance, groupRepository, localization);
        var workspace = await DashboardWorkspace.OpenAsync(settings, library, groups, groupRepository);
        Assert.AreEqual(2, workspace.Mods.Count);
        foreach (var mod in workspace.Mods)
            await workspace.SetEnabledAsync(mod, true);
        var snapshot = workspace.CaptureProfileSnapshot();
        var ordered = DeploymentOrderHelper.BuildDeploymentMods(snapshot, settings.UseDeploymentOrder,
            settings.DeploymentOrderGuids, settings.DeployBottomToTop);
        CollectionAssert.AreEqual(new[] { secondId, firstId },
            ordered.Select(mod => mod.Manifest.Guid).ToArray());

        var hashes = new ModHashService(NullLogger<ModHashService>.Instance,
            new FileHashRepository(NullLogger<FileHashRepository>.Instance, _database),
            _database, localization, new BackgroundTaskService(action => action(), () => true));
        var service = new ModService(NullLogger<ModService>.Instance, hashes, localization,
            new GameProcessService(NullLogger<GameProcessService>.Instance));
        service.Init(settings);
        await workspace.SaveCurrentAsync();
        await service.DeployAsync(ordered);
        Assert.AreEqual("second", File.ReadAllText(Path.Combine(data.FullName, patchName)));
        Assert.AreEqual("first", File.ReadAllText(Path.Combine(data.FullName,
            "0123456789abcdef.patch_1")));
        Assert.IsTrue(File.Exists(Path.Combine(data.FullName, "0123456789abcdef.patch_0.stream")));

        await service.PurgeAsync();
        Assert.AreEqual(0, data.GetFiles("*.patch_*").Length);
        Assert.AreEqual("untouched", File.ReadAllText(Path.Combine(data.FullName, "keep.txt")));
    }

    private static void AddFixtureMod(string storage, string name, Guid guid,
        string patchName, string content)
    {
        var directory = Directory.CreateDirectory(Path.Combine(storage, "Mods", name));
        File.WriteAllText(Path.Combine(directory.FullName, patchName), content);
        ModManifest.SaveToFile(new LegacyModManifest
        {
            Guid = guid,
            Name = name,
            Description = string.Empty,
            Options = [],
        }, directory);
    }

    private static string GetLanguageDirectory([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language");
        throw new DirectoryNotFoundException("Repository language files not found.");
    }
}
