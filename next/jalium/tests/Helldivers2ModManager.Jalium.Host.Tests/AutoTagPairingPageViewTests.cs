using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Jalium.Host;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Jalium.UI;
using Jalium.UI.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Host.Tests;

[TestClass]
public sealed class AutoTagPairingPageViewTests
{
    [TestMethod]
    public void Page_PreservesTypeRowsAndLocalizedActions()
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(Path.GetTempPath(), "hd2mm-jalium-pairing-unused.json"), Path.GetTempPath());
        settings.InitDefault();
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            GetLanguageDirectory()) { SelectedLanguage = "zh-CN" };
        using var editor = new AutoTagPairingEditor(settings, localization);
        using var messageBox = new MessageBoxOverlay(localization);
        using var view = new AutoTagPairingPageView(editor, localization, messageBox,
            () => { }, ex => throw ex);

        Assert.AreEqual(3, view.RowDefinitions.Count);
        Assert.AreEqual("自动识别标签配对", ((TextBlock)view.Children[0]).Text);
        var body = (Border)view.Children[1];
        var content = (StackPanel)((ScrollViewer)body.Child!).Content!;
        var rows = (StackPanel)content.Children[1];
        Assert.AreEqual(editor.Rows.Count, rows.Children.Count);
        var first = (Grid)rows.Children[0];
        Assert.AreEqual(200, first.ColumnDefinitions[0].Width.Value);
        Assert.IsInstanceOfType<ComboBox>(first.Children[1]);

        localization.SelectedLanguage = "en-US";
        Assert.AreEqual("Auto-tag Pairing", ((TextBlock)view.Children[0]).Text);
    }

    private static string GetLanguageDirectory([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language");
        throw new DirectoryNotFoundException("Repository language files not found.");
    }
}
