#if DEBUG
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Threading;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

internal static class FolderPickerVisualCheck
{
    public static int Run(string screenshotPath)
    {
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            Path.Combine(AppContext.BaseDirectory, "Language"));
        localization.SelectedLanguage = "zh-CN";
        var root = Path.Combine(Path.GetTempPath(), "hd2mm-folder-picker-check");
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "Maps", "Audio", "Textures" })
            Directory.CreateDirectory(Path.Combine(root, name));
        var window = new Window { Title = "Folder Picker Visual Check", Width = 900, Height = 650 };
        FolderPickerDialog? picker = null;
        var captured = false;
        window.ContentRendered += (_, _) =>
        {
            picker = new FolderPickerDialog(localization["CreatePage.BrowseSourceDialog"], root,
                localization, window);
            picker.ContentRendered += (_, _) =>
            {
                picker.CreateFolder();
                _ = Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        await Task.Delay(300);
                        PatchViewerVisualCheck.Capture(picker.Handle, screenshotPath);
                        captured = true;
                        Console.WriteLine($"SCREENSHOT_OK {screenshotPath}");
                    }
                    catch (Exception ex) { Console.Error.WriteLine(ex); }
                    finally
                    {
                        picker.Close();
                        window.Close();
                    }
                });
            };
            picker.Show();
        };
        var builder = AppBuilder.CreateBuilder([]);
        builder.ConfigureApplication(app => app.MainWindow = window);
        using var jalium = builder.Build();
        jalium.Run();
        try { Directory.Delete(root, true); } catch { }
        return captured ? 0 : 1;
    }
}
#endif
