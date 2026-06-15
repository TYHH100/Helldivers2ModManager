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
				ModListScrollViewer?.ScrollToVerticalOffset(s_savedScrollOffset)),
				System.Windows.Threading.DispatcherPriority.Background);
		}
	}

	private void DashboardPageView_Unloaded(object? sender, RoutedEventArgs e)
	{
		// 离开页面时保存当前滚动位置
		if (ModListScrollViewer is not null)
			s_savedScrollOffset = ModListScrollViewer.VerticalOffset;
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

	private void ImagePreviewOverlay_KeyDown(object sender, KeyEventArgs e)
	{
		// 按 ESC 键关闭图片预览
		if (e.Key == Key.Escape)
		{
			var viewModel = DataContext as DashboardPageViewModel;
			viewModel?.HideImagePreviewCommand.Execute(null);
			e.Handled = true;
		}
	}

	private void ImagePreviewOverlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
	{
		// 预览变为可见时，让 overlay 获得焦点以接收 ESC 按键事件
		if (e.NewValue is true && sender is Border border)
		{
			border.Focusable = true;
			_ = border.Focus();
		}
	}

	private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		e.Handled = true;
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