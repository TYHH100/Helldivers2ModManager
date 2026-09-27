using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Host;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Host.Tests;

[TestClass]
public sealed class FolderPickerDialogTests
{
    [TestMethod]
    public void SelectingChildThenConfirmingReturnsTheCurrentDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-folder-picker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Child"));
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance, GetLanguageDirectory());
        var owner = new Window();
        try
        {
            var dialog = new FolderPickerDialog("Choose folder", root, localization, owner);
            var layout = (Grid)dialog.Content!;
            var border = (Border)layout.Children[2];
            var list = (StackPanel)((ScrollViewer)border.Child!).Content!;
            Assert.AreEqual(1, list.Children.Count);
            ((Button)list.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var commands = (StackPanel)layout.Children[3];
            ((Button)commands.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.IsTrue(dialog.Accepted);
            Assert.AreEqual(Path.Combine(root, "Child"), dialog.SelectedPath);
        }
        finally
        {
            owner.Close();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string GetLanguageDirectory([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!); current is not null;
             current = current.Parent)
        {
            var path = Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("Language resources were not found.");
    }
}
