using Helldivers2ModManager.Adapters;
using Helldivers2ModManager.Core.Nexus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Text;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class NexusModsServiceAdapterTests
{
    [TestMethod]
    public async Task GetModFilesAsync_UsesGlobalModIdAndMapsCoreFilesToLegacyModels()
    {
        var handler = new QueueHttpHandler(
            """
            {"data":{"id":"99","game_scoped_id":"42","name":"Test Mod"}}
            """,
            """
            {"data":{"groups":[{"id":"g1","name":"Main","is_active":true}]}}
            """,
            """
            {"data":{"versions":[{"id":"v1","position":"3","file":{"id":"f1","version":"2.0","size_bytes":128,"update_group_version":{"position":"3"}}}]}}
            """);
        var service = CreateService(handler, "secret");

        await service.InitAsync("secret");
        var files = await service.GetModFilesAsync("helldivers2", "42");

        Assert.AreEqual(1, files.Count);
        Assert.AreEqual("f1", files[0].Id);
        Assert.AreEqual("2.0", files[0].Version);
        Assert.AreEqual(128, files[0].SizeBytes);
        Assert.AreEqual(128L, files[0].SizeBytes);
        Assert.AreEqual("3", files[0].UpdateGroupVersion?.Position);
        StringAssert.Contains(handler.RequestUris[0], "/v3/games/helldivers2/mods/42");
        StringAssert.Contains(handler.RequestUris[1], "/v3/mods/99/file-update-groups");
        StringAssert.Contains(handler.RequestUris[2], "/v3/file-update-groups/g1/versions");
    }

    [TestMethod]
    public async Task GetModFilesAsync_FallsBackWhenUpdateGroupsFail()
    {
        var handler = new QueueHttpHandler(
            """
            {"data":{"id":"99","game_scoped_id":"42","name":"Test Mod"}}
            """,
            """
            {"detail":"update groups unavailable"}
            """,
            """
            {"data":{"id":"f9","version":"4.0","size_bytes":64}}
            """);
        handler.Statuses = [HttpStatusCode.OK, HttpStatusCode.InternalServerError, HttpStatusCode.OK];
        var service = CreateService(handler, "secret");
        await service.InitAsync("secret");

        var files = await service.GetModFilesAsync("helldivers2", "42");

        Assert.AreEqual(1, files.Count);
        Assert.AreEqual("f9", files[0].Id);
        StringAssert.Contains(handler.RequestUris[2], "/v3/games/helldivers2/mod-files/99");
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_ReturnsNoUpdateOnApiFailure()
    {
        var handler = new QueueHttpHandler("""{"detail":"unavailable"}""");
        handler.Statuses = [HttpStatusCode.ServiceUnavailable];
        var service = CreateService(handler, "secret");
        await service.InitAsync("secret");

        var update = await service.CheckForUpdatesAsync("42", "1.0");

        Assert.IsFalse(update.HasUpdate);
        Assert.AreEqual("1.0", update.CurrentVersion);
        Assert.IsNull(update.LatestModFile);
    }

    private static NexusModsServiceAdapter CreateService(QueueHttpHandler handler, string apiKey) =>
        new(apiKey => new NexusApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://unit.test/v3/")
        }, apiKey));

    private sealed class QueueHttpHandler(params string[] bodies) : HttpMessageHandler
    {
        private int _index;
        public List<string> RequestUris { get; } = [];
        public List<HttpStatusCode> Statuses { get; set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = _index++;
            RequestUris.Add(request.RequestUri!.PathAndQuery);
            var body = bodies[Math.Min(index, bodies.Length - 1)];
            var status = Statuses.Count == 0 ? HttpStatusCode.OK : Statuses[Math.Min(index, Statuses.Count - 1)];
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}

internal static class NexusModsServiceAdapterTestExtensions
{
    public static async Task InitAsync(this NexusModsServiceAdapter adapter, string apiKey)
    {
        adapter.Init(apiKey);
        await Task.CompletedTask;
    }
}
