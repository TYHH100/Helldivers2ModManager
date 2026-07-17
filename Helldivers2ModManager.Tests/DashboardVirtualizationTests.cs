using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Diagnostics;
using Xunit;

namespace Helldivers2ModManager.Tests;

public sealed class DashboardVirtualizationTests
{
    [Fact]
    public void DashboardXamlUsesRecyclingVirtualizationWithoutOuterScrollViewer()
    {
        var xamlPath = Path.Combine(FindRepositoryRoot(), "Helldivers2ModManager", "Views", "DashboardPageView.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains("x:Name=\"ModList\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VirtualizingPanel.IsVirtualizing=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VirtualizingPanel.VirtualizationMode=\"Recycling\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer.CanContentScroll=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<VirtualizingStackPanel/>", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"ModListScrollViewer\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsNavigationWrapsAndTextListsDoNotUseChineseSizedWidths()
    {
        var xamlPath = Path.Combine(FindRepositoryRoot(), "Helldivers2ModManager", "Views", "SettingsPageView.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains("<WrapPanel Orientation=\"Horizontal\">", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ListBox Width=\"400\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"TextWrapping\" Value=\"Wrap\"/>", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RecyclingListBoxWithFiveThousandItemsRealizesOnlyVisibleContainers()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var listBox = new ListBox
                {
                    ItemsSource = Enumerable.Range(0, 5_000).ToArray(),
                    Width = 1_000,
                    Height = 600
                };
                ScrollViewer.SetCanContentScroll(listBox, true);
                VirtualizingPanel.SetIsVirtualizing(listBox, true);
                VirtualizingPanel.SetVirtualizationMode(listBox, VirtualizationMode.Recycling);

                var window = new Window
                {
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Width = 1_000,
                    Height = 600,
                    Content = listBox
                };

                window.Show();
                listBox.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var realized = Enumerable.Range(0, 5_000)
                    .Count(index => listBox.ItemContainerGenerator.ContainerFromIndex(index) is not null);
                Assert.InRange(realized, 1, 100);

                var scrollViewer = FindVisualChild<ScrollViewer>(listBox)
                    ?? throw new InvalidOperationException("The virtualized ListBox did not create a ScrollViewer.");
                var stopwatch = Stopwatch.StartNew();
                var peakRealized = realized;
                foreach (var offset in Enumerable.Range(0, 10).Select(index => index * 450.0))
                {
                    scrollViewer.ScrollToVerticalOffset(offset);
                    listBox.UpdateLayout();
                    peakRealized = Math.Max(
                        peakRealized,
                        Enumerable.Range(0, 5_000)
                            .Count(index => listBox.ItemContainerGenerator.ContainerFromIndex(index) is not null));
                }
                stopwatch.Stop();
                Assert.InRange(peakRealized, 1, 120);
                Assert.True(
                    stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                    $"Scrolling the synthetic 5,000 item list took {stopwatch.Elapsed}.");

                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF virtualization test timed out.");
        Assert.Null(failure);
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

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
                return descendant;
        }
        return null;
    }
}
