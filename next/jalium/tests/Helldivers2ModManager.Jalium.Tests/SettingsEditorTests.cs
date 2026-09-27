using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Tests;

[TestClass]
public sealed class SettingsEditorTests
{
    [TestMethod]
    public async Task PathsSaveAndCancelRestorePersistedSettings()
    {
        var root = CreateRoot();
        try
        {
            var settings = CreateSettings(root);
            settings.InitDefault();
            var localization = CreateLocalization();
            var editor = new SettingsEditor(settings, localization);
            var game = CreateGame(root);
            editor.SetGameDirectory(Path.Combine(game, "data"));
            editor.SetStorageDirectory(Path.Combine(root, "library"));
            editor.SetTempDirectory(Path.Combine(root, "temp"));
            editor.SetHardLinks(true);
            editor.SetSymbolicLinks(true);
            Assert.IsFalse(settings.UseHardLinks);
            await editor.SaveAsync();

            settings.ShowSeparator = true;
            editor.SetStorageDirectory(Path.Combine(root, "other"));
            await editor.CancelAsync();
            Assert.AreEqual(Path.Combine(root, "library"), settings.StorageDirectory);
            Assert.IsFalse(settings.ShowSeparator);
            Assert.AreEqual(game, settings.GameDirectory);
            Assert.IsTrue(settings.UseSymbolicLinks);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public void RejectsInvalidGamePathAndProtectsDefaultFolders()
    {
        var root = CreateRoot();
        try
        {
            var settings = CreateSettings(root);
            settings.InitDefault();
            var editor = new SettingsEditor(settings, CreateLocalization());
            var original = settings.GameDirectory;
            Assert.ThrowsException<DirectoryNotFoundException>(() => editor.SetGameDirectory(Path.Combine(root, "missing")));
            Assert.AreEqual(original, settings.GameDirectory);
            Assert.IsTrue(editor.AddOrganizationFolder("  Custom  "));
            Assert.IsFalse(editor.AddOrganizationFolder("custom"));
            Assert.IsFalse(editor.RemoveOrganizationFolder("models"));
            Assert.IsTrue(editor.RemoveOrganizationFolder("Custom"));
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task SavedStorageChangeReloadsNewLibraryAndCancelRestoresSavedPath()
    {
        var root = CreateRoot();
        try
        {
            var settings = CreateSettings(root);
            settings.InitDefault();
            settings.GameDirectory = CreateGame(root);
            var oldStorage = Path.Combine(root, "old-library");
            var newStorage = Path.Combine(root, "new-library");
            settings.StorageDirectory = oldStorage;
            settings.TempDirectory = Path.Combine(root, "temp");
            AddMod(oldStorage, "Old");
            AddMod(newStorage, "New");
            await settings.SaveAsync();

            using var database = new DatabaseService(NullLogger<DatabaseService>.Instance);
            var groupRepository = new ModGroupRepository(NullLogger<ModGroupRepository>.Instance, database);
            var localization = CreateLocalization();
            var groups = new ModGroupService(NullLogger<ModGroupService>.Instance, groupRepository, localization);
            var library = new DashboardLibraryService(
                new ModCatalogService(NullLogger<ModCatalogService>.Instance),
                new EnabledDataRepository(NullLogger<EnabledDataRepository>.Instance, database),
                NullLogger<DashboardLibraryService>.Instance);
            var initial = await DashboardWorkspace.OpenAsync(settings, library, groups, groupRepository);
            Assert.AreEqual("Old", initial.Mods.Single().Manifest.Name);

            var editor = new SettingsEditor(settings, localization);
            editor.SetStorageDirectory(newStorage);
            await editor.SaveAsync();
            var reopened = await DashboardWorkspace.OpenAsync(settings, library, groups, groupRepository);
            Assert.AreEqual("New", reopened.Mods.Single().Manifest.Name);

            editor.SetStorageDirectory(oldStorage);
            await editor.CancelAsync();
            Assert.AreEqual(newStorage, settings.StorageDirectory);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteRoot(root);
        }
    }

    private static void AddMod(string storage, string name)
    {
        var directory = Directory.CreateDirectory(Path.Combine(storage, "Mods", name));
        ModManifest.SaveToFile(new LegacyModManifest
        {
            Guid = Guid.NewGuid(), Name = name, Description = string.Empty,
        }, directory);
    }

    private static SettingsService CreateSettings(string root) => new(
        NullLogger<SettingsService>.Instance, Path.Combine(root, "settings.json"), root);

    private static LocalizationService CreateLocalization() => new(
        NullLogger<LocalizationService>.Instance, GetLanguageDirectory());

    private static string CreateGame(string root)
    {
        var game = Path.Combine(root, "Helldivers 2");
        Directory.CreateDirectory(Path.Combine(game, "data"));
        Directory.CreateDirectory(Path.Combine(game, "tools"));
        Directory.CreateDirectory(Path.Combine(game, "bin"));
        File.WriteAllBytes(Path.Combine(game, "bin", "helldivers2.exe"), []);
        return game;
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hd2mm-jalium-settings-tests"))
            + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its dedicated directory.");
        if (Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }

    private static string GetLanguageDirectory([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language");
        throw new DirectoryNotFoundException("Repository language files not found.");
    }
}
