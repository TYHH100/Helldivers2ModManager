using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Helldivers2ModManager;

/// <summary>
/// 启动闪屏窗口：以透明分层窗口显示 LOGO。
/// WPF 默认 SplashScreen 在首帧渲染前（CompositionTarget.Rendering 在帧开始触发）就关闭，
/// 此时主窗口首帧尚未提交给 DWM，主窗口区域在屏幕上呈现为黑色，
/// 黑底会透过 LOGO 的透明区域一闪而过。本窗口由 App.OnStartup 在
/// MainWindow.ContentRendered（首帧真正渲染完成后）再关闭，实现无缝过渡。
/// </summary>
internal sealed class SplashWindow : Window
{
	public SplashWindow()
	{
		WindowStyle = WindowStyle.None;
		AllowsTransparency = true;
		Background = Brushes.Transparent;
		ResizeMode = ResizeMode.NoResize;
		ShowInTaskbar = false;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		Topmost = true;
		SizeToContent = SizeToContent.WidthAndHeight;
		Content = new Image
		{
			Source = LoadSplashImage(),
			Stretch = Stretch.None
		};
	}

	private static ImageSource LoadSplashImage()
	{
		var bitmap = new BitmapImage();
		bitmap.BeginInit();
		bitmap.UriSource = new Uri("pack://application:,,,/Resources/Images/logo_splash.png");
		bitmap.CacheOption = BitmapCacheOption.OnLoad;
		bitmap.EndInit();
		bitmap.Freeze();
		return bitmap;
	}
}
