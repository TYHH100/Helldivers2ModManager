#if DEBUG
using System.Runtime.InteropServices;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Parsing;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Threading;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

internal static class PatchViewerVisualCheck
{
    public static int Run(string page, string screenshotPath)
    {
        if (page == "nexus")
            return NexusVisualCheck.Run(screenshotPath);
        if (page == "model")
            return ModelPreviewVisualCheck.Run(screenshotPath);
        if (page == "version")
            return VersionDetailVisualCheck.Run(screenshotPath);
        if (page == "image")
            return ImagePreviewVisualCheck.Run(screenshotPath);
        if (page == "message")
            return MessageBoxVisualCheck.Run(screenshotPath);
        if (page == "password")
            return MessageBoxVisualCheck.Run(screenshotPath, passwordPrompt: true);
        if (page == "input")
            return MessageBoxVisualCheck.Run(screenshotPath, textPrompt: true);
        if (page == "selection")
            return MessageBoxVisualCheck.Run(screenshotPath, selectionPrompt: true);
        if (page == "single")
            return MessageBoxVisualCheck.Run(screenshotPath, singleSelectionPrompt: true);
        if (page == "checklist")
            return MessageBoxVisualCheck.Run(screenshotPath, checklistPrompt: true);
        if (page == "progress")
            return MessageBoxVisualCheck.Run(screenshotPath, progressPrompt: true);
        if (page is not ("audio" or "text" or "texture"))
            return 2;
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            Path.Combine(AppContext.BaseDirectory, "Language"));
        localization.SelectedLanguage = "zh-CN";
        var mod = new ModData(new DirectoryInfo(Path.GetTempPath()),
            new LegacyModManifest { Guid = Guid.NewGuid(), Name = "Visual Check Mod",
                Description = string.Empty, Options = [] });
        var resource = new PatchResourceInspectionResult { PatchFileCount = 2 };
        TexturePreviewData? texturePreview = null;
        if (page == "texture")
        {
            resource.Textures.Add(new TextureInspectionItem
            {
                PatchFile = "sample.patch_0", PatchPath = "sample.patch_0", PatchOrder = 0,
                TocEntryIndex = 0, TextureId = 1, MainOffset = 0, MainSize = 4,
                GpuOffset = 0, GpuSize = 0, StreamOffset = 0, StreamSize = 0,
                Width = 128, Height = 128, MipCount = 1, DxgiFormat = 28,
                PayloadKind = "PNG", PayloadSource = "Main",
            });
            var pixels = new byte[128 * 128 * 4];
            for (var y = 0; y < 128; y++)
                for (var x = 0; x < 128; x++)
                {
                    var offset = (y * 128 + x) * 4;
                    pixels[offset] = (byte)(x * 2);
                    pixels[offset + 1] = (byte)(y * 2);
                    pixels[offset + 2] = 200;
                    pixels[offset + 3] = 255;
                }
            texturePreview = new TexturePreviewData
            {
                Width = 128, Height = 128, BgraPixels = pixels, Description = "RGBA",
            };
        }
        var audio = new AudioInventoryResult(
        [
            new AudioBankGroup("sample.patch_0", "Voice Bank", 1,
                Enumerable.Range(1, 16).Select(id => new AudioEntry((ulong)id,
                    AudioEntryOrigin.BankMedia, "sample.patch_0", "Voice Bank", 1,
                    "sample.patch_0", 72, 20_480, 2, 48_000, AudioEntryIssue.None,
                    id % 2 == 0)).ToArray()),
            new AudioBankGroup("sample.patch_1", "Effects", 2,
                Enumerable.Range(17, 12).Select(id => new AudioEntry((ulong)id,
                    AudioEntryOrigin.StreamMedia, "sample.patch_1", "Effects", 2,
                    "sample.patch_1", 72, 35_840, 1, 44_100, AudioEntryIssue.None,
                    id % 2 == 0)).ToArray()),
        ], 2, null);
        var text = new TextInventoryResult(
        [
            new TextBankGroup("sample.patch_0", 1, 0,
                Enumerable.Range(1, 16).Select(id => new TextEntry("sample.patch_0", 1, 0,
                    (uint)id, $"Sample localized text entry {id}",
                    id % 2 == 0 ? $"Original text entry {id}" : null,
                    id % 2 == 0 ? false : null)).ToArray()),
            new TextBankGroup("sample.patch_1", 2, 0,
                Enumerable.Range(17, 12).Select(id => new TextEntry("sample.patch_1", 2, 0,
                    (uint)id, $"Second bank localized text entry {id}" )).ToArray()),
        ], 2, null);
        using var view = new PatchResourceViewerPageView(localization, () => [mod],
            (_, _) => Task.FromResult(resource),
            (_, _, _, _) => Task.FromResult(texturePreview),
            (_, _) => Task.FromResult<AudioInventoryResult?>(page == "audio" ? audio : AudioInventoryResult.Empty),
            (_, _) => Task.FromResult(page == "text" ? text : TextInventoryResult.Empty),
            (_, _) => Task.FromResult(LuaScriptInventoryResult.Empty),
            (_, _, _) => Task.FromResult(new LuaScriptInspectionService.LuaExtractionResult(0,
                string.Empty, null)), () => { });
        var window = new Window { Title = "Patch Resource Viewer Visual Check",
            Width = 1000, Height = 700, Content = view };
        var dispatcher = Dispatcher.CurrentDispatcher;
        var captured = false;
        var scheduled = false;
        window.ContentRendered += (_, _) =>
        {
            if (scheduled)
                return;
            scheduled = true;
            System.Threading.Timer? timer = null;
            timer = new System.Threading.Timer(_ => dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    if (page == "texture")
                    {
                        await view.PendingResourceLoad;
                        await view.PendingTextureLoad;
                        view.OpenTextureZoom();
                        await Task.Delay(200);
                    }
                    Capture(window.Handle, screenshotPath);
                    captured = true;
                    Console.WriteLine($"SCREENSHOT_OK {screenshotPath}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                }
                finally
                {
                    timer?.Dispose();
                    window.Close();
                }
            }), null, 800, Timeout.Infinite);
        };
        var builder = AppBuilder.CreateBuilder([]);
        builder.ConfigureApplication(app => app.MainWindow = window);
        using var jalium = builder.Build();
        jalium.Run();
        return captured ? 0 : 1;
    }

    internal static void Capture(nint handle, string path)
    {
        if (handle == nint.Zero || !GetWindowRect(handle, out var rect))
            throw new InvalidOperationException("Visual check window is unavailable.");
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        try
        {
            if (!PrintWindow(handle, hdc, 2))
                throw new InvalidOperationException("Visual check window could not be captured.");
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint handle, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint handle, nint hdc, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
#endif
