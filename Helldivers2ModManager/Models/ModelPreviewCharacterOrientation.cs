namespace Helldivers2ModManager.Models;

/// <summary>
/// Determines whether a decoded customization model needs a presentation-only rotation
/// for WPF's Y-up viewport. Resource coordinates are left untouched.
/// </summary>
internal static class ModelPreviewCharacterOrientation
{
    private const double DominanceRatio = 1.5;
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

        if (!TryGetCentroid(meshes, TorsoSlots, out var torso) ||
            !TryGetCentroid(meshes, LegSlots, out var legs))
            return ModelPreviewPresentationRotation.None;

        var upX = torso.X - legs.X;
        var upY = torso.Y - legs.Y;
        var upZ = torso.Z - legs.Z;
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

    private static bool TryGetCentroid(
        IReadOnlyList<ModelPreviewMesh> meshes,
        IReadOnlySet<ModelPreviewCustomizationSlot> slots,
        out (double X, double Y, double Z) centroid)
    {
        var x = 0d;
        var y = 0d;
        var z = 0d;
        var pointCount = 0L;
        foreach (var mesh in meshes)
        {
            if (!slots.Contains(mesh.CustomizationSlot))
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
}

internal enum ModelPreviewPresentationRotation
{
    None,
    PositiveXToPositiveY,
    NegativeXToPositiveY,
    PositiveZToPositiveY,
    NegativeZToPositiveY
}
