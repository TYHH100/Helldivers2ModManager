using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModServicePatchFileSelectionTests
{
    [TestMethod]
    [DataRow("9ba626afa44a3aa3.patch_7", true, DisplayName = "Main patch")]
    [DataRow("9ba626afa44a3aa3.patch_10", true, DisplayName = "Main patch with multi-digit index")]
    [DataRow("9ba626afa44a3aa3.patch_7.gpu_resources", false, DisplayName = "GPU companion")]
    [DataRow("9ba626afa44a3aa3.patch_7.stream", false, DisplayName = "Stream companion")]
    [DataRow("9ba626afa44a3aa3.patch_7.bak", false, DisplayName = "Backup file")]
    [DataRow("9BA626AFA44A3AA3.patch_7", false, DisplayName = "Uppercase resource ID")]
    [DataRow("9ba626afa44a3aa.patch_7", false, DisplayName = "Short resource ID")]
    public void IsMainPatchFileName_MainAndCompanionNames_MatchesOnlyMainPatch(
        string fileName,
        bool expected)
    {
        var isMainPatch = ModService.IsMainPatchFileName(fileName);

        Assert.AreEqual(expected, isMainPatch);
    }

    [TestMethod]
    public void GetSelectedPatchFiles_EmptyOptionsList_FallsBackToModRoot()
    {
        // V1 manifest 的 "Options": []（如纯文本模组导入产物）必须与无选项等同：
        // 返回模组根目录补丁，否则部署/覆盖扫描/预览全部拿到 0 个补丁。
        var root = Path.Combine(Path.GetTempPath(), "patch_sel_tests_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var patchPath = Path.Combine(root, "9ba626afa44a3aa3.patch_0");
            File.WriteAllBytes(patchPath, [0x11, 0x00, 0x00, 0xF0]);
            File.WriteAllText(Path.Combine(root, "manifest.json"),
                "{\"Version\": 1, \"Guid\": \"eb60bddb-cc86-473a-86c3-99ecb73875df\", \"Name\": \"t\", \"Options\": []}");
            var mod = new ModData(
                new DirectoryInfo(root),
                ModManifest.DeserializeFromDirectory(new DirectoryInfo(root)));

            Assert.IsInstanceOfType(mod.Manifest, typeof(V1ModManifest));
            var service = CreateInitializedModService();
            var files = service.GetSelectedPatchFiles(mod);

            Assert.AreEqual(1, files.Count);
            Assert.AreEqual(patchPath, files[0].FullName, ignoreCase: true);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static ModService CreateInitializedModService()
    {
        var service = new ModService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ModService>.Instance,
            null!, null!, null!, null!, null!, null!);
        var initializedField = typeof(ModService).GetField(
            "<Initialized>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(initializedField);
        initializedField.SetValue(service, true);
        return service;
    }
}
