using Helldivers2ModManager.Models;
using Helldivers2ModManager.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;

namespace Helldivers2ModManager.Views;

internal partial class ModelPreviewPageView
{
    private const double GizmoCenter = 36d;
    private const double GizmoAxisLength = 23d;
    private double _yaw = 35;
    private double _pitch = 15;
    private double _distance = 5;
    private Point _lastPoint;
    private Point3D _target;
    private bool _isRotating;
    private bool _isPanning;
    private ModelPreviewGizmoAxis? _lastClickedGizmoAxis;
    private bool _lastGizmoAxisWasOpposite;

    public ModelPreviewPageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ResetCamera();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Viewport3D keeps a native scene graph. Clearing it here is intentional:
        // releasing only the ViewModel binding leaves the old ImageBrush/BitmapSource
        // alive until WPF eventually tears down this view, which is especially costly
        // for a user-requested source-resolution texture.
        EndInteraction();
        GroundGridVisual.Content = null;
        PreviewViewport.Children.Clear();

        if (DataContext is INotifyPropertyChanged notify)
            notify.PropertyChanged -= ViewModelOnPropertyChanged;
        DataContextChanged -= OnDataContextChanged;
        DataContext = null;
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
        if (e.PropertyName is nameof(ModelPreviewPageViewModel.ModelGroup) or
            nameof(ModelPreviewPageViewModel.SuggestedCameraDistance) or
            nameof(ModelPreviewPageViewModel.SuggestedCameraYaw))
            Dispatcher.BeginInvoke(ResetCamera);
    }

    private void PreviewViewport_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isRotating = true;
        _lastPoint = e.GetPosition(PreviewInteractionSurface);
        PreviewInteractionSurface.CaptureMouse();
        PreviewInteractionSurface.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void PreviewViewport_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndInteraction();
        e.Handled = true;
    }

    private void PreviewViewport_OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _lastPoint = e.GetPosition(PreviewInteractionSurface);
        PreviewInteractionSurface.CaptureMouse();
        PreviewInteractionSurface.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void PreviewViewport_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndInteraction();
        e.Handled = true;
    }

    private void PreviewViewport_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isRotating && !_isPanning)
            return;

        if ((_isRotating && e.LeftButton != MouseButtonState.Pressed) ||
            (_isPanning && e.RightButton != MouseButtonState.Pressed))
        {
            EndInteraction();
            return;
        }

        var point = e.GetPosition(PreviewInteractionSurface);
        var deltaX = point.X - _lastPoint.X;
        var deltaY = point.Y - _lastPoint.Y;
        if (_isRotating)
        {
            _lastClickedGizmoAxis = null;
            _lastGizmoAxisWasOpposite = false;
            _yaw += deltaX * 0.45;
            _pitch -= deltaY * 0.45;
        }
        else
        {
            PanCameraTarget(deltaX, deltaY);
        }

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

    private void FrontView_OnClick(object sender, RoutedEventArgs e) => SetCamera(GetSuggestedFrontYaw(), 0);

    private void SideView_OnClick(object sender, RoutedEventArgs e) => SetCamera(GetSuggestedFrontYaw() + 90, 0);

    private void TopView_OnClick(object sender, RoutedEventArgs e) => SetCamera(GetSuggestedFrontYaw(), 90);

    private void GizmoAxis_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string axisName } ||
            !Enum.TryParse<ModelPreviewGizmoAxis>(axisName, out var axis))
            return;

        var opposite = _lastClickedGizmoAxis == axis && !_lastGizmoAxisWasOpposite;
        var pose = ModelPreviewViewportGuides.GetAxisView(axis, opposite);
        _lastClickedGizmoAxis = axis;
        _lastGizmoAxisWasOpposite = opposite;
        _yaw = pose.Yaw;
        _pitch = pose.Pitch;
        UpdateCamera();
        e.Handled = true;
    }

    private void ResetCamera()
    {
        if (DataContext is ModelPreviewPageViewModel vm)
            _distance = Math.Max(vm.SuggestedCameraDistance, 1);
        _target = new Point3D();
        _yaw = GetSuggestedFrontYaw();
        _pitch = 0;
        UpdateGroundGrid();
        UpdateCamera();
    }

    private double GetSuggestedFrontYaw() => DataContext is ModelPreviewPageViewModel vm
        ? vm.SuggestedCameraYaw
        : 0;

    private void UpdateCamera()
    {
        var basis = ModelPreviewViewportGuides.GetCameraBasis(_yaw, _pitch);
        var position = _target - basis.Forward * _distance;
        PreviewCamera.Position = position;
        PreviewCamera.LookDirection = basis.Forward * _distance;
        PreviewCamera.UpDirection = basis.Up;
        PreviewCamera.NearPlaneDistance = Math.Max(_distance / 10000, 0.001);
        PreviewCamera.FarPlaneDistance = Math.Max(_distance * 100, 1000);
        UpdateOrientationGizmo();
        if (DataContext is ModelPreviewPageViewModel vm)
            vm.UpdateCameraOrientation(_yaw, _pitch);
    }

    private void UpdateOrientationGizmo()
    {
        var gizmo = ModelPreviewViewportGuides.GetAxisGizmo(_yaw, _pitch);
        UpdateGizmoAxis(GizmoXAxis, GizmoXDot, GizmoXLabel, gizmo.X);
        UpdateGizmoAxis(GizmoYAxis, GizmoYDot, GizmoYLabel, gizmo.Y);
        UpdateGizmoAxis(GizmoZAxis, GizmoZDot, GizmoZLabel, gizmo.Z);
    }

    private static void UpdateGizmoAxis(
        Line line,
        Ellipse dot,
        TextBlock label,
        ModelPreviewAxisProjection projection)
    {
        var endX = GizmoCenter + projection.ScreenX * GizmoAxisLength;
        var endY = GizmoCenter + projection.ScreenY * GizmoAxisLength;
        line.X1 = GizmoCenter;
        line.Y1 = GizmoCenter;
        line.X2 = endX;
        line.Y2 = endY;

        var size = 10d + Math.Clamp((projection.Depth + 1d) * 2d, 0d, 4d);
        dot.Width = size;
        dot.Height = size;
        dot.Opacity = 0.45d + Math.Clamp((projection.Depth + 1d) * 0.275d, 0d, 0.55d);
        Canvas.SetLeft(dot, endX - size / 2d);
        Canvas.SetTop(dot, endY - size / 2d);
        Canvas.SetZIndex(dot, 100 + (int)Math.Round(projection.Depth * 10d));

        var labelOffset = projection.ScreenLength > 0.1d
            ? 4d / projection.ScreenLength
            : 0d;
        Canvas.SetLeft(label, endX + projection.ScreenX * labelOffset - 3d);
        Canvas.SetTop(label, endY + projection.ScreenY * labelOffset - 7d);
        label.Opacity = dot.Opacity;
        Canvas.SetZIndex(label, Canvas.GetZIndex(dot) + 1);
    }

    private void UpdateGroundGrid()
    {
        if (DataContext is not ModelPreviewPageViewModel { ModelGroup: { } modelGroup })
        {
            GroundGridVisual.Content = null;
            return;
        }

        var layout = ModelPreviewViewportGuides.CreateGroundGridLayout(modelGroup.Bounds);
        GroundGridVisual.Content = layout.HasGrid ? CreateGroundGrid(layout) : null;
    }

    private static Model3DGroup CreateGroundGrid(ModelPreviewGroundGridLayout layout)
    {
        var minorLines = new MeshGeometry3D();
        var xAxis = new MeshGeometry3D();
        var zAxis = new MeshGeometry3D();
        var thickness = Math.Max(layout.CellSize * 0.012d, 0.002d);
        var minX = layout.CenterX - layout.HalfExtent;
        var maxX = layout.CenterX + layout.HalfExtent;
        var minZ = layout.CenterZ - layout.HalfExtent;
        var maxZ = layout.CenterZ + layout.HalfExtent;

        for (var index = -layout.HalfLineCount; index <= layout.HalfLineCount; index++)
        {
            var offset = index * layout.CellSize;
            AppendGridLine(
                index == 0 ? xAxis : minorLines,
                new Point3D(minX, layout.FloorY, layout.CenterZ + offset),
                new Point3D(maxX, layout.FloorY, layout.CenterZ + offset),
                thickness);
            AppendGridLine(
                index == 0 ? zAxis : minorLines,
                new Point3D(layout.CenterX + offset, layout.FloorY, minZ),
                new Point3D(layout.CenterX + offset, layout.FloorY, maxZ),
                thickness);
        }

        var group = new Model3DGroup();
        AddGroundGridModel(group, minorLines, Color.FromArgb(100, 126, 145, 170));
        AddGroundGridModel(group, xAxis, Color.FromArgb(205, 224, 108, 117));
        AddGroundGridModel(group, zAxis, Color.FromArgb(205, 124, 169, 255));
        group.Freeze();
        return group;
    }

    private static void AppendGridLine(MeshGeometry3D geometry, Point3D start, Point3D end, double thickness)
    {
        var perpendicular = Math.Abs(end.X - start.X) >= Math.Abs(end.Z - start.Z)
            ? new Vector3D(0, 0, thickness / 2d)
            : new Vector3D(thickness / 2d, 0, 0);
        var index = geometry.Positions.Count;
        geometry.Positions.Add(start - perpendicular);
        geometry.Positions.Add(start + perpendicular);
        geometry.Positions.Add(end + perpendicular);
        geometry.Positions.Add(end - perpendicular);
        geometry.TriangleIndices.Add(index);
        geometry.TriangleIndices.Add(index + 1);
        geometry.TriangleIndices.Add(index + 2);
        geometry.TriangleIndices.Add(index);
        geometry.TriangleIndices.Add(index + 2);
        geometry.TriangleIndices.Add(index + 3);
    }

    private static void AddGroundGridModel(Model3DGroup group, MeshGeometry3D geometry, Color color)
    {
        if (geometry.Positions.Count == 0)
            return;

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        geometry.Freeze();
        var model = new GeometryModel3D(geometry, material) { BackMaterial = material };
        model.Freeze();
        group.Children.Add(model);
    }

    private void SetCamera(double yaw, double pitch)
    {
        _lastClickedGizmoAxis = null;
        _lastGizmoAxisWasOpposite = false;
        _yaw = yaw;
        _pitch = pitch;
        UpdateCamera();
    }

    private void PanCameraTarget(double deltaX, double deltaY)
    {
        var basis = ModelPreviewViewportGuides.GetCameraBasis(_yaw, _pitch);
        var pixels = Math.Max(PreviewInteractionSurface.ActualHeight, 1d);
        var viewportHeight = 2d * _distance * Math.Tan(PreviewCamera.FieldOfView * Math.PI / 360d);
        var unitsPerPixel = viewportHeight / pixels;
        _target -= basis.Right * (deltaX * unitsPerPixel);
        _target += basis.Up * (deltaY * unitsPerPixel);
    }

    private void EndInteraction()
    {
        _isRotating = false;
        _isPanning = false;
        PreviewInteractionSurface.Cursor = Cursors.Arrow;
        if (PreviewInteractionSurface.IsMouseCaptured)
            PreviewInteractionSurface.ReleaseMouseCapture();
    }
}
