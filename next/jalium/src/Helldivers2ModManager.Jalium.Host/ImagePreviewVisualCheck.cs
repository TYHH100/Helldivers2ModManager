#if DEBUG
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media.Imaging;
using Jalium.UI.Threading;

namespace Helldivers2ModManager.Jalium.Host;

internal static class ImagePreviewVisualCheck
{
    public static int Run(string screenshotPath)
    {
        var preview = new ImagePreviewOverlay();
        var root = new Grid();
        root.Children.Add(new TextBlock { Text = "Helldivers 2 Mod Manager",
            FontSize = 24, Margin = new Thickness(32) });
        root.Children.Add(preview);
        var window = new Window { Title = "Image Preview Visual Check",
            Width = 1000, Height = 700, Content = root };
        var captured = false;
        window.ContentRendered += (_, _) =>
        {
            preview.Show(BitmapImage.FromFile(Path.Combine(AppContext.BaseDirectory,
                "Images", "logo_icon.png")));
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
