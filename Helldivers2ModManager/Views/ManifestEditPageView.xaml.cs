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
}
