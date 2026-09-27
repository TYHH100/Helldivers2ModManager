using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Host;
using Helldivers2ModManager.Services;
using Jalium.UI.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Host.Tests;

[TestClass]
public sealed class BackgroundTasksPageViewTests
{
    [TestMethod]
    public void Page_ShowsBackgroundTasksAndClearsCompleted()
    {
        var service = new BackgroundTaskService(action => action(), () => true);
        var background = service.Add("scan");
        service.Add("deploy", isForeground: true);
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            GetLanguageDirectory());
        localization.SelectedLanguage = "zh-CN";
        using var view = new BackgroundTasksPageView(service, localization, () => { });

        Assert.AreEqual(2, view.RowDefinitions.Count);
        var list = (StackPanel)((ScrollViewer)view.Children[1]).Content!;
        Assert.AreEqual(1, list.Children.Count);
        Assert.AreEqual("任务中心", ((TextBlock)((StackPanel)((Grid)((Border)view.Children[0]).Child!).Children[1]).Children[0]).Text);
        service.Complete(background);
        service.ClearCompleted();
        Assert.AreEqual(1, list.Children.Count);
        Assert.IsInstanceOfType<StackPanel>(list.Children[0]);

        localization.SelectedLanguage = "en-US";
        Assert.AreEqual("Task Center", ((TextBlock)((StackPanel)((Grid)((Border)view.Children[0]).Child!).Children[1]).Children[0]).Text);
        Assert.AreEqual("No tasks", ((TextBlock)((StackPanel)list.Children[0]).Children[0]).Text);
    }

    private static string GetLanguageDirectory([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language");
        throw new DirectoryNotFoundException("Repository language files not found.");
    }
}
