using System.IO;
using Helldivers2ModManager.Core.Common;
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
public sealed class ConflictAnalysisFacadeTests
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
    public async Task ScanEnabledAsync_ReturnsEmptyResultWithoutEnabledMods()
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
        services.AddSingleton<ApplicationSettingsService>();
        services.AddSingleton<TaskExecutionService>();
        services.AddSingleton<PatchStructureAnalyzer>();
        services.AddSingleton<VersionCheckFacade>();
        services.AddSingleton<ConflictAnalysisFacade>();
        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ApplicationSettingsService>().InitializeAsync();
        var facade = provider.GetRequiredService<ConflictAnalysisFacade>();

        var result = await facade.ScanEnabledAsync([]);

        Assert.AreEqual(0, result.ScannedModCount);
        Assert.AreEqual(0, result.ScannedUnitCount);
        Assert.AreEqual(0, result.Conflicts.Count);
    }
}
