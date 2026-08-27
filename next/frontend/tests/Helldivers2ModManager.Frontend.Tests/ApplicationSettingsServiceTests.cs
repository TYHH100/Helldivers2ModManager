using System.IO;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Frontend.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Frontend.Tests;

[TestClass]
public sealed class ApplicationSettingsServiceTests
{
    [TestMethod]
    public async Task Initialize_NormalizesLegacyDuplicatedPathsAndSavePreservesCollections()
    {
        var root = Path.Combine(Path.GetTempPath(), "Helldivers2ModManagerFrontendTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new ApplicationPaths(root);
            Directory.CreateDirectory(paths.Data);
            await BootConfigurationStore.SaveAsync(new BootConfiguration
            {
                StorageDirectory = Path.Combine(paths.Data, "data", "mods"),
                TempDirectory = Path.Combine(paths.Data, "data", "temp"),
            }, paths.Boot);

            var tagId = Guid.NewGuid();
            using var database = new Database(paths.Database);
            var preferences = new PreferenceRepository(database);
            await preferences.SetAppSettingsAsync("frontend.app", new AppSettings
            {
                StorageDirectory = Path.Combine(paths.Data, "data", "mods"),
                TempDirectory = Path.Combine(paths.Data, "data", "temp"),
                Tags = [new TagSetting(tagId, "Armor", "#FF60CDFF")],
                DeploymentOrderGuids = [tagId],
            });

            var service = new ApplicationSettingsService(paths, preferences);
            await service.InitializeAsync();

            Assert.AreEqual(paths.Data, service.Current.StorageDirectory);
            Assert.AreEqual(paths.Temp, service.Current.TempDirectory);
            Assert.AreEqual(paths.Data, BootConfigurationStore.Load(paths.Boot)!.StorageDirectory);

            service.Current.GameDirectory = @"C:\Games\Helldivers 2";
            await service.SaveAsync(service.Current);

            var saved = await preferences.GetAppSettingsAsync("frontend.app");
            Assert.IsNotNull(saved);
            Assert.AreEqual(@"C:\Games\Helldivers 2", saved.GameDirectory);
            Assert.AreEqual(1, saved.Tags.Count);
            Assert.AreEqual("Armor", saved.Tags[0].Name);
            Assert.AreEqual(tagId, saved.DeploymentOrderGuids.Single());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
