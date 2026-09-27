using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Host;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Host.Tests;

[TestClass]
public sealed class ArmorReusePageViewTests
{
    [TestMethod]
    public async Task ScanRendersOriginalSummaryAndResultsInBothLanguages()
    {
        var localization = CreateLocalization();
        var tasks = new BackgroundTaskService(action => action(), () => true);
        var backCount = 0;
        using var view = new ArmorReusePageView(localization, tasks, () => [],
            (_, _) => Task.FromResult(new ArmorReuseAnalysisResult
            {
                ScannedModCount = 3,
                ScannedPatchCount = 5,
                Records = [new ArmorReuseRecord
                {
                    ModGuid = Guid.NewGuid(), ModName = "Example",
                    SourceArmorId = "0011223344556677", SourceArmorName = "Primary",
                    SharedUnitCount = 2,
                    ReusedBy = [new ArmorReuseTarget { ArmorId = "123", ArmorName = "Secondary" }],
                }],
            }), () => backCount++);

        Assert.AreEqual(3, view.RowDefinitions.Count);
        await view.ScanAsync();
        var stats = (Grid)((Border)view.Children[1]).Child!;
        Assert.AreEqual("3", ((TextBlock)((StackPanel)stats.Children[1]).Children[0]).Text);
        Assert.AreEqual("5", ((TextBlock)((StackPanel)stats.Children[2]).Children[0]).Text);
        Assert.AreEqual("1", ((TextBlock)((StackPanel)stats.Children[3]).Children[0]).Text);
        Assert.AreEqual("1", ((TextBlock)((StackPanel)stats.Children[4]).Children[0]).Text);
        var resultArea = (Grid)view.Children[2];
        var scroll = (ScrollViewer)resultArea.Children[1];
        Assert.AreEqual(Visibility.Visible, scroll.Visibility);
        var row = (StackPanel)((Border)((StackPanel)scroll.Content!).Children[0]).Child!;
        Assert.AreEqual("Example", ((TextBlock)row.Children[0]).Text);
        Assert.IsTrue(((TextBlock)row.Children[1]).Text.Contains("0x0011223344556677"));
        Assert.AreEqual("Secondary", ((TextBlock)row.Children[2]).Text);
        Assert.AreEqual(1, tasks.Tasks.Count);

        localization.SelectedLanguage = "en-US";
        Assert.IsTrue(((TextBlock)stats.Children[0]).Text.Contains("1 mod-to-armor"));
        var header = (Grid)view.Children[0];
        ((Button)header.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(1, backCount);
    }

    [TestMethod]
    public async Task LeavingPageCancelsActiveScan()
    {
        var localization = CreateLocalization();
        var tasks = new BackgroundTaskService(action => action(), () => true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = new ArmorReusePageView(localization, tasks, () => [],
            async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new ArmorReuseAnalysisResult();
            }, () => { });
        var scan = view.ScanAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        view.Dispose();
        await scan.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(BackgroundTaskStatus.Cancelled, tasks.Tasks[0].Status);
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
