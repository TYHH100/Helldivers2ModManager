using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewPageStructureTests
{
    [TestMethod]
    public void ModelPreviewPage_SeparatesSimplePreviewAndDecodedMeshesIntoTabs()
    {
        var xamlPath = Path.Combine(
            FindRepositoryRoot().FullName,
            "Helldivers2ModManager", "Views", "ModelPreviewPageView.xaml");
        var document = XDocument.Load(xamlPath);
        var tabControls = document.Descendants().Where(element => element.Name.LocalName == "TabControl").ToArray();
        Assert.AreEqual(1, tabControls.Length);
        var tabControl = tabControls[0];
        var tabs = tabControl.Elements().Where(element => element.Name.LocalName == "TabItem").ToArray();

        Assert.AreEqual(2, tabs.Length);
        Assert.AreEqual("{loc:Loc ModelPreviewPage.PartsAndVariants}", tabs[0].Attribute("Header")?.Value);
        Assert.AreEqual("{loc:Loc ModelPreviewPage.DecodedMeshesTab}", tabs[1].Attribute("Header")?.Value);
        Assert.IsFalse(tabs[0].Descendants().Any(element => element.Name.LocalName == "DataGrid"));
        Assert.AreEqual(1, tabs[1].Descendants().Count(element => element.Name.LocalName == "DataGrid"));
        Assert.IsTrue(tabs[0].Descendants().Any(element =>
            element.Name.LocalName == "ItemsControl" &&
            element.Attribute("ItemsSource")?.Value == "{Binding PreviewOptions}"));

        var bodyShapeOptions = document
            .Descendants()
            .Where(element => element.Name.LocalName == "RadioButton" &&
                              element.Attribute("GroupName")?.Value == "ModelPreviewBodyShape")
            .ToArray();
        Assert.AreEqual(2, bodyShapeOptions.Length);
        Assert.AreEqual("{loc:Loc ModelPreviewPage.StockyBody}", bodyShapeOptions[0].Attribute("Content")?.Value);
        Assert.AreEqual("{loc:Loc ModelPreviewPage.SlimBody}", bodyShapeOptions[1].Attribute("Content")?.Value);

        var armorComboBox = document.Descendants().Single(element =>
            element.Name.LocalName == "ComboBox" &&
            element.Attribute("ItemsSource")?.Value == "{Binding Armors}");
        Assert.IsNull(armorComboBox.Attribute("DisplayMemberPath"));
        Assert.IsTrue(armorComboBox.Descendants().Any(element =>
            element.Name.LocalName == "TextBlock" &&
            element.Attribute("Text")?.Value == "{Binding DisplayName}"));
    }

    [TestMethod]
    public void FluentComboBox_SelectionPresenterUsesTheItemTemplateSelector()
    {
        var xamlPath = Path.Combine(
            FindRepositoryRoot().FullName,
            "Helldivers2ModManager", "Resources", "Styles", "FluentControls.xaml");
        var document = XDocument.Load(xamlPath);
        var comboBoxStyle = document.Descendants().Single(element =>
            element.Name.LocalName == "Style" &&
            element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "FluentComboBox");
        var selectionPresenter = comboBoxStyle.Descendants().Single(element =>
            element.Name.LocalName == "ContentPresenter" &&
            element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "ContentSite");

        Assert.AreEqual(
            "{TemplateBinding ItemTemplateSelector}",
            selectionPresenter.Attribute("ContentTemplateSelector")?.Value);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (DirectoryInfo? current = new(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return current;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root for the model-preview XAML.");
    }
}
