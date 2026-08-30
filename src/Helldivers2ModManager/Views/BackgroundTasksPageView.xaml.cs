using System.Windows;
using System.Windows.Controls;

namespace Helldivers2ModManager.Views;

public partial class BackgroundTasksPageView : UserControl
{
	public BackgroundTasksPageView()
	{
		InitializeComponent();
	}

	/// <summary>
	/// 与部署进度弹窗一致：列表顶部是最新（正在部署）步骤，内容变化时无条件
	/// 滚回顶部，保证"正在部署"行始终可见，不受历史滚动位置影响。
	/// </summary>
	private void StepsScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		if (e.ExtentHeightChange <= 0)
			return;
		((ScrollViewer)sender).ScrollToHome();
	}
}
