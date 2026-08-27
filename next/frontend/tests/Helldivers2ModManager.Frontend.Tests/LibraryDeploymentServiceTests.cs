using System.IO;
using Helldivers2ModManager.Core.Common;
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
public sealed class LibraryDeploymentServiceTests
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
    public async Task DeployEnabledModsAsync_PersistsStateAndCreatesGameData()
    {
        _root = Path.Combine(Path.GetTempPath(), "Helldivers2ModManagerFrontendTests", Guid.NewGuid().ToString("N"));
        var paths = new ApplicationPaths(_root);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCommon();
        services.AddPersistence(paths.Database);
        services.AddMods();
        services.AddProfiles();
        services.AddDeployment();
        services.AddSingleton(paths);
        services.AddSingleton<ApplicationSettingsService>();
        services.AddSingleton<TaskExecutionService>();
        services.AddSingleton<ModLibraryService>();
        services.AddSingleton<DeploymentServiceFacade>();
        services.AddSingleton<LibraryDeploymentService>();
        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ApplicationSettingsService>().InitializeAsync();
        var library = provider.GetRequiredService<ModLibraryService>();
        var service = provider.GetRequiredService<LibraryDeploymentService>();
        var mod = new ModItem(new DiscoveredMod(Directory.CreateDirectory(Path.Combine(paths.Data, "Mods", "Empty")), new LegacyModManifest
        {
            Guid = Guid.NewGuid(),
            Name = "Empty",
            Description = string.Empty,
        }))
        {
            IsEnabled = true,
        };

        await library.SaveAsync([mod]);
        var result = await service.DeployEnabledModsAsync([mod]);

        Assert.AreEqual(BackgroundTaskStatus.Succeeded, result.Status);
        using var database = new Database(paths.Database);
        var repository = new EnabledStateRepository(database, new ProfileRepository(database));
        var records = await repository.LoadAllAsync();
        Assert.IsTrue(records.Single(record => record.ModGuid == mod.Id).Enabled);
    }

    [TestMethod]
    public void CreateAndApply_V1Options_PreserveEnabledAndSelectedState()
    {
        var manifest = new V1ModManifest
        {
            Guid = Guid.NewGuid(),
            Name = "V1",
            Description = string.Empty,
            Options =
            [
                new("Audio", "base", null, null, null),
                new("Colors", "variant", null, null,
                    [new("Blue", string.Empty, [], null), new("Red", string.Empty, ["red"], null)]),
            ],
        };
        var mod = new ModItem(new DiscoveredMod(new DirectoryInfo("unused"), manifest))
        {
            EnabledOptions = [true, false],
            SelectedOptions = [0, 1],
        };

        var options = LibraryDeploymentService.CreateOptions(mod);

        Assert.AreEqual(2, options.Count);
        Assert.IsTrue(options[0].IsEnabled);
        Assert.IsFalse(options[1].IsEnabled);
        Assert.AreEqual(1, options[1].SelectedSubOption);

        options[0].IsEnabled = false;
        options[0].SelectedSubOption = 1;
        LibraryDeploymentService.ApplyOptions(mod, options);

        Assert.IsFalse(mod.EnabledOptions[0]);
        Assert.IsFalse(mod.EnabledOptions[1]);
        Assert.AreEqual(0, mod.SelectedOptions[0]);
    }

    [TestMethod]
    public void CreateAndApply_LegacyOption_UsesRadioSelection()
    {
        var manifest = new LegacyModManifest
        {
            Guid = Guid.NewGuid(),
            Name = "Legacy",
            Description = string.Empty,
            Options = ["Alpha", "Beta"],
        };
        var mod = new ModItem(new DiscoveredMod(new DirectoryInfo("unused"), manifest))
        {
            SelectedOptions = [1],
        };

        var options = LibraryDeploymentService.CreateOptions(mod);

        Assert.IsFalse(options[0].IsEnabled);
        Assert.IsTrue(options[1].IsEnabled);

        options[0].IsEnabled = true;
        options[1].IsEnabled = false;
        LibraryDeploymentService.ApplyOptions(mod, options);

        Assert.AreEqual(0, mod.SelectedOptions.Single());
    }
}
