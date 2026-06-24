// Ignore Spelling: Helldivers

using CommunityToolkit.Mvvm.Messaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Helldivers2ModManager.Components;

/// <summary>
/// 显示图片预览消息
/// </summary>
internal sealed class ImagePreviewShowMessage
{
	public required ImageSource ImageSource { get; init; }
}

/// <summary>
/// 隐藏图片预览消息
/// </summary>
internal sealed class ImagePreviewHideMessage { }

/// <summary>
/// 图片预览覆盖层控件 - 统一的图片预览实现
/// 支持点击背景关闭、ESC 键关闭、点击关闭按钮关闭
/// </summary>
internal partial class ImagePreviewOverlay : UserControl,
	IRecipient<ImagePreviewShowMessage>,
	IRecipient<ImagePreviewHideMessage>
{
	public static bool IsRegistered { get; private set; }

	public static event EventHandler? Registered;

	public ImagePreviewOverlay()
	{
		InitializeComponent();

		WeakReferenceMessenger.Default.Register<ImagePreviewShowMessage>(this);
		WeakReferenceMessenger.Default.Register<ImagePreviewHideMessage>(this);

		if (!IsRegistered)
		{
			IsRegistered = true;
			Registered?.Invoke(this, EventArgs.Empty);
		}
	}

	public void Receive(ImagePreviewShowMessage message)
	{
		previewImage.Source = message.ImageSource;
		Visibility = Visibility.Visible;
		Focus();
	}

	public void Receive(ImagePreviewHideMessage message)
	{
		previewImage.Source = null;
		Visibility = Visibility.Hidden;
	}

	/// <summary>
	/// 按 ESC 键关闭图片预览
	/// </summary>
	private void Overlay_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			Receive(new ImagePreviewHideMessage());
			e.Handled = true;
		}
	}

	/// <summary>
	/// 预览可见性变化时自动获取焦点，以接收 ESC 按键事件
	/// </summary>
	private void Overlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
	{
		if (e.NewValue is true && sender is UserControl control)
		{
			control.Focusable = true;
			_ = control.Focus();
		}
	}

	/// <summary>
	/// 点击图片容器内部时阻止事件冒泡（避免触发背景关闭）
	/// </summary>
	private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		e.Handled = true;
	}

	/// <summary>
	/// 点击关闭按钮或背景区域关闭预览
	/// </summary>
	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		Receive(new ImagePreviewHideMessage());
	}
}
