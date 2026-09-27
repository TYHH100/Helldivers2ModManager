using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Host;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Parsing;
using Jalium.UI;
using Jalium.UI.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Host.Tests;

[TestClass]
public sealed class PatchResourceViewerPageViewTests
{
    [TestMethod]
    public async Task ViewerKeepsOriginalRegionsAndLoadsSelectedModResources()
    {
        var localization = CreateLocalization();
        var mod = new ModData(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "Sample")),
            new LegacyModManifest { Guid = Guid.NewGuid(), Name = "Sample", Description = string.Empty, Options = [] });
        var inspected = new List<Guid>();
        var result = new PatchResourceInspectionResult { PatchFileCount = 1 };
        result.TocEntries.Add(new PatchTocInspectionItem
        {
            PatchFile = "sample.patch_0", PatchPath = "sample.patch_0", PatchOrder = 0,
            EntryIndex = 1, FileId = 1, TypeId = 2, MainSize = 20, GpuSize = 0,
            StreamSize = 0, MainOffset = 72, GpuOffset = 0, StreamOffset = 0,
        });
        using var view = new PatchResourceViewerPageView(localization, () => [mod],
            (selected, _) =>
            {
                inspected.Add(selected.Manifest.Guid);
                return Task.FromResult(result);
            }, (_, _, _, _) => Task.FromResult<TexturePreviewData?>(null),
            (_, _) => Task.FromResult<AudioInventoryResult?>(AudioInventoryResult.Empty),
            (_, _) => Task.FromResult(TextInventoryResult.Empty),
            (_, _) => Task.FromResult(LuaScriptInventoryResult.Empty),
            (_, _, _) => Task.FromResult(new LuaScriptInspectionService.LuaExtractionResult(0, string.Empty, null)),
            () => { });
        await view.PendingResourceLoad;

        Assert.AreEqual(2, view.RowDefinitions.Count);
        var workspace = (Grid)view.Children[1];
        Assert.AreEqual(240, workspace.ColumnDefinitions[0].Width.Value);
        var sidebar = (Grid)((Border)workspace.Children[0]).Child!;
        Assert.AreEqual(1, ((ListBox)sidebar.Children[1]).Items.Count);
        var right = (Grid)workspace.Children[1];
        var tabs = (TabControl)right.Children[1];
        Assert.AreEqual(6, tabs.Items.Count);
        var toc = (DataGrid)((TabItem)tabs.Items[0]).Content!;
        Assert.AreEqual(1, toc.Items.Count);
        Assert.AreEqual(mod.Manifest.Guid, inspected.Single());
        Assert.IsTrue(((TextBlock)right.Children[0]).Text.Contains("1 个 Patch"));

        localization.SelectedLanguage = "en-US";
        Assert.AreEqual("Patch TOC Entries", ((TabItem)tabs.Items[0]).Header);
        Assert.AreEqual("File ID", toc.Columns[2].Header);
    }

    [TestMethod]
    public async Task TextureZoomOpensWithPreviewAndClosesOnDispose()
    {
        var mod = new ModData(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "Sample")),
            new LegacyModManifest { Guid = Guid.NewGuid(), Name = "Sample", Description = string.Empty, Options = [] });
        var texture = new TextureInspectionItem
        {
            PatchFile = "sample.patch_0", PatchPath = "sample.patch_0", PatchOrder = 0,
            TocEntryIndex = 0, TextureId = 1, MainOffset = 0, MainSize = 4,
            GpuOffset = 0, GpuSize = 0, StreamOffset = 0, StreamSize = 0,
            Width = 1, Height = 1, MipCount = 1, DxgiFormat = 28,
            PayloadKind = "PNG", PayloadSource = "Main",
        };
        var resources = new PatchResourceInspectionResult();
        resources.Textures.Add(texture);
        var view = new PatchResourceViewerPageView(CreateLocalization(), () => [mod],
            (_, _) => Task.FromResult(resources),
            (_, _, _, _) => Task.FromResult<TexturePreviewData?>(new TexturePreviewData
            {
                Width = 1, Height = 1, BgraPixels = [0, 0, 255, 255], Description = "RGBA",
            }),
            (_, _) => Task.FromResult<AudioInventoryResult?>(AudioInventoryResult.Empty),
            (_, _) => Task.FromResult(TextInventoryResult.Empty),
            (_, _) => Task.FromResult(LuaScriptInventoryResult.Empty),
            (_, _, _) => Task.FromResult(new LuaScriptInspectionService.LuaExtractionResult(0, string.Empty, null)),
            () => { });
        await view.PendingResourceLoad;
        await view.PendingTextureLoad;

        var overlay = (Grid)view.Children[2];
        Assert.AreEqual(Visibility.Collapsed, overlay.Visibility);
        view.OpenTextureZoom();
        Assert.AreEqual(Visibility.Visible, overlay.Visibility);
        var content = (Grid)overlay.Children[0];
        var viewport = (Border)content.Children[0];
        var image = (Image)((Canvas)viewport.Child!).Children[0];
        Assert.IsNotNull(image.Source);
        view.Dispose();
        Assert.AreEqual(Visibility.Collapsed, overlay.Visibility);
    }

    [TestMethod]
    public async Task LeavingViewerCancelsPendingInspection()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mod = new ModData(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "Sample")),
            new LegacyModManifest { Guid = Guid.NewGuid(), Name = "Sample", Description = string.Empty, Options = [] });
        var view = new PatchResourceViewerPageView(CreateLocalization(), () => [mod],
            async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new PatchResourceInspectionResult();
            }, (_, _, _, _) => Task.FromResult<TexturePreviewData?>(null),
            (_, _) => Task.FromResult<AudioInventoryResult?>(AudioInventoryResult.Empty),
            (_, _) => Task.FromResult(TextInventoryResult.Empty),
            (_, _) => Task.FromResult(LuaScriptInventoryResult.Empty),
            (_, _, _) => Task.FromResult(new LuaScriptInspectionService.LuaExtractionResult(0, string.Empty, null)),
            () => { });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        view.Dispose();
        await view.PendingResourceLoad.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task LuaTabShowsSelectedPatchReport()
    {
        var mod = new ModData(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "Sample")),
            new LegacyModManifest { Guid = Guid.NewGuid(), Name = "Sample", Description = string.Empty, Options = [] });
        var script = new LuaScriptEntry("sample.patch_0", 1, 2, 72, 24, 1, 1,
            "sample.lua", "return 'sample'", "source");
        var inventory = new LuaScriptInventoryResult(
            [new LuaScriptPatchGroup("sample.patch_0", [script])], 1, null);
        var view = new PatchResourceViewerPageView(CreateLocalization(), () => [mod],
            (_, _) => Task.FromResult(new PatchResourceInspectionResult { PatchFileCount = 1 }),
            (_, _, _, _) => Task.FromResult<TexturePreviewData?>(null),
            (_, _) => Task.FromResult<AudioInventoryResult?>(AudioInventoryResult.Empty),
            (_, _) => Task.FromResult(TextInventoryResult.Empty),
            (_, _) => Task.FromResult(inventory),
            (_, _, _) => Task.FromResult(new LuaScriptInspectionService.LuaExtractionResult(0, string.Empty, null)),
            () => { });
        await view.PendingResourceLoad;

        var workspace = (Grid)view.Children[1];
        var right = (Grid)workspace.Children[1];
        var tabs = (TabControl)right.Children[1];
        var lua = (TabItem)tabs.Items[5];
        var layout = (Grid)lua.Content!;
        Assert.AreEqual(Visibility.Visible, lua.Visibility);
        Assert.AreEqual(5, tabs.SelectedIndex);
        Assert.AreEqual(script, ((ListBox)layout.Children[3]).SelectedItem);
        Assert.AreEqual(script.Report, ((TextBox)layout.Children[4]).Text);
        view.Dispose();
    }

    [TestMethod]
    public async Task TextTabShowsSelectedPatchEntriesAndFiltersByContent()
    {
        var mod = new ModData(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "Sample")),
            new LegacyModManifest { Guid = Guid.NewGuid(), Name = "Sample", Description = string.Empty, Options = [] });
        var entries = new[]
        {
            new TextEntry("sample.patch_0", 1, 0, 10, "Replacement", "Original", false),
            new TextEntry("sample.patch_0", 1, 0, 11, "Unchanged", "Unchanged", true),
        };
        var inventory = new TextInventoryResult(
            [new TextBankGroup("sample.patch_0", 1, 0, entries)], 1, null);
        using var view = new PatchResourceViewerPageView(CreateLocalization(), () => [mod],
            (_, _) => Task.FromResult(new PatchResourceInspectionResult { PatchFileCount = 1 }),
            (_, _, _, _) => Task.FromResult<TexturePreviewData?>(null),
            (_, _) => Task.FromResult<AudioInventoryResult?>(AudioInventoryResult.Empty),
            (_, _) => Task.FromResult(inventory),
            (_, _) => Task.FromResult(LuaScriptInventoryResult.Empty),
            (_, _, _) => Task.FromResult(new LuaScriptInspectionService.LuaExtractionResult(0, string.Empty, null)),
            () => { });
        await view.PendingResourceLoad;

        var workspace = (Grid)view.Children[1];
        var right = (Grid)workspace.Children[1];
        var tabs = (TabControl)right.Children[1];
        var textTab = (TabItem)tabs.Items[4];
        var layout = (Grid)textTab.Content!;
        var list = (ListBox)layout.Children[1];
        Assert.AreEqual(Visibility.Visible, textTab.Visibility);
        Assert.AreEqual(4, tabs.SelectedIndex);
        Assert.AreEqual(3, list.Items.Count);
        Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(list));
        var controls = (Grid)layout.Children[0];
        ((TextBox)controls.Children[0]).Text = "Replacement";
        Assert.AreEqual(2, list.Items.Count);
    }

    [TestMethod]
    public async Task AudioTabShowsSelectedPatchMediaAndFiltersByBank()
    {
        var mod = new ModData(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "Sample")),
            new LegacyModManifest { Guid = Guid.NewGuid(), Name = "Sample", Description = string.Empty, Options = [] });
        var entries = new[]
        {
            new AudioEntry(10, AudioEntryOrigin.BankMedia, "sample.patch_0", "Voice Bank", 1,
                "sample.patch_0", 72, 256, 2, 48000, AudioEntryIssue.None, false),
            new AudioEntry(11, AudioEntryOrigin.StreamMedia, "sample.patch_0", "Effects", 2,
                "sample.patch_0", 328, 256, 1, 48000, AudioEntryIssue.Truncated, true),
        };
        var inventory = new AudioInventoryResult(
            [new AudioBankGroup("sample.patch_0", "Voice Bank", 1, [entries[0]]),
                new AudioBankGroup("sample.patch_0", "Effects", 2, [entries[1]])], 1, null);
        using var view = new PatchResourceViewerPageView(CreateLocalization(), () => [mod],
            (_, _) => Task.FromResult(new PatchResourceInspectionResult { PatchFileCount = 1 }),
            (_, _, _, _) => Task.FromResult<TexturePreviewData?>(null),
            (_, _) => Task.FromResult<AudioInventoryResult?>(inventory),
            (_, _) => Task.FromResult(TextInventoryResult.Empty),
            (_, _) => Task.FromResult(LuaScriptInventoryResult.Empty),
            (_, _, _) => Task.FromResult(new LuaScriptInspectionService.LuaExtractionResult(0, string.Empty, null)),
            () => { });
        await view.PendingResourceLoad;

        var workspace = (Grid)view.Children[1];
        var right = (Grid)workspace.Children[1];
        var tabs = (TabControl)right.Children[1];
        var audioTab = (TabItem)tabs.Items[3];
        var layout = (Grid)audioTab.Content!;
        var list = (ListBox)layout.Children[2];
        Assert.AreEqual(Visibility.Visible, audioTab.Visibility);
        Assert.AreEqual(3, tabs.SelectedIndex);
        Assert.AreEqual(4, list.Items.Count);
        Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(list));
        var controls = (Grid)layout.Children[0];
        ((TextBox)controls.Children[5]).Text = "Voice Bank";
        Assert.AreEqual(2, list.Items.Count);
    }

    [TestMethod]
    public async Task LeavingViewerCancelsPendingAudioInventory()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mod = new ModData(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "Sample")),
            new LegacyModManifest { Guid = Guid.NewGuid(), Name = "Sample", Description = string.Empty, Options = [] });
        var view = new PatchResourceViewerPageView(CreateLocalization(), () => [mod],
            (_, _) => Task.FromResult(new PatchResourceInspectionResult()),
            (_, _, _, _) => Task.FromResult<TexturePreviewData?>(null),
            async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return AudioInventoryResult.Empty;
            },
            (_, _) => Task.FromResult(TextInventoryResult.Empty),
            (_, _) => Task.FromResult(LuaScriptInventoryResult.Empty),
            (_, _, _) => Task.FromResult(new LuaScriptInspectionService.LuaExtractionResult(0, string.Empty, null)),
            () => { });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        view.Dispose();
        await view.PendingResourceLoad.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public void TextureChannelsPreserveOriginalAndExposeAlpha()
    {
        byte[] pixel = [7, 13, 29, 41];
        CollectionAssert.AreEqual(new byte[] { 7, 13, 29, 41 },
            PatchResourceViewerPageView.ConvertChannel(pixel, PatchTextureChannel.Rgba));
        CollectionAssert.AreEqual(new byte[] { 7, 13, 29, 255 },
            PatchResourceViewerPageView.ConvertChannel(pixel, PatchTextureChannel.Rgb));
        CollectionAssert.AreEqual(new byte[] { 41, 41, 41, 255 },
            PatchResourceViewerPageView.ConvertChannel(pixel, PatchTextureChannel.Alpha));
        CollectionAssert.AreEqual(new byte[] { 7, 13, 29, 41 }, pixel);
    }

    [TestMethod]
    public void ZigTextureChannelsMatchManagedConversion()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "jalium_texture.dll")))
            Assert.Inconclusive("Build next/jalium/native/texture.zig to run the native parity check.");

        var random = new Random(24819);
        foreach (var count in new[] { 1, 5, 257, 4096 })
        {
            var source = new byte[count * 4];
            random.NextBytes(source);
            var original = (byte[])source.Clone();
            foreach (var channel in Enum.GetValues<PatchTextureChannel>())
            {
                var native = new byte[source.Length];
                Assert.IsTrue(NativeTextureConverter.TryConvert(source, native, channel, force: true));
                CollectionAssert.AreEqual(
                    PatchResourceViewerPageView.ConvertChannelManaged(source, channel), native);
                CollectionAssert.AreEqual(original, source);
            }
        }
        Assert.IsFalse(NativeTextureConverter.TryConvert([1, 2, 3], new byte[3],
            PatchTextureChannel.Rgb, force: true));
        CollectionAssert.AreEqual(Array.Empty<byte>(),
            PatchResourceViewerPageView.ConvertChannel([], PatchTextureChannel.Rgb));
        var thresholdSource = new byte[4_194_304 * 4];
        Assert.IsFalse(NativeTextureConverter.TryConvert(thresholdSource,
            new byte[thresholdSource.Length], PatchTextureChannel.Rgb));
        Assert.IsTrue(NativeTextureConverter.TryConvert(thresholdSource,
            new byte[thresholdSource.Length], PatchTextureChannel.Alpha));
    }

    private static LocalizationService CreateLocalization([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!);
             current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
            {
                var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
                    Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language"));
                localization.SelectedLanguage = "zh-CN";
                return localization;
            }
        throw new DirectoryNotFoundException("Repository language files not found.");
    }
}
