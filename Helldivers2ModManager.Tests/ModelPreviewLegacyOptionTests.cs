using System.Reflection;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewLegacyOptionTests
{
    [TestMethod]
    public void LegacyVariantSelector_ChangesPreviewPatchFolderWithoutMutatingDashboardSelection()
    {
        var mod = LoadFixture();
        var legacy = (LegacyModManifest)mod.Manifest;
        var service = CreateInitializedModService();
        var originalDashboardSelection = mod.SelectedOptions.ToArray();
        var selectionChangedCount = 0;
        var selector = new ModelPreviewOptionViewModel(
            "Legacy variants",
            legacy.Options!,
            mod.SelectedOptions[0],
            () => selectionChangedCount++);

        selector.SelectedSubOption = selector.SubOptions[1];
        var previewFiles = service.GetSelectedPatchFiles(
            mod,
            mod.EnabledOptions,
            [selector.SelectedSubOptionIndex]);
        var relativeFiles = previewFiles
            .Select(file => Path.GetRelativePath(mod.Directory.FullName, file.FullName).Replace('/', '\\'))
            .ToArray();

        Assert.IsFalse(selector.CanToggle);
        Assert.AreEqual(1, selectionChangedCount);
        Assert.AreEqual(1, selector.SelectedSubOptionIndex);
        CollectionAssert.AreEqual(new[] { "有尾巴\\9ba626afa44a3aa3.patch_10" }, relativeFiles);
        CollectionAssert.AreEqual(originalDashboardSelection, mod.SelectedOptions);
    }

    private static ModData LoadFixture()
    {
        var directory = new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName,
            "Test", "Mods", "Mods",
            "VRC_瑞希 寄染赛车服 替换 CM-10全套 + EX00全套 +CM17头+无畏头_02508ace"));
        return new ModData(directory, ModManifest.DeserializeFromDirectory(directory));
    }

    private static ModService CreateInitializedModService()
    {
        var service = new ModService(
            NullLogger<ModService>.Instance,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
        var initializedField = typeof(ModService).GetField(
            "<Initialized>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(initializedField);
        initializedField.SetValue(service, true);
        return service;
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory.Parent is not null && !File.Exists(Path.Combine(directory.FullName, "Helldivers2ModManager.sln")))
            directory = directory.Parent;
        Assert.IsTrue(File.Exists(Path.Combine(directory.FullName, "Helldivers2ModManager.sln")));
        return directory;
    }
}
