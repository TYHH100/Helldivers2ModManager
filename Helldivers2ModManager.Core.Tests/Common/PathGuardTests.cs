using Helldivers2ModManager.Core.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Common;

[TestClass]
public sealed class PathGuardTests
{
    [TestMethod]
    public void EnsureInside_ShouldNormalizePathInsideRoot()
    {
        var result = PathGuard.EnsureInside(@"D:\mods\", @"D:\mods\sub\manifest.json");

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(@"D:\mods\sub\manifest.json", result.Value);
    }

    [TestMethod]
    public void EnsureInside_ShouldAllowExactRoot()
    {
        var result = PathGuard.EnsureInside(@"D:\mods", @"D:\mods");

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(@"D:\mods", result.Value);
    }

    [TestMethod]
    public void EnsureInside_ShouldRejectParentTraversal()
    {
        var result = PathGuard.EnsureInside(@"D:\mods", @"D:\mods\..\game\patch_0");

        Assert.IsTrue(result.Failed);
        Assert.AreEqual(CoreErrorCode.PathOutsideRoot, result.Error.Code);
    }

    [TestMethod]
    public void EnsureInside_ShouldRejectDifferentRoot()
    {
        var result = PathGuard.EnsureInside(@"D:\mods", @"C:\Windows\system32\cmd.exe");

        Assert.IsTrue(result.Failed);
        Assert.AreEqual(CoreErrorCode.PathOutsideRoot, result.Error.Code);
    }

    [TestMethod]
    public void EnsureInside_ShouldRejectRelativePath()
    {
        var result = PathGuard.EnsureInside(@"D:\mods", @"sub\manifest.json");

        Assert.IsTrue(result.Failed);
        Assert.AreEqual(CoreErrorCode.InvalidInput, result.Error.Code);
    }

    [TestMethod]
    public void EnsureInside_ShouldRejectEmptyInput()
    {
        Assert.IsTrue(PathGuard.EnsureInside("", @"D:\mod").Failed);
        Assert.IsTrue(PathGuard.EnsureInside(@"D:\mods", " ").Failed);
    }
}
