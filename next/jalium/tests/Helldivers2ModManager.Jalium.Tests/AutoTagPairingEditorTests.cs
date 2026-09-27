using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Tests;

[TestClass]
public sealed class AutoTagPairingEditorTests
{
    [TestMethod]
    public async Task PairingsReuseExistingTagsAndPersistOnlyAfterSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-pairing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new SettingsService(NullLogger<SettingsService>.Instance,
                Path.Combine(root, "settings.json"), root);
            settings.InitDefault();
            var audio = new ModTag("音效");
            var custom = new ModTag("Custom");
            settings.Tags.Add(audio);
            settings.Tags.Add(custom);
            await settings.SaveAsync();
            var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
                GetLanguageDirectory()) { SelectedLanguage = "zh-CN" };

            Guid newTagId;
            using (var editor = new AutoTagPairingEditor(settings, localization))
            {
                var audioRow = editor.Rows.Single(row => row.Type == ModType.Audio);
                Assert.AreEqual(audio.Id, audioRow.SelectedTagId);
                editor.Select(audioRow, custom.Id);
                var modelRow = editor.Rows.Single(row => row.Type == ModType.Model);
                var created = await editor.CreateTagAsync(modelRow, "New model tag");
                newTagId = created.Id;
                Assert.AreEqual(0, settings.AutoTagMappings.Count);
                await editor.SaveAsync();
            }

            using (var unsaved = new AutoTagPairingEditor(settings, localization))
                unsaved.Select(unsaved.Rows.Single(row => row.Type == ModType.Audio), null);
            await settings.ReloadAsync();
            Assert.AreEqual(custom.Id, settings.AutoTagMappings.Single(mapping => mapping.Type == ModType.Audio).TagId);
            Assert.AreEqual(newTagId, settings.AutoTagMappings.Single(mapping => mapping.Type == ModType.Model).TagId);
            Assert.IsTrue(settings.Tags.Any(tag => tag.Id == newTagId));
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hd2mm-jalium-pairing-tests"))
                + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup escaped its dedicated directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static string GetLanguageDirectory([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language");
        throw new DirectoryNotFoundException("Repository language files not found.");
    }
}
