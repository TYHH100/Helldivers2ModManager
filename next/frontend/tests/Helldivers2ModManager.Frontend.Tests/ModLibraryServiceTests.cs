using System.IO;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Deployment;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Core.Profiles;
using Helldivers2ModManager.Frontend.Models;
using Helldivers2ModManager.Frontend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Frontend.Tests;

[TestClass]
public sealed class ModLibraryServiceTests
{
    private string? _root;

    [TestCleanup]
    public void Cleanup()
    {
        if (_root is not null && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void CreateDeploymentInput_PreservesRuntimeOptionsAndBuildsDefaults()
    {
        var manifest = new V1ModManifest
        {
            Guid = Guid.NewGuid(),
            Name = "Options",
            Description = string.Empty,
            Options =
            [
                new("Disabled", string.Empty, null, null, null),
                new("Colors", string.Empty, null, null,
                    [new("Blank", string.Empty, [], null), new("Red", string.Empty, ["red"], null)]),
            ],
        };
        var mod = new ModItem(new DiscoveredMod(new DirectoryInfo("unused"), manifest))
        {
            EnabledOptions = [false, true],
            SelectedOptions = [0, 1],
        };

        var input = mod.CreateDeploymentInput();

        Assert.IsFalse(input.EnabledOptions[0]);
        Assert.IsTrue(input.EnabledOptions[1]);
        Assert.AreEqual(1, input.SelectedOptions[1]);
    }

    [TestMethod]
    public async Task SaveItemAsync_PreservesOtherStatesAndWritesRuntimeOptions()
    {
        var root = Path.Combine(Path.GetTempPath(), "Helldivers2ModManagerFrontendTests", Guid.NewGuid().ToString("N"));
        _root = root;
        var paths = new ApplicationPaths(root);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCommon();
        services.AddPersistence(paths.Database);
        services.AddMods();
        services.AddProfiles();
        services.AddSingleton(paths);
        services.AddSingleton<LocalizationCatalog>();
        services.AddSingleton<ApplicationSettingsService>();
        services.AddSingleton<TaskExecutionService>();
        services.AddSingleton<ModLibraryService>();
        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ApplicationSettingsService>().InitializeAsync();
        var library = provider.GetRequiredService<ModLibraryService>();
        var first = CreateMod("First");
        var second = CreateMod("Second");
        await library.SaveAsync([first, second]);

        first.EnabledOptions = [false];
        first.SelectedOptions = [1];
        await library.SaveItemAsync(first);

        using var database = new Database(paths.Database);
        var profileRepository = new ProfileRepository(database);
        var repository = new EnabledStateRepository(database, profileRepository);
        var records = await repository.LoadAllAsync();
        Assert.AreEqual(2, records.Count);
        var saved = records.Single(record => record.ModGuid == first.Id);
        var runtime = ProfileStateService.DeserializeRuntimeState(saved.StateJson);
        Assert.IsFalse(runtime.EnabledOptions[0]);
        Assert.AreEqual(1, runtime.SelectedOptions[0]);
    }

    [TestMethod]
    public async Task GroupLifecycle_PersistsMembershipAndRestoresFlatStatesOnDelete()
    {
        var root = Path.Combine(Path.GetTempPath(), "Helldivers2ModManagerFrontendTests", Guid.NewGuid().ToString("N"));
        _root = root;
        var paths = new ApplicationPaths(root);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCommon();
        services.AddPersistence(paths.Database);
        services.AddMods();
        services.AddProfiles();
        services.AddSingleton(paths);
        services.AddSingleton<LocalizationCatalog>();
        services.AddSingleton<ApplicationSettingsService>();
        services.AddSingleton<TaskExecutionService>();
        services.AddSingleton<ModLibraryService>();
        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ApplicationSettingsService>().InitializeAsync();
        var library = provider.GetRequiredService<ModLibraryService>();
        var first = CreateMod("First");
        var second = CreateMod("Second");
        first.IsEnabled = true;
        await library.SaveAsync([first, second]);

        var group = await library.CreateGroupAsync("Alpha");
        await library.SetModsGroupAsync([first, second], [first], group.Id, group.Name);

        var profileId = (await new ProfileRepository(new Database(paths.Database)).GetOrCreateDefaultAsync()).Id;
        var flat = await new EnabledStateRepository(new Database(paths.Database), new ProfileRepository(new Database(paths.Database))).LoadAllAsync();
        Assert.AreEqual(1, flat.Count, "grouped mod must leave the flat state set");
        Assert.AreEqual(second.Id, flat[0].ModGuid);
        var members = await new GroupRepository(new Database(paths.Database)).LoadMemberIdsAsync(profileId, group.Id);
        CollectionAssert.AreEqual(new[] { first.Id }, members.ToArray());

        Assert.ThrowsException<InvalidOperationException>(() => library.CreateGroupAsync("alpha").GetAwaiter().GetResult());

        // 删除分组：成员启用状态并入平面状态，分组被清除。
        await library.DeleteGroupAsync(group.Id, [first, second]);
        var flatAfterDelete = await new EnabledStateRepository(new Database(paths.Database), new ProfileRepository(new Database(paths.Database))).LoadAllAsync();
        Assert.AreEqual(2, flatAfterDelete.Count);
        var restored = flatAfterDelete.Single(record => record.ModGuid == first.Id);
        Assert.IsTrue(restored.Enabled, "member enabled state must survive group deletion");
        var groupsAfterDelete = await new GroupRepository(new Database(paths.Database)).LoadForProfileAsync(profileId);
        Assert.AreEqual(0, groupsAfterDelete.Count);
    }

    private static ModItem CreateMod(string name)
    {
        var manifest = new LegacyModManifest
        {
            Guid = Guid.NewGuid(),
            Name = name,
            Description = string.Empty,
            Options = ["OptionA"],
        };
        return new ModItem(new DiscoveredMod(new DirectoryInfo(name), manifest));
    }
}
