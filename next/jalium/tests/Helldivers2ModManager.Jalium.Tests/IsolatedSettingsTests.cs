using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Tests;

[TestClass]
public sealed class IsolatedSettingsTests
{
    [TestMethod]
    public async Task ExplicitPaths_KeepSettingsAndDeploymentOrderInIsolatedRoot()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-settings-tests");
        var root = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            var dataRoot = Path.Combine(root, "data");
            var id = Guid.NewGuid();
            var settings = new SettingsService(NullLogger<SettingsService>.Instance, settingsPath, dataRoot);
            settings.InitDefault();

            Assert.AreEqual(dataRoot, settings.StorageDirectory);
            Assert.AreEqual(Path.Combine(dataRoot, "temp"), settings.TempDirectory);
            Assert.IsTrue(settings.EnableMusicPlayer);
            settings.UseDeploymentOrder = true;
            settings.DeploymentOrderGuids.Add(id);
            await settings.SaveAsync();

            var reloaded = new SettingsService(NullLogger<SettingsService>.Instance, settingsPath, dataRoot);
            Assert.IsTrue(await reloaded.InitAsync());
            Assert.AreEqual(dataRoot, reloaded.StorageDirectory);
            Assert.IsTrue(reloaded.UseDeploymentOrder);
            CollectionAssert.AreEqual(new[] { id }, reloaded.DeploymentOrderGuids);
        }
        finally
        {
            var resolvedRoot = Path.GetFullPath(root);
            var allowedRoot = Path.GetFullPath(testRoot) + Path.DirectorySeparatorChar;
            if (resolvedRoot.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(resolvedRoot, recursive: true);
        }
    }
}
