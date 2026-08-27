using Helldivers2ModManager.Core.Nexus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Helldivers2ModManager.Core.Tests.Nexus;

[TestClass]
public sealed class NexusApiClientTests
{
    [TestMethod]
    public async Task CheckForUpdatesAsync_SelectsHighestPositionAndComparesCurrentVersion()
    {
        var handler = new QueueHttpHandler(
            """
            {"data":{"groups":[{"id":"g1","name":"Main","is_active":true}]}}
            """,
            """
            {"data":{"versions":[
              {"id":"v1","position":"1","file":{"id":"f1","version":"1.0","update_group_version":{"position":"1"}}},
              {"id":"v3","position":"3","file":{"id":"f3","version":"1.5","update_group_version":{"position":"3"}}},
              {"id":"v2","position":"10","file":{"id":"f2","version":"1.2","update_group_version":{"position":"10"}}}
            ]}}
            """);
        using var client = CreateClient(handler);

        var update = await client.CheckForUpdatesAsync("42", "1.0");

        Assert.IsTrue(update.HasUpdate);
        Assert.AreEqual("1.2", update.LatestVersion);
        Assert.AreEqual("f2", update.LatestFile?.Id);
        StringAssert.Contains(handler.RequestUris[0], "/v3/mods/42/file-update-groups");
    }

    [TestMethod]
    public async Task GetJsonAsync_RetriesTransientNetworkFailures()
    {
        using var handler = new TransientNetworkHandler();
        using var client = CreateClient(handler);

        var mod = await client.GetModAsync("helldivers2", "42");

        Assert.AreEqual(2, handler.RequestCount);
        Assert.AreEqual("99", mod.Id);
    }

    [TestMethod]
    public async Task GetJsonAsync_DoesNotRetryUserCancellation()
    {
        using var handler = new CancelingHandler();
        using var client = CreateClient(handler);

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => client.GetModAsync("helldivers2", "42"));
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task GetModFileAsync_UsesSingleFileEndpointAndMapsPayload()
    {
        var handler = new QueueHttpHandler("""
            {"data":{"id":"f7","version":"3.1","size_bytes":256}}
            """);
        using var client = CreateClient(handler);

        var file = await client.GetModFileAsync("helldivers2", "42");

        Assert.IsNotNull(file);
        Assert.AreEqual("f7", file.Id);
        StringAssert.Contains(handler.RequestUris[0], "/v3/games/helldivers2/mod-files/42");
    }

    [TestMethod]
    public async Task GetModAsync_SendsApiKeyAndMapsPayload()
    {
        var handler = new QueueHttpHandler("""
            {"data":{"id":"99","game_scoped_id":"42","name":"Test Mod","author":"Author","downloads":7}}
            """);
        using var client = CreateClient(handler);

        var mod = await client.GetModAsync("helldivers2", "42");

        Assert.AreEqual("99", mod.Id);
        Assert.AreEqual("42", mod.GameScopedId);
        Assert.AreEqual("Test Mod", mod.Name);
        Assert.AreEqual(7, mod.Downloads);
        Assert.AreEqual("secret", handler.RequestHeaders[0].GetValues("apikey").Single());
    }

    [TestMethod]
    public async Task RequestErrors_MapPremiumAndRateLimit()
    {
        var handler = new QueueHttpHandler(HttpStatusCode.Forbidden, """{"detail":"Premium membership required"}""");
        using var client = CreateClient(handler);
        var premium = await Assert.ThrowsExceptionAsync<NexusApiException>(() => client.GetModAsync("helldivers2", "42"));
        Assert.AreEqual("PremiumRequired", premium.Code);

        var rateHandler = new QueueHttpHandler(HttpStatusCode.TooManyRequests, "{}", retryAfter: TimeSpan.FromSeconds(9));
        using var rateClient = CreateClient(rateHandler);
        var rate = await Assert.ThrowsExceptionAsync<NexusRateLimitException>(() => rateClient.GetModAsync("helldivers2", "42"));
        Assert.AreEqual(TimeSpan.FromSeconds(9), rate.RetryAfter);
    }

    [TestMethod]
    public async Task DownloadModFileAsync_WritesResponseToSafePath()
    {
        var directory = Directory.CreateTempSubdirectory("hd2mm-core-nexus-");
        try
        {
            var savePath = Path.Combine(directory.FullName, "download.zip");
            var handler = new QueueHttpHandler(
                """
                {"URI":"https://unit.test/file.bin"}
                """,
                "archive-bytes");
            using var client = CreateClient(handler);

            var result = await client.DownloadModFileAsync("helldivers2", "42", "7", savePath);

            Assert.AreEqual(savePath, result);
            CollectionAssert.AreEqual("archive-bytes"u8.ToArray(), await File.ReadAllBytesAsync(savePath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static NexusApiClient CreateClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://unit.test/v3/")
        };
        return new NexusApiClient(httpClient, "secret");
    }

    private sealed class TransientNetworkHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requestCount) == 1)
                throw new HttpRequestException("transient network failure");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":{"id":"99","game_scoped_id":"42"}}""", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CancelingHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            throw new TaskCanceledException("user canceled");
        }
    }

    private sealed class QueueHttpHandler : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _responses;
        public List<string> RequestUris { get; } = [];
        public List<HttpRequestHeaders> RequestHeaders { get; } = [];

        public QueueHttpHandler(params string[] bodies)
        {
            _responses = bodies.Select(body => CreateResponse(HttpStatusCode.OK, body)).ToList();
        }

        public QueueHttpHandler(HttpStatusCode status, string body, TimeSpan? retryAfter = null)
        {
            var response = CreateResponse(status, body);
            if (retryAfter is { } retry)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retry);
            _responses = [response];
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.PathAndQuery);
            RequestHeaders.Add(request.Headers);
            var response = _responses[Math.Min(RequestUris.Count - 1, _responses.Count - 1)];
            if (response.Content is ByteArrayContent or StringContent)
                return await Task.FromResult(response);
            return response;
        }

        private static HttpResponseMessage CreateResponse(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}



