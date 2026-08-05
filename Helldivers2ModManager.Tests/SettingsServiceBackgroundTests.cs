using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class SettingsServiceBackgroundTests
{
    [TestMethod]
    public async Task BackgroundSettingsRoundTrip()
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

            service.BackgroundMode = BackgroundMode.Image;
            service.BackgroundImagePath = @"C:\someackground.png";
            service.BackgroundOpacity = 0.35f;
            service.CardOpacity = 0.45f;
            await service.SaveAsync();

            var reloaded = new SettingsService(NullLogger<SettingsService>.Instance);
            Assert.IsTrue(await reloaded.InitAsync(false), "保存后应能重新读取 settings.json");
            Assert.AreEqual(BackgroundMode.Image, reloaded.BackgroundMode);
            Assert.AreEqual(@"C:\someackground.png", reloaded.BackgroundImagePath);
            Assert.AreEqual(0.35f, reloaded.BackgroundOpacity, 0.001f);
            Assert.AreEqual(0.45f, reloaded.CardOpacity, 0.001f);
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task BackgroundSettingsDefaultsToOff()
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

            Assert.AreEqual(BackgroundMode.Default, service.BackgroundMode);
            Assert.AreEqual(string.Empty, service.BackgroundImagePath);
            Assert.AreEqual(0.6f, service.BackgroundOpacity, 0.001f);
            Assert.AreEqual(0.7f, service.CardOpacity, 0.001f);
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
