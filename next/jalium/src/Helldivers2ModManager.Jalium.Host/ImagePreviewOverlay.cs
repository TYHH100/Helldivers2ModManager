using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class ImagePreviewOverlay : Grid
{
    private readonly Image _image = new();
    private readonly Button _close = new();

    internal bool IsOpen => Visibility == Visibility.Visible;
    internal ImageSource? Source => _image.Source;

    public ImagePreviewOverlay(string? closeLabel = null)
    {
        Visibility = Visibility.Collapsed;
        Focusable = true;
        Background = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0));
        MouseLeftButtonDown += (_, _) => Hide();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Hide();
            e.Handled = true;
        };

        var dialog = new Border
        {
            MaxWidth = 800, MaxHeight = 600, Margin = new Thickness(24),
            Padding = new Thickness(12), Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x46, 0x46, 0x46)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dialog.MouseLeftButtonDown += (_, e) => e.Handled = true;
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _close.Content = new TextBlock { Text = "\u00D7", FontFamily = new FontFamily("Segoe UI"), FontSize = 20 };
        _close.ToolTip = closeLabel;
        _close.Width = 36;
        _close.Height = 36;
        _close.HorizontalAlignment = HorizontalAlignment.Right;
        _close.Margin = new Thickness(0, 0, 0, 8);
        _close.Click += (_, _) => Hide();
        layout.Children.Add(_close);
        _image.Stretch = Stretch.Uniform;
        _image.MaxWidth = 780;
        _image.MaxHeight = 550;
        Grid.SetRow(_image, 1);
        layout.Children.Add(_image);
        dialog.Child = layout;
        Children.Add(dialog);
    }

    public void Show(ImageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _image.Source = source;
        Visibility = Visibility.Visible;
        Focus();
    }

    public void Hide()
    {
        _image.Source = null;
        Visibility = Visibility.Collapsed;
    }
}
