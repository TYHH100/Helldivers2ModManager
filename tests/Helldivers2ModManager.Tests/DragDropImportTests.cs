using System.Reflection;
using System.Windows;
using Helldivers2ModManager;
using Helldivers2ModManager.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// 拖拽导入（压缩包拖到主窗口导入）相关纯逻辑测试：
/// - 主窗口从 FileDrop 数据中过滤支持的压缩包扩展名；
/// - Dashboard / 部署顺序页面的 IDropTarget 识别文件拖拽并拒绝进入排序拖拽管线。
/// </summary>
[TestClass]
public sealed class DragDropImportTests
{
    private static readonly MethodInfo s_getArchivePaths = typeof(MainWindow)
        .GetMethod("GetArchivePaths", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("MainWindow.GetArchivePaths not found");

    private static readonly MethodInfo s_dashboardIsFileDrop = typeof(DashboardPageViewModel)
        .GetMethod("IsFileDrop", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DashboardPageViewModel.IsFileDrop not found");

    private static readonly MethodInfo s_deploymentIsFileDrop = typeof(DeploymentOrderPageViewModel)
        .GetMethod("IsFileDrop", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DeploymentOrderPageViewModel.IsFileDrop not found");

    // ========== GetArchivePaths：压缩包过滤 ==========

    [TestMethod]
    public void GetArchivePaths_MixedFiles_ReturnsOnlySupportedArchives()
    {
        var data = new DataObject(DataFormats.FileDrop, new[]
        {
            @"C:\mods\weapon_mod.zip",
            @"C:\mods\armor.7Z",   // 大小写不敏感
            @"C:\mods\helmet.rar",
            @"C:\mods\cape.tar",
            @"C:\mods\readme.txt",
            @"C:\mods\noextension",
            @"C:\mods\folder"
        });

        var result = (string[])s_getArchivePaths.Invoke(null, [data])!;

        CollectionAssert.AreEquivalent(
            new[] { @"C:\mods\weapon_mod.zip", @"C:\mods\armor.7Z", @"C:\mods\helmet.rar", @"C:\mods\cape.tar" },
            result);
    }

    [TestMethod]
    public void GetArchivePaths_NoSupportedArchives_ReturnsEmpty()
    {
        var data = new DataObject(DataFormats.FileDrop, new[] { @"C:\mods\readme.txt", @"C:\mods\notes.md" });

        var result = (string[])s_getArchivePaths.Invoke(null, [data])!;

        Assert.AreEqual(0, result.Length);
    }

    [TestMethod]
    public void GetArchivePaths_EmptyDrop_ReturnsEmpty()
    {
        var data = new DataObject(DataFormats.FileDrop, Array.Empty<string>());

        var result = (string[])s_getArchivePaths.Invoke(null, [data])!;

        Assert.AreEqual(0, result.Length);
    }

    [TestMethod]
    public void GetArchivePaths_NonFileDropData_ReturnsEmpty()
    {
        // 非 FileDrop 数据（如内部拖拽对象）应返回空
        var data = new DataObject("custom-format", "payload");

        var result = (string[])s_getArchivePaths.Invoke(null, [data])!;

        Assert.AreEqual(0, result.Length);
    }

    // ========== IsFileDrop：文件拖拽识别（gong 排序拖拽防护） ==========

    [TestMethod]
    public void IsFileDrop_StringArrayData_ReturnsTrue()
    {
        // gong 解析文件拖拽后 DropInfo.Data 是 FileDrop 的 string[]
        object[] args = [new[] { @"C:\mods\a.zip" }];

        Assert.IsTrue((bool)s_dashboardIsFileDrop.Invoke(null, args)!);
        Assert.IsTrue((bool)s_deploymentIsFileDrop.Invoke(null, args)!);
    }

    [TestMethod]
    public void IsFileDrop_FileDropDataObject_ReturnsTrue()
    {
        // 兜底场景：DropInfo.Data 仍是原始 IDataObject
        object[] args = [new DataObject(DataFormats.FileDrop, new[] { @"C:\mods\a.zip" })];

        Assert.IsTrue((bool)s_dashboardIsFileDrop.Invoke(null, args)!);
        Assert.IsTrue((bool)s_deploymentIsFileDrop.Invoke(null, args)!);
    }

    [TestMethod]
    public void IsFileDrop_InternalDragData_ReturnsFalse()
    {
        Assert.IsFalse((bool)s_dashboardIsFileDrop.Invoke(null, [new object()])!);
        Assert.IsFalse((bool)s_dashboardIsFileDrop.Invoke(null, ["plain string"])!);
        Assert.IsFalse((bool)s_deploymentIsFileDrop.Invoke(null, [new object()])!);
    }
}
