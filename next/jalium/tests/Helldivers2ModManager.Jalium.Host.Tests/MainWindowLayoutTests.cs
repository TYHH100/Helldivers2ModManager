using Helldivers2ModManager.Jalium.Host;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media.Imaging;
using Jalium.UI.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Host.Tests;

[TestClass]
public sealed class MainWindowLayoutTests
{
    [TestMethod]
    public void Shell_PreservesWindowDimensionsAndLayerOrder()
    {
        var page = new Border();
        var overlays = Enumerable.Range(0, 6).Select(_ => (UIElement)new Border()).ToArray();
        var labels = new MainWindowLabels("App", "v1", "Report", "Help", "Minimize", "Maximize", "Close", "Import", "Invalid");
        var layout = new MainWindowLayout(page, new BitmapImage(), labels, () => { }, () => { }, overlays);

        Assert.AreEqual(1000, layout.Window.Width);
        Assert.AreEqual(700, layout.Window.Height);
        Assert.AreEqual(800, layout.Window.MinWidth);
        Assert.AreEqual(600, layout.Window.MinHeight);
        Assert.AreEqual(WindowStyle.None, layout.Window.WindowStyle);
        Assert.AreEqual(48, WindowChrome.GetWindowChrome(layout.Window)!.CaptionHeight);

        var root = layout.Window.Content as Border;
        Assert.IsNotNull(root);
        var grid = root.Child as Grid;
        Assert.IsNotNull(grid);
        Assert.AreEqual(48, grid.RowDefinitions[0].Height.Value);
        Assert.AreSame(page, layout.PagePresenter.Content);
        Assert.AreEqual(11, grid.Children.Count);
        var background = grid.Children[0] as Image;
        Assert.IsNotNull(background);
        Assert.AreEqual(Visibility.Collapsed, background.Visibility);
        layout.SetBackground(new BitmapImage(), 0.4);
        Assert.AreEqual(Visibility.Visible, background.Visibility);
        Assert.AreEqual(0.4, background.Opacity);
        for (var index = 0; index < overlays.Length; index++)
            Assert.AreSame(overlays[index], grid.Children[index + 3]);
        Assert.AreSame(layout.MusicLayer, grid.Children[9]);
        Assert.AreSame(layout.DropHint, grid.Children[10]);

        var titleBar = grid.Children[1] as Border;
        Assert.IsNotNull(titleBar);
        var titleGrid = titleBar.Child as Grid;
        Assert.IsNotNull(titleGrid);
        var controls = (StackPanel)titleGrid.Children[1];
        Assert.AreEqual(5, controls.Children.Count);
        foreach (Button button in controls.Children)
        {
            Assert.AreEqual(46, button.Width);
            Assert.AreEqual(32, button.Height);
            Assert.IsTrue(WindowChrome.GetIsHitTestVisibleInChrome(button));
        }
        Assert.AreEqual("Import", ((TextBlock)layout.ValidDropHint.Children[1]).Text);
        Assert.AreEqual("Invalid", ((TextBlock)layout.InvalidDropHint.Children[1]).Text);

        layout.SetDropHint(visible: true, supported: false);
        Assert.AreEqual(Visibility.Visible, layout.DropHint.Visibility);
        Assert.AreEqual(Visibility.Collapsed, layout.ValidDropHint.Visibility);
        Assert.AreEqual(Visibility.Visible, layout.InvalidDropHint.Visibility);
    }
}
