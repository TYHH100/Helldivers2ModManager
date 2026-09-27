using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Jalium.Host;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Jalium.UI;
using Jalium.UI.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Host.Tests;

[TestClass]
public sealed class TagManagementPageViewTests
{
    [TestMethod]
    public void Page_KeepsHeaderActionsListFooterAndLocalization()
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), Path.GetTempPath());
        settings.InitDefault();
        settings.Tags.Add(new ModTag("tag"));
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            GetLanguageDirectory());
        localization.SelectedLanguage = "zh-CN";
        using var editor = new TagManagementEditor(settings);
        using var messageBox = new MessageBoxOverlay(localization);
        using var view = new TagManagementPageView(editor, localization, messageBox,
            () => { }, () => { }, _ => { });

        Assert.AreEqual(3, view.RowDefinitions.Count);
        Assert.AreEqual("标签管理", ((TextBlock)view.Children[0]).Text);
        var content = (Grid)((Border)view.Children[1]).Child!;
        Assert.IsInstanceOfType<Button>(((Border)content.Children[0]).Child);
        var rows = (StackPanel)((ScrollViewer)content.Children[1]).Content!;
        Assert.AreEqual(1, rows.Children.Count);
        var row = (Grid)((Border)rows.Children[0]).Child!;
        Assert.AreEqual("tag", ((TextBlock)row.Children[1]).Text);
        Assert.AreEqual(3, ((StackPanel)row.Children[2]).Children.Count);
        Assert.IsInstanceOfType<Button>(((Border)view.Children[2]).Child);

        localization.SelectedLanguage = "en-US";
        Assert.AreEqual("Tag Management", ((TextBlock)view.Children[0]).Text);
    }

    private static string GetLanguageDirectory([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language");
        throw new DirectoryNotFoundException("Repository language files not found.");
    }
}
