using System.IO;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Deployment;
using Helldivers2ModManager.Core.GameData;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Core.Profiles;
using Helldivers2ModManager.Core.Repair;
using Helldivers2ModManager.Frontend;
using Helldivers2ModManager.Frontend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Frontend.Tests;

[TestClass]
public sealed class DiagnosticsFacadeTests
{
    private string? _root;
    private ServiceProvider? _provider;

    [TestCleanup]
    public void Cleanup()
    {
        _provider?.Dispose();
        _provider = null;
        if (_root is not null)
        {
            DeleteDirectoryWithRetry(_root);
        }
    }

    [TestMethod]
    public async Task CreateRepairPlan_MarksPatchlessModAsUnsupported()
    {
        var facade = await CreateFacadeAsync();
        var plan = await facade.CreateRepairPlanAsync();

        Assert.AreEqual(1, plan.Count);
        Assert.AreEqual(BatchRepairState.SkippedUnsupported, plan[0].State);
        StringAssert.Contains(plan[0].Message, "No supported Unit resources");
    }

    [TestMethod]
    public async Task ExecuteRepairsAsync_KeepsUnsupportedItemWithoutFileChanges()
    {
        var facade = await CreateFacadeAsync();
        var plan = await facade.CreateRepairPlanAsync();

        var results = await facade.ExecuteRepairsAsync([.. plan.Select(item => item.Source)]);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(BatchRepairState.SkippedUnsupported, results[0].State);
    }

    private async Task<DiagnosticsFacade> CreateFacadeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "Helldivers2ModManagerFrontendTests", Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCommon();
        services.AddPersistence(Path.Combine(_root, "data", "mod_manager.db"));
        services.AddMods();
        services.AddProfiles();
        services.AddDeployment();
        services.AddGameData();
        services.AddSingleton(new ApplicationPaths(_root));
        services.AddFrontend();
        _provider = services.BuildServiceProvider();
        await _provider.GetRequiredService<ApplicationSettingsService>().InitializeAsync();

        var modsRoot = Path.Combine(_root, "data", "Mods");
        Directory.CreateDirectory(modsRoot);
        var modDirectory = Directory.CreateDirectory(Path.Combine(modsRoot, "Empty"));
        ModManifest.SaveToFile(new LegacyModManifest
        {
            Guid = Guid.NewGuid(),
            Name = "Empty",
            Description = string.Empty,
        }, modDirectory);

        return _provider.GetRequiredService<DiagnosticsFacade>();
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(150);
            }
        }
    }
}
