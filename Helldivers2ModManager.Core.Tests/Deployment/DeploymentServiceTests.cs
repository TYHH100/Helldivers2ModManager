using Helldivers2ModManager.Core.Deployment;
using Helldivers2ModManager.Core.Mods;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Deployment;

[TestClass]
public sealed class DeploymentServiceTests
{
    [TestMethod]
    public void GroupPatchFiles_ShouldCreateUnionIndexesWithMissingCompanions()
    {
        var root = CreateTempDirectory();
        try
        {
            var patch = new FileInfo(Path.Combine(root, "0123456789abcdef.patch_0"));
            var gpu = new FileInfo(Path.Combine(root, "0123456789abcdef.patch_0.gpu_resources"));
            var stream = new FileInfo(Path.Combine(root, "0123456789abcdef.patch_0.stream"));
            var grouped = DeploymentService.GroupPatchFiles([patch, gpu, stream]);
            var triplet = grouped["0123456789abcdef"].Single();

            Assert.AreEqual(patch, triplet.Patch);
            Assert.AreEqual(gpu, triplet.GpuResources);
            Assert.AreEqual(stream, triplet.Stream);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CreatePlan_ShouldAssignLaterIndexesToConflictingBaseNames()
    {
        var root = CreateTempDirectory();
        try
        {
            var modA = CreateLegacyMod(Path.Combine(root, "A"), "aaaaaaaaaaaaaaaa");
            var modB = CreateLegacyMod(Path.Combine(root, "B"), "aaaaaaaaaaaaaaaa");
            var inputs = new[] { CreateInput(modA), CreateInput(modB) };
            var options = DeploymentOptions.Copy(new DirectoryInfo(Path.Combine(root, "data")));
            var plan = new DeploymentService().CreatePlan(inputs, options);
            var mainFiles = plan.Files.Where(static item => item.SourcePath is not null).ToArray();

            Assert.AreEqual(2, mainFiles.Length);
            Assert.AreEqual("aaaaaaaaaaaaaaaa.patch_0", Path.GetFileName(mainFiles[0].DestinationPath));
            Assert.AreEqual("aaaaaaaaaaaaaaaa.patch_1", Path.GetFileName(mainFiles[1].DestinationPath));
            Assert.AreEqual(4, plan.Files.Count - mainFiles.Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DeploymentOrderBuilder_ShouldUseExplicitOrderAndReverseDirection()
    {
        var first = CreateInput(new DirectoryInfo(@"C:\first"));
        var second = CreateInput(new DirectoryInfo(@"C:\second"));
        var third = CreateInput(new DirectoryInfo(@"C:\third"));
        var ordered = DeploymentOrderBuilder.Build([first, second, third], [first.Guid, third.Guid, second.Guid], true, [third.Guid, first.Guid], false);

        CollectionAssert.AreEqual(new[] { third.Guid, first.Guid, second.Guid }, ordered.Select(static input => input.Guid).ToArray());
        var reversed = DeploymentOrderBuilder.Build([first, second, third], null, false, [], true);
        CollectionAssert.AreEqual(new[] { third.Guid, second.Guid, first.Guid }, reversed.Select(static input => input.Guid).ToArray());
    }

    [TestMethod]
    public void DeploymentOrderBuilder_GenericOverloadOrdersByKeySelector()
    {
        var first = new OrderItem(Guid.NewGuid(), "first");
        var second = new OrderItem(Guid.NewGuid(), "second");
        var third = new OrderItem(Guid.NewGuid(), "third");
        var ordered = DeploymentOrderBuilder.Build(
            [first, second, third], item => item.Guid, null, true, [third.Guid, first.Guid], false);
        var reversed = DeploymentOrderBuilder.Build(
            [first, second, third], item => item.Guid, null, false, [], true);
        CollectionAssert.AreEqual(new[] { "third", "first", "second" }, ordered.Select(item => item.Label).ToArray());
        CollectionAssert.AreEqual(new[] { "third", "second", "first" }, reversed.Select(item => item.Label).ToArray());
    }

    [TestMethod]
    public async Task DeployAsync_ShouldPurgeCopyPlaceholdersAndHonorSkipList()
    {
        var root = CreateTempDirectory();
        try
        {
            var data = Directory.CreateDirectory(Path.Combine(root, "data"));
            await File.WriteAllTextAsync(Path.Combine(data.FullName, "fedcba9876543210.patch_9"), "old");
            var mod = CreateLegacyMod(Path.Combine(root, "Mod"), "0123456789abcdef");
            await File.WriteAllTextAsync(Path.Combine(mod.FullName, "0123456789abcdef.patch_0"), "deployed");
            var options = new DeploymentOptions(data, false, ["0123456789abcdef"]);
            var progressReports = new List<DeploymentProgress>();

            await new DeploymentService().DeployAsync(
                [CreateInput(mod)],
                options,
                new Progress<DeploymentProgress>(progressReports.Add));

            Assert.IsFalse(File.Exists(Path.Combine(data.FullName, "fedcba9876543210.patch_9")));
            Assert.AreEqual("deployed", await File.ReadAllTextAsync(Path.Combine(data.FullName, "0123456789abcdef.patch_1")));
            Assert.IsTrue(File.Exists(Path.Combine(data.FullName, "0123456789abcdef.patch_1.gpu_resources")));
            Assert.IsTrue(File.Exists(Path.Combine(data.FullName, "0123456789abcdef.patch_1.stream")));
            Assert.IsFalse(File.Exists(Path.Combine(data.FullName, "0123456789abcdef.patch_0")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task DeployPlanAsync_ShouldReportStepLifecycleByModOrder()
    {
        var root = CreateTempDirectory();
        try
        {
            var modA = CreateLegacyMod(Path.Combine(root, "A"), "aaaaaaaaaaaaaaaa");
            var modB = CreateLegacyMod(Path.Combine(root, "B"), "bbbbbbbbbbbbbbbb");
            var inputA = CreateInput(modA);
            var inputB = CreateInput(modB);
            var options = DeploymentOptions.Copy(new DirectoryInfo(Path.Combine(root, "data")));
            var plan = new DeploymentService().CreatePlan([inputA, inputB], options);
            var events = new List<string>();
            var callbacks = new DeploymentStepCallbacks(
                ModStarted: guid => events.Add($"start:{(guid == inputA.Guid ? "A" : "B")}"),
                FileCopied: item => events.Add($"file:{(item.ModGuid == inputA.Guid ? "A" : "B")}"),
                ModCompleted: guid => events.Add($"done:{(guid == inputA.Guid ? "A" : "B")}"));

            await new DeploymentService().DeployPlanAsync(plan, options, callbacks);

            CollectionAssert.AreEqual(new[] { "start:A", "file:A", "done:A", "start:B", "file:B", "done:B" }, events);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CleanupDeployedFilesAsync_ShouldDeleteUniqueButPreserveSharedFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var data = Directory.CreateDirectory(Path.Combine(root, "data"));
            var removed = CreateLegacyMod(Path.Combine(root, "Removed"), "aaaaaaaaaaaaaaaa");
            var remaining = CreateLegacyMod(Path.Combine(root, "Remaining"), "bbbbbbbbbbbbbbbb");
            await File.WriteAllTextAsync(Path.Combine(removed.FullName, "aaaaaaaaaaaaaaaa.patch_0.gpu_resources"), "gpu");
            await File.WriteAllTextAsync(Path.Combine(removed.FullName, "aaaaaaaaaaaaaaaa.patch_0.stream"), "stream");
            foreach (var path in new[]
                     {
                         Path.Combine(data.FullName, "aaaaaaaaaaaaaaaa.patch_0"),
                         Path.Combine(data.FullName, "aaaaaaaaaaaaaaaa.patch_0.gpu_resources"),
                         Path.Combine(data.FullName, "aaaaaaaaaaaaaaaa.patch_0.stream"),
                         Path.Combine(data.FullName, "bbbbbbbbbbbbbbbb.patch_0"),
                         Path.Combine(data.FullName, "bbbbbbbbbbbbbbbb.patch_0.gpu_resources"),
                         Path.Combine(data.FullName, "bbbbbbbbbbbbbbbb.patch_0.stream"),
                     })
            {
                await File.WriteAllTextAsync(path, "deployed");
            }

            var deleted = await new DeploymentService().CleanupDeployedFilesAsync(
                CreateInput(removed),
                [CreateInput(remaining)],
                DeploymentOptions.Copy(data));

            Assert.AreEqual(3, deleted.Count);
            Assert.IsFalse(File.Exists(Path.Combine(data.FullName, "aaaaaaaaaaaaaaaa.patch_0")));
            Assert.IsFalse(File.Exists(Path.Combine(data.FullName, "aaaaaaaaaaaaaaaa.patch_0.gpu_resources")));
            Assert.IsFalse(File.Exists(Path.Combine(data.FullName, "aaaaaaaaaaaaaaaa.patch_0.stream")));
            Assert.IsTrue(File.Exists(Path.Combine(data.FullName, "bbbbbbbbbbbbbbbb.patch_0")));
            Assert.IsTrue(File.Exists(Path.Combine(data.FullName, "bbbbbbbbbbbbbbbb.patch_0.gpu_resources")));
            Assert.IsTrue(File.Exists(Path.Combine(data.FullName, "bbbbbbbbbbbbbbbb.patch_0.stream")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static ModDeploymentInput CreateInput(DirectoryInfo directory) => new(
        Guid.NewGuid(),
        directory,
        new LegacyModManifest
        {
            Guid = Guid.NewGuid(),
            Name = directory.Name,
            Description = string.Empty,
        },
        [],
        []);

    private static DirectoryInfo CreateLegacyMod(string path, string hash)
    {
        var directory = Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, $"{hash}.patch_0"), $"content:{path}");
        return directory;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hd2mm-deploy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record OrderItem(Guid Guid, string Label);
}
