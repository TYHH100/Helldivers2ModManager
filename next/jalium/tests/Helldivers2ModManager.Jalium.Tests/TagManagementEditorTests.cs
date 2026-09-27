using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Tests;

[TestClass]
public sealed class TagManagementEditorTests
{
    [TestMethod]
    public async Task EditsPersistAndReload()
    {
        var root = CreateRoot();
        try
        {
            var settings = CreateSettings(root);
            settings.InitDefault();
            using var editor = new TagManagementEditor(settings);
            var tag = await editor.CreateAsync("  first  ");
            await editor.RenameAsync(tag, "renamed");
            await editor.SetColorAsync(tag, "#FF18A558");

            var reloaded = CreateSettings(root);
            Assert.IsTrue(await reloaded.InitAsync());
            Assert.AreEqual("renamed", reloaded.Tags.Single().Name);
            Assert.AreEqual("#FF18A558", reloaded.Tags.Single().Color);

            await editor.DeleteAsync(tag);
            reloaded = CreateSettings(root);
            Assert.IsTrue(await reloaded.InitAsync());
            Assert.AreEqual(0, reloaded.Tags.Count);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task ReadonlyAndFailedSaveDoNotChangeTags()
    {
        var root = CreateRoot();
        try
        {
            var settings = CreateSettings(root);
            settings.InitDefault(@readonly: true);
            using var readonlyEditor = new TagManagementEditor(settings);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => readonlyEditor.CreateAsync("blocked"));
            Assert.AreEqual(0, settings.Tags.Count);

            var writable = CreateSettings(root);
            writable.InitDefault();
            using var editor = new TagManagementEditor(writable);
            Directory.Delete(root);
            await Assert.ThrowsExceptionAsync<DirectoryNotFoundException>(() => editor.CreateAsync("failed"));
            Assert.AreEqual(0, writable.Tags.Count);
        }
        finally { DeleteRoot(root); }
    }

    private static SettingsService CreateSettings(string root) => new(
        NullLogger<SettingsService>.Instance, Path.Combine(root, "settings.json"), root);

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-tag-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hd2mm-jalium-tag-tests"))
            + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its dedicated directory.");
        if (Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }
}
