using Helldivers2ModManager.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Helldivers2ModManager.Views;

internal partial class DashboardPageView : Page
{
	public DashboardPageView()
	{
		InitializeComponent();
	}

	private void ModIcon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is Image image && image.Source != null)
		{
			var viewModel = DataContext as DashboardPageViewModel;
			viewModel?.ShowImagePreviewCommand.Execute(image.Source);
		}
	}

	private void ImagePreviewOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		var viewModel = DataContext as DashboardPageViewModel;
		viewModel?.HideImagePreviewCommand.Execute(null);
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
}