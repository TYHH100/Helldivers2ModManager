using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Helldivers2ModManager;

/// <summary>
/// 拖拽边界自动滚动附加行为。
/// 附加到 ItemsControl 上，当拖拽到列表上下边缘时自动滚动父级 ScrollViewer。
/// </summary>
/// <remarks>
/// XAML 用法：
/// <code>
/// xmlns:local="clr-namespace:Helldivers2ModManager"
/// &lt;ItemsControl local:DragDropAutoScrollBehavior.IsEnabled="True" ... /&gt;
/// </code>
/// 会自动从可视化树向上查找第一个 ScrollViewer 作为滚动目标。
/// </remarks>
internal static class DragDropAutoScrollBehavior
{
	public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
		"IsEnabled", typeof(bool), typeof(DragDropAutoScrollBehavior),
		new PropertyMetadata(false, OnIsEnabledChanged));

	public static void SetIsEnabled(DependencyObject element, bool value)
		=> element.SetValue(IsEnabledProperty, value);

	public static bool GetIsEnabled(DependencyObject element)
		=> (bool)element.GetValue(IsEnabledProperty);

	// ── 常量 ──────────────────────────────────────────

	/// <summary>边界检测区域高度（像素），拖拽进入此区域时触发自动滚动</summary>
	private const double AutoScrollZoneHeight = 40.0;

	/// <summary>自动滚动速度（像素/次）</summary>
	private const double AutoScrollSpeed = 12.0;

	// ── 状态管理 ──────────────────────────────────────

	/// <summary>每个 ItemsControl 对应一个滚动状态</summary>
	private static readonly Dictionary<ItemsControl, AutoScrollState> s_states = [];

	private static AutoScrollState GetOrCreateState(ItemsControl control)
	{
		if (!s_states.TryGetValue(control, out var state))
		{
			state = new AutoScrollState();
			s_states[control] = state;
		}
		return state;
	}

	// ── 行为开关 ──────────────────────────────────────

	private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is not ItemsControl itemsControl)
			return;

		if ((bool)e.NewValue)
		{
			itemsControl.PreviewDragOver += OnPreviewDragOver;
			itemsControl.DragLeave += OnDragStop;
			itemsControl.Drop += OnDragStop;
			itemsControl.Unloaded += OnUnloaded;
		}
		else
		{
			itemsControl.PreviewDragOver -= OnPreviewDragOver;
			itemsControl.DragLeave -= OnDragStop;
			itemsControl.Drop -= OnDragStop;
			itemsControl.Unloaded -= OnUnloaded;
			if (s_states.Remove(itemsControl, out var state))
				state.Timer?.Stop();
		}
	}

	private static void OnUnloaded(object sender, RoutedEventArgs e)
	{
		if (sender is ItemsControl control && s_states.Remove(control, out var state))
			state.Timer?.Stop();
	}

	// ── 事件处理 ──────────────────────────────────────

	private static void OnPreviewDragOver(object sender, DragEventArgs e)
	{
		if (sender is not ItemsControl itemsControl)
			return;

		var scrollViewer = FindScrollViewer(itemsControl);
		if (scrollViewer is null)
			return;

		var position = e.GetPosition(scrollViewer);
		var scrollHeight = scrollViewer.ActualHeight;
		var scrollOffset = scrollViewer.VerticalOffset;
		var maxOffset = scrollViewer.ScrollableHeight;

		if (maxOffset <= 0)
			return;

		var state = GetOrCreateState(itemsControl);

		if (position.Y <= AutoScrollZoneHeight && scrollOffset > 0)
		{
			StartAutoScroll(state, scrollViewer, -AutoScrollSpeed);
		}
		else if (position.Y >= scrollHeight - AutoScrollZoneHeight && scrollOffset < maxOffset)
		{
			StartAutoScroll(state, scrollViewer, AutoScrollSpeed);
		}
		else
		{
			StopAutoScroll(state);
		}
	}

	private static void OnDragStop(object sender, DragEventArgs e)
	{
		if (sender is ItemsControl control && s_states.TryGetValue(control, out var state))
			StopAutoScroll(state);
	}

	// ── 计时器控制 ────────────────────────────────────

	private static void StartAutoScroll(AutoScrollState state, ScrollViewer scrollViewer, double direction)
	{
		if (state.Timer is not null)
		{
			state.Timer.Tag = direction;
			return;
		}

		state.Timer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(16), // 约 60fps
			Tag = direction
		};
		state.Timer.Tick += (_, _) => OnTick(state, scrollViewer);
		state.Timer.Start();
	}

	private static void StopAutoScroll(AutoScrollState state)
	{
		if (state.Timer is not null)
		{
			state.Timer.Stop();
			state.Timer = null;
		}
	}

	private static void OnTick(AutoScrollState state, ScrollViewer scrollViewer)
	{
		if (state.Timer is null)
			return;

		var direction = (double)state.Timer.Tag!;
		var newOffset = scrollViewer.VerticalOffset + direction;
		newOffset = Math.Max(0, Math.Min(newOffset, scrollViewer.ScrollableHeight));
		scrollViewer.ScrollToVerticalOffset(newOffset);
	}

	// ── 辅助方法 ──────────────────────────────────────

	/// <summary>从 ItemsControl 向上查找第一个 ScrollViewer</summary>
	private static ScrollViewer? FindScrollViewer(DependencyObject element)
	{
		while (element is not null)
		{
			element = VisualTreeHelper.GetParent(element);
			if (element is ScrollViewer sv)
				return sv;
		}
		return null;
	}

	// ── 内部状态 ──────────────────────────────────────

	private sealed class AutoScrollState
	{
		public DispatcherTimer? Timer { get; set; }
	}
}
