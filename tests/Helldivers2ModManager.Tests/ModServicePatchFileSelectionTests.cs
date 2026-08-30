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
}
