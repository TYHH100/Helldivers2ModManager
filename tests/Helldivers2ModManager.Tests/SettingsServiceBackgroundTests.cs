using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;

namespace Helldivers2ModManager.Tests;

[TestClass]
[DoNotParallelize]
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
			service.EnableMusicPlayer = false;
			service.AutoPlayBackgroundMusic = true;
			service.MusicPlayerHorizontalPosition = 0.25;
			service.MusicPlayerVerticalPosition = 0.75;
			service.MusicPlayerVolume = 0.62;
			service.MusicPlayerPlaybackMode = MusicPlaybackMode.Shuffle;
			service.LastMusicTrackRelativePath = Path.Combine("Album", "track.ogg");
            await service.SaveAsync();

            var reloaded = new SettingsService(NullLogger<SettingsService>.Instance);
            Assert.IsTrue(await reloaded.InitAsync(false), "保存后应能重新读取 settings.json");
            Assert.AreEqual(BackgroundMode.Image, reloaded.BackgroundMode);
            Assert.AreEqual(@"C:\someackground.png", reloaded.BackgroundImagePath);
			Assert.AreEqual(0.35f, reloaded.BackgroundOpacity, 0.001f);
			Assert.AreEqual(0.45f, reloaded.CardOpacity, 0.001f);
			Assert.IsFalse(reloaded.EnableMusicPlayer);
			Assert.IsTrue(reloaded.AutoPlayBackgroundMusic);
			Assert.AreEqual(0.25, reloaded.MusicPlayerHorizontalPosition, 0.001);
			Assert.AreEqual(0.75, reloaded.MusicPlayerVerticalPosition, 0.001);
			Assert.AreEqual(0.62, reloaded.MusicPlayerVolume, 0.001);
			Assert.AreEqual(MusicPlaybackMode.Shuffle, reloaded.MusicPlayerPlaybackMode);
			Assert.AreEqual(Path.Combine("Album", "track.ogg"), reloaded.LastMusicTrackRelativePath);
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
			Assert.IsTrue(service.EnableMusicPlayer);
			Assert.IsFalse(service.AutoPlayBackgroundMusic);
			Assert.AreEqual(0.98, service.MusicPlayerHorizontalPosition);
			Assert.AreEqual(0.75, service.MusicPlayerVerticalPosition);
			Assert.AreEqual(0.3, service.MusicPlayerVolume);
			Assert.AreEqual(MusicPlaybackMode.Sequential, service.MusicPlayerPlaybackMode);
			Assert.AreEqual(string.Empty, service.LastMusicTrackRelativePath);
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
