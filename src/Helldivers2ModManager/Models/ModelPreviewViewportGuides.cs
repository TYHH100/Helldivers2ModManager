using System.Windows.Media.Media3D;

namespace Helldivers2ModManager.Models;

/// <summary>
/// Pure viewport-guide calculations shared by the WPF preview surface and its tests.
/// Geometry construction stays in the View, while this type keeps camera direction and
/// grid sizing deterministic and independent of the current device size.
/// </summary>
internal static class ModelPreviewViewportGuides
{
    private const double TopBottomPitchThreshold = 55d;

    public static ModelPreviewCameraDirection GetCameraDirection(
        double cameraYaw,
        double frontYaw,
        double cameraPitch)
    {
        var pitchRadians = cameraPitch * Math.PI / 180d;
        var vertical = Math.Sin(pitchRadians);
        var horizontal = Math.Abs(Math.Cos(pitchRadians));
        if (vertical >= Math.Sin(TopBottomPitchThreshold * Math.PI / 180d) && vertical > horizontal)
            return ModelPreviewCameraDirection.Top;
        if (vertical <= -Math.Sin(TopBottomPitchThreshold * Math.PI / 180d) && -vertical > horizontal)
            return ModelPreviewCameraDirection.Bottom;

        var quarterTurns = (int)Math.Round(
            NormalizeDegrees(cameraYaw - frontYaw + (Math.Cos(pitchRadians) < 0d ? 180d : 0d)) / 90d,
            MidpointRounding.AwayFromZero);
        return (((quarterTurns % 4) + 4) % 4) switch
        {
            0 => ModelPreviewCameraDirection.Front,
            1 => ModelPreviewCameraDirection.Right,
            2 => ModelPreviewCameraDirection.Back,
            _ => ModelPreviewCameraDirection.Left
        };
    }

    /// <summary>
    /// Creates a continuous view basis for unrestricted orbit rotation. Right is derived
    /// from yaw instead of a cross product against global Y, so passing through the
    /// poles does not create a zero vector or a sudden pan-direction failure.
    /// </summary>
    public static ModelPreviewCameraBasis GetCameraBasis(double cameraYaw, double cameraPitch)
    {
        var yaw = cameraYaw * Math.PI / 180d;
        var pitch = cameraPitch * Math.PI / 180d;
        var forward = new Vector3D(
            -Math.Cos(pitch) * Math.Cos(yaw),
            -Math.Sin(pitch),
            -Math.Cos(pitch) * Math.Sin(yaw));
        forward.Normalize();
        var right = new Vector3D(Math.Sin(yaw), 0, -Math.Cos(yaw));
        var up = Vector3D.CrossProduct(right, forward);
        up.Normalize();
        return new ModelPreviewCameraBasis(forward, right, up);
    }

    /// <summary>
    /// Projects the positive world axes into the camera plane for a Blender-style
    /// viewport gizmo. Screen Y is intentionally inverted for WPF Canvas coordinates.
    /// </summary>
    public static ModelPreviewAxisGizmo GetAxisGizmo(double cameraYaw, double cameraPitch)
    {
        var basis = GetCameraBasis(cameraYaw, cameraPitch);

        return new ModelPreviewAxisGizmo(
            ProjectAxis(new Vector3D(1, 0, 0), basis.Right, basis.Up, basis.Forward),
            ProjectAxis(new Vector3D(0, 1, 0), basis.Right, basis.Up, basis.Forward),
            // Character-facing presentation uses -Z. Project the same direction that
            // the Z click enters, so the visible blue pointer and its action agree.
            ProjectAxis(new Vector3D(0, 0, -1), basis.Right, basis.Up, basis.Forward));
    }

    public static ModelPreviewCameraPose GetAxisView(ModelPreviewGizmoAxis axis, bool opposite) => axis switch
    {
        ModelPreviewGizmoAxis.X => new ModelPreviewCameraPose(opposite ? 180d : 0d, 0d),
        ModelPreviewGizmoAxis.Y => new ModelPreviewCameraPose(0d, opposite ? -90d : 90d),
        // The character presentation transform uses -Z as the forward-facing view.
        // Keep the positive Z gizmo click aligned with the visible front, not its back.
        ModelPreviewGizmoAxis.Z => new ModelPreviewCameraPose(opposite ? 90d : -90d, 0d),
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, null)
    };

    public static ModelPreviewGroundGridLayout CreateGroundGridLayout(Rect3D bounds)
    {
        if (bounds.IsEmpty || !IsFinite(bounds.X) || !IsFinite(bounds.Y) ||
            !IsFinite(bounds.Z) || !IsFinite(bounds.SizeX) ||
            !IsFinite(bounds.SizeY) || !IsFinite(bounds.SizeZ))
            return ModelPreviewGroundGridLayout.Empty;

        var span = Math.Max(Math.Max(bounds.SizeX, bounds.SizeZ), 1d);
        var cellSize = GetNiceGridStep(span / 6d);
        var halfLineCount = Math.Clamp((int)Math.Ceiling(span * 0.8d / cellSize), 4, 20);
        return new ModelPreviewGroundGridLayout(
            bounds.X + bounds.SizeX / 2d,
            bounds.Z + bounds.SizeZ / 2d,
            bounds.Y - Math.Max(cellSize * 0.01d, 0.002d),
            cellSize,
            halfLineCount);
    }

    private static double NormalizeDegrees(double degrees)
    {
        var normalized = degrees % 360d;
        if (normalized <= -180d)
            normalized += 360d;
        else if (normalized > 180d)
            normalized -= 360d;
        return normalized;
    }

    private static ModelPreviewAxisProjection ProjectAxis(
        Vector3D axis,
        Vector3D cameraRight,
        Vector3D cameraUp,
        Vector3D cameraForward) => new(
        Vector3D.DotProduct(axis, cameraRight),
        -Vector3D.DotProduct(axis, cameraUp),
        Vector3D.DotProduct(axis, -cameraForward));

    private static double GetNiceGridStep(double target)
    {
        var magnitude = Math.Pow(10d, Math.Floor(Math.Log10(Math.Max(target, 0.001d))));
        var normalized = target / magnitude;
        var multiplier = normalized <= 1d ? 1d : normalized <= 2d ? 2d : normalized <= 5d ? 5d : 10d;
        return multiplier * magnitude;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

internal enum ModelPreviewCameraDirection
{
    Front,
    Right,
    Back,
    Left,
    Top,
    Bottom
}

internal readonly record struct ModelPreviewAxisProjection(double ScreenX, double ScreenY, double Depth)
{
    public double ScreenLength => Math.Sqrt(ScreenX * ScreenX + ScreenY * ScreenY);
}

internal readonly record struct ModelPreviewAxisGizmo(
    ModelPreviewAxisProjection X,
    ModelPreviewAxisProjection Y,
    ModelPreviewAxisProjection Z);

internal enum ModelPreviewGizmoAxis
{
    X,
    Y,
    Z
}

internal readonly record struct ModelPreviewCameraPose(double Yaw, double Pitch);

internal readonly record struct ModelPreviewCameraBasis(
    Vector3D Forward,
    Vector3D Right,
    Vector3D Up);

internal readonly record struct ModelPreviewGroundGridLayout(
    double CenterX,
    double CenterZ,
    double FloorY,
    double CellSize,
    int HalfLineCount)
{
    public static ModelPreviewGroundGridLayout Empty { get; } = new(0, 0, 0, 0, 0);
    public bool HasGrid => CellSize > 0 && HalfLineCount > 0;
    public double HalfExtent => CellSize * HalfLineCount;
}
