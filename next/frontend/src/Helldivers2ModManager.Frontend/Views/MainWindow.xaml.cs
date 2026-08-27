using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Frontend.ViewModels;

namespace Helldivers2ModManager.Frontend.Views;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar",
    };

    private readonly LocalizationCatalog _localization;
    private System.Windows.Controls.Border? _dropOverlay;
    private System.Windows.Controls.TextBlock? _dropOverlayText;

    public MainWindow(MainViewModel viewModel, LocalizationCatalog localization)
    {
        InitializeComponent();
        DataContext = viewModel;
        _localization = localization;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _dropOverlay = Template.FindName("DropOverlay", this) as System.Windows.Controls.Border;
        _dropOverlayText = Template.FindName("DropOverlayText", this) as System.Windows.Controls.TextBlock;
    }

    private void OnPageTargetUpdated(object sender, System.Windows.Data.DataTransferEventArgs e)
    {
        if (sender is not UIElement pageHost)
        {
            return;
        }

        if (pageHost.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            pageHost.RenderTransform = transform;
        }

        var opacityAnimation = new DoubleAnimation(0D, 1D, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        var offsetAnimation = new DoubleAnimation(14D, 0D, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        pageHost.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
        transform.BeginAnimation(TranslateTransform.YProperty, offsetAnimation);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnShowHelp(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.NavigateCommand.Execute("System.Help");
        }
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!TryGetArchivePaths(e.Data, out var message))
        {
            if (_dropOverlayText is not null)
            {
                _dropOverlayText.Text = message;
            }
        }
        else
        {
            if (_dropOverlayText is not null)
            {
                _dropOverlayText.Text = _localization.GetString("MainWindow.DropImportHint");
            }
        }

        if (_dropOverlay is not null)
        {
            _dropOverlay.Visibility = Visibility.Visible;
        }

        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (_dropOverlay is not null)
        {
            _dropOverlay.Visibility = Visibility.Collapsed;
        }

        if (DataContext is MainViewModel viewModel && TryGetArchivePaths(e.Data, out _))
        {
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            viewModel.ReceiveImportedArchives(paths);
        }

        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (_dropOverlay is not null)
        {
            _dropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private bool TryGetArchivePaths(IDataObject dataObject, out string validationMessage)
    {
        validationMessage = _localization.GetString("MainWindow.DropImportInvalid");
        if (!dataObject.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (dataObject.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return false;
        }

        return paths.All(path => System.IO.File.Exists(path) && ArchiveExtensions.Contains(System.IO.Path.GetExtension(path)));
    }
}
