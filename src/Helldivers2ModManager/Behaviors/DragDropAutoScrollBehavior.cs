using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Helldivers2ModManager;

/// <summary>
/// 拖拽边界自动滚动附加行为。
/// 附加到 ItemsControl 上，当拖拽到列表上下边缘时自动滚动父级 ScrollViewer。
/// 特性：
/// 1. 边界滚动带速度加速（越贴边越快），按时间积分，与刷新率无关；
/// 2. 滚动后合成冒泡 DragOver 刷新拖拽插入指示线，避免指示线停留在过期位置；
/// 3. 拖拽期间拦截鼠标滚轮（OLE 拖拽循环会吞掉 WM_MOUSEWHEEL，WPF 收不到，
///    必须用 WH_MOUSE_LL 钩子，钩子直接装在 UI 线程上即可，OLE 循环会泵消息）；
/// 4. 光标越过列表边缘但仍在应用窗口内时保持满速滚动（模拟资源管理器行为）。
/// </summary>
/// <remarks>
/// XAML 用法：
/// <code>
/// xmlns:local="clr-namespace:Helldivers2ModManager"
/// &lt;ItemsControl local:DragDropAutoScrollBehavior.IsEnabled="True" ... /&gt;
/// </code>
/// 会自动从 ItemsControl 的祖先或后代中查找 ScrollViewer（ListBox 的 ScrollViewer 是后代）。
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

	/// <summary>边界检测区域高度（设备无关像素），拖拽进入此区域时触发自动滚动</summary>
	private const double AutoScrollZoneHeight = 64.0;

	/// <summary>区域边缘的最小滚动速度（像素/秒）</summary>
	private const double MinAutoScrollSpeed = 140.0;

	/// <summary>贴边时的最大滚动速度（像素/秒）</summary>
	private const double MaxAutoScrollSpeed = 1800.0;

	/// <summary>每格滚轮的滚动像素数（120 = 一格）</summary>
	private const double WheelScrollPixelsPerNotch = 96.0;

	private const int WhMouseLl = 14;
	private const int VkLButton = 0x01;
	private static readonly IntPtr WmMouseWheel = new(0x020A);

	// ── 状态管理 ──────────────────────────────────────

	/// <summary>每个 ItemsControl 对应一个滚动状态（全部仅在 UI 线程访问）</summary>
	private static readonly Dictionary<ItemsControl, AutoScrollState> s_states = [];

	private static bool s_isRendering;
	private static IntPtr s_wheelHookHandle;

	private static AutoScrollState GetOrCreateState(ItemsControl control)
	{
		if (!s_states.TryGetValue(control, out var state))
		{
			state = new AutoScrollState(control);
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
			itemsControl.Drop += OnDragDrop;
			itemsControl.Unloaded += OnUnloaded;
		}
		else
		{
			itemsControl.PreviewDragOver -= OnPreviewDragOver;
			itemsControl.Drop -= OnDragDrop;
			itemsControl.Unloaded -= OnUnloaded;
			if (s_states.Remove(itemsControl, out var state))
				Deactivate(state);
		}
	}

	private static void OnUnloaded(object sender, RoutedEventArgs e)
	{
		if (sender is ItemsControl control && s_states.Remove(control, out var state))
			Deactivate(state);
	}

	// ── 事件处理 ──────────────────────────────────────

	private static void OnPreviewDragOver(object sender, DragEventArgs e)
	{
		try
		{
			if (sender is not ItemsControl itemsControl)
				return;

			var state = GetOrCreateState(itemsControl);
			if (e.Data is IDataObject data)
				state.LastDragData = data;
			Activate(state);
		}
		catch
		{
			// 滚动增强功能绝不允许干扰拖拽事件管线本身
		}
	}

	private static void OnDragDrop(object sender, DragEventArgs e)
	{
		try
		{
			if (sender is ItemsControl control && s_states.TryGetValue(control, out var state))
				Deactivate(state);
		}
		catch
		{
		}
	}

	// ── 激活 / 停用 ───────────────────────────────────

	private static void Activate(AutoScrollState state)
	{
		try
		{
			state.ScrollViewer ??= FindScrollViewer(state.ItemsControl);
			if (state.ScrollViewer is null)
				return;

			if (!state.IsActive)
			{
				state.IsActive = true;
				state.LastTime = null;
			}
			TryStartRendering();
			InstallWheelHook();
			EnsureWatchdogTimer();
		}
		catch
		{
			// 激活失败时不影响拖拽本身
		}
	}

	private static void Deactivate(AutoScrollState state)
	{
		try
		{
			state.IsActive = false;
			state.LastDragData = null;

			if (!s_states.Values.Any(s => s.IsActive))
			{
				StopRendering();
				StopWatchdogTimer();
				UninstallWheelHook();
			}
		}
		catch
		{
		}
	}

	// ── 看门狗：CompositionTarget.Rendering 在应用空闲（无渲染）时会停发，
	// 单靠渲染回调检测左键释放会留下僵尸状态和钩子，因此用 DispatcherTimer 兜底 ──

	private static DispatcherTimer? s_watchdogTimer;

	private static void EnsureWatchdogTimer()
	{
		if (s_watchdogTimer is not null)
			return;

		s_watchdogTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(300)
		};
		s_watchdogTimer.Tick += (_, _) =>
		{
			// 左键已释放但状态仍处于激活 → 拖拽已结束（Esc 取消、窗口外释放等），清理
			if ((GetAsyncKeyState(VkLButton) & 0x8000) != 0)
				return;

			foreach (var state in s_states.Values.Where(s => s.IsActive).ToList())
				Deactivate(state);
		};
		s_watchdogTimer.Start();
	}

	private static void StopWatchdogTimer()
	{
		if (s_watchdogTimer is null)
			return;
		s_watchdogTimer.Stop();
		s_watchdogTimer = null;
	}

	// ── 渲染同步滚动 ────────────────────────────────────

	private static void TryStartRendering()
	{
		if (s_isRendering)
			return;

		s_isRendering = true;
		CompositionTarget.Rendering += OnRendering;
	}

	private static void StopRendering()
	{
		s_isRendering = false;
		CompositionTarget.Rendering -= OnRendering;
	}

	/// <summary>
	/// 每帧执行：轮询真实光标位置（OLE 拖拽期间 WPF 鼠标状态不可靠，用 GetCursorPos），
	/// 按贴近边界的深度计算加速速度，滚动后刷新拖拽插入指示线。
	/// </summary>
	private static void OnRendering(object? sender, EventArgs e)
	{
		var now = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;

		foreach (var state in s_states.Values)
		{
			try
			{
				ProcessActiveState(state, now);
			}
			catch
			{
				// 单次帧处理的异常不能中断渲染管线
			}
		}
	}

	/// <summary>每帧处理单个激活状态：检测结束、计算边界滚动、刷新指示线</summary>
	private static void ProcessActiveState(AutoScrollState state, TimeSpan now)
	{
		if (!state.IsActive || state.ScrollViewer is not { } scrollViewer)
			return;

		// 左键释放说明拖拽结束（Drop 之外的情况也兜底：Esc 取消、在窗口外释放等）
		if ((GetAsyncKeyState(VkLButton) & 0x8000) == 0)
		{
			Deactivate(state);
			return;
		}

		if (!GetCursorPos(out var px))
			return;
		if (PresentationSource.FromVisual(scrollViewer)?.CompositionTarget is not { } ct)
			return;

		// 屏幕像素 → 设备无关坐标，再换算到 ScrollViewer 局部坐标
		var dip = ct.TransformFromDevice.Transform(new Point(px.X, px.Y));
		var local = scrollViewer.PointFromScreen(dip);

		var dt = now > TimeSpan.Zero && state.LastTime is TimeSpan last
			? (now - last).TotalSeconds
			: 1.0 / 60.0;
		if (dt <= 0 || dt > 0.25)
			dt = 1.0 / 60.0;
		state.LastTime = now;

		var maxOffset = scrollViewer.ScrollableHeight;
		if (maxOffset <= 0)
			return;
		var viewHeight = scrollViewer.ActualHeight;
		if (viewHeight <= 0)
			return;

		// 计算方向与深度（深度越大速度越快；光标越过列表边缘但仍在窗口内时按满速处理）
		var direction = 0;
		var depth = 0.0;
		if (local.Y < 0 && scrollViewer.VerticalOffset > 0 && IsCursorInsideAppWindow(px, state))
		{
			direction = -1;
			depth = AutoScrollZoneHeight;
		}
		else if (local.Y > viewHeight && scrollViewer.VerticalOffset < maxOffset && IsCursorInsideAppWindow(px, state))
		{
			direction = 1;
			depth = AutoScrollZoneHeight;
		}
		else if (local.Y < AutoScrollZoneHeight && scrollViewer.VerticalOffset > 0)
		{
			direction = -1;
			depth = AutoScrollZoneHeight - local.Y;
		}
		else if (local.Y > viewHeight - AutoScrollZoneHeight && scrollViewer.VerticalOffset < maxOffset)
		{
			direction = 1;
			depth = local.Y - (viewHeight - AutoScrollZoneHeight);
		}

		if (direction == 0 || depth <= 0)
			return;

		// 二次加速曲线：贴边程度越深滚动越快
		var t = Math.Clamp(depth / AutoScrollZoneHeight, 0.0, 1.0);
		var speed = MinAutoScrollSpeed + (MaxAutoScrollSpeed - MinAutoScrollSpeed) * t * t;

		scrollViewer.ScrollToVerticalOffset(
			Math.Clamp(scrollViewer.VerticalOffset + direction * speed * dt, 0.0, maxOffset));

		// 内容移动后光标下的目标已变化，合成冒泡 DragOver 让拖拽指示线跟随
		RefreshDropIndicator(state, dip);
	}

	/// <summary>
	/// 用真实拖拽数据合成一次 DragOver，gong 会重建 DropInfo 并把插入指示线
	/// 重新定位到光标当前指向的项目（自动滚动期间指示线会随内容滚走，必须刷新）。
	/// 注意：ItemsControl 在默认 EventType.Auto 下 gong 只监听冒泡的 DragOver，
	/// 必须合成冒泡事件（PreviewDragOver 不会进入 gong 管线）。
	/// </summary>
	private static void RefreshDropIndicator(AutoScrollState state, Point dipScreen)
	{
		if (state.LastDragData is not { } data)
			return;

		try
		{
			// DragEventArgs 带坐标的构造函数是 internal，只能反射创建
			var ctor = s_dragEventArgsCtor ??= typeof(DragEventArgs).GetConstructor(
				BindingFlags.NonPublic | BindingFlags.Instance, null,
				[typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)], null);
			if (ctor is null)
				return;

			var point = state.ItemsControl.PointFromScreen(dipScreen);
			var args = (DragEventArgs)ctor.Invoke(
				[data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, state.ItemsControl, point]);
			args.RoutedEvent = DragDrop.DragOverEvent;
			state.ItemsControl.RaiseEvent(args);
		}
		catch (InvalidOperationException)
		{
			// 视觉树未连接等边界情况，跳过本次刷新
		}
		catch (TargetInvocationException)
		{
			// 反射调用失败时跳过本次刷新
		}
	}

	private static ConstructorInfo? s_dragEventArgsCtor;

	/// <summary>判断屏幕像素点是否在本应用的某个窗口内（主窗口或拖拽预览窗口均算）</summary>
	private static bool IsCursorInsideAppWindow(POINT screenPx, AutoScrollState state)
	{
		if (state.WindowHandle == IntPtr.Zero)
		{
			if (state.ScrollViewer is null || Window.GetWindow(state.ScrollViewer) is not { } window)
				return false;
			state.WindowHandle = new WindowInteropHelper(window).Handle;
			if (state.WindowHandle == IntPtr.Zero)
				return false;
		}

		var hwnd = WindowFromPoint(screenPx);
		if (hwnd == IntPtr.Zero)
			return false;
		GetWindowThreadProcessId(hwnd, out var pid);
		return pid == Environment.ProcessId;
	}

	// ── 滚轮钩子（拖拽期间 WPF 收不到滚轮消息，必须用低级钩子） ──

	private static void InstallWheelHook()
	{
		if (s_wheelHookHandle != IntPtr.Zero)
			return;
		s_wheelHookHandle = SetWindowsHookEx(WhMouseLl, s_wheelHookProc, GetModuleHandle(null), 0);
	}

	private static void UninstallWheelHook()
	{
		if (s_wheelHookHandle == IntPtr.Zero)
			return;
		UnhookWindowsHookEx(s_wheelHookHandle);
		s_wheelHookHandle = IntPtr.Zero;
	}

	/// <summary>
	/// 钩子回调运行在 UI 线程（OLE 拖拽循环会泵消息）。
	/// 拖拽期间的滚轮消息直接吞掉（返回 1 不再下发）：滚轮滚动由本回调自己完成，
	/// 若让滚轮消息进入 OLE 拖拽循环，可能被当作按键状态变化导致拖拽被意外终止。
	/// </summary>
	private static IntPtr OnWheelHookProc(int nCode, IntPtr wParam, IntPtr lParam)
	{
		try
		{
			if (nCode >= 0 && wParam == WmMouseWheel && IsAnyDragActive())
			{
				var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
				var delta = (short)((info.mouseData >> 16) & 0xFFFF);
				if (delta != 0)
				{
					var cursor = new Point(info.pt.X, info.pt.Y);

					foreach (var state in s_states.Values)
					{
						if (!state.IsActive || state.ScrollViewer is not { } scrollViewer)
							continue;
						if ((GetAsyncKeyState(VkLButton) & 0x8000) == 0)
							continue;
						if (PresentationSource.FromVisual(scrollViewer)?.CompositionTarget is not { } ct)
							continue;

						// 光标必须在列表视口内才响应滚轮
						var dip = ct.TransformFromDevice.Transform(cursor);
						var topLeft = scrollViewer.PointToScreen(new Point(0, 0));
						var bottomRight = scrollViewer.PointToScreen(new Point(scrollViewer.ActualWidth, scrollViewer.ActualHeight));
						if (dip.X < topLeft.X || dip.X > bottomRight.X ||
							dip.Y < topLeft.Y || dip.Y > bottomRight.Y)
							continue;

						scrollViewer.ScrollToVerticalOffset(Math.Clamp(
							scrollViewer.VerticalOffset - delta / 120.0 * WheelScrollPixelsPerNotch,
							0.0, scrollViewer.ScrollableHeight));
					}
				}

				// 吞掉滚轮消息：拖拽期间 OLE 循环不应见到滚轮（见方法注释）
				return 1;
			}
		}
		catch
		{
			// 钩子回调绝不允许抛出异常破坏全局鼠标消息链
			return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
		}
		return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
	}

	private static bool IsAnyDragActive() => s_states.Values.Any(s => s.IsActive);

	private static readonly LowLevelMouseProc s_wheelHookProc = OnWheelHookProc;

	// ── 辅助方法 ──────────────────────────────────────

	/// <summary>先向上查找祖先 ScrollViewer（ItemsControl 包在 ScrollViewer 内的布局）</summary>
	private static ScrollViewer? FindScrollViewer(DependencyObject element)
	{
		var current = element;
		while (current is not null)
		{
			current = VisualTreeHelper.GetParent(current);
			if (current is ScrollViewer sv)
				return sv;
		}
		// 再向下查找后代 ScrollViewer（ListBox 等自带 ScrollViewer 的控件）
		return FindDescendantScrollViewer(element);
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

	// ── 内部状态 ──────────────────────────────────────

	private sealed class AutoScrollState
	{
		public AutoScrollState(ItemsControl itemsControl)
		{
			ItemsControl = itemsControl;
		}

		/// <summary>附加行为的宿主 ItemsControl</summary>
		public ItemsControl ItemsControl { get; }

		/// <summary>滚动目标 ScrollViewer</summary>
		public ScrollViewer? ScrollViewer { get; set; }

		/// <summary>拖拽是否正在进行</summary>
		public bool IsActive { get; set; }

		/// <summary>上一帧的渲染时间，用于时间积分</summary>
		public TimeSpan? LastTime { get; set; }

		/// <summary>最近一次真实拖拽事件的数据对象，用于合成指示线刷新事件</summary>
		public IDataObject? LastDragData { get; set; }

		/// <summary>宿主窗口句柄（懒加载缓存）</summary>
		public IntPtr WindowHandle { get; set; }
	}

	// ── Win32 ─────────────────────────────────────────

	private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

	[StructLayout(LayoutKind.Sequential)]
	private struct MSLLHOOKSTRUCT
	{
		public POINT pt;
		public uint mouseData;
		public uint flags;
		public uint time;
		public UIntPtr dwExtraInfo;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT
	{
		public int X;
		public int Y;

		public POINT(int x, int y)
		{
			X = x;
			Y = y;
		}
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool UnhookWindowsHookEx(IntPtr hhk);

	[DllImport("user32.dll")]
	private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT lpPoint);

	[DllImport("user32.dll")]
	private static extern short GetAsyncKeyState(int vKey);

	[DllImport("user32.dll")]
	private static extern IntPtr WindowFromPoint(POINT point);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
