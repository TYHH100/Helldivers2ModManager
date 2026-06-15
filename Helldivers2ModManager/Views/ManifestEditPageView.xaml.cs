using Helldivers2ModManager.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Helldivers2ModManager.Views;

internal partial class ManifestEditPageView : Page
{
	public ManifestEditPageView()
	{
		InitializeComponent();
		DataContextChanged += (_, _) =>
		{
			if (DataContext is ManifestEditPageViewModel vm && string.IsNullOrEmpty(vm.ModName))
				vm.InitializeFromMod();
		};
	}

	private void OptionImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is Image image && image.Source != null)
		{
			var viewModel = DataContext as ManifestEditPageViewModel;
			viewModel?.ShowImagePreviewCommand.Execute(image.Source);
		}
	}

	private void ImagePreviewOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		var viewModel = DataContext as ManifestEditPageViewModel;
		viewModel?.HideImagePreviewCommand.Execute(null);
	}

	private void ImagePreviewBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		e.Handled = true;
	}
}
