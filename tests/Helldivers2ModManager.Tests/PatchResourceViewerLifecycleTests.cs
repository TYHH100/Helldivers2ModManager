using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Windows.Threading;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Helldivers2ModManager.Services.Parsing;
using Helldivers2ModManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class PatchResourceViewerLifecycleTests
{
    [TestMethod]
    public Task OriginalTextureResolution_PreservesPngPixelsAndLatestSelection() => RunOnDispatcher(async () =>
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hd2_resource_view_" + Guid.NewGuid().ToString("N")));
        try
        {
            WritePatch(root, PatchResourceTypeIds.Script, Encoding.UTF8.GetBytes("return 1"));
            var patch = Path.Combine(root.FullName, "9ba626afa44a3aa3.patch_0");
            const int width = 4096, height = 4;
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = (byte)(i / 4 % 256);
                pixels[i + 3] = 255;
            }
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var stream = File.Create(patch + ".gpu_resources")) encoder.Save(stream);
            using var provider = new ServiceCollection().BuildServiceProvider();
            using var audio = new AudioPlaybackService(NullLogger<AudioPlaybackService>.Instance);
            using var page = CreatePage(provider, audio);
            page.SetInitialMod(CreateMod(root));
            await WaitForLoad(page);
            page.SelectedTexture = new TextureInspectionItem
            {
                PatchFile = Path.GetFileName(patch), PatchPath = patch, PatchOrder = 0, TocEntryIndex = 0,
                TextureId = 1, MainOffset = 0, MainSize = 0, GpuOffset = 0,
                GpuSize = (uint)new FileInfo(patch + ".gpu_resources").Length,
                StreamOffset = 0, StreamSize = 0, Width = width, Height = height, MipCount = 1,
                DxgiFormat = 0, PayloadKind = "PNG", PayloadSource = "gpu"
            };
            await page.PendingTextureLoad;
            Assert.AreEqual(2048, ((System.Windows.Media.Imaging.BitmapSource)page.TexturePreview!).PixelWidth);
            page.UseOriginalTextureResolution = true;
            await page.PendingTextureLoad;
            var original = (System.Windows.Media.Imaging.BitmapSource)page.TexturePreview!;
            Assert.AreEqual(width, original.PixelWidth);
            var actual = new byte[pixels.Length];
            original.CopyPixels(actual, width * 4, 0);
            CollectionAssert.AreEqual(pixels, actual);
            page.UseOriginalTextureResolution = false;
            var superseded = page.PendingTextureLoad;
            page.UseOriginalTextureResolution = true;
            await Task.WhenAll(superseded, page.PendingTextureLoad);
            Assert.AreEqual(width, ((System.Windows.Media.Imaging.BitmapSource)page.TexturePreview!).PixelWidth);
        }
        finally { root.Delete(recursive: true); }
    });

    [TestMethod]
    public Task SwitchingMods_UsesCurrentDeploymentSelection() => RunOnDispatcher(async () =>
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hd2_resource_view_" + Guid.NewGuid().ToString("N")));
        try
        {
            var luaDir = root.CreateSubdirectory("lua");
            var textDir = root.CreateSubdirectory("text");
            WritePatch(luaDir, PatchResourceTypeIds.Script, Encoding.UTF8.GetBytes("local message = 'resource viewer test'\nreturn message\n"));
            WritePatch(textDir, PatchResourceTypeIds.Strings, TextBankFormat.Write(0, [new(42u, "Viewer subtitle")]));
            var lua = CreateMod(luaDir);
            var text = new ModData(textDir, new V1ModManifest
            {
                Guid = Guid.NewGuid(), Name = "text", Description = "",
                Options = [new ModOption { Name = "subtitles", Include = ["."] }]
            });
            using var provider = new ServiceCollection().BuildServiceProvider();
            using var audio = new AudioPlaybackService(NullLogger<AudioPlaybackService>.Instance);
            using var page = CreatePage(provider, audio);
            page.SetInitialMod(lua);
            var superseded = page.PendingResourceLoad;
            page.SetInitialMod(text); // Supersede a pending Lua load before its UI continuation.
            await WaitForLoad(page);
            await superseded;
            Assert.IsTrue(page.HasTextEntries, page.StatusText);
            Assert.IsFalse(page.HasLuaEntries);
            Assert.AreEqual(PatchResourceViewerPageViewModel.TextPreviewTabIndex, page.SelectedPreviewTabIndex);
            Assert.AreEqual(1, page.TocEntries.Count);
            var deploymentEnabled = text.EnabledOptions.ToArray();
            CollectionAssert.AreEqual(deploymentEnabled, text.EnabledOptions);
            page.SetInitialMod(lua);
            await WaitForLoad(page);
            Assert.IsTrue(page.HasLuaEntries, page.StatusText);
            Assert.AreEqual(PatchResourceViewerPageViewModel.LuaScriptsPreviewTabIndex, page.SelectedPreviewTabIndex);
            StringAssert.Contains(page.LuaReportText, "resource viewer test");

            page.SetInitialMod(text);
            var pending = page.PendingResourceLoad;
            page.Dispose();
            await pending;
            Assert.AreEqual(0, page.TocEntries.Count);
            Assert.IsFalse(page.HasTextEntries);
            Assert.IsFalse(page.HasLuaEntries);
            Assert.AreEqual(AudioPlaybackState.Idle, audio.State);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    });

    private static ModData CreateMod(DirectoryInfo directory) => new(directory,
        new V1ModManifest { Guid = Guid.NewGuid(), Name = directory.Name, Description = "" });

    private static PatchResourceViewerPageViewModel CreatePage(IServiceProvider provider, AudioPlaybackService audio)
    {
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        var mods = new ModService(NullLogger<ModService>.Instance, null!, null!, localization, null!, null!, null!);
        var page = new PatchResourceViewerPageViewModel(
            NullLogger<PatchResourceViewerPageViewModel>.Instance, provider, mods,
            new PatchResourceInspectionService(), localization,
            new AudioBankInspectionService(NullLogger<AudioBankInspectionService>.Instance, settings), audio,
            new ModTypeDetectionService(NullLogger<ModTypeDetectionService>.Instance),
            new TextBankInspectionService(NullLogger<TextBankInspectionService>.Instance, settings),
            new LuaScriptInspectionService(NullLogger<LuaScriptInspectionService>.Instance));
        typeof(ModService).GetField("<Initialized>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mods, true);
        return page;
    }

    private static async Task WaitForLoad(PatchResourceViewerPageViewModel page)
    {
        await page.PendingResourceLoad.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(page.IsLoading, "Resource loading timed out.");
    }

    private static void WritePatch(DirectoryInfo directory, ulong type, byte[] payload)
    {
        const int offset = 72 + 32 + 80 + 8;
        var bytes = new byte[offset + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xF0000011);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(80), type);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(88), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(104), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(112), type);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(120), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(160), (uint)payload.Length);
        payload.CopyTo(bytes, offset);
        File.WriteAllBytes(Path.Combine(directory.FullName, "9ba626afa44a3aa3.patch_0"), bytes);
    }

    private static Task RunOnDispatcher(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
