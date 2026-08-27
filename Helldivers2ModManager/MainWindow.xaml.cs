using Helldivers2ModManager.Stores;
using Helldivers2ModManager.ViewModels;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager;

internal partial class MainWindow : Window
{
	/// <summary>
	/// 支持拖拽导入的压缩包扩展名（与文件对话框过滤器、ModService 嵌套压缩包识别保持一致）。
	/// </summary>
	private static readonly HashSet<string> s_supportedArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".zip", ".7z", ".rar", ".tar"
	};

	/// <summary>
	/// 拖拽离开后的延迟复查计时器：鼠标在窗口内子元素间移动也会触发 DragLeave，
	/// 延迟 300ms 后检查是否还有持续的 DragOver，避免误隐藏提示。
	/// </summary>
	private readonly Stores.NavigationStore _navigationStore;
	private readonly DispatcherTimer _dropHintTimer;
	private DateTime _lastFileDragOverTime = DateTime.MinValue;

	// 拖拽提示覆盖层位于 Window.Style 的 ControlTemplate 内（模板作用域的 x:Name 不会生成字段），
	// 在 OnApplyTemplate 中通过 Template.FindName 获取引用。
	private Border? _dropHintOverlay;
	private StackPanel? _dropHintValidPanel;
	private StackPanel? _dropHintInvalidPanel;

	public MainWindow(MainViewModel viewModel, Stores.NavigationStore navigationStore)
	{
		InitializeComponent();

		_navigationStore = navigationStore;
		DataContext = viewModel;

		_dropHintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
		_dropHintTimer.Tick += DropHintTimer_Tick;
	}

	public override void OnApplyTemplate()
	{
		base.OnApplyTemplate();

		_dropHintOverlay = Template.FindName("dropHintOverlay", this) as Border;
		_dropHintValidPanel = Template.FindName("dropHintValidPanel", this) as StackPanel;
		_dropHintInvalidPanel = Template.FindName("dropHintInvalidPanel", this) as StackPanel;
	}

	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		if (e.Key == Key.F12)
		{
			_navigationStore.Navigate<BackendTestCenterPageViewModel>();
			e.Handled = true;
		}

		base.OnPreviewKeyDown(e);
	}

	protected override void OnActivated(EventArgs e)
	{
		DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, 1, sizeof(int));
		base.OnActivated(e);
	}

	private void TestCenterButton_Click(object sender, RoutedEventArgs e)
	{
		_navigationStore.Navigate<BackendTestCenterPageViewModel>();
	}

	private void HelpButton_Click(object sender, RoutedEventArgs e)
	{
		(DataContext as MainViewModel)?.HelpCommand.Execute(null);
	}

	private void MinButton_Click(object sender, RoutedEventArgs e)
	{
		WindowState = WindowState.Minimized;
	}

	private void MaxButton_Click(object sender, RoutedEventArgs e)
	{
		if (WindowState == WindowState.Maximized)
			WindowState = WindowState.Normal;
		else
			WindowState = WindowState.Maximized;
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	// ===== 拖拽导入 =====

	/// <summary>
	/// 拖拽悬停：识别文件拖拽并显示导入提示。使用 Preview（隧道）事件并在处理文件时
	/// 标记 Handled，阻止拖拽进入子元素管线（gong 排序拖拽、自动滚动行为等）。
	/// </summary>
	private void Window_PreviewDragOver(object sender, DragEventArgs e)
	{
		if (!e.Data.GetDataPresent(DataFormats.FileDrop))
		{
			HideDropHint();
			return;
		}

		// 文件拖拽由主窗口统一处理，不再路由到页面内部（避免 gong 把文件路径当排序数据）
		e.Handled = true;

		_lastFileDragOverTime = DateTime.Now;

		var archives = GetArchivePaths(e.Data);
		if (archives.Length > 0)
		{
			e.Effects = DragDropEffects.Copy;
			ShowDropHint(valid: true);
		}
		else
		{
			e.Effects = DragDropEffects.None;
			ShowDropHint(valid: false);
		}
	}

	/// <summary>
	/// 拖拽放下：过滤出压缩包并交给主窗口 ViewModel 导入（非 Dashboard 页面会先导航过去）。
	/// </summary>
	private void Window_PreviewDrop(object sender, DragEventArgs e)
	{
		HideDropHint();

		if (!e.Data.GetDataPresent(DataFormats.FileDrop))
			return;

		e.Handled = true;

		var archives = GetArchivePaths(e.Data);
		if (archives.Length == 0)
			return;

		(DataContext as MainViewModel)?.ImportArchives(archives);
	}

	private void Window_DragLeave(object sender, DragEventArgs e)
	{
		// 立即隐藏会让鼠标在窗口内子元素间移动时提示闪烁；
		// 延迟复查：若仍在窗口内拖拽，随后的 DragOver 会重新显示并刷新 _lastFileDragOverTime
		_dropHintTimer.Stop();
		_dropHintTimer.Start();
	}

	private void DropHintTimer_Tick(object? sender, EventArgs e)
	{
		_dropHintTimer.Stop();

		// 拖拽悬停时 DragOver 持续触发；300ms 内没有新的文件 DragOver
		// 说明拖拽已离开窗口或被取消（Esc），此时才隐藏提示
		if (DateTime.Now - _lastFileDragOverTime > _dropHintTimer.Interval)
			HideDropHint();
	}

	/// <summary>
	/// 从拖拽数据中过滤出支持的压缩包路径。
	/// </summary>
	private static string[] GetArchivePaths(IDataObject data)
	{
		if (data.GetData(DataFormats.FileDrop) is not string[] paths)
			return [];

		return paths.Where(p => s_supportedArchiveExtensions.Contains(Path.GetExtension(p))).ToArray();
	}

	private void ShowDropHint(bool valid)
	{
		if (_dropHintValidPanel is null || _dropHintInvalidPanel is null || _dropHintOverlay is null)
			return;

		_dropHintValidPanel.Visibility = valid ? Visibility.Visible : Visibility.Collapsed;
		_dropHintInvalidPanel.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
		_dropHintOverlay.Visibility = Visibility.Visible;
	}

	private void HideDropHint()
	{
		if (_dropHintOverlay is not null)
			_dropHintOverlay.Visibility = Visibility.Collapsed;
	}

	[LibraryImport("dwmapi.dll")]
	private static partial void DwmSetWindowAttribute(nint hwnd, uint dwAttribute, in int pvAttribute, uint cbAttribute);
}
