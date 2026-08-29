using System.IO;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.GameData;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Core.Profiles;
using Helldivers2ModManager.Core.Versioning;
using Helldivers2ModManager.Frontend.Models;
using Helldivers2ModManager.Frontend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Frontend.Tests;

[TestClass]
public sealed class VersionCheckFacadeTests
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
    public async Task CheckAllAsync_MapsEmptyModToCompatibleResult()
    {
        _root = Path.Combine(Path.GetTempPath(), "Helldivers2ModManagerFrontendTests", Guid.NewGuid().ToString("N"));
        var paths = new ApplicationPaths(_root);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCommon();
        services.AddPersistence(paths.Database);
        services.AddMods();
        services.AddProfiles();
        services.AddGameData();
        services.AddSingleton(paths);
        services.AddSingleton<LocalizationCatalog>();
        services.AddSingleton<ApplicationSettingsService>();
        services.AddSingleton<TaskExecutionService>();
        services.AddSingleton<PatchStructureAnalyzer>();
        services.AddSingleton<VersionCheckFacade>();
        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<ApplicationSettingsService>();
        await settings.InitializeAsync();
        var facade = provider.GetRequiredService<VersionCheckFacade>();
        var directory = Directory.CreateDirectory(Path.Combine(_root, "Empty Mod"));
        var mod = new ModItem(new DiscoveredMod(
            directory,
            new LegacyModManifest { Guid = Guid.NewGuid(), Name = "Empty Mod", Description = string.Empty }));

        var results = await facade.CheckAllAsync([mod]);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(ModVersionStatus.Unknown, results[0].Status);
        Assert.AreEqual(0, results[0].UnitCount);
        // 摘要文案随 UI 文化变化，期望值经目录解析而不是硬编码。
        var catalog = provider.GetRequiredService<LocalizationCatalog>();
        Assert.AreEqual(catalog.GetString("Next.VersionCheck.Undetermined"), results[0].Summary);
        Assert.AreNotEqual(string.Empty, results[0].Summary);
    }
}
