using System.Reflection;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
[TestCategory("Fixture")]
public sealed class ModelPreviewOptionSelectionTests
{
    [TestMethod]
    public void GetSelectedPatchFiles_PlumPreviewOptions_SelectsOneMaterialAndIndependentAccessories()
    {
        var mod = LoadFixture("【学園制服】Plum 替换 CW-9+CE-27+I-92");
        var service = CreateInitializedModService();
        var enabled = Enumerable.Repeat(true, 7).ToArray();
        var selected = new int[7];

        var materialA = GetRelativePatchPaths(service.GetSelectedPatchFiles(mod, enabled, selected), mod.Directory);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "Material\\A\\9ba626afa44a3aa3.patch_0",
                "Model\\本体\\9ba626afa44a3aa3.patch_7",
                "Model\\衣服\\9ba626afa44a3aa3.patch_10",
                "Model\\弹挂\\9ba626afa44a3aa3.patch_8",
                "Model\\尾巴\\9ba626afa44a3aa3.patch_12",
                "Model\\袜子\\9ba626afa44a3aa3.patch_11",
                "Model\\鞋子\\9ba626afa44a3aa3.patch_9"
            },
            materialA);
        Assert.IsFalse(materialA.Any(path => path.StartsWith("Material\\B\\", StringComparison.OrdinalIgnoreCase)));

        selected[1] = 1;
        var materialB = GetRelativePatchPaths(service.GetSelectedPatchFiles(mod, enabled, selected), mod.Directory);
        Assert.IsTrue(materialB.Contains("Material\\B\\9ba626afa44a3aa3.patch_0"));
        Assert.IsFalse(materialB.Any(path => path.StartsWith("Material\\A\\", StringComparison.OrdinalIgnoreCase)));

        enabled[5] = false;
        var withoutSocks = GetRelativePatchPaths(service.GetSelectedPatchFiles(mod, enabled, selected), mod.Directory);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "Material\\B\\9ba626afa44a3aa3.patch_0",
                "Model\\本体\\9ba626afa44a3aa3.patch_7",
                "Model\\衣服\\9ba626afa44a3aa3.patch_10",
                "Model\\弹挂\\9ba626afa44a3aa3.patch_8",
                "Model\\尾巴\\9ba626afa44a3aa3.patch_12",
                "Model\\鞋子\\9ba626afa44a3aa3.patch_9"
            },
            withoutSocks);
    }

    [TestMethod]
    public void GetSelectedPatchFiles_StarWingPreviewOptions_SelectsEnabledWeaponsAndOneSubOptionPerWeapon()
    {
        var mod = LoadFixture("星之翼 风 武装美化包");
        var service = CreateInitializedModService();
        bool[] enabled = [true, false, false, false, true, true, true];
        int[] selected = [0, 0, 0, 0, 0, 0, 1];

        var antiMaterielScope = GetRelativePatchPaths(service.GetSelectedPatchFiles(mod, enabled, selected), mod.Directory);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "ar59\\9ba626afa44a3aa3.patch_0",
                "cz\\9ba626afa44a3aa3.patch_0",
                "fqc\\9ba626afa44a3aa3.patch_0",
                "bj\\9ba626afa44a3aa3.patch_0",
                "js\\9ba626afa44a3aa3.patch_0"
            },
            antiMaterielScope);
        Assert.IsFalse(antiMaterielScope.Any(path => path.StartsWith("jsj\\", StringComparison.OrdinalIgnoreCase)));

        selected[5] = 1;
        selected[6] = 0;
        var acceleratorScope = GetRelativePatchPaths(service.GetSelectedPatchFiles(mod, enabled, selected), mod.Directory);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "ar59\\9ba626afa44a3aa3.patch_0",
                "cz\\9ba626afa44a3aa3.patch_0",
                "fqc\\9ba626afa44a3aa3.patch_0",
                "js\\9ba626afa44a3aa3.patch_0",
                "jsj\\9ba626afa44a3aa3.patch_0"
            },
            acceleratorScope);
        Assert.IsFalse(acceleratorScope.Any(path => path.StartsWith("bj\\", StringComparison.OrdinalIgnoreCase)));

        enabled[5] = false;
        var antiMaterielDisabled = GetRelativePatchPaths(service.GetSelectedPatchFiles(mod, enabled, selected), mod.Directory);
        Assert.IsFalse(antiMaterielDisabled.Any(path => path.StartsWith("fqc\\", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(antiMaterielDisabled.Any(path => path.StartsWith("bj\\", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(antiMaterielDisabled.Contains("js\\9ba626afa44a3aa3.patch_0"));
        Assert.IsTrue(antiMaterielDisabled.Contains("jsj\\9ba626afa44a3aa3.patch_0"));
    }

    [TestMethod]
    public void PreviewOptionState_ChangesPreviewPatchSetWithoutMutatingDeploymentOptionState()
    {
        var mod = LoadFixture("【学園制服】Plum 替换 CW-9+CE-27+I-92");
        var service = CreateInitializedModService();
        var deploymentEnabledReference = mod.EnabledOptions;
        var deploymentSelectedReference = mod.SelectedOptions;
        var expectedDeploymentEnabled = mod.EnabledOptions.ToArray();
        var expectedDeploymentSelected = mod.SelectedOptions.ToArray();
        Assert.IsInstanceOfType<V1ModManifest>(mod.Manifest);
        var materialOption = ((V1ModManifest)mod.Manifest).Options![1];
        var callbackCount = 0;
        var previewOption = new ModelPreviewOptionViewModel(
            1,
            materialOption,
            mod.Directory,
            enabled: true,
            selectedSubOption: 0,
            () => callbackCount++);

        previewOption.Enabled = false;
        previewOption.SelectedSubOption = previewOption.SubOptions[1];
        var previewEnabled = mod.EnabledOptions.ToArray();
        var previewSelected = mod.SelectedOptions.ToArray();
        previewEnabled[1] = previewOption.Enabled;
        previewSelected[1] = previewOption.SelectedSubOptionIndex;
        var previewFiles = GetRelativePatchPaths(
            service.GetSelectedPatchFiles(mod, previewEnabled, previewSelected),
            mod.Directory);

        Assert.AreEqual(2, callbackCount);
        Assert.IsFalse(previewFiles.Any(path => path.StartsWith("Material\\", StringComparison.OrdinalIgnoreCase)));
        Assert.AreSame(deploymentEnabledReference, mod.EnabledOptions);
        Assert.AreSame(deploymentSelectedReference, mod.SelectedOptions);
        CollectionAssert.AreEqual(expectedDeploymentEnabled, mod.EnabledOptions);
        CollectionAssert.AreEqual(expectedDeploymentSelected, mod.SelectedOptions);
    }

    private static ModData LoadFixture(string directoryName)
    {
        var directory = new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName,
            "Test", "Mods", "Mods", directoryName));
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
            null!,
            null!);
        var initializedField = typeof(ModService).GetField(
            "<Initialized>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(initializedField, "The test requires the existing two-phase initialization guard backing field.");
        initializedField.SetValue(service, true);
        return service;
    }

    private static string[] GetRelativePatchPaths(IEnumerable<FileInfo> files, DirectoryInfo modDirectory) =>
        files
            .Select(file => Path.GetRelativePath(modDirectory.FullName, file.FullName).Replace('/', '\\'))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (DirectoryInfo? current = new(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return current;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root for the model-preview fixtures.");
    }
}
