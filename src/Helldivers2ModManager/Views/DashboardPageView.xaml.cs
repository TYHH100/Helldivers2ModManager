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

	/// <summary>
	/// 列表内部 ScrollViewer（虚拟化改造后由 ItemsControl 模板承载滚动，
	/// 模板内的命名元素不会生成字段，需在视觉树中查找；Unloaded 后视觉树被拆除需重查）。
	/// </summary>
	private ScrollViewer? _modListScrollViewer;

	private ScrollViewer? ModListScrollViewer
	{
		get
		{
			if (_modListScrollViewer is null && ModList is not null)
				_modListScrollViewer = FindDescendantScrollViewer(ModList);
			return _modListScrollViewer;
		}
	}

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

	private void DashboardPageView_Unloaded(object sender, RoutedEventArgs e)
	{
		// 离开页面时保存当前滚动位置
		if (ModListScrollViewer is not null)
			s_savedScrollOffset = ModListScrollViewer.VerticalOffset;
		_modListScrollViewer = null;
	}

	private static ScrollViewer? FindDescendantScrollViewer(DependencyObject parent)
	{
		var count = VisualTreeHelper.GetChildrenCount(parent);
		for (var i = 0; i < count; i++)
		{
			var child = VisualTreeHelper.GetChild(parent, i);
			if (child is ScrollViewer sv)
				return sv;
			if (FindDescendantScrollViewer(child) is { } found)
				return found;
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
	/// Shift+范围选择的锚点（最后一次单击/Ctrl 点击/Shift 范围结束的 Mod）。
	/// 页面级实例字段：导航离开页面即随 View 释放，不跨页面残留。
	/// </summary>
	private ModViewModel? _selectionAnchor;

	/// <summary>
	/// 卡片点击选择逻辑：
	/// - 普通单击：不做任何选择（不进入多选模式），也不拦截事件，保持拖拽不受干扰。
	/// - Ctrl+单击：切换该项选中状态（多选）。
	/// - Shift+单击：从锚点范围选择（Ctrl+Shift = 追加范围）。
	/// </summary>
	private void ModCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is Border border && border.DataContext is ModViewModel vm)
		{
			// 排除交互控件（按钮/复选框/下拉框等 Control）和图片预览
			if (e.OriginalSource is Control or Image)
				return;

			var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
			var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

			if (shift)
			{
				// Shift+单击：范围选择（Ctrl+Shift 追加范围）；无锚点时退化为单选
				if (_selectionAnchor is not null && DataContext is DashboardPageViewModel dashboard)
					dashboard.SelectRange(_selectionAnchor, vm, additive: ctrl);
				else if (DataContext is DashboardPageViewModel dashboard0)
					dashboard0.SelectRange(vm, vm, additive: false);
				_selectionAnchor = vm;
				e.Handled = true;
				return;
			}

			if (ctrl)
			{
				// Ctrl+单击：切换选中状态（多选）
				vm.IsSelected = !vm.IsSelected;
				_selectionAnchor = vm;
				e.Handled = true;
				return;
			}

			// 普通单击：不做选择，也不拦截，gong 拖拽照常工作
		}
	}

	/// <summary>
	/// 点击列表空白区域：清空选择并重置范围锚点。
	/// 卡片外空白区域命中列表模板内 ScrollViewer 的透明背景（卡片内部命中不会是 ScrollViewer）。
	/// </summary>
	private void ModListArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.OriginalSource is ScrollViewer)
		{
			(DataContext as DashboardPageViewModel)?.DeselectAllCommand.Execute(null);
			_selectionAnchor = null;
		}
	}

	/// <summary>
	/// 键盘快捷键：Ctrl+A 全选，Esc 清空选择。
	/// </summary>
	private void DashboardPageView_KeyDown(object sender, KeyEventArgs e)
	{
		if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.A)
		{
			(DataContext as DashboardPageViewModel)?.SelectAllCommand.Execute(null);
			e.Handled = true;
		}
		else if (e.Key == Key.Escape)
		{
			(DataContext as DashboardPageViewModel)?.DeselectAllCommand.Execute(null);
			_selectionAnchor = null;
			e.Handled = true;
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

	/// <summary>
	/// 点击覆盖状态指示器，打开与版本检查相同风格的覆盖详情面板。
	/// </summary>
	private void ConflictStatusIndicator_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is Border border && border.DataContext is ModViewModel vm)
		{
			vm.ShowConflictDetailCommand.Execute(null);
			e.Handled = true;
		}
	}

}
