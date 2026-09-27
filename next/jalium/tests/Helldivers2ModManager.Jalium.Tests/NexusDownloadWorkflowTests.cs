using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models.Nexus;
using Helldivers2ModManager.Services.Nexus;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Tests;

[TestClass]
public sealed class NexusDownloadWorkflowTests
{
    [TestMethod]
    public void ParsesOnlyNexusModPageUrls()
    {
        Assert.IsTrue(NexusDownloadWorkflow.TryParseUrl(
            "https://www.nexusmods.com/helldivers2/mods/123?tab=files", out var domain, out var id));
        Assert.AreEqual("helldivers2", domain);
        Assert.AreEqual("123", id);
        Assert.IsFalse(NexusDownloadWorkflow.TryParseUrl(
            "https://www.nexusmods.com.evil.example/helldivers2/mods/123", out _, out _));
        Assert.IsFalse(NexusDownloadWorkflow.TryParseUrl(
            "http://www.nexusmods.com/helldivers2/mods/123", out _, out _));
        Assert.IsFalse(NexusDownloadWorkflow.TryParseUrl(
            "https://www.nexusmods.com/helldivers2/mods/not-a-number", out _, out _));
    }

    [TestMethod]
    public async Task FetchUsesOriginalServiceAndNamesUnnamedFiles()
    {
        var service = new FakeNexusService();
        var workflow = new NexusDownloadWorkflow(service);
        var (mod, files) = await workflow.FetchAsync(
            "https://www.nexusmods.com/helldivers2/mods/123", "test-key", CancellationToken.None);
        Assert.AreEqual("test-key", service.ApiKey);
        Assert.AreEqual("helldivers2", service.LastDomain);
        Assert.AreEqual("123", service.LastModId);
        Assert.AreEqual("Sample", mod.Name);
        Assert.AreEqual("Sample v1.2", files.Single().Name);
        await workflow.FetchAsync("https://www.nexusmods.com/helldivers2/mods/123",
            "replacement-key", CancellationToken.None);
        Assert.AreEqual("replacement-key", service.ApiKey);
    }

    [TestMethod]
    public async Task DownloadUsesIsolatedNameAndDeletesPartialFileOnFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-nexus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new FakeNexusService();
            var workflow = new NexusDownloadWorkflow(service);
            var mod = new Mod { GameScopedId = "123" };
            var file = new ModFile { GameScopedId = "456", Name = "..\\outside.7z" };
            var path = await workflow.DownloadAsync(
                "https://www.nexusmods.com/helldivers2/mods/123", mod, file, root,
                CancellationToken.None);
            Assert.AreEqual(root, Path.GetDirectoryName(path));
            Assert.AreEqual(".7z", Path.GetExtension(path));
            Assert.IsTrue(File.Exists(path));
            service.FailDownload = true;
            await Assert.ThrowsExceptionAsync<IOException>(() => workflow.DownloadAsync(
                "https://www.nexusmods.com/helldivers2/mods/123", mod, file, root,
                CancellationToken.None));
            Assert.AreEqual(1, Directory.GetFiles(root).Length);
        }
        finally
        {
            if (Path.GetFileName(root).StartsWith("hd2mm-jalium-nexus-", StringComparison.Ordinal)
                && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    internal sealed class FakeNexusService : INexusModsService
    {
        public bool Initialized => ApiKey is not null;
        public string? ApiKey { get; private set; }
        public string? LastDomain { get; private set; }
        public string? LastModId { get; private set; }
        public bool FailDownload { get; set; }

        public void Init(string apiKey) => ApiKey = apiKey;

        public Task<Mod> GetModAsync(string gameDomain, string modId,
            CancellationToken cancellationToken = default)
        {
            LastDomain = gameDomain;
            LastModId = modId;
            return Task.FromResult(new Mod { Name = "Sample", GameScopedId = modId });
        }

        public Task<List<ModFile>> GetModFilesAsync(string gameDomain, string modId,
            CancellationToken cancellationToken = default) => Task.FromResult(new List<ModFile>
        {
            new() { GameScopedId = "456", Version = "1.2", IsPrimary = true },
        });

        public async Task<string> DownloadModFileAsync(string gameDomain, string modId,
            string fileId, string savePath, CancellationToken cancellationToken = default)
        {
            await File.WriteAllBytesAsync(savePath, [1, 2, 3], cancellationToken);
            if (FailDownload)
                throw new IOException("Network interrupted.");
            return savePath;
        }

        public Task<List<ModFileUpdateGroup>> GetUpdateGroupsAsync(string modId,
            CancellationToken cancellationToken = default) => Task.FromResult(new List<ModFileUpdateGroup>());
        public Task<List<ModFileUpdateGroupVersion>> GetUpdateGroupVersionsAsync(string groupId,
            CancellationToken cancellationToken = default) => Task.FromResult(new List<ModFileUpdateGroupVersion>());
        public Task<List<TrendingMod>> GetTrendingModsAsync(string gameDomain,
            CancellationToken cancellationToken = default) => Task.FromResult(new List<TrendingMod>());
        public Task<UpdateInfo> CheckForUpdatesAsync(string modId, string currentVersion,
            CancellationToken cancellationToken = default) => Task.FromResult(new UpdateInfo());
        public void ClearCache() { }
    }
}
