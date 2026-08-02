using Helldivers2ModManager.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;

namespace Helldivers2ModManager.Views;

internal partial class ModelPreviewPageView
{
    private double _yaw = 35;
    private double _pitch = 15;
    private double _distance = 5;
    private Point _lastPoint;
    private bool _isDragging;

    public ModelPreviewPageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ResetCamera();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldNotify)
            oldNotify.PropertyChanged -= ViewModelOnPropertyChanged;
        if (e.NewValue is INotifyPropertyChanged newNotify)
            newNotify.PropertyChanged += ViewModelOnPropertyChanged;
        ResetCamera();
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModelPreviewPageViewModel.ModelGroup) or nameof(ModelPreviewPageViewModel.SuggestedCameraDistance))
            Dispatcher.BeginInvoke(ResetCamera);
    }

    private void PreviewViewport_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _lastPoint = e.GetPosition(PreviewViewport);
        PreviewViewport.CaptureMouse();
        PreviewViewport.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void PreviewViewport_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndDragging();
        e.Handled = true;
    }

    private void PreviewViewport_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed)
            return;

        var point = e.GetPosition(PreviewViewport);
        _yaw += (point.X - _lastPoint.X) * 0.45;
        _pitch = Math.Clamp(_pitch - (point.Y - _lastPoint.Y) * 0.45, -80, 80);
        _lastPoint = point;
        UpdateCamera();
        e.Handled = true;
    }

    private void PreviewViewport_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.85 : 1.18), 0.05, 100000);
        UpdateCamera();
        e.Handled = true;
    }

    private void ResetView_OnClick(object sender, RoutedEventArgs e) => ResetCamera();

    private void FrontView_OnClick(object sender, RoutedEventArgs e) => SetCamera(0, 0);

    private void SideView_OnClick(object sender, RoutedEventArgs e) => SetCamera(90, 0);

    private void TopView_OnClick(object sender, RoutedEventArgs e) => SetCamera(0, 80);

    private void ResetCamera()
    {
        if (DataContext is ModelPreviewPageViewModel vm)
            _distance = Math.Max(vm.SuggestedCameraDistance, 1);
        _yaw = 35;
        _pitch = 15;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        var yaw = _yaw * Math.PI / 180;
        var pitch = _pitch * Math.PI / 180;
        var horizontal = _distance * Math.Cos(pitch);
        var position = new Point3D(
            horizontal * Math.Cos(yaw),
            _distance * Math.Sin(pitch),
            horizontal * Math.Sin(yaw));
        PreviewCamera.Position = position;
        PreviewCamera.LookDirection = new Vector3D(-position.X, -position.Y, -position.Z);
        PreviewCamera.UpDirection = new Vector3D(0, 1, 0);
        PreviewCamera.NearPlaneDistance = Math.Max(_distance / 10000, 0.001);
        PreviewCamera.FarPlaneDistance = Math.Max(_distance * 100, 1000);
    }

    private void SetCamera(double yaw, double pitch)
    {
        _yaw = yaw;
        _pitch = pitch;
        UpdateCamera();
    }

    private void EndDragging()
    {
        _isDragging = false;
        PreviewViewport.Cursor = Cursors.Arrow;
        if (PreviewViewport.IsMouseCaptured)
            PreviewViewport.ReleaseMouseCapture();
    }
}
