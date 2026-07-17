using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Helldivers2ModManager.Infrastructure.Security;
using Helldivers2ModManager.Infrastructure.Settings;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Helldivers2ModManager.Tests;

public sealed class BrowserExtensionLoopbackTests
{
    [Fact]
    public async Task LoopbackProtocolEnforcesPairingAuthenticationReplayAndBodyLimits()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(temporaryDirectory.Path, "settings.json");
        using var settingsStore = new AtomicJsonSettingsStore(settingsPath);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, settingsStore);
        settings.InitDefault();
        settings.StorageDirectory = Path.Combine(temporaryDirectory.Path, "storage");
        settings.TempDirectory = Path.Combine(temporaryDirectory.Path, "temp");
        settings.ExtensionPort = GetAvailableTcpPort();

        using var service = new BrowserExtensionService(
            NullLogger<BrowserExtensionService>.Instance,
            settings,
            new LocalizationService(NullLogger<LocalizationService>.Instance),
            new BackgroundTaskService(),
            new SafePathPolicy());
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{settings.ExtensionPort}/"),
            Timeout = TimeSpan.FromSeconds(10)
        };

        service.Start();
        try
        {
            var code = service.GeneratePairingCode();
            using var ordinaryWebPair = await PostPairAsync(client, code, "https://example.com");
            Assert.Equal(HttpStatusCode.Unauthorized, ordinaryWebPair.StatusCode);
            Assert.NotEqual("*", ordinaryWebPair.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins)
                ? Assert.Single(origins)
                : null);

            const string pairedOrigin = "chrome-extension://abcdefghijklmnop";
            using var pairResponse = await PostPairAsync(client, code, pairedOrigin);
            Assert.Equal(HttpStatusCode.OK, pairResponse.StatusCode);
            Assert.Equal(pairedOrigin, Assert.Single(pairResponse.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.DoesNotContain("*", pairResponse.Headers.GetValues("Access-Control-Allow-Origin"));
            var token = await ReadTokenAsync(pairResponse);

            using var unauthenticatedHealth = await client.GetAsync("api/v2/health");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedHealth.StatusCode);

            var replayId = Guid.NewGuid();
            using var authenticatedHealth = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "api/v2/health", token, pairedOrigin, replayId, DateTimeOffset.UtcNow);
            Assert.Equal(HttpStatusCode.OK, authenticatedHealth.StatusCode);

            using var replayedHealth = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "api/v2/health", token, pairedOrigin, replayId, DateTimeOffset.UtcNow);
            Assert.Equal(HttpStatusCode.Unauthorized, replayedHealth.StatusCode);
            Assert.Contains("Auth.ReplayedRequest", await replayedHealth.Content.ReadAsStringAsync());

            using var wrongOriginHealth = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "api/v2/health", token, "chrome-extension://different", Guid.NewGuid(), DateTimeOffset.UtcNow);
            Assert.Equal(HttpStatusCode.Unauthorized, wrongOriginHealth.StatusCode);

            using var expiredHealth = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "api/v2/health", token, pairedOrigin, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-6));
            Assert.Equal(HttpStatusCode.Unauthorized, expiredHealth.StatusCode);
            Assert.Contains("Auth.ExpiredRequest", await expiredHealth.Content.ReadAsStringAsync());

            using var oversizedBody = new StringContent(
                JsonSerializer.Serialize(new { code = new string('0', (64 * 1024) + 1) }),
                Encoding.UTF8,
                "application/json");
            using var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, "api/v2/pair")
            {
                Content = oversizedBody
            };
            oversizedRequest.Headers.Add("Origin", pairedOrigin);
            using var oversizedResponse = await client.SendAsync(oversizedRequest);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResponse.StatusCode);

            using var unpairResponse = await SendAuthenticatedAsync(
                client, HttpMethod.Post, "api/v2/unpair", token, pairedOrigin, Guid.NewGuid(), DateTimeOffset.UtcNow);
            Assert.Equal(HttpStatusCode.OK, unpairResponse.StatusCode);
            Assert.Empty(settings.BrowserExtensionTokenHash);
            Assert.Empty(settings.BrowserExtensionOrigin);

            using var healthAfterUnpair = await SendAuthenticatedAsync(
                client, HttpMethod.Get, "api/v2/health", token, pairedOrigin, Guid.NewGuid(), DateTimeOffset.UtcNow);
            Assert.Equal(HttpStatusCode.Unauthorized, healthAfterUnpair.StatusCode);
        }
        finally
        {
            service.Stop();
        }
    }

    private static async Task<HttpResponseMessage> PostPairAsync(HttpClient client, string code, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/v2/pair")
        {
            Content = JsonContent.Create(new { code })
        };
        request.Headers.Add("Origin", origin);
        return await client.SendAsync(request);
    }

    private static async Task<string> ReadTokenAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Pairing response did not contain a token.");
    }

    private static Task<HttpResponseMessage> SendAuthenticatedAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string token,
        string origin,
        Guid requestId,
        DateTimeOffset timestamp)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("X-Request-Id", requestId.ToString("D"));
        request.Headers.Add("X-Timestamp", timestamp.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        return client.SendAsync(request);
    }

    private static int GetAvailableTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
