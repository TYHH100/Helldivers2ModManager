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
public sealed class BisectIntegrationTests
{
    private string _root = null!;
    private DatabaseService _database = null!;
    private ModService? _modService;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-bisect-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _database = new DatabaseService(NullLogger<DatabaseService>.Instance);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        if (_modService is not null)
            await _modService.HashMigrationTask;
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hd2mm-jalium-bisect-tests"))
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
                await Task.Delay(50);
            }
        }
    }

    [TestMethod]
    public async Task BisectDeploysSessionStateAndRestoresOriginalGroup()
    {
        const string patchName = "0123456789abcdef.patch_0";
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(_root, "settings.json"), _root);
        settings.InitDefault();
        settings.GameDirectory = Path.Combine(_root, "game");
        settings.StorageDirectory = Path.Combine(_root, "storage");
        settings.TempDirectory = Path.Combine(_root, "temp");
        var data = Directory.CreateDirectory(Path.Combine(settings.GameDirectory, "data"));
        for (var index = 0; index < ids.Length; index++)
            AddMod(settings.StorageDirectory, $"Mod{index}", ids[index], patchName, $"patch{index}");

        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            GetLanguageDirectory());
        var enabledRepository = new EnabledDataRepository(
            NullLogger<EnabledDataRepository>.Instance, _database);
        var library = new DashboardLibraryService(
            new ModCatalogService(NullLogger<ModCatalogService>.Instance), enabledRepository,
            NullLogger<DashboardLibraryService>.Instance);
        var groupRepository = new ModGroupRepository(
            NullLogger<ModGroupRepository>.Instance, _database);
        var groups = new ModGroupService(NullLogger<ModGroupService>.Instance,
            groupRepository, localization);
        var workspace = await DashboardWorkspace.OpenAsync(settings, library, groups, groupRepository);
        foreach (var mod in workspace.Mods)
            await workspace.SetEnabledAsync(mod, mod.Manifest.Guid != ids[3]);
        var originalOrder = workspace.Mods.Select(mod => mod.Manifest.Guid).ToArray();

        var hashes = new ModHashService(NullLogger<ModHashService>.Instance,
            new FileHashRepository(NullLogger<FileHashRepository>.Instance, _database),
            _database, localization, new BackgroundTaskService(action => action(), () => true));
        _modService = new ModService(NullLogger<ModService>.Instance, hashes, localization,
            new GameProcessService(NullLogger<GameProcessService>.Instance));
        _modService.Init(settings);
        var profile = new ProfileService(NullLogger<ProfileService>.Instance,
            enabledRepository, _database,
            new ModLinkRepository(NullLogger<ModLinkRepository>.Instance, _database));
        var coordinator = new ProfileSaveCoordinator(
            NullLogger<ProfileSaveCoordinator>.Instance, profile, groups, settings);
        var bisect = new BisectService(NullLogger<BisectService>.Instance,
            groups, _modService, settings, coordinator, localization);

        var session = await bisect.StartAsync(groups.SelectedGroup, workspace.Mods);
        CollectionAssert.AreEqual(originalOrder, session.OriginalOrder.ToArray());
        Assert.AreEqual(3, session.Candidates.Count);
        var round = await bisect.PrepareRoundAsync();
        Assert.AreEqual(1, round.TestedMods.Count);
        await bisect.DeployAsync();
        Assert.AreEqual($"patch{Array.IndexOf(ids, round.TestedMods[0].Manifest.Guid)}",
            File.ReadAllText(Path.Combine(data.FullName, patchName)));

        bisect.ApplyResult(crashed: true, round);
        await bisect.PrepareSingleVerificationAsync();
        await bisect.DisableSuspectAsync();
        var remaining = bisect.GetRemainingEnabledMods();
        CollectionAssert.AreEquivalent(ids.Skip(1).Take(2).ToArray(),
            remaining.Select(mod => mod.Manifest.Guid).ToArray());
        await bisect.PrepareRemainingVerificationAsync(remaining);
        await bisect.DeployAsync();
        Assert.IsFalse(round.TestedMods[0].Enabled);
        Assert.AreEqual(2, groups.FilterMods(workspace.Mods).Count(mod => mod.Enabled));
        await bisect.FinishAsync(false);

        Assert.IsNull(bisect.Current);
        Assert.AreEqual(session.OriginalGroupId, groups.SelectedGroup.Id);
        Assert.IsFalse(groups.Groups.Any(group => group.Id == session.TempGroup.Id));
        CollectionAssert.AreEqual(new[] { true, true, true, false },
            originalOrder.Select(id => workspace.Mods.First(mod => mod.Manifest.Guid == id).Enabled).ToArray());

        var completed = await bisect.StartAsync(groups.SelectedGroup, workspace.Mods);
        var completedRound = await bisect.PrepareRoundAsync();
        bisect.ApplyResult(crashed: true, completedRound);
        await bisect.PrepareSingleVerificationAsync();
        await bisect.DisableSuspectAsync();
        await bisect.FinishAsync(true);
        Assert.IsFalse(groups.Groups.Any(group => group.Id == completed.TempGroup.Id));
        var reloaded = await DashboardWorkspace.OpenAsync(settings, library, groups, groupRepository);
        Assert.IsFalse(reloaded.Mods.First(mod => mod.Manifest.Guid == ids[0]).Enabled);
        Assert.IsFalse(reloaded.Mods.First(mod => mod.Manifest.Guid == ids[3]).Enabled);

        var custom = await workspace.CreateGroupAsync("Custom");
        await workspace.AddToGroupAsync(custom.Id, workspace.Mods.Take(2).ToArray());
        await workspace.SelectGroupAsync(custom.Id);
        await workspace.SetEnabledAsync(workspace.Mods.First(mod => mod.Manifest.Guid == ids[0]), true);
        var customSession = await bisect.StartAsync(custom, workspace.Mods);
        var customRound = await bisect.PrepareRoundAsync();
        bisect.ApplyResult(crashed: true, customRound);
        await bisect.PrepareSingleVerificationAsync();
        await bisect.DisableSuspectAsync();
        await bisect.FinishAsync(false);
        Assert.AreEqual(custom.Id, groups.SelectedGroup.Id);
        Assert.IsFalse(groups.Groups.Any(group => group.Id == customSession.TempGroup.Id));
        Assert.IsTrue(workspace.Mods.First(mod => mod.Manifest.Guid == ids[0]).Enabled);
        await workspace.SelectGroupAsync(session.OriginalGroupId);
        Assert.IsFalse(workspace.Mods.First(mod => mod.Manifest.Guid == ids[0]).Enabled);
    }

    private static void AddMod(string storage, string name, Guid id, string patchName, string content)
    {
        var directory = Directory.CreateDirectory(Path.Combine(storage, "Mods", name));
        File.WriteAllText(Path.Combine(directory.FullName, patchName), content);
        ModManifest.SaveToFile(new LegacyModManifest
        {
            Guid = id, Name = name, Description = string.Empty, Options = [],
        }, directory);
    }

    private static string GetLanguageDirectory([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!);
             current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return Path.Combine(current.FullName, "src", "Helldivers2ModManager",
                    "Resources", "Language");
        throw new DirectoryNotFoundException("Repository language files not found.");
    }
}
