using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Helldivers2ModManager;

/// <summary>
/// 拖拽边界自动滚动附加行为。
/// 附加到 ItemsControl 上，当拖拽到列表上下边缘时自动滚动父级 ScrollViewer。
/// 使用 CompositionTarget.Rendering 与 WPF 渲染管线同步，实现丝滑的滚动效果。
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

	/// <summary>自动滚动速度（像素/帧），配合 60fps 渲染实现丝滑滚动</summary>
	private const double AutoScrollSpeed = 3.0;

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
				StopAutoScroll(state);
		}
	}

	private static void OnUnloaded(object sender, RoutedEventArgs e)
	{
		if (sender is ItemsControl control && s_states.Remove(control, out var state))
			StopAutoScroll(state);
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
		state.ScrollViewer = scrollViewer;

		if (position.Y <= AutoScrollZoneHeight && scrollOffset > 0)
		{
			state.Direction = -AutoScrollSpeed;
			TryStartRendering(state);
		}
		else if (position.Y >= scrollHeight - AutoScrollZoneHeight && scrollOffset < maxOffset)
		{
			state.Direction = AutoScrollSpeed;
			TryStartRendering(state);
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

	// ── 渲染同步滚动 ────────────────────────────────────

	/// <summary>
	/// 尝试启动渲染事件监听。已在监听中则跳过。
	/// </summary>
	private static void TryStartRendering(AutoScrollState state)
	{
		if (state.IsRendering)
			return;

		state.IsRendering = true;
		CompositionTarget.Rendering += OnRendering;
	}

	/// <summary>
	/// 停止自动滚动，移除渲染事件监听。
	/// </summary>
	private static void StopAutoScroll(AutoScrollState state)
	{
		state.Direction = 0;
		state.IsRendering = false;
		CompositionTarget.Rendering -= OnRendering;
	}

	/// <summary>
	/// 渲染事件处理，每帧执行一次滚动。
	/// 使用 CompositionTarget.Rendering 与 WPF 渲染管线同步，实现丝滑滚动。
	/// </summary>
	private static void OnRendering(object? sender, EventArgs e)
	{
		// 遍历所有活跃的滚动状态，执行滚动
		foreach (var kvp in s_states)
		{
			var state = kvp.Value;
			if (state.Direction == 0 || state.ScrollViewer is null)
				continue;

			var newOffset = state.ScrollViewer.VerticalOffset + state.Direction;
			newOffset = Math.Max(0, Math.Min(newOffset, state.ScrollViewer.ScrollableHeight));
			state.ScrollViewer.ScrollToVerticalOffset(newOffset);
		}
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
		/// <summary>滚动目标 ScrollViewer</summary>
		public ScrollViewer? ScrollViewer { get; set; }

		/// <summary>滚动方向和速度（正数向下，负数向上，0 表示停止）</summary>
		public double Direction { get; set; }

		/// <summary>是否正在监听渲染事件</summary>
		public bool IsRendering { get; set; }
	}
}
