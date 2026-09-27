using Helldivers2ModManager.Models;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;
using Jalium.UI.Media.Media3D;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class ModelPreviewScene : Grid, IDisposable
{
    private readonly ModelPreviewNativeSurface _surface = new();
    private readonly PerspectiveCamera _camera = new()
    {
        FieldOfView = 45,
        NearPlaneDistance = 0.01,
        FarPlaneDistance = 100000,
    };
    private readonly ModelVisual3D _model = new();
    private Point3D _target;
    private Point _lastPointer;
    private double _yaw;
    private double _pitch;
    private double _distance = 5;
    private double _suggestedDistance = 5;
    private double _frontYaw;
    private bool _rotating;
    private bool _panning;

    internal int DisplayedMeshCount { get; private set; }
    internal Model3DGroup? ModelGroup => _model.Content as Model3DGroup;
    internal PerspectiveCamera Camera => _camera;
    internal ModelPreviewNativeSurface Surface => _surface;

    public ModelPreviewScene()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x25, 0x2A));
        ClipToBounds = true;
        Children.Add(_surface);
        _surface.NativeDrag += ApplyPointerDelta;
        _surface.WheelMoved += ApplyWheel;
        MouseLeftButtonDown += OnRotateStart;
        MouseRightButtonDown += OnPanStart;
        MouseLeftButtonUp += OnPointerUp;
        MouseRightButtonUp += OnPointerUp;
        MouseMove += OnPointerMove;
        MouseWheel += OnWheel;
        LostMouseCapture += (_, _) => EndInteraction();
        ResetCamera();
    }

    internal void SetMeshes(IReadOnlyList<ModelPreviewMesh> meshes,
        IReadOnlyList<ModelPreviewMesh> orientationMeshes,
        Func<ModelPreviewMesh, Material>? resolveMaterial = null,
        bool resetCamera = true)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(orientationMeshes);
        var group = new Model3DGroup();
        var fallback = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(184, 193, 202)));
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;
        var count = 0;
        foreach (var mesh in meshes)
        {
            if (mesh.Positions.Length == 0 || mesh.Positions.Length % 3 != 0
                || mesh.TriangleIndices.Length == 0 || mesh.TriangleIndices.Length % 3 != 0
                || mesh.TriangleIndices.Any(index => index < 0
                    || index >= mesh.Positions.Length / 3))
                continue;
            var geometry = new MeshGeometry3D();
            for (var index = 0; index < mesh.Positions.Length; index += 3)
            {
                var x = mesh.Positions[index];
                var y = mesh.Positions[index + 1];
                var z = mesh.Positions[index + 2];
                geometry.Positions.Add(new Point3D(x, y, z));
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                minZ = Math.Min(minZ, z);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                maxZ = Math.Max(maxZ, z);
            }
            if (mesh.Normals?.Length == mesh.Positions.Length)
                for (var index = 0; index < mesh.Normals.Length; index += 3)
                    geometry.Normals.Add(new Vector3D(mesh.Normals[index],
                        mesh.Normals[index + 1], mesh.Normals[index + 2]));
            if (mesh.TextureCoordinates?.Length == mesh.VertexCount * 2)
                for (var index = 0; index < mesh.TextureCoordinates.Length; index += 2)
                    geometry.TextureCoordinates.Add(new Point(mesh.TextureCoordinates[index],
                        mesh.TextureCoordinates[index + 1]));
            foreach (var triangleIndex in mesh.TriangleIndices)
                geometry.TriangleIndices.Add(triangleIndex);
            var material = resolveMaterial?.Invoke(mesh) ?? fallback;
            group.Children.Add(new GeometryModel3D(geometry, material) { BackMaterial = material });
            count++;
        }

        DisplayedMeshCount = count;
        if (count == 0)
        {
            Clear();
            return;
        }

        var center = new Vector3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        var rotation = ModelPreviewCharacterOrientation.GetRequiredRotation(orientationMeshes);
        group.Transform = CreatePresentationTransform(center, rotation);
        _model.Content = group;
        var radius = Math.Sqrt(Math.Pow(maxX - minX, 2) + Math.Pow(maxY - minY, 2)
            + Math.Pow(maxZ - minZ, 2)) / 2;
        _suggestedDistance = Math.Max(Math.Max(radius, 0.5) * 3, 1);
        _frontYaw = ModelPreviewCharacterOrientation.GetSuggestedFrontYaw(rotation);
        if (resetCamera)
            ResetCamera();
        _surface.SetScene(meshes, center, rotation, group.Bounds, _camera, resolveMaterial);
    }

    internal void Clear()
    {
        _model.Content = null;
        _surface.SetScene([], default, ModelPreviewPresentationRotation.None,
            Rect3D.Empty, _camera);
        DisplayedMeshCount = 0;
    }

    internal void ApplyPose(IReadOnlyDictionary<ModelPreviewMesh, float[]>? positions) =>
        _surface.SetPositionOverrides(positions);

    internal void ResetCamera()
    {
        _target = new Point3D();
        _yaw = _frontYaw;
        _pitch = 0;
        _distance = _suggestedDistance;
        UpdateCamera();
    }

    internal void FrontView() => SetCamera(_frontYaw, 0);
    internal void SideView() => SetCamera(_frontYaw + 90, 0);
    internal void TopView() => SetCamera(_frontYaw, 90);

    private void SetCamera(double yaw, double pitch)
    {
        _yaw = yaw;
        _pitch = pitch;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        var yaw = _yaw * Math.PI / 180;
        var pitch = _pitch * Math.PI / 180;
        var forward = new Vector3D(-Math.Cos(pitch) * Math.Cos(yaw),
            -Math.Sin(pitch), -Math.Cos(pitch) * Math.Sin(yaw));
        forward.Normalize();
        var right = new Vector3D(Math.Sin(yaw), 0, -Math.Cos(yaw));
        var up = Vector3D.CrossProduct(right, forward);
        up.Normalize();
        _camera.Position = _target - forward * _distance;
        _camera.LookDirection = forward * _distance;
        _camera.UpDirection = up;
        _camera.NearPlaneDistance = Math.Max(_distance / 10000, 0.001);
        _camera.FarPlaneDistance = Math.Max(_distance * 100, 1000);
        _surface.Redraw();
    }

    private void OnRotateStart(object? sender, MouseButtonEventArgs args)
    {
        _rotating = true;
        _lastPointer = args.GetPosition(this);
        CaptureMouse();
        args.Handled = true;
    }

    private void OnPanStart(object? sender, MouseButtonEventArgs args)
    {
        _panning = true;
        _lastPointer = args.GetPosition(this);
        CaptureMouse();
        args.Handled = true;
    }

    private void OnPointerUp(object? sender, MouseButtonEventArgs args)
    {
        EndInteraction();
        args.Handled = true;
    }

    private void OnPointerMove(object? sender, MouseEventArgs args)
    {
        if ((!_rotating || args.LeftButton != MouseButtonState.Pressed)
            && (!_panning || args.RightButton != MouseButtonState.Pressed))
        {
            if (_rotating || _panning)
                EndInteraction();
            return;
        }
        var point = args.GetPosition(this);
        ApplyPointerDelta(point.X - _lastPointer.X, point.Y - _lastPointer.Y, _panning);
        _lastPointer = point;
        args.Handled = true;
    }

    private void ApplyPointerDelta(double deltaX, double deltaY, bool pan)
    {
        if (!pan)
        {
            _yaw += deltaX * 0.45;
            _pitch -= deltaY * 0.45;
        }
        else
        {
            var yaw = _yaw * Math.PI / 180;
            var pitch = _pitch * Math.PI / 180;
            var right = new Vector3D(Math.Sin(yaw), 0, -Math.Cos(yaw));
            var forward = new Vector3D(-Math.Cos(pitch) * Math.Cos(yaw),
                -Math.Sin(pitch), -Math.Cos(pitch) * Math.Sin(yaw));
            var up = Vector3D.CrossProduct(right, forward);
            up.Normalize();
            var viewportHeight = 2 * _distance * Math.Tan(_camera.FieldOfView * Math.PI / 360);
            var unitsPerPixel = viewportHeight / Math.Max(ActualHeight, 1);
            _target -= right * (deltaX * unitsPerPixel);
            _target += up * (deltaY * unitsPerPixel);
        }
        UpdateCamera();
    }

    private void OnWheel(object? sender, MouseWheelEventArgs args)
    {
        ApplyWheel(args.Delta);
        args.Handled = true;
    }

    private void ApplyWheel(int delta)
    {
        _distance = Math.Clamp(_distance * (delta > 0 ? 0.85 : 1.18), 0.05, 100000);
        UpdateCamera();
    }

    private void EndInteraction()
    {
        _rotating = _panning = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    private static Transform3D CreatePresentationTransform(Vector3D center,
        ModelPreviewPresentationRotation rotation)
    {
        if (rotation == ModelPreviewPresentationRotation.None)
            return new TranslateTransform3D(-center.X, -center.Y, -center.Z);
        var (axis, angle) = rotation switch
        {
            ModelPreviewPresentationRotation.PositiveXToPositiveY => (new Vector3D(0, 0, 1), 90d),
            ModelPreviewPresentationRotation.NegativeXToPositiveY => (new Vector3D(0, 0, 1), -90d),
            ModelPreviewPresentationRotation.PositiveZToPositiveY => (new Vector3D(1, 0, 0), -90d),
            ModelPreviewPresentationRotation.NegativeZToPositiveY => (new Vector3D(1, 0, 0), 90d),
            _ => throw new ArgumentOutOfRangeException(nameof(rotation), rotation, null),
        };
        var transform = new Transform3DGroup();
        transform.Children.Add(new TranslateTransform3D(-center.X, -center.Y, -center.Z));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(axis, angle)));
        return transform;
    }

    private static Model3DGroup? CreateGroundGrid(Rect3D bounds)
    {
        if (bounds.IsEmpty || !double.IsFinite(bounds.SizeX) || !double.IsFinite(bounds.SizeZ))
            return null;
        var span = Math.Max(Math.Max(bounds.SizeX, bounds.SizeZ), 1);
        var target = span / 6;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(target, 0.001))));
        var normalized = target / magnitude;
        var step = (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10)
            * magnitude;
        var halfLines = Math.Clamp((int)Math.Ceiling(span * 0.8 / step), 4, 20);
        var halfExtent = step * halfLines;
        var centerX = bounds.X + bounds.SizeX / 2;
        var centerZ = bounds.Z + bounds.SizeZ / 2;
        var floorY = bounds.Y - Math.Max(step * 0.01, 0.002);
        var minor = new MeshGeometry3D();
        var xAxis = new MeshGeometry3D();
        var zAxis = new MeshGeometry3D();
        var thickness = Math.Max(step * 0.012, 0.002);
        for (var index = -halfLines; index <= halfLines; index++)
        {
            var offset = index * step;
            AppendLine(index == 0 ? xAxis : minor,
                new Point3D(centerX - halfExtent, floorY, centerZ + offset),
                new Point3D(centerX + halfExtent, floorY, centerZ + offset), thickness);
            AppendLine(index == 0 ? zAxis : minor,
                new Point3D(centerX + offset, floorY, centerZ - halfExtent),
                new Point3D(centerX + offset, floorY, centerZ + halfExtent), thickness);
        }
        var group = new Model3DGroup();
        AddGridModel(group, minor, Color.FromArgb(100, 126, 145, 170));
        AddGridModel(group, xAxis, Color.FromArgb(205, 224, 108, 117));
        AddGridModel(group, zAxis, Color.FromArgb(205, 124, 169, 255));
        return group;
    }

    private static void AppendLine(MeshGeometry3D geometry, Point3D start, Point3D end,
        double thickness)
    {
        var perpendicular = Math.Abs(end.X - start.X) >= Math.Abs(end.Z - start.Z)
            ? new Vector3D(0, 0, thickness / 2)
            : new Vector3D(thickness / 2, 0, 0);
        var first = geometry.Positions.Count;
        geometry.Positions.Add(start - perpendicular);
        geometry.Positions.Add(start + perpendicular);
        geometry.Positions.Add(end + perpendicular);
        geometry.Positions.Add(end - perpendicular);
        foreach (var index in new[] { 0, 1, 2, 0, 2, 3 })
            geometry.TriangleIndices.Add(first + index);
    }

    private static void AddGridModel(Model3DGroup group, MeshGeometry3D geometry, Color color)
    {
        var material = new DiffuseMaterial(new SolidColorBrush(color));
        group.Children.Add(new GeometryModel3D(geometry, material) { BackMaterial = material });
    }

    public void Dispose()
    {
        EndInteraction();
        Clear();
        _surface.Dispose();
    }
}
