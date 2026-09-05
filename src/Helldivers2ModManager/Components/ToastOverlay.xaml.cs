using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Helldivers2ModManager.Components;

/// <summary>
/// 气泡通知消息：用于"需要及时看到、但不需要用户确认"的结果提示
/// （如版本兼容性检查完成）。由任意层通过 <see cref="WeakReferenceMessenger"/> 发送，
/// <see cref="ToastOverlay"/> 接收后右下角弹出，数秒后自动消失。
/// </summary>
internal sealed record ToastMessage(string Title, string Message, bool IsError = false);

/// <summary>单条气泡的数据（仅供 ToastOverlay 内部绑定）。</summary>
internal sealed class ToastItem
{
	public string Title { get; }
	public string Message { get; }

	/// <summary>true 时显示错误图标与红色强调（如检查失败）。</summary>
	public bool IsError { get; }

	public ToastItem(string title, string message, bool isError = false)
	{
		Title = title;
		Message = message;
		IsError = isError;
	}
}

/// <summary>
/// 全局气泡通知覆盖层：右下角堆叠展示 <see cref="ToastMessage"/>，
/// 每条停留数秒后自动淡出移除，点击可提前关闭。不阻塞任何交互
/// （根元素 IsHitTestVisible=False，仅气泡本体可点击）。
/// </summary>
internal sealed partial class ToastOverlay : UserControl
{
	private const int MaxVisibleToasts = 4;
	private static readonly Duration FadeOutDuration = new(TimeSpan.FromMilliseconds(300));

	private readonly ObservableCollection<ToastItem> _toasts = [];

	public ObservableCollection<ToastItem> Toasts => _toasts;

	public ToastOverlay()
	{
		InitializeComponent();
		DataContext = this;
		WeakReferenceMessenger.Default.Register<ToastOverlay, ToastMessage>(this, static (receiver, message) =>
			receiver.OnToastMessage(message));
	}

	private void OnToastMessage(ToastMessage message)
	{
		var dispatcher = Application.Current?.Dispatcher;
		if (dispatcher is null)
			return;
		if (!dispatcher.CheckAccess())
		{
			// 发送方一般在 UI 线程；后台线程发送时切回 UI 再入列
			dispatcher.BeginInvoke(() => OnToastMessage(message));
			return;
		}

		var item = new ToastItem(message.Title, message.Message, message.IsError);
		_toasts.Add(item);

		// 超出上限时立即移除最旧的，避免气泡堆积遮住右下角内容
		while (_toasts.Count > MaxVisibleToasts)
			_toasts.RemoveAt(0);

		var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
		timer.Tick += (_, _) =>
		{
			((DispatcherTimer)timer).Stop();
			FadeOutAndRemove(item);
		};
		timer.Start();
	}

	private void FadeOutAndRemove(ToastItem item)
	{
		if (ToastsHost.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement container)
		{
			var animation = new DoubleAnimation(0, FadeOutDuration)
			{
				FillBehavior = FillBehavior.Stop
			};
			animation.Completed += (_, _) => _toasts.Remove(item);
			container.BeginAnimation(OpacityProperty, animation);
		}
		else
		{
			_toasts.Remove(item);
		}
	}

	private void Toast_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: ToastItem item })
		{
			FadeOutAndRemove(item);
			e.Handled = true;
		}
	}
}
