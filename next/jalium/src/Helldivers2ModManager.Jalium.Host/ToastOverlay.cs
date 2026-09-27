using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;
using Jalium.UI.Threading;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class ToastOverlay : Grid, IDisposable
{
    private const int MaxVisibleToasts = 4;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StackPanel _toasts = new();
    private bool _disposed;

    internal int VisibleCount => _toasts.Children.Count;

    public ToastOverlay(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Visibility = Visibility.Collapsed;
        _toasts.HorizontalAlignment = HorizontalAlignment.Right;
        _toasts.VerticalAlignment = VerticalAlignment.Bottom;
        _toasts.Margin = new Thickness(24);
        Children.Add(_toasts);
    }

    public void Show(string title, string message, bool isError = false)
    {
        if (_disposed)
            return;
        var color = isError ? Color.FromRgb(0xDC, 0x50, 0x37)
            : Color.FromRgb(0x28, 0xA0, 0x5F);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = isError ? "\uE711" : "\uE73E",
            FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16,
            Foreground = new SolidColorBrush(color),
            Margin = new Thickness(0, 1, 10, 0),
        });
        var copy = new StackPanel();
        copy.Children.Add(new TextBlock { Text = title, FontSize = 13,
            FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis });
        copy.Children.Add(new TextBlock { Text = message, FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        row.Children.Add(copy);
        var card = new Border
        {
            Child = row, MaxWidth = 360, Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 6, 0, 0), CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x14, 0x14, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x46, 0x46, 0x46)),
            BorderThickness = new Thickness(1),
        };
        card.MouseLeftButtonUp += (_, _) => Remove(card);
        _toasts.Children.Add(card);
        while (_toasts.Children.Count > MaxVisibleToasts)
            _toasts.Children.RemoveAt(0);
        Visibility = Visibility.Visible;
        _ = ExpireAsync(card, _lifetime.Token);
    }

    private async Task ExpireAsync(Border card, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);
            _ = _dispatcher.BeginInvoke(() => Remove(card));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void Remove(Border card)
    {
        if (_disposed)
            return;
        _toasts.Children.Remove(card);
        if (_toasts.Children.Count == 0)
            Visibility = Visibility.Collapsed;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _toasts.Children.Clear();
    }
}
