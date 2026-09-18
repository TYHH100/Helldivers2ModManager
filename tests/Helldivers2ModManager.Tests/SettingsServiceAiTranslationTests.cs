using System.IO;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SettingsServiceAiTranslationTests
{
    [TestMethod]
    public async Task AiTranslationSettingsRoundTripAndEncryptApiKey()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "hd2mm-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var originalDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = tempDir;
            var service = new SettingsService(NullLogger<SettingsService>.Instance);
            service.InitDefault();
            service.AiTranslationEndpoint = "https://example.test/v1/chat/completions";
            service.AiTranslationModel = "test-model";
            service.AiTranslationTargetLanguage = "Japanese";
            service.AiTranslationApiKey = "secret-api-key";
            service.AiTranslationEnableThinking = true;
            service.AiTranslationReasoningEffort = "low";
            await service.SaveAsync();

            var settingsJson = await File.ReadAllTextAsync(Path.Combine(tempDir, "settings.json"));
            Assert.IsFalse(settingsJson.Contains("secret-api-key", StringComparison.Ordinal));

            var reloaded = new SettingsService(NullLogger<SettingsService>.Instance);
            Assert.IsTrue(await reloaded.InitAsync());
            Assert.AreEqual("https://example.test/v1/chat/completions", reloaded.AiTranslationEndpoint);
            Assert.AreEqual("test-model", reloaded.AiTranslationModel);
            Assert.AreEqual("Japanese", reloaded.AiTranslationTargetLanguage);
            Assert.AreEqual("secret-api-key", reloaded.AiTranslationApiKey);
            Assert.IsTrue(reloaded.AiTranslationEnableThinking);
            Assert.AreEqual("low", reloaded.AiTranslationReasoningEffort);

            reloaded.Reset();
            Assert.AreEqual("https://api.deepseek.com/chat/completions", reloaded.AiTranslationEndpoint);
            Assert.AreEqual("deepseek-flash", reloaded.AiTranslationModel);
            Assert.AreEqual("简体中文", reloaded.AiTranslationTargetLanguage);
            Assert.IsNull(reloaded.AiTranslationApiKey);
            Assert.IsFalse(reloaded.AiTranslationEnableThinking);
            Assert.AreEqual("high", reloaded.AiTranslationReasoningEffort);
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
