using Helldivers2ModManager.Models;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;

namespace Helldivers2ModManager.Jalium.Host;

internal enum PatchTextureChannel { Rgb, Rgba, Alpha }

internal sealed partial class PatchResourceViewerPageView
{
    private const double MaxTextureZoom = 16;
    private double _textureZoom = 1;
    private double _zoomOffsetX;
    private double _zoomOffsetY;
    private Point _lastPanPoint;
    private bool _isPanningTexture;

    private void BuildZoomOverlay()
    {
        _zoomOverlay.Visibility = Visibility.Collapsed;
        _zoomOverlay.Focusable = true;
        _zoomOverlay.Background = new SolidColorBrush(Color.FromArgb(230, 0, 0, 0));
        _zoomOverlay.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape)
                return;
            CloseTextureZoom();
            args.Handled = true;
        };
        var content = new Grid { Margin = new Thickness(24) };
        _zoomViewport.Background = Paint(0x10, 0x10, 0x10);
        _zoomViewport.BorderBrush = Stroke;
        _zoomViewport.BorderThickness = new Thickness(1);
        _zoomViewport.ClipToBounds = true;
        var canvas = new Canvas { Background = Brushes.Transparent };
        _zoomImage.Stretch = Stretch.Uniform;
        canvas.Children.Add(_zoomImage);
        _zoomViewport.Child = canvas;
        _zoomViewport.SizeChanged += (_, _) => UpdateZoomLayout();
        _zoomViewport.PreviewMouseWheel += OnZoomWheel;
        _zoomViewport.MouseLeftButtonDown += OnZoomMouseDown;
        _zoomViewport.MouseMove += OnZoomMouseMove;
        _zoomViewport.MouseLeftButtonUp += (_, _) => EndTexturePan();
        _zoomViewport.LostMouseCapture += (_, _) => EndTexturePan();
        content.Children.Add(_zoomViewport);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12) };
        _resetZoomButton.Content = Icon("\uE72C");
        _resetZoomButton.Width = _resetZoomButton.Height = 34;
        _resetZoomButton.Click += (_, _) => ResetTextureZoom();
        toolbar.Children.Add(_resetZoomButton);
        _closeZoomButton.Content = Icon("\uE711");
        _closeZoomButton.Width = _closeZoomButton.Height = 34;
        _closeZoomButton.Margin = new Thickness(4, 0, 0, 0);
        _closeZoomButton.Click += (_, _) => CloseTextureZoom();
        toolbar.Children.Add(_closeZoomButton);
        Panel.SetZIndex(toolbar, 1);
        content.Children.Add(toolbar);
        _zoomOverlay.Children.Add(content);
        Grid.SetRowSpan(_zoomOverlay, 2);
        Panel.SetZIndex(_zoomOverlay, 1000);
        Children.Add(_zoomOverlay);
    }

    internal void OpenTextureZoom()
    {
        if (_textureImage.Source is null || _disposed)
            return;
        _zoomImage.Source = _textureImage.Source;
        ResetTextureZoom();
        _zoomOverlay.Visibility = Visibility.Visible;
        _zoomOverlay.Focus();
        UpdateZoomLayout();
    }

    private void OnZoomWheel(object? sender, MouseWheelEventArgs args)
    {
        var requested = Math.Clamp(_textureZoom * (args.Delta > 0 ? 1.2 : 1 / 1.2), 1, MaxTextureZoom);
        if (Math.Abs(requested - _textureZoom) < 0.0001)
            return;
        var point = args.GetPosition(_zoomViewport);
        var width = _zoomViewport.ActualWidth;
        var height = _zoomViewport.ActualHeight;
        var ratio = requested / _textureZoom;
        _zoomOffsetX = point.X - (point.X - (width * (1 - _textureZoom) / 2 + _zoomOffsetX)) * ratio
            - width * (1 - requested) / 2;
        _zoomOffsetY = point.Y - (point.Y - (height * (1 - _textureZoom) / 2 + _zoomOffsetY)) * ratio
            - height * (1 - requested) / 2;
        _textureZoom = requested;
        UpdateZoomLayout();
        args.Handled = true;
    }

    private void OnZoomMouseDown(object? sender, MouseButtonEventArgs args)
    {
        if (args.ClickCount == 2)
        {
            ResetTextureZoom();
            args.Handled = true;
            return;
        }
        if (_textureZoom <= 1)
            return;
        _isPanningTexture = true;
        _lastPanPoint = args.GetPosition(_zoomViewport);
        _zoomViewport.CaptureMouse();
        args.Handled = true;
    }

    private void OnZoomMouseMove(object? sender, MouseEventArgs args)
    {
        if (!_isPanningTexture || args.LeftButton != MouseButtonState.Pressed)
            return;
        var point = args.GetPosition(_zoomViewport);
        _zoomOffsetX += point.X - _lastPanPoint.X;
        _zoomOffsetY += point.Y - _lastPanPoint.Y;
        _lastPanPoint = point;
        UpdateZoomLayout();
        args.Handled = true;
    }

    private void EndTexturePan()
    {
        _isPanningTexture = false;
        if (_zoomViewport.IsMouseCaptured)
            _zoomViewport.ReleaseMouseCapture();
    }

    private void ResetTextureZoom()
    {
        EndTexturePan();
        _textureZoom = 1;
        _zoomOffsetX = _zoomOffsetY = 0;
        UpdateZoomLayout();
    }

    private void CloseTextureZoom()
    {
        EndTexturePan();
        _zoomOverlay.Visibility = Visibility.Collapsed;
        _zoomImage.Source = null;
        _textureZoom = 1;
        _zoomOffsetX = _zoomOffsetY = 0;
    }

    private void UpdateZoomLayout()
    {
        var width = _zoomViewport.ActualWidth;
        var height = _zoomViewport.ActualHeight;
        if (width <= 0 || height <= 0)
            return;
        _zoomImage.Width = width * _textureZoom;
        _zoomImage.Height = height * _textureZoom;
        Canvas.SetLeft(_zoomImage, width * (1 - _textureZoom) / 2 + _zoomOffsetX);
        Canvas.SetTop(_zoomImage, height * (1 - _textureZoom) / 2 + _zoomOffsetY);
    }

    private async Task LoadTextureAsync()
    {
        _textureCancellation?.Cancel();
        CloseTextureZoom();
        _rawPixels = null;
        _loadedTexture = null;
        _textureImage.Source = null;
        var mod = _selectedMod;
        var texture = _selectedTexture;
        if (_disposed || mod is null || texture is null)
        {
            _previewStatus.Text = _localization["PatchResourceViewerPage.SelectTexture"];
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _textureCancellation = cancellation;
        var generation = _generation;
        var original = _originalResolution.IsChecked == true;
        _previewStatus.Text = _localization["PatchResourceViewerPage.LoadingTexture"]
            .Replace("{id}", texture.TextureIdText);
        try
        {
            const int originalLimit = 67_108_864;
            if (original && (long)texture.Width * texture.Height > originalLimit)
            {
                _previewStatus.Text = _localization["PatchResourceViewerPage.OriginalTextureTooLarge"];
                return;
            }
            var preview = await _preview(mod, texture, original ? originalLimit : 4_194_304,
                cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || generation != _generation
                || !ReferenceEquals(texture, _selectedTexture))
                return;
            if (preview is null || (preview.BgraPixels is null && preview.EncodedImageBytes is null))
            {
                _previewStatus.Text = _localization["PatchResourceViewerPage.TextureUnavailable"];
                return;
            }
            _loadedTexture = preview;
            if (preview.BgraPixels is not null)
            {
                _pixelWidth = preview.Width;
                _pixelHeight = preview.Height;
                if (_pixelWidth <= 0 || _pixelHeight <= 0
                    || preview.BgraPixels.Length != checked(_pixelWidth * _pixelHeight * 4))
                    throw new InvalidDataException("Invalid BGRA texture dimensions.");
                _rawPixels = preview.BgraPixels;
            }
            else
            {
                using var stream = new MemoryStream(preview.EncodedImageBytes!, writable: false);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                if (!original)
                    image.DecodePixelWidth = Math.Min(preview.Width, 2048);
                image.StreamSource = stream;
                image.EndInit();
                _pixelWidth = image.PixelWidth;
                _pixelHeight = image.PixelHeight;
                var stride = checked(_pixelWidth * 4);
                _rawPixels = new byte[checked(stride * _pixelHeight)];
                image.CopyPixels(new Int32Rect(0, 0, _pixelWidth, _pixelHeight),
                    _rawPixels, stride, 0);
            }
            await ApplyChannelAsync();
            if (!_disposed && !cancellation.IsCancellationRequested && generation == _generation)
                _previewStatus.Text = _localization["PatchResourceViewerPage.TextureLoaded"]
                    .Replace("{width}", preview.Width.ToString())
                    .Replace("{height}", preview.Height.ToString())
                    .Replace("{format}", preview.Description);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed && !cancellation.IsCancellationRequested && generation == _generation)
                _previewStatus.Text = _localization["PatchResourceViewerPage.TextureUnavailable"];
        }
        finally
        {
            if (ReferenceEquals(_textureCancellation, cancellation))
                _textureCancellation = null;
        }
    }

    private void SelectChannel(PatchTextureChannel channel)
    {
        _channel = channel;
        for (var index = 0; index < _channels.Length; index++)
            _channels[index].Background = index == (int)channel
                ? Paint(0x00, 0x78, 0xD4) : Paint(0x32, 0x32, 0x32);
        _ = ApplyChannelAsync();
    }

    private async Task ApplyChannelAsync()
    {
        var source = _rawPixels;
        var texture = _loadedTexture;
        if (_disposed || source is null || texture is null)
            return;
        var channel = _channel;
        var generation = _generation;
        var selectedTexture = _selectedTexture;
        var width = _pixelWidth;
        var height = _pixelHeight;
        try
        {
            var pixels = await Task.Run(() => ConvertChannel(source, channel));
            if (_disposed || generation != _generation || !ReferenceEquals(selectedTexture, _selectedTexture)
                || !ReferenceEquals(texture, _loadedTexture) || channel != _channel)
                return;
            _textureImage.Source = BitmapImage.FromPixels(pixels, width, height, width * 4);
            if (_zoomOverlay.Visibility == Visibility.Visible)
                _zoomImage.Source = _textureImage.Source;
        }
        catch (Exception)
        {
            if (!_disposed && generation == _generation)
                _previewStatus.Text = _localization["PatchResourceViewerPage.TextureUnavailable"];
        }
    }

    internal static byte[] ConvertChannel(byte[] bgra, PatchTextureChannel channel)
    {
        if (bgra.Length % 4 != 0)
            throw new ArgumentException("BGRA data must contain complete pixels.", nameof(bgra));
        var pixels = new byte[bgra.Length];
        if (NativeTextureConverter.TryConvert(bgra, pixels, channel))
            return pixels;
        ConvertChannelManagedInto(bgra, pixels, channel);
        return pixels;
    }

    internal static byte[] ConvertChannelManaged(byte[] bgra, PatchTextureChannel channel)
    {
        if (bgra.Length % 4 != 0)
            throw new ArgumentException("BGRA data must contain complete pixels.", nameof(bgra));
        var pixels = new byte[bgra.Length];
        ConvertChannelManagedInto(bgra, pixels, channel);
        return pixels;
    }

    private static void ConvertChannelManagedInto(byte[] bgra, byte[] pixels,
        PatchTextureChannel channel)
    {
        Buffer.BlockCopy(bgra, 0, pixels, 0, bgra.Length);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (channel == PatchTextureChannel.Alpha)
            {
                var alpha = pixels[offset + 3];
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = alpha;
                pixels[offset + 3] = byte.MaxValue;
            }
            else if (channel == PatchTextureChannel.Rgb)
                pixels[offset + 3] = byte.MaxValue;
        }
    }
}
