#if DEBUG
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models.Nexus;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Helldivers2ModManager.Services.Nexus;
using Jalium.UI;
using Jalium.UI.Controls;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

internal static class NexusVisualCheck
{
    public static int Run(string path)
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(Path.GetTempPath(), "jalium-nexus-visual-settings.json"), Path.GetTempPath());
        settings.InitDefault(@readonly: true);
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            Path.Combine(AppContext.BaseDirectory, "Language"));
        localization.SelectedLanguage = "zh-CN";
        using var view = new NexusDownloadPageView(new SampleNexusService(), settings, localization,
            _ => Task.FromResult<ModImportResult?>(null), () => { }, Console.Error.WriteLine);
        var sections = (StackPanel)((ScrollViewer)view.Children[1]).Content!;
        var input = (StackPanel)((Border)sections.Children[0]).Child!;
        ((TextBox)((Grid)input.Children[2]).Children[0]).Text =
            "https://www.nexusmods.com/helldivers2/mods/123";
        ((PasswordBox)input.Children[4]).Password = "sample-key";
        var window = new Window { Title = "Nexus Visual Check",
            Width = 1000, Height = 700, Content = view };
        var captured = false;
        var scheduled = false;
        window.ContentRendered += async (_, _) =>
        {
            if (scheduled)
                return;
            scheduled = true;
            try
            {
                await view.FetchAsync();
                await Task.Delay(300);
                PatchViewerVisualCheck.Capture(window.Handle, path);
                captured = true;
                Console.WriteLine($"SCREENSHOT_OK {path}");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { window.Close(); }
        };
        var builder = AppBuilder.CreateBuilder([]);
        builder.ConfigureApplication(app => app.MainWindow = window);
        using var app = builder.Build();
        app.Run();
        return captured ? 0 : 1;
    }

    private sealed class SampleNexusService : INexusModsService
    {
        public bool Initialized => true;
        public void Init(string apiKey) { }
        public Task<Mod> GetModAsync(string gameDomain, string modId,
            CancellationToken cancellationToken = default) => Task.FromResult(new Mod
        {
            Name = "Sample HD2 Mod", Author = "Community Author",
            Summary = "A sample mod description for visual layout inspection.",
            GameScopedId = modId,
        });
        public Task<List<ModFile>> GetModFilesAsync(string gameDomain, string modId,
            CancellationToken cancellationToken = default) => Task.FromResult(new List<ModFile>
        {
            new() { Name = "Sample HD2 Mod 1.2.zip", Version = "1.2",
                SizeBytes = 12_582_912, UploadedAt = new DateTime(2026, 9, 18), IsPrimary = true },
            new() { Name = "Optional texture pack.7z", Version = "1.2",
                SizeBytes = 4_194_304, UploadedAt = new DateTime(2026, 9, 17) },
        });
        public Task<string> DownloadModFileAsync(string gameDomain, string modId,
            string fileId, string savePath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
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
#endif
