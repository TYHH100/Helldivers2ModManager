using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Host;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Host.Tests;

[TestClass]
public sealed class ModelPreviewPageViewTests
{
    [TestMethod]
    public async Task PreviewKeepsOriginalRegionsAndRendersDecodedGeometry()
    {
        var mod = CreateMod("Example");
        var result = CreateResult("sample.patch_0");
        using var view = new ModelPreviewPageView(CreateLocalization(), () => [mod],
            (selected, _, _, _, _) =>
            {
                Assert.AreEqual(mod, selected);
                return Task.FromResult(result);
            }, (_, _, _, _) => Task.FromResult<TexturePreviewData?>(null),
            () => { }, _ => { }, mod);
        await view.PendingLoad.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(3, view.RowDefinitions.Count);
        var workspace = (Grid)view.Children[2];
        Assert.AreEqual(240, workspace.ColumnDefinitions[0].Width.Value);
        var right = (Grid)workspace.Children[1];
        Assert.AreEqual(4, right.RowDefinitions.Count);
        Assert.AreEqual(230, right.RowDefinitions[3].Height.Value);
        Assert.AreEqual(1, view.Scene.DisplayedMeshCount);
        Assert.AreEqual(1, view.Scene.ModelGroup!.Children.Count);
        var tabs = (TabControl)((Border)right.Children[2]).Child!;
        Assert.AreEqual(2, tabs.Items.Count);
        var meshArea = (Grid)((TabItem)tabs.Items[1]).Content!;
        Assert.AreEqual(1, ((DataGrid)meshArea.Children[1]).Items.Count);
    }

    [TestMethod]
    public async Task EarlierModLoadCannotReplaceNewSelection()
    {
        var first = CreateMod("First");
        var second = CreateMod("Second");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ModelPreviewResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(() =>
        {
            try
            {
                var current = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(current));
                ready.SetResult(current);
                Dispatcher.Run();
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        });
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        var dispatcher = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ModelPreviewPageView? view = null;
        try
        {
            dispatcher.Invoke(() => view = new ModelPreviewPageView(CreateLocalization(),
                () => [first, second],
                (mod, _, _, _, _) =>
                {
                    if (ReferenceEquals(mod, first))
                    {
                        started.TrySetResult();
                        return release.Task;
                    }
                    return Task.FromResult(CreateResult("second.patch_0"));
                }, (_, _, _, _) => Task.FromResult<TexturePreviewData?>(null),
                () => { }, _ => { }, first));
            var earlierLoad = dispatcher.Invoke(() => view!.PendingLoad);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            dispatcher.Invoke(() =>
            {
                var workspace = (Grid)view!.Children[2];
                var sidebar = (Grid)((Border)workspace.Children[0]).Child!;
                ((ListBox)sidebar.Children[1]).SelectedItem = second;
            });
            await dispatcher.Invoke(() => view!.PendingLoad).WaitAsync(TimeSpan.FromSeconds(5));
            release.SetResult(CreateResult("first.patch_0"));
            await earlierLoad.WaitAsync(TimeSpan.FromSeconds(5));
            dispatcher.Invoke(() =>
            {
                Assert.AreEqual(1, view!.Scene.DisplayedMeshCount);
                var workspace = (Grid)view.Children[2];
                var right = (Grid)workspace.Children[1];
                var tabs = (TabControl)((Border)right.Children[2]).Child!;
                var meshArea = (Grid)((TabItem)tabs.Items[1]).Content!;
                var mesh = (ModelPreviewMesh)((DataGrid)meshArea.Children[1]).Items[0];
                Assert.AreEqual("second.patch_0", mesh.PatchFile);
            });
        }
        finally
        {
            dispatcher.Invoke(() => view?.Dispose());
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            Assert.IsTrue(uiThread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    private static ModData CreateMod(string name) => new(
        new DirectoryInfo(Path.Combine(Path.GetTempPath(), name)),
        new LegacyModManifest { Guid = Guid.NewGuid(), Name = name,
            Description = string.Empty, Options = [] });

    private static ModelPreviewResult CreateResult(string patchFile)
    {
        var positions = new List<float>();
        var indices = new List<int>();
        for (var y = 0; y < 6; y++)
            for (var z = 0; z < 6; z++)
            {
                positions.Add(0);
                positions.Add(y / 5f - 0.5f);
                positions.Add(z / 5f - 0.5f);
            }
        for (var y = 0; y < 5; y++)
            for (var z = 0; z < 5; z++)
            {
                var first = y * 6 + z;
                indices.AddRange([first, first + 1, first + 6,
                    first + 1, first + 7, first + 6]);
            }
        var result = new ModelPreviewResult { PatchFileCount = 1 };
        result.Meshes.Add(new ModelPreviewMesh
        {
            PatchFile = patchFile, UnitId = 1, StreamIndex = 0,
            Positions = positions.ToArray(), TriangleIndices = indices.ToArray(),
        });
        return result;
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
