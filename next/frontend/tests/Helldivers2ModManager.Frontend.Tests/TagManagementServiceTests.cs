using System.IO;
using Helldivers2ModManager.Core.Common;
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
public sealed class TagManagementServiceTests
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
    public async Task DeleteAsync_RemovesTagFromSettingsAndModRuntimeStates()
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
        services.AddSingleton<ApplicationSettingsService>();
        services.AddSingleton<TaskExecutionService>();
        services.AddSingleton<TagManagementService>();
        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<ApplicationSettingsService>();
        await settings.InitializeAsync();
        var tags = provider.GetRequiredService<TagManagementService>();
        var tag = await tags.AddAsync("Armor", "#FF60CDFF");
        var mod = new ModItem(new DiscoveredMod(
            new DirectoryInfo("unused"),
            new LegacyModManifest { Guid = Guid.NewGuid(), Name = "Mod", Description = string.Empty }))
        {
            IsEnabled = true,
            TagIds = [tag.Id],
        };
        using (var setupDatabase = new Database(paths.Database))
        {
            var setupRepository = new EnabledStateRepository(setupDatabase, new ProfileRepository(setupDatabase));
            await setupRepository.ReplaceAllAsync([new(
                mod.Id,
                true,
                0,
                ProfileStateService.SerializeRuntimeState(mod.CreateRuntimeState()))]);
        }

        await tags.DeleteAsync(tag.Id);

        Assert.AreEqual(0, settings.Current.Tags.Count);
        using var database = new Database(paths.Database);
        var repository = new EnabledStateRepository(database, new ProfileRepository(database));
        var state = (await repository.LoadAllAsync()).Single();
        Assert.AreEqual(0, ProfileStateService.DeserializeRuntimeState(state.StateJson).TagIds!.Count);
    }
}
