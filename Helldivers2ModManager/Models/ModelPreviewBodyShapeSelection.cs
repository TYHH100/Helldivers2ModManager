namespace Helldivers2ModManager.Models;

/// <summary>
/// Applies body-shape filtering at customization-slot scope. Body-shape metadata is
/// not a global mesh switch: Any/unknown parts and unrelated slots must remain visible.
/// </summary>
internal static class ModelPreviewBodyShapeSelection
{
    public static IReadOnlySet<ModelPreviewCustomizationSlot> GetSwitchableSlots(
        IReadOnlyList<ModelPreviewMesh> meshes,
        IReadOnlyCollection<ModelPreviewMesh>? renderableMeshes = null)
    {
        ArgumentNullException.ThrowIfNull(meshes);

        var candidates = renderableMeshes ?? meshes;
        return candidates
            .Where(static mesh => mesh.CustomizationSlot != ModelPreviewCustomizationSlot.Unknown &&
                                  mesh.BodyShape is ModelPreviewBodyShape.Slim or ModelPreviewBodyShape.Stocky)
            .GroupBy(static mesh => mesh.CustomizationSlot)
            .Where(static group => group.Any(mesh => mesh.BodyShape == ModelPreviewBodyShape.Slim) &&
                                   group.Any(mesh => mesh.BodyShape == ModelPreviewBodyShape.Stocky))
            .Select(static group => group.Key)
            .ToHashSet();
    }

    public static IReadOnlyList<ModelPreviewMesh> Filter(
        IReadOnlyList<ModelPreviewMesh> meshes,
        IReadOnlyCollection<ModelPreviewMesh> renderableMeshes,
        bool showStockyBody)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(renderableMeshes);

        var switchableSlots = GetSwitchableSlots(meshes, renderableMeshes);
        if (switchableSlots.Count == 0)
            return meshes;

        var selectedShape = showStockyBody
            ? ModelPreviewBodyShape.Stocky
            : ModelPreviewBodyShape.Slim;
        var renderableSelectedSlots = renderableMeshes
            .Where(mesh => mesh.BodyShape == selectedShape && switchableSlots.Contains(mesh.CustomizationSlot))
            .Select(static mesh => mesh.CustomizationSlot)
            .ToHashSet();

        return meshes
            .Where(mesh => mesh.BodyShape is ModelPreviewBodyShape.Unknown or ModelPreviewBodyShape.Any ||
                           mesh.CustomizationSlot == ModelPreviewCustomizationSlot.Unknown ||
                           !switchableSlots.Contains(mesh.CustomizationSlot) ||
                           !renderableSelectedSlots.Contains(mesh.CustomizationSlot) ||
                           mesh.BodyShape == selectedShape)
            .ToArray();
    }
}
