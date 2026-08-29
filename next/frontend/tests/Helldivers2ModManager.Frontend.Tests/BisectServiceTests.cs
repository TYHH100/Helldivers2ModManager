using System.IO;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Localization;
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
public sealed class BisectServiceTests
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
    public async Task BisectFlow_ConvergesConfirmsSuspectAndRestoresOriginalState()
    {
        var paths = CreatePaths();
        var provider = CreateProvider(paths);
        _provider = provider;
        await provider.GetRequiredService<ApplicationSettingsService>().InitializeAsync();
        var library = provider.GetRequiredService<ModLibraryService>();
        var mods = await CreateModsAsync(paths);
        await library.SaveAsync(mods);
        var repository = provider.GetRequiredService<EnabledStateRepository>();

        var bisect = provider.GetRequiredService<BisectService>();
        var firstRound = await bisect.StartAsync();

        AssertHasIds(firstRound.Tested, mods[0].Id, mods[1].Id);
        var savedFirst = await repository.LoadAllAsync();
        AssertEnabled(savedFirst, (mods[0].Id, true), (mods[1].Id, true), (mods[2].Id, false), (mods[3].Id, false));

        var afterFirstReport = await bisect.ApplyReportAsync(firstRound.Tested, crashed: false);
        AssertHasIds(afterFirstReport.Candidates, mods[2].Id, mods[3].Id);
        var secondRound = await bisect.PrepareRoundAsync();
        AssertHasIds(secondRound.Tested, mods[2].Id);

        var afterSecondReport = await bisect.ApplyReportAsync(secondRound.Tested, crashed: true);
        AssertHasIds(afterSecondReport.Candidates, mods[2].Id);
        var singleRound = await bisect.PrepareSingleVerificationAsync();
        AssertHasIds(singleRound.Tested, mods[2].Id);

        await bisect.ApplySingleReportAsync(crashed: true);
        var result = await bisect.FinishAsync(disableSuspectsInOriginalGroup: true);

        Assert.AreEqual(1, result.Session.Suspects.Count);
        Assert.AreEqual(mods[2].Id, result.Session.Suspects[0].Id);
        Assert.IsTrue(result.SuspectsApplied);
        var finalStates = await repository.LoadAllAsync();
        AssertEnabled(finalStates,
            (mods[0].Id, true),
            (mods[1].Id, true),
            (mods[2].Id, false),
            (mods[3].Id, true));
    }

    [TestMethod]
    public async Task Cancel_RestoresOriginalSnapshotAndClearsSession()
    {
        var paths = CreatePaths();
        var provider = CreateProvider(paths);
        _provider = provider;
        await provider.GetRequiredService<ApplicationSettingsService>().InitializeAsync();
        var library = provider.GetRequiredService<ModLibraryService>();
        var mods = await CreateModsAsync(paths);
        foreach (var (mod, index) in mods.Select((mod, index) => (mod, index)))
        {
            mod.IsEnabled = index % 2 == 0;
        }

        await library.SaveAsync(mods);
        var repository = provider.GetRequiredService<EnabledStateRepository>();
        var beforeStart = await provider.GetRequiredService<ModLibraryService>().LoadAsync();
        Assert.AreEqual(4, beforeStart.Mods.Count);
        Assert.AreEqual(2, beforeStart.Mods.Count(mod => mod.IsEnabled));
        var bisect = provider.GetRequiredService<BisectService>();

        await bisect.StartAsync();
        await bisect.RestoreOriginalAsync();

        Assert.IsFalse(bisect.HasSession);
        var restored = await repository.LoadAllAsync();
        Assert.AreEqual(2, restored.Count(record => record.Enabled));
        CollectionAssert.Contains(
            beforeStart.Mods.Select(mod => mod.Id).ToList(),
            restored.Single(record => record.Enabled && record.SortOrder == 0).ModGuid);
    }

    private ApplicationPaths CreatePaths()
    {
        _root = Path.Combine(Path.GetTempPath(), "Helldivers2ModManagerFrontendTests", Guid.NewGuid().ToString("N"));
        return new ApplicationPaths(_root);
    }

    private ServiceProvider CreateProvider(ApplicationPaths paths)
    {
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
        services.AddSingleton<BisectService>();
        return services.BuildServiceProvider();
    }

    private static async Task<List<ModItem>> CreateModsAsync(ApplicationPaths paths)
    {
        var modsRoot = Path.Combine(paths.Data, "Mods");
        Directory.CreateDirectory(modsRoot);
        List<ModItem> mods = [];
        foreach (var name in new[] { "Alpha", "Bravo", "Charlie", "Delta" })
        {
            var directory = Directory.CreateDirectory(Path.Combine(modsRoot, name));
            var manifest = new LegacyModManifest
            {
                Guid = Guid.NewGuid(),
                Name = name,
                Description = string.Empty,
            };
            ModManifest.SaveToFile(manifest, directory);
            mods.Add(new ModItem(new DiscoveredMod(directory, manifest)) { IsEnabled = true });
        }

        return mods;
    }

    private static void AssertHasIds(IReadOnlyList<BisectCandidate> candidates, params Guid[] expected)
    {
        CollectionAssert.AreEqual(expected, candidates.Select(candidate => candidate.Id).ToArray());
    }

    private static void AssertEnabled(
        IReadOnlyList<EnabledStateRecord> records,
        params (Guid Id, bool Enabled)[] expected)
    {
        foreach (var item in expected)
        {
            Assert.AreEqual(item.Enabled, records.Single(record => record.ModGuid == item.Id).Enabled);
        }
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
