using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class PatchResourceInspectionDirectoryStateTests
{
    [TestMethod]
    public void RefreshDirectoryExists_DirectoryCreatedAfterInitialCheck_IsAvailableWithoutRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "hd2mm-preview-directory-state", Guid.NewGuid().ToString("N"));
        var directory = new DirectoryInfo(path);

        try
        {
            Assert.IsFalse(directory.Exists);
            Directory.CreateDirectory(path);

            Assert.IsTrue(PatchResourceInspectionService.RefreshDirectoryExists(directory));
        }
        finally
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }
}
