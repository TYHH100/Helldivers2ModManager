using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Tests;

[TestClass]
public sealed class DeploymentOrderEditorTests
{
    private string _root = null!;
    private SettingsService _settings = null!;
    private LocalizationService _localization = null!;
    private ModData[] _mods = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-order-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _settings = new SettingsService(
            NullLogger<SettingsService>.Instance,
            Path.Combine(_root, "settings.json"),
            Path.Combine(_root, "data"));
        _settings.InitDefault();
        _settings.UseDeploymentOrder = true;
        _localization = new LocalizationService(NullLogger<LocalizationService>.Instance, GetLanguageDirectory());
        _localization.SelectedLanguage = "zh-CN";
        _mods = [CreateMod("A"), CreateMod("B"), CreateMod("C")];
    }

    [TestCleanup]
    public void Cleanup()
    {
        var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hd2mm-jalium-order-tests")) + Path.DirectorySeparatorChar;
        var resolvedRoot = Path.GetFullPath(_root);
        if (resolvedRoot.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedRoot))
            Directory.Delete(resolvedRoot, recursive: true);
    }

    [TestMethod]
    public void Load_KeepsSavedOrderAndDeletedModPlaceholder()
    {
        var deleted = Guid.NewGuid();
        _settings.DeploymentOrderGuids.AddRange([_mods[1].Manifest.Guid, deleted]);

        using var editor = CreateEditor();

        CollectionAssert.AreEqual(new[] { "B", _localization["DeploymentOrderPage.DeletedModPlaceholder"], "A", "C" },
            editor.Items.Select(item => item.Name).ToArray());
        Assert.AreEqual(deleted, editor.Items[1].Guid);
    }

    [TestMethod]
    public async Task MoveToTop_PersistsOrderAndCanReload()
    {
        using var editor = CreateEditor();
        editor.Items[2].IsSelected = true;
        Assert.IsTrue(editor.CanMoveToTop);

        await editor.MoveToTopAsync();

        CollectionAssert.AreEqual(new[] { "C", "A", "B" }, editor.Items.Select(item => item.Name).ToArray());
        var reloaded = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(_root, "settings.json"), Path.Combine(_root, "data"));
        Assert.IsTrue(await reloaded.InitAsync());
        CollectionAssert.AreEqual(new[] { _mods[2].Manifest.Guid, _mods[0].Manifest.Guid, _mods[1].Manifest.Guid },
            reloaded.DeploymentOrderGuids);
    }

    [TestMethod]
    public async Task SyncAndMultiSelectDrop_UseDashboardOrderAndKeepSelectedOrder()
    {
        using var editor = CreateEditor(() => [_mods[2].Manifest.Guid, _mods[0].Manifest.Guid, _mods[1].Manifest.Guid]);
        await editor.ClearOrderAsync();
        await editor.SyncFromDashboardAsync();
        CollectionAssert.AreEqual(new[] { "C", "A", "B" }, editor.Items.Select(item => item.Name).ToArray());

        editor.Items[0].IsSelected = true;
        editor.Items[1].IsSelected = true;
        await editor.MoveByDropAsync(editor.Items[0], editor.Items.Count);

        CollectionAssert.AreEqual(new[] { "B", "C", "A" }, editor.Items.Select(item => item.Name).ToArray());
        CollectionAssert.AreEqual(editor.Items.Select(item => item.Guid).ToArray(), _settings.DeploymentOrderGuids);
    }

    [TestMethod]
    public async Task FailedSave_RestoresPreviousOrder()
    {
        using var editor = CreateEditor();
        editor.Items[2].IsSelected = true;
        Directory.Delete(_root, recursive: true);

        await Assert.ThrowsExceptionAsync<DirectoryNotFoundException>(() => editor.MoveToTopAsync());

        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, editor.Items.Select(item => item.Name).ToArray());
        CollectionAssert.AreEqual(_mods.Select(mod => mod.Manifest.Guid).ToArray(), _settings.DeploymentOrderGuids);
    }

    private DeploymentOrderEditor CreateEditor(Func<IReadOnlyList<Guid>?>? dashboardOrder = null)
        => new(_settings, _localization, _mods, dashboardOrder ?? (() => null));

    private ModData CreateMod(string name)
    {
        var dir = new DirectoryInfo(Path.Combine(_root, "mods", name));
        dir.Create();
        return new ModData(dir, new LegacyModManifest
        {
            Guid = Guid.NewGuid(),
            Name = name,
            Description = string.Empty,
        });
    }

    private static string GetLanguageDirectory([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language");
        throw new DirectoryNotFoundException("Repository language files not found.");
    }
}
