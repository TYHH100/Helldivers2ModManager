using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Jalium.Host;
using Helldivers2ModManager.Models.Nexus;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Helldivers2ModManager.Services.Nexus;
using Jalium.UI.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Host.Tests;

[TestClass]
public sealed class NexusDownloadPageViewTests
{
    [TestMethod]
    public async Task FetchSelectsPrimaryFileAndDownloadUsesImportPipeline()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-nexus-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new SettingsService(NullLogger<SettingsService>.Instance,
                Path.Combine(root, "settings.json"), root);
            settings.InitDefault();
            settings.TempDirectory = root;
            var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
                GetLanguageDirectory());
            localization.SelectedLanguage = "zh-CN";
            var service = new FakeNexusService();
            var imported = 0;
            var returned = 0;
            using var view = new NexusDownloadPageView(service, settings, localization,
                paths =>
                {
                    Assert.AreEqual(1, paths.Count);
                    Assert.IsTrue(File.Exists(paths[0]));
                    imported++;
                    return Task.FromResult<ModImportResult?>(new ModImportResult(1, 0, [], []));
                }, () => returned++, error => Assert.Fail(error.ToString()));
            var sections = (StackPanel)((ScrollViewer)view.Children[1]).Content!;
            var input = (StackPanel)((Border)sections.Children[0]).Child!;
            var urlLine = (Grid)input.Children[2];
            ((TextBox)urlLine.Children[0]).Text = "https://www.nexusmods.com/helldivers2/mods/123";
            ((PasswordBox)input.Children[4]).Password = "test-key";

            await view.FetchAsync();
            var details = (StackPanel)((Border)sections.Children[1]).Child!;
            var modInfo = (StackPanel)details.Children[1];
            var files = (ListBox)modInfo.Children[2];
            Assert.AreEqual(2, files.Items.Count);
            Assert.AreEqual("Primary.zip", files.SelectedItem!.GetType().GetProperty("Name")!.GetValue(files.SelectedItem));
            Assert.AreEqual("test-key", settings.NexusApiKey);
            Assert.AreEqual("模组信息", ((TextBlock)details.Children[0]).Text);
            await view.DownloadAsync();
            Assert.AreEqual(1, imported);
            Assert.AreEqual(1, returned);
            Assert.AreEqual(0, Directory.GetFiles(root, "nexus-*").Length);
            localization.SelectedLanguage = "en-US";
            Assert.AreEqual("Download Mods from Nexus Mods",
                ((TextBlock)((Grid)view.Children[0]).Children[1]).Text);
        }
        finally
        {
            if (Path.GetFileName(root).StartsWith("hd2mm-jalium-nexus-page-", StringComparison.Ordinal)
                && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChangingUrlPreventsEarlierPictureFromReturning()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-nexus-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new SettingsService(NullLogger<SettingsService>.Instance,
                Path.Combine(root, "settings.json"), root);
            settings.InitDefault();
            var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
                GetLanguageDirectory());
            var pictureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pictureBytes = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var view = new NexusDownloadPageView(new FakeNexusService { PictureUrl = "https://example.test/picture.png" },
                settings, localization, _ => Task.FromResult<ModImportResult?>(null),
                () => { }, error => Assert.Fail(error.ToString()),
                (_, _) => { pictureStarted.SetResult(); return pictureBytes.Task; });
            var sections = (StackPanel)((ScrollViewer)view.Children[1]).Content!;
            var input = (StackPanel)((Border)sections.Children[0]).Child!;
            var url = (TextBox)((Grid)input.Children[2]).Children[0];
            url.Text = "https://www.nexusmods.com/helldivers2/mods/123";
            ((PasswordBox)input.Children[4]).Password = "test-key";
            await view.FetchAsync();
            await pictureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            url.Text = "https://www.nexusmods.com/helldivers2/mods/456";
            pictureBytes.SetResult([1, 2, 3]);
            await view.PendingPicture.WaitAsync(TimeSpan.FromSeconds(5));
            var details = (StackPanel)((Border)sections.Children[1]).Child!;
            var modInfo = (StackPanel)details.Children[1];
            var identity = (Grid)modInfo.Children[0];
            Assert.IsNull(((Image)identity.Children[0]).Source);
        }
        finally
        {
            if (Path.GetFileName(root).StartsWith("hd2mm-jalium-nexus-page-", StringComparison.Ordinal)
                && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string GetLanguageDirectory([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language");
        throw new DirectoryNotFoundException("Repository language files not found.");
    }

    private sealed class FakeNexusService : INexusModsService
    {
        public string? PictureUrl { get; init; }
        public bool Initialized { get; private set; }
        public void Init(string apiKey) => Initialized = true;
        public Task<Mod> GetModAsync(string gameDomain, string modId,
            CancellationToken cancellationToken = default) => Task.FromResult(new Mod
        {
            Name = "Example", GameScopedId = modId, Author = "Author", Summary = "Summary",
            PictureUrl = PictureUrl,
        });
        public Task<List<ModFile>> GetModFilesAsync(string gameDomain, string modId,
            CancellationToken cancellationToken = default) => Task.FromResult(new List<ModFile>
        {
            new() { Name = "Old.zip", GameScopedId = "1" },
            new() { Name = "Primary.zip", GameScopedId = "2", IsPrimary = true },
        });
        public async Task<string> DownloadModFileAsync(string gameDomain, string modId,
            string fileId, string savePath, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("2", fileId);
            await File.WriteAllBytesAsync(savePath, [1, 2, 3], cancellationToken);
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
