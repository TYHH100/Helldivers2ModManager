using Helldivers2ModManager.ViewModels;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Helldivers2ModManager;

internal partial class MainWindow : Window
{
	public MainWindow(MainViewModel viewModel)
	{
		InitializeComponent();

		DataContext = viewModel;
	}

	protected override void OnActivated(EventArgs e)
	{
		DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, 1, sizeof(int));
		base.OnActivated(e);
	}

	private void HelpButton_Click(object sender, RoutedEventArgs e)
	{
		(DataContext as MainViewModel)?.HelpCommand.Execute(null);
	}

	private void MinButton_Click(object sender, RoutedEventArgs e)
	{
		WindowState = WindowState.Minimized;
	}

	private void MaxButton_Click(object sender, RoutedEventArgs e)
	{
		if (WindowState == WindowState.Maximized)
			WindowState = WindowState.Normal;
		else
			WindowState = WindowState.Maximized;
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void Window_DragOver(object sender, DragEventArgs e)
	{
		if (e.Data.GetDataPresent(DataFormats.FileDrop))
		{
			var files = (string[])e.Data.GetData(DataFormats.FileDrop);
			var supportedExtensions = new[] { ".rar", ".zip", ".7z", ".tar" };
			if (files.Any(file => supportedExtensions.Contains(System.IO.Path.GetExtension(file).ToLowerInvariant())))
			{
				e.Effects = DragDropEffects.Copy;
				e.Handled = true;
				return;
			}
		}
		e.Effects = DragDropEffects.None;
		e.Handled = true;
	}

	private async void Window_Drop(object sender, DragEventArgs e)
	{
		if (e.Data.GetDataPresent(DataFormats.FileDrop))
		{
			var files = (string[])e.Data.GetData(DataFormats.FileDrop);
			var supportedExtensions = new[] { ".rar", ".zip", ".7z", ".tar" };
			var validFiles = files.Where(file => supportedExtensions.Contains(System.IO.Path.GetExtension(file).ToLowerInvariant())).ToArray();

			if (validFiles.Any())
			{
				var viewModel = DataContext as MainViewModel;
				var dashboardViewModel = viewModel?.CurrentViewModel as DashboardPageViewModel;

				if (dashboardViewModel != null)
				{
					foreach (var file in validFiles)
					{
						await dashboardViewModel.AddCommand.ExecuteAsync(file);
					}
				}
			}
		}
		e.Handled = true;
	}

	[LibraryImport("dwmapi.dll")]
	private static partial void DwmSetWindowAttribute(nint hwnd, uint dwAttribute, in int pvAttribute, uint cbAttribute);
}