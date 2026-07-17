using Helldivers2ModManager.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Helldivers2ModManager.Views;

internal partial class DashboardPageView : Page
{
    /// <summary>
    /// 跨导航生命周期保存上一次的滚动位置
    /// </summary>
    private static double s_savedScrollOffset;

    public DashboardPageView()
    {
        InitializeComponent();
        Loaded += DashboardPageView_Loaded;
        Unloaded += DashboardPageView_Unloaded;
    }

    private void DashboardPageView_Loaded(object? sender, RoutedEventArgs e)
    {
        // 恢复上一次的滚动位置（在布局完成后再滚动）
        if (s_savedScrollOffset > 0)
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
                FindVisualChild<ScrollViewer>(ModList)?.ScrollToVerticalOffset(s_savedScrollOffset)),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void DashboardPageView_Unloaded(object sender, RoutedEventArgs e)
    {
        // 离开页面时保存当前滚动位置
        var scrollViewer = FindVisualChild<ScrollViewer>(ModList);
        if (scrollViewer is not null)
            s_savedScrollOffset = scrollViewer.VerticalOffset;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
                return descendant;
        }

        return null;
    }

    private void ModIcon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Image image && image.Source != null)
        {
            var viewModel = DataContext as DashboardPageViewModel;
            viewModel?.ShowImagePreviewCommand.Execute(image.Source);
        }
    }

    /// <summary>
    /// Ctrl+单击切换选中状态（多选），普通单击不干扰拖拽操作
    /// </summary>
    private void ModCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.DataContext is ModViewModel vm)
        {
            // 仅排除交互控件（按钮/复选框）和图片预览
            if (e.OriginalSource is Button or CheckBox)
                return;

            if (e.OriginalSource is Image)
                return;

            // 只有按住 Ctrl 才进入多选模式，普通单击不做任何选择（保持拖拽不受干扰）
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                vm.IsSelected = !vm.IsSelected;
                e.Handled = true;
            }
        }
    }

    private void GithubButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            button.ContextMenu.DataContext = DataContext;
            button.ContextMenu.IsOpen = true;
        }
    }

    /// <summary>
    /// 点击版本状态指示器，显示详细的版本兼容性信息
    /// </summary>
    private void VersionStatusIndicator_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.DataContext is ModViewModel vm)
        {
            vm.ShowVersionDetailCommand.Execute(null);
            e.Handled = true;
        }
    }

}
