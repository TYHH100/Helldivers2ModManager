using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Helldivers2ModManager.Views;

public partial class PatchResourceViewerPageView : UserControl
{
    private const double MinZoom = 1.0;
    private const double MaxZoom = 16.0;
    private bool _isPanning;
    private Point _lastPanPoint;

    public PatchResourceViewerPageView()
    {
        InitializeComponent();
    }

    private void PreviewImage_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ZoomImage.Source is null)
            return;

        ResetZoom();
        TextureZoomOverlay.Visibility = Visibility.Visible;
        TextureZoomOverlay.Focus();
        e.Handled = true;
    }

    private void ZoomViewport_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var matrix = ZoomTransform.Matrix;
        var currentZoom = matrix.M11;
        var requestedZoom = Math.Clamp(currentZoom * (e.Delta > 0 ? 1.2 : 1.0 / 1.2), MinZoom, MaxZoom);
        var scaleChange = requestedZoom / currentZoom;
        if (Math.Abs(scaleChange - 1.0) < 0.0001)
            return;

        var mousePosition = e.GetPosition(ZoomViewport);
        matrix.ScaleAt(scaleChange, scaleChange, mousePosition.X, mousePosition.Y);
        ZoomTransform.Matrix = matrix;
        e.Handled = true;
    }

    private void ZoomViewport_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ResetZoom();
            e.Handled = true;
            return;
        }

        if (ZoomTransform.Matrix.M11 <= MinZoom)
            return;

        _isPanning = true;
        _lastPanPoint = e.GetPosition(ZoomViewport);
        ZoomViewport.Cursor = Cursors.SizeAll;
        ZoomViewport.CaptureMouse();
        e.Handled = true;
    }

    private void ZoomViewport_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning || e.LeftButton != MouseButtonState.Pressed)
            return;

        var currentPoint = e.GetPosition(ZoomViewport);
        var matrix = ZoomTransform.Matrix;
        matrix.OffsetX += currentPoint.X - _lastPanPoint.X;
        matrix.OffsetY += currentPoint.Y - _lastPanPoint.Y;
        ZoomTransform.Matrix = matrix;
        _lastPanPoint = currentPoint;
        e.Handled = true;
    }

    private void ZoomViewport_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndPanning();

    private void ZoomViewport_OnLostMouseCapture(object sender, MouseEventArgs e) => EndPanning();

    private void ResetZoom_OnClick(object sender, RoutedEventArgs e) => ResetZoom();

    private void ClosePreview_OnClick(object sender, RoutedEventArgs e) => ClosePreview();

    private void TextureZoomOverlay_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        ClosePreview();
        e.Handled = true;
    }

    private void ResetZoom()
    {
        EndPanning();
        ZoomTransform.Matrix = Matrix.Identity;
    }

    private void ClosePreview()
    {
        EndPanning();
        TextureZoomOverlay.Visibility = Visibility.Collapsed;
        ZoomTransform.Matrix = Matrix.Identity;
    }

    private void EndPanning()
    {
        _isPanning = false;
        ZoomViewport.Cursor = Cursors.Arrow;
        if (ZoomViewport.IsMouseCaptured)
            ZoomViewport.ReleaseMouseCapture();
    }
}
