using System.Globalization;
using System.Xml.Linq;
using System.Xml;
using System.Text.RegularExpressions;
using Xunit;

namespace Helldivers2ModManager.Tests;

public sealed class LocalizationLayoutStaticTests
{
    [Fact]
    public void TextBearingControlsDoNotUseFixedPixelDimensions()
    {
        foreach (var path in XamlFiles())
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants())
            {
                var name = element.Name.LocalName;
                if (name is not ("TextBlock" or "TextBox" or "ComboBox" or "ListBox" or "DataGrid" or "Label" or "RadioButton" or "Button"))
                    continue;
                var isIcon = element.Attribute("FontFamily")?.Value.Contains("Fluent Icons", StringComparison.OrdinalIgnoreCase) == true;
                var allowedIconSize = name == "Button" || isIcon ? 40 : 0;
                AssertDimensionIsFlexible(path, element, "Width", allowedIconSize);
                AssertDimensionIsFlexible(path, element, "Height", allowedIconSize);
            }
        }
    }

    [Fact]
    public void LocalizedTextIsNeverSilentlyEllipsized()
    {
        foreach (var path in XamlFiles())
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants())
            {
                var text = element.Attribute("Text")?.Value;
                if (text?.StartsWith("{loc:Loc ", StringComparison.Ordinal) != true)
                    continue;
                Assert.Null(element.Attribute("TextTrimming"));
            }
        }
    }

    [Fact]
    public void XamlDoesNotContainHardCodedUserVisibleWords()
    {
        var visibleAttributes = new[] { "Text", "Content", "Header", "ToolTip" };
        foreach (var path in XamlFiles())
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants())
            {
                foreach (var attributeName in visibleAttributes)
                {
                    var value = element.Attribute(attributeName)?.Value;
                    if (string.IsNullOrWhiteSpace(value) || value.StartsWith('{'))
                        continue;
                    Assert.False(
                        value.Any(char.IsLetter),
                        $"{Path.GetFileName(path)}:{((IXmlLineInfo)element).LineNumber} hard-codes {attributeName}=\"{value}\".");
                }
            }
        }
    }

    [Fact]
    public void OverlayDialogBoundsAreRelativeToTheirHost()
    {
        foreach (var path in XamlFiles())
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var border in document.Descendants().Where(element =>
                element.Name.LocalName == "Border"
                && element.Attribute("Style")?.Value == "{StaticResource FluentMessageDialog}"))
            {
                AssertRelativeHostBound(path, border, "MaxWidth", "ActualWidth");
                AssertRelativeHostBound(path, border, "MaxHeight", "ActualHeight");
            }
        }
    }

    [Fact]
    public void VersionDetailOverlayDoesNotInheritShellViewModelBeforeItIsShown()
    {
        var repository = FindRepositoryRoot();
        var path = Path.Combine(repository, "Helldivers2ModManager", "Components", "VersionCheckDetailOverlay.xaml");
        var document = XDocument.Load(path);
        Assert.Equal("{x:Null}", document.Root?.Attribute("DataContext")?.Value);
    }

    [Fact]
    public void IconOnlyButtonsDeclareAccessibleNames()
    {
        var xamlNamespace = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        foreach (var path in XamlFiles())
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var button in document.Descendants().Where(static element => element.Name.LocalName == "Button"))
            {
                var textBlocks = button.Descendants().Where(static element => element.Name.LocalName == "TextBlock").ToArray();
                if (!textBlocks.Any(IsFluentIcon))
                    continue;
                var hasTextContent = textBlocks.Any(element =>
                    !IsFluentIcon(element) &&
                    (!string.IsNullOrWhiteSpace(element.Attribute("Text")?.Value) ||
                     !string.IsNullOrWhiteSpace(element.Attribute(xamlNamespace + "Name")?.Value)));
                if (hasTextContent || !string.IsNullOrWhiteSpace(button.Attribute("Content")?.Value))
                    continue;

                Assert.False(
                    string.IsNullOrWhiteSpace(button.Attribute("AutomationProperties.Name")?.Value),
                    $"{Path.GetFileName(path)}:{((IXmlLineInfo)button).LineNumber} icon-only button has no AutomationProperties.Name.");
            }
        }
    }

    [Fact]
    public void HighContrastThemeOverridesCoreTokensWithWindowsSystemColors()
    {
        var repository = FindRepositoryRoot();
        var path = Path.Combine(
            repository,
            "Helldivers2ModManager",
            "Resources",
            "Styles",
            "Theme.HighContrast.xaml");
        var document = XDocument.Load(path);
        var xaml = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var resources = document.Root!
            .Elements()
            .Where(element => element.Attribute(xaml + "Key") is not null)
            .ToDictionary(element => element.Attribute(xaml + "Key")!.Value, StringComparer.Ordinal);
        foreach (var key in new[]
                 {
                     "ThemeWindowColor", "ThemeSurfaceColor", "ThemeTextColor",
                     "ThemeSecondaryTextColor", "ThemeBorderColor", "ThemeWindowBrush",
                     "ThemeSurfaceBrush", "ThemeTextBrush", "ThemeSecondaryTextBrush",
                     "ThemeBorderBrush"
                 })
        {
            Assert.Contains(key, resources.Keys);
        }

        var serviceSource = File.ReadAllText(Path.Combine(
            repository,
            "Helldivers2ModManager",
            "Services",
            "ThemeService.cs"));
        foreach (var key in new[]
                 {
                     "SystemAccentColor", "NeutralBackgroundColor", "NeutralForegroundColor",
                     "NeutralForegroundColorDisabled", "NeutralBorderColor", "DangerColor",
                     "SuccessColor", "WarningColor", "ThemeWindowColor", "ThemeTextColor",
                     "ThemeBorderColor"
                 })
        {
            Assert.Contains($"\"{key}\"", serviceSource, StringComparison.Ordinal);
        }
        Assert.Contains("SystemColors.WindowBrush", serviceSource, StringComparison.Ordinal);
        Assert.Contains("SystemColors.WindowTextBrush", serviceSource, StringComparison.Ordinal);
        Assert.Contains("SystemColors.HighlightBrush", serviceSource, StringComparison.Ordinal);
        Assert.Contains("SystemColors.GrayTextBrush", serviceSource, StringComparison.Ordinal);
        Assert.Contains("SystemColors.HotTrackBrush", serviceSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ThemeColorTokensUseDynamicResourceReferences()
    {
        var repository = FindRepositoryRoot();
        var roots = new[]
        {
            Path.Combine(repository, "Helldivers2ModManager", "Resources", "Styles"),
            Path.Combine(repository, "Helldivers2ModManager", "Views"),
            Path.Combine(repository, "Helldivers2ModManager", "Components")
        };
        var files = roots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
            .Append(Path.Combine(repository, "Helldivers2ModManager", "App.xaml"))
            .Append(Path.Combine(repository, "Helldivers2ModManager", "MainWindow.xaml"));
        var staticThemeToken = new Regex(
            @"\{StaticResource\s+(?:SystemAccent|Neutral|Layer|Mica|Theme|Danger|Success|Warning)\w*(?:Brush|Color)\}",
            RegexOptions.CultureInvariant);
        foreach (var file in files)
            Assert.DoesNotMatch(staticThemeToken, File.ReadAllText(file));
    }

    private static bool IsFluentIcon(XElement element) =>
        element.Attribute("FontFamily")?.Value.Contains("Fluent Icons", StringComparison.OrdinalIgnoreCase) == true;

    private static void AssertDimensionIsFlexible(string path, XElement element, string attributeName, double allowedIconSize)
    {
        var attribute = element.Attribute(attributeName);
        if (attribute is null || !double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return;
        Assert.True(allowedIconSize > 0 && value <= allowedIconSize,
            $"{Path.GetFileName(path)}:{((IXmlLineInfo)element).LineNumber} {element.Name.LocalName} uses fixed {attributeName}={value} for text-bearing content.");
    }

    private static IEnumerable<string> XamlFiles()
    {
        var repository = FindRepositoryRoot();
        foreach (var relative in new[] { "Views", "Components" })
        {
            var root = Path.Combine(repository, "Helldivers2ModManager", relative);
            foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
                yield return file;
        }
        yield return Path.Combine(repository, "Helldivers2ModManager", "MainWindow.xaml");
    }

    private static void AssertRelativeHostBound(string path, XElement element, string attributeName, string hostDimension)
    {
        var value = element.Attribute(attributeName)?.Value;
        Assert.True(
            value?.StartsWith("{Binding ", StringComparison.Ordinal) == true
            && value.Contains(hostDimension, StringComparison.Ordinal)
            && value.Contains("ProportionalDoubleConverter", StringComparison.Ordinal),
            $"{Path.GetFileName(path)}:{((IXmlLineInfo)element).LineNumber} dialog {attributeName} must be proportional to host {hostDimension}.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Helldivers2ModManager.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
