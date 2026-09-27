using Helldivers2ModManager.Jalium.Host;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Host.Tests;

[TestClass]
public sealed class ImagePreviewOverlayTests
{
    [TestMethod]
    public void Preview_OpensAndCloseButtonReleasesImage()
    {
        var overlay = new ImagePreviewOverlay();
        var source = new BitmapImage();
        Assert.IsFalse(overlay.IsOpen);

        overlay.Show(source);

        Assert.IsTrue(overlay.IsOpen);
        Assert.AreSame(source, overlay.Source);
        var dialog = (Border)overlay.Children[0];
        var layout = (Grid)dialog.Child!;
        var close = (Button)layout.Children[0];
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsFalse(overlay.IsOpen);
        Assert.IsNull(overlay.Source);
    }

    [TestMethod]
    public void Preview_UsesTheOriginalFirstOverlayLayer()
    {
        var preview = new ImagePreviewOverlay();
        var overlays = Enumerable.Range(0, 6)
            .Select(index => index == 0 ? (UIElement)preview : new Border()).ToArray();
        var labels = new MainWindowLabels("App", "v1", "Report", "Help", "Minimize", "Maximize", "Close", "Import", "Invalid");
        var layout = new MainWindowLayout(new Border(), new BitmapImage(), labels,
            () => { }, () => { }, overlays);
        var root = (Grid)((Border)layout.Window.Content!).Child!;

        Assert.AreSame(preview, root.Children[3]);
        Assert.AreEqual(1, Grid.GetRow(preview));
    }
}
