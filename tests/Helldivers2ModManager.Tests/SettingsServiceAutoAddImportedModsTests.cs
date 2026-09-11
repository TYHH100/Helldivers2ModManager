using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class SettingsServiceAutoAddImportedModsTests
{
    [TestMethod]
    public async Task AutoAddImportedModsToActiveProfile_RoundTrip()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "hd2mm-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var originalDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = tempDir;

            var service = new SettingsService(NullLogger<SettingsService>.Instance);
            Assert.IsFalse(await service.InitAsync(false), "临时目录不应有 settings.json");
            service.InitDefault(false);

            service.AutoAddImportedModsToActiveProfile = true;
            await service.SaveAsync();

            var reloaded = new SettingsService(NullLogger<SettingsService>.Instance);
            Assert.IsTrue(await reloaded.InitAsync(false), "保存后应能重新读取 settings.json");
            Assert.IsTrue(reloaded.AutoAddImportedModsToActiveProfile);
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task AutoAddImportedModsToActiveProfile_DefaultsToFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "hd2mm-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var originalDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = tempDir;

            var service = new SettingsService(NullLogger<SettingsService>.Instance);
            Assert.IsFalse(await service.InitAsync(false));
            service.InitDefault(false);

            Assert.IsFalse(service.AutoAddImportedModsToActiveProfile);
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}