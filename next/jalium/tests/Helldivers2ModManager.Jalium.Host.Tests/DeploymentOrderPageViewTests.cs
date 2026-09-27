using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Jalium.Host;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Jalium.UI.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Host.Tests;

[TestClass]
public sealed class DeploymentOrderPageViewTests
{
    [TestMethod]
    public void Page_HasOriginalRowsToolbarAndLocalizedItems()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-view-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new SettingsService(NullLogger<SettingsService>.Instance,
                Path.Combine(root, "settings.json"), Path.Combine(root, "data"));
            settings.InitDefault();
            settings.UseDeploymentOrder = true;
            var localization = new LocalizationService(NullLogger<LocalizationService>.Instance, GetLanguageDirectory());
            localization.SelectedLanguage = "zh-CN";
            var modDirectory = new DirectoryInfo(Path.Combine(root, "mod"));
            modDirectory.Create();
            var mod = new ModData(modDirectory, new LegacyModManifest
            {
                Guid = Guid.NewGuid(), Name = "测试模组", Description = string.Empty,
            });
            using var editor = new DeploymentOrderEditor(settings, localization, [mod], () => null);
            using var view = new DeploymentOrderPageView(editor, localization, () => { }, _ => { });

            Assert.AreEqual(5, view.RowDefinitions.Count);
            Assert.AreEqual(5, view.Children.Count);
            var header = (Grid)view.Children[0];
            var title = (TextBlock)header.Children[1];
            Assert.AreEqual("部署顺序", title.Text);
            Assert.AreEqual(28, title.FontSize);
            var toolbarBorder = (Border)view.Children[2];
            var toolbar = toolbarBorder.Child as WrapPanel;
            Assert.IsNotNull(toolbar);
            Assert.AreEqual(9, toolbar.Children.Count);
            Assert.AreEqual("置顶", ((Button)toolbar.Children[0]).Content);
            var listBorder = (Border)view.Children[3];
            var list = listBorder.Child as ListBox;
            Assert.IsNotNull(list);
            Assert.AreSame(editor.Items, list.ItemsSource);
            var item = list.ItemTemplate!.LoadContent() as Grid;
            Assert.IsNotNull(item);
            Assert.AreEqual(3, item.ColumnDefinitions.Count);
            Assert.IsInstanceOfType<CheckBox>(item.Children[0]);

            localization.SelectedLanguage = "en-US";
            Assert.AreEqual("Deployment Order", title.Text);
            Assert.AreEqual("Move to Top", ((Button)toolbar.Children[0]).Content);
        }
        finally
        {
            var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hd2mm-jalium-view-tests")) + Path.DirectorySeparatorChar;
            var resolvedRoot = Path.GetFullPath(root);
            if (resolvedRoot.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(resolvedRoot, recursive: true);
        }
    }

    private static string GetLanguageDirectory([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language");
        throw new DirectoryNotFoundException("Repository language files not found.");
    }
}
