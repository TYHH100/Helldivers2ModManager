using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Core.Profiles;
using Helldivers2ModManager.Frontend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Frontend.Tests;

[TestClass]
public sealed class NexusDownloadServiceTests
{
    private string? _root;
    private ServiceProvider? _provider;

    [TestCleanup]
    public void Cleanup()
    {
        _provider?.Dispose();
        _provider = null;
        if (_root is not null)
        {
            DeleteDirectoryWithRetry(_root);
        }
    }

    [TestMethod]
    public void ParseUrl_ReadsGameDomainAndModId()
    {
        var parsed = NexusDownloadService.ParseUrl("https://www.nexusmods.com/helldivers2/mods/42?tab=files");

        Assert.IsNotNull(parsed);
        Assert.AreEqual("helldivers2", parsed.Value.GameDomain);
        Assert.AreEqual("42", parsed.Value.ModId);
        Assert.IsNull(NexusDownloadService.ParseUrl("https://example.com/mods/42"));
    }

    [TestMethod]
    public async Task FetchAsync_UsesUpdateGroupsAndGlobalModId()
    {
        var handler = new QueueHttpHandler(
            """{"data":{"id":"99","game_scoped_id":"42","name":"Test Mod"}}""",
            """{"data":{"groups":[{"id":"g1","name":"Main","is_active":true}]}}""",
            """{"data":{"versions":[{"id":"v1","position":"3","file":{"id":"f1","name":"archive.zip","version":"2.0","size_bytes":128,"update_group_version":{"position":"3"}}}]}}""");
        var service = CreateService(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://unit.test/v3/"),
        });

        var result = await service.FetchAsync("helldivers2", "42");

        Assert.AreEqual("Test Mod", result.Mod.Name);
        Assert.AreEqual(1, result.Files.Count);
        Assert.AreEqual("f1", result.Files[0].Id);
        StringAssert.Contains(handler.RequestUris[0], "/v3/games/helldivers2/mods/42");
        StringAssert.Contains(handler.RequestUris[1], "/v3/mods/99/file-update-groups");
        StringAssert.Contains(handler.RequestUris[2], "/v3/file-update-groups/g1/versions");
    }

    private NexusDownloadService CreateService(HttpClient httpClient)
    {
        _root = Path.Combine(Path.GetTempPath(), "Helldivers2ModManagerFrontendTests", Guid.NewGuid().ToString("N"));
        var paths = new ApplicationPaths(_root);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCommon();
        services.AddPersistence(paths.Database);
        services.AddMods();
        services.AddProfiles();
        services.AddSingleton(paths);
        services.AddSingleton(httpClient);
        services.AddSingleton<ApplicationSettingsService>();
        services.AddSingleton<TaskExecutionService>();
        services.AddSingleton<NexusDownloadService>();
        _provider = services.BuildServiceProvider();
        var settings = _provider.GetRequiredService<ApplicationSettingsService>();
        settings.InitializeAsync().GetAwaiter().GetResult();
        settings.Current.NexusApiKey = "secret";
        return _provider.GetRequiredService<NexusDownloadService>();
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(150);
            }
        }
    }

    private sealed class QueueHttpHandler(params string[] bodies) : HttpMessageHandler
    {
        private int _index;
        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.PathAndQuery);
            var body = bodies[Math.Min(_index++, bodies.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
