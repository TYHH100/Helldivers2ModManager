using System.IO;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Core.Profiles;
using Helldivers2ModManager.Frontend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Frontend.Tests;

[TestClass]
public sealed class AutoTagPairingServiceTests
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
    public async Task CreateTypeTagAsync_CreatesLocalizedTagAndRejectsDuplicate()
    {
        var service = CreateService(out _);
        var tag = await service.CreateTypeTagAsync(ModType.Armor);

        Assert.IsFalse(Guid.Empty == tag.Id);
        Assert.IsFalse(string.IsNullOrWhiteSpace(tag.Name));
        Assert.AreEqual("#10B981", tag.Color);
        try
        {
            await service.CreateTypeTagAsync(ModType.Armor);
            Assert.Fail("Expected duplicate tag creation to fail.");
        }
        catch (InvalidOperationException)
        {
        }
    }

    [TestMethod]
    public async Task SaveAsync_PersistsOnlySelectedMappings()
    {
        var service = CreateService(out _);
        var first = await service.CreateTypeTagAsync(ModType.Audio);
        var second = await service.CreateTypeTagAsync(ModType.Script);

        await service.SaveAsync([new((int)ModType.Audio, first.Id)]);

        Assert.AreEqual(first.Id, service.GetMapping(ModType.Audio));
        Assert.IsNull(service.GetMapping(ModType.Script));
        Assert.AreEqual(first.Id, service.GetExistingTagForType(ModType.Audio));
        Assert.AreEqual(second.Id, service.GetExistingTagForType(ModType.Script));
    }

    private AutoTagPairingService CreateService(out ApplicationPaths paths)
    {
        _root = Path.Combine(Path.GetTempPath(), "Helldivers2ModManagerFrontendTests", Guid.NewGuid().ToString("N"));
        paths = new ApplicationPaths(_root);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCommon();
        services.AddPersistence(paths.Database);
        services.AddMods();
        services.AddProfiles();
        services.AddSingleton(paths);
        services.AddSingleton<ApplicationSettingsService>();
        services.AddSingleton<LocalizationCatalog>();
        services.AddSingleton<AutoTagPairingService>();
        _provider = services.BuildServiceProvider();
        var provider = _provider;
        provider.GetRequiredService<ApplicationSettingsService>().InitializeAsync().GetAwaiter().GetResult();
        return provider.GetRequiredService<AutoTagPairingService>();
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
