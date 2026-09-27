#if DEBUG
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Threading;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

internal static class VersionDetailVisualCheck
{
    public static int Run(string screenshotPath)
    {
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            Path.Combine(AppContext.BaseDirectory, "Language"));
        localization.SelectedLanguage = "zh-CN";
        var settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(Path.GetTempPath(), "jalium-version-visual-settings.json"), Path.GetTempPath());
        using var messageBoxOverlay = new MessageBoxOverlay(localization);
        Window? window = null;
        using var overlay = new VersionCheckDetailOverlay(localization, settings,
            () => null, () => Task.CompletedTask, () => window, messageBoxOverlay);
        var root = new Grid();
        root.Children.Add(new TextBlock { Text = "Helldivers 2 Mod Manager", FontSize = 26,
            Margin = new Thickness(32) });
        root.Children.Add(overlay);
        root.Children.Add(messageBoxOverlay);
        window = new Window { Title = "Version Detail Visual Check", Width = 1000, Height = 700,
            Content = root };
        var mod = new ModData(new DirectoryInfo(Path.GetTempPath()),
            new LegacyModManifest { Guid = Guid.NewGuid(), Name = "VRC Tell - Armor Refit",
                Description = string.Empty, Options = [] });
        var analysis = new ModDetailedAnalysis
        {
            TotalPatchFiles = 2,
            FilesWithUnits = 2,
            CorruptedFileCount = 1,
            PatchFiles =
            [
                new PatchFileAnalysis
                {
                    FileName = "9ba626afa44a3aa3.patch_43", FileSize = 12582912,
                    HealthStatus = PatchHealthStatus.Corrupted, HeaderValid = true,
                    FileEntriesInBounds = true, TypeDistributionValid = true,
                    TotalResources = 187, HasGpuResources = true, HasStream = true,
                    UnitDetails =
                    [
                        new UnitResourceDetail { EntryIndex = 32, FileId = 0x123456789,
                            Version = 10800438, DataSize = 8192, ExpectedDataSize = 9216,
                            IsTruncated = true, DeclaredSizeMatchesInternal = false,
                            UnitDataInBounds = true, LODGroupInBounds = true },
                    ],
                },
                new PatchFileAnalysis
                {
                    FileName = "9ba626afa44a3aa3.patch_44", FileSize = 4096,
                    HealthStatus = PatchHealthStatus.Healthy, HeaderValid = true,
                    FileEntriesInBounds = true, TypeDistributionValid = true,
                    TotalResources = 22, HasGpuResources = true, HasStream = true,
                },
            ],
        };
        var result = new ModVersionCheckResult
        {
            Status = ModVersionStatus.Incompatible,
            GameVersion = 10800438,
            LastChecked = DateTime.Now,
            DetailedAnalysis = analysis,
        };
        var captured = false;
        window.ContentRendered += (_, _) =>
        {
            overlay.Show(mod, result);
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await Task.Delay(300);
                    PatchViewerVisualCheck.Capture(window.Handle, screenshotPath);
                    captured = true;
                    Console.WriteLine($"SCREENSHOT_OK {screenshotPath}");
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally { window.Close(); }
            });
        };
        var builder = AppBuilder.CreateBuilder([]);
        builder.ConfigureApplication(app => app.MainWindow = window);
        using var jalium = builder.Build();
        jalium.Run();
        return captured ? 0 : 1;
    }
}
#endif
