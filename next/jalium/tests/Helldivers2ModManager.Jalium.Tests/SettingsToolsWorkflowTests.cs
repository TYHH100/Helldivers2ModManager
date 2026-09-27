using Helldivers2ModManager.Jalium.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Tests;

[TestClass]
public sealed class SettingsToolsWorkflowTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-tools-workflow-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "hd2mm-jalium-tools-workflow-tests")) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(_root);
        if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its dedicated directory.");
        if (Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }

    [TestMethod]
    public async Task HardPurgeKeepsUnrelatedAndNestedFiles()
    {
        var storage = Directory.CreateDirectory(Path.Combine(_root, "storage"));
        var data = Directory.CreateDirectory(Path.Combine(_root, "game", "data"));
        var nested = Directory.CreateDirectory(Path.Combine(data.FullName, "nested"));
        File.WriteAllText(Path.Combine(storage.FullName, "installed.txt"), "managed");
        File.WriteAllText(Path.Combine(data.FullName, "0123456789abcdef.patch_0"), "patch");
        File.WriteAllText(Path.Combine(data.FullName, "0123456789abcdef.patch_0.stream"), "stream");
        File.WriteAllText(Path.Combine(data.FullName, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(nested.FullName, "0123456789abcdef.patch_0"), "nested");
        var progress = new List<double>();

        var result = await SettingsToolsWorkflow.HardPurgeAsync(storage.FullName,
            data.Parent!.FullName, progress.Add);

        Assert.AreEqual(3, result.Deleted);
        Assert.AreEqual(0, result.Failed);
        Assert.AreEqual(0, data.GetFiles("*.patch_*").Length);
        Assert.IsFalse(File.Exists(Path.Combine(storage.FullName, "installed.txt")));
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(data.FullName, "keep.txt")));
        Assert.AreEqual("nested", File.ReadAllText(Path.Combine(nested.FullName,
            "0123456789abcdef.patch_0")));
        Assert.AreEqual(1.0, progress[^1]);
    }

    [TestMethod]
    public async Task HardPurgeHonorsCancellationBeforeDeleting()
    {
        var storage = Directory.CreateDirectory(Path.Combine(_root, "storage"));
        var data = Directory.CreateDirectory(Path.Combine(_root, "game", "data"));
        var patch = Path.Combine(data.FullName, "0123456789abcdef.patch_0");
        File.WriteAllText(patch, "patch");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() =>
            SettingsToolsWorkflow.HardPurgeAsync(storage.FullName,
                data.Parent!.FullName, cancellationToken: cancellation.Token));
        Assert.IsTrue(File.Exists(patch));
    }
}
