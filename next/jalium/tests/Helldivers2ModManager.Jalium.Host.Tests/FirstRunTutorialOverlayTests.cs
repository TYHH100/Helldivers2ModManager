using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Host;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Host.Tests;

[TestClass]
public sealed class FirstRunTutorialOverlayTests
{
    [TestMethod]
    public async Task TutorialRetainsTwelveStepsAndCompletesOnce()
    {
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            GetLanguageDirectory());
        localization.SelectedLanguage = "zh-CN";
        var completions = 0;
        using var overlay = new FirstRunTutorialOverlay(localization, _ => null,
            () => { completions++; return Task.CompletedTask; }, ex => throw ex);

        overlay.Start();
        overlay.Measure(new Size(1000, 650));
        overlay.Arrange(new Rect(0, 0, 1000, 650));
        Assert.AreEqual(Visibility.Visible, overlay.Visibility);
        Assert.AreEqual(12, overlay.StepCount);
        Assert.AreEqual(0, overlay.Step);
        var canvas = (Canvas)overlay.Children[0];
        var dim = (Border)canvas.Children[0];
        Assert.AreEqual(1000, dim.Width);
        Assert.AreEqual(650, dim.Height);
        var card = (Border)canvas.Children[5];
        var content = (Grid)card.Child!;
        var title = content.Children.OfType<TextBlock>().First();
        Assert.AreEqual(localization["FirstRunTutorial.WelcomeTitle"], title.Text);

        overlay.Move(100);
        Assert.AreEqual(11, overlay.Step);
        Assert.AreEqual(localization["FirstRunTutorial.FinishTitle"], title.Text);
        var actions = content.Children.OfType<StackPanel>().Single();
        Assert.AreEqual(Visibility.Collapsed, ((Button)actions.Children[2]).Visibility);
        Assert.AreEqual(Visibility.Visible, ((Button)actions.Children[3]).Visibility);
        overlay.Move(-100);
        Assert.AreEqual(0, overlay.Step);
        Assert.IsFalse(((Button)actions.Children[1]).IsEnabled);

        await overlay.CompleteAsync();
        await overlay.CompleteAsync();
        Assert.AreEqual(1, completions);
        Assert.AreEqual(Visibility.Collapsed, overlay.Visibility);
    }

    private static string GetLanguageDirectory([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!);
             current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return Path.Combine(current.FullName, "src", "Helldivers2ModManager",
                    "Resources", "Language");
        throw new DirectoryNotFoundException("Repository language files not found.");
    }
}
