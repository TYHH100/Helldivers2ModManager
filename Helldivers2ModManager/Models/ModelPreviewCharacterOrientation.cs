namespace Helldivers2ModManager.Models;

/// <summary>
/// Determines whether a decoded customization model needs a presentation-only rotation
/// for WPF's Y-up viewport. Resource coordinates are left untouched.
/// </summary>
internal static class ModelPreviewCharacterOrientation
{
    private const double DominanceRatio = 1.5;
    private const double UnlabeledCharacterDominanceRatio = 1.25;
    private static readonly IReadOnlySet<ModelPreviewCustomizationSlot> TorsoSlots =
        new HashSet<ModelPreviewCustomizationSlot> { ModelPreviewCustomizationSlot.Torso };
    private static readonly IReadOnlySet<ModelPreviewCustomizationSlot> LegSlots =
        new HashSet<ModelPreviewCustomizationSlot>
        {
            ModelPreviewCustomizationSlot.LeftLeg,
            ModelPreviewCustomizationSlot.RightLeg
        };

    public static ModelPreviewPresentationRotation GetRequiredRotation(IReadOnlyList<ModelPreviewMesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);

        if (!TryGetCentroid(meshes, TorsoSlots, out var torso))
            return GetUnlabeledCharacterRotation(meshes);

        if (TryGetCentroid(meshes, LegSlots, out var legs))
            return GetRotation(torso, legs);

        // Some full-body replacements label only the torso Unit. For those, use the
        // remaining body geometry as a conservative lower-body proxy, but require
        // enough independent meshes and vertices to avoid rotating a torso prop.
        var unlabeledBodyMeshes = meshes
            .Where(mesh => !TorsoSlots.Contains(mesh.CustomizationSlot) && mesh.Positions.Length >= 3)
            .ToArray();
        var torsoPointCount = meshes
            .Where(mesh => TorsoSlots.Contains(mesh.CustomizationSlot))
            .Sum(mesh => mesh.Positions.Length / 3);
        var unlabeledPointCount = unlabeledBodyMeshes.Sum(mesh => mesh.Positions.Length / 3);
        if (unlabeledBodyMeshes.Length < 3 || unlabeledPointCount < torsoPointCount ||
            !TryGetCentroid(unlabeledBodyMeshes, null, out var unlabeledBody))
            return ModelPreviewPresentationRotation.None;

        // Unlike explicitly labeled legs, this remainder also includes the head and
        // hair. Its centroid points toward the source body's upper end for the
        // partially labeled character resources, so use the inverse presentation axis.
        return Reverse(GetRotation(torso, unlabeledBody));
    }

    /// <summary>
    /// A few full-body replacements omit customization metadata entirely. They cannot
    /// participate in the torso/leg comparison above, but their assembled geometry is
    /// still recognizably a character: several meshes with a source height axis longer
    /// than its width/depth. The source origin is normally near the feet, so the longer
    /// positive or negative extent identifies the head direction.
    /// </summary>
    private static ModelPreviewPresentationRotation GetUnlabeledCharacterRotation(
        IReadOnlyList<ModelPreviewMesh> meshes)
    {
        const int minimumMeshCount = 3;
        if (meshes.Count < minimumMeshCount || !TryGetBounds(meshes, out var bounds))
            return ModelPreviewPresentationRotation.None;

        var xSpan = bounds.MaxX - bounds.MinX;
        var ySpan = bounds.MaxY - bounds.MinY;
        var zSpan = bounds.MaxZ - bounds.MinZ;
        var horizontalAxis = xSpan >= zSpan ? xSpan : zSpan;
        var otherAxis = xSpan >= zSpan ? zSpan : xSpan;
        if (horizontalAxis <= 1 || horizontalAxis < Math.Max(ySpan, otherAxis) * UnlabeledCharacterDominanceRatio)
            return ModelPreviewPresentationRotation.None;

        if (xSpan >= zSpan)
            return Math.Abs(bounds.MaxX) > Math.Abs(bounds.MinX)
                ? ModelPreviewPresentationRotation.PositiveXToPositiveY
                : ModelPreviewPresentationRotation.NegativeXToPositiveY;

        return Math.Abs(bounds.MaxZ) > Math.Abs(bounds.MinZ)
            ? ModelPreviewPresentationRotation.PositiveZToPositiveY
            : ModelPreviewPresentationRotation.NegativeZToPositiveY;
    }

    public static double GetSuggestedFrontYaw(ModelPreviewPresentationRotation rotation) => rotation switch
    {
        ModelPreviewPresentationRotation.PositiveXToPositiveY => 180d,
        ModelPreviewPresentationRotation.NegativeXToPositiveY => 0d,
        ModelPreviewPresentationRotation.PositiveZToPositiveY => -90d,
        ModelPreviewPresentationRotation.NegativeZToPositiveY => 90d,
        _ => 0d
    };

    private static ModelPreviewPresentationRotation GetRotation(
        (double X, double Y, double Z) torso,
        (double X, double Y, double Z) lowerBody)
    {
        var upX = torso.X - lowerBody.X;
        var upY = torso.Y - lowerBody.Y;
        var upZ = torso.Z - lowerBody.Z;
        var components = new[]
        {
            (Magnitude: Math.Abs(upX), Axis: 0),
            (Magnitude: Math.Abs(upY), Axis: 1),
            (Magnitude: Math.Abs(upZ), Axis: 2)
        }.OrderByDescending(static component => component.Magnitude).ToArray();

        if (components[0].Magnitude <= 0 ||
            components[0].Magnitude < components[1].Magnitude * DominanceRatio)
            return ModelPreviewPresentationRotation.None;

        return components[0].Axis switch
        {
            0 when upX > 0 => ModelPreviewPresentationRotation.PositiveXToPositiveY,
            0 => ModelPreviewPresentationRotation.NegativeXToPositiveY,
            2 when upZ > 0 => ModelPreviewPresentationRotation.PositiveZToPositiveY,
            2 => ModelPreviewPresentationRotation.NegativeZToPositiveY,
            _ => ModelPreviewPresentationRotation.None
        };
    }

    private static ModelPreviewPresentationRotation Reverse(ModelPreviewPresentationRotation rotation) => rotation switch
    {
        ModelPreviewPresentationRotation.PositiveXToPositiveY => ModelPreviewPresentationRotation.NegativeXToPositiveY,
        ModelPreviewPresentationRotation.NegativeXToPositiveY => ModelPreviewPresentationRotation.PositiveXToPositiveY,
        ModelPreviewPresentationRotation.PositiveZToPositiveY => ModelPreviewPresentationRotation.NegativeZToPositiveY,
        ModelPreviewPresentationRotation.NegativeZToPositiveY => ModelPreviewPresentationRotation.PositiveZToPositiveY,
        _ => ModelPreviewPresentationRotation.None
    };

    private static bool TryGetCentroid(
        IReadOnlyList<ModelPreviewMesh> meshes,
        IReadOnlySet<ModelPreviewCustomizationSlot>? slots,
        out (double X, double Y, double Z) centroid)
    {
        var x = 0d;
        var y = 0d;
        var z = 0d;
        var pointCount = 0L;
        foreach (var mesh in meshes)
        {
            if (slots is not null && !slots.Contains(mesh.CustomizationSlot))
                continue;

            for (var index = 0; index < mesh.Positions.Length; index += 3)
            {
                x += mesh.Positions[index];
                y += mesh.Positions[index + 1];
                z += mesh.Positions[index + 2];
                pointCount++;
            }
        }

        if (pointCount == 0)
        {
            centroid = default;
            return false;
        }

        centroid = (x / pointCount, y / pointCount, z / pointCount);
        return true;
    }

    private static bool TryGetBounds(
        IReadOnlyList<ModelPreviewMesh> meshes,
        out (double MinX, double MaxX, double MinY, double MaxY, double MinZ, double MaxZ) bounds)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;
        var pointCount = 0;
        foreach (var mesh in meshes)
        {
            for (var index = 0; index < mesh.Positions.Length; index += 3)
            {
                var x = mesh.Positions[index];
                var y = mesh.Positions[index + 1];
                var z = mesh.Positions[index + 2];
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                minZ = Math.Min(minZ, z);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                maxZ = Math.Max(maxZ, z);
                pointCount++;
            }
        }

        bounds = (minX, maxX, minY, maxY, minZ, maxZ);
        return pointCount > 0;
    }
}

internal enum ModelPreviewPresentationRotation
{
    None,
    PositiveXToPositiveY,
    NegativeXToPositiveY,
    PositiveZToPositiveY,
    NegativeZToPositiveY
}
