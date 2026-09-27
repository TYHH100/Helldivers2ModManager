#if DEBUG
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Threading;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

internal static class MessageBoxVisualCheck
{
    public static int Run(string screenshotPath, bool passwordPrompt = false, bool textPrompt = false,
        bool selectionPrompt = false, bool singleSelectionPrompt = false, bool checklistPrompt = false,
        bool progressPrompt = false)
    {
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            Path.Combine(AppContext.BaseDirectory, "Language"));
        localization.SelectedLanguage = "zh-CN";
        using var overlay = new MessageBoxOverlay(localization);
        var root = new Grid();
        root.Children.Add(new TextBlock { Text = "Helldivers 2 Mod Manager",
            FontSize = 24, Margin = new Thickness(32) });
        root.Children.Add(overlay);
        var window = new Window { Title = "Message Box Visual Check",
            Width = 1000, Height = 700, Content = root };
        var captured = false;
        window.ContentRendered += (_, _) =>
        {
            if (progressPrompt)
            {
                overlay.ShowProgress(localization["DashboardPage.ExportSaveDialog"] + " - Sample Mod",
                    localization["SettingsPage.PleaseWait"]);
                overlay.UpdateProgress("archive/patch_0001", 0.42);
            }
            else if (textPrompt)
                _ = overlay.PromptAsync(localization["DashboardPage.EditNameTitle"],
                    localization["DashboardPage.EditNameMsg"], "Sample Mod", 64);
            else if (selectionPrompt)
                _ = overlay.SelectManyAsync(localization["ModGroup.AddToGroup"],
                    localization["ModGroup.AddToGroupsMessage"].Replace("{count}", "3"),
                    [new MessageBoxSelectionOption("Default", "#FF3B82F6"),
                     new MessageBoxSelectionOption("Combat", "#FF18A558"),
                     new MessageBoxSelectionOption("Visual", "#FFEAB308")], [0, 2]);
            else if (singleSelectionPrompt)
                _ = overlay.ChooseOneAsync(localization["Bisect.ReportTitle"],
                    localization["Bisect.ReportMessage"].Replace("{count}", "2")
                        .Replace("{names}", "Sample Mod A\nSample Mod B"),
                    [localization["Bisect.Crashed"], localization["Bisect.NotCrashed"], localization["Bisect.Cancel"]]);
            else if (checklistPrompt)
                _ = overlay.SelectManyAsync(localization["VersionCheckDetail.UnitSelectionTitle"],
                    localization["VersionCheckDetail.UnitSelectionMessage"],
                    [new MessageBoxSelectionOption("Custom unit", Detail: "3 patch entries | LOD 4096->8192 | ID 0x0123456789ABCDEF"),
                     new MessageBoxSelectionOption("Standard unit", Detail: "1 patch entry | LOD 2048->4096 | ID 0xFEDCBA9876543210")]);
            else if (passwordPrompt)
                _ = overlay.PromptPasswordAsync(localization["DashboardPage.ArchivePasswordTitle"],
                    localization["DashboardPage.ArchivePasswordMessage"].Replace("{file}", "Example.zip"));
            else
                _ = overlay.ConfirmAsync(localization["DashboardPage.DeleteConfirmTitle"],
                    localization["DashboardPage.DeleteConfirmPrefix"] + "Example Mod"
                    + localization["DashboardPage.DeleteConfirmSuffix"]);
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
