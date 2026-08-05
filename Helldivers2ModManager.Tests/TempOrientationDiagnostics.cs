using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class TempOrientationDiagnostics
{
    [TestMethod]
    public async Task DumpOrientation()
    {
        var root = FindRepositoryRoot();
        var modsRoot = new DirectoryInfo(Path.Combine(root.FullName, "Test", "Mods", "Mods"));
        foreach (var modDirectory in modsRoot.EnumerateDirectories())
        {
            var patches = modDirectory.EnumerateFiles("*.patch_*", SearchOption.AllDirectories)
                .Where(f => !f.Name.Contains(".gpu_resources") && !f.Name.Contains(".stream"))
                .OrderBy(f => f.FullName)
                .ToArray();
            if (patches.Length == 0)
                continue;
            try
            {
                var result = await new PatchResourceInspectionService().PreviewModelAsync(modDirectory, patches);
                var selection = ModelPreviewMeshSelector.Select(result.Meshes);
                var visible = selection.VisibleMeshes.Where(m => m.RenderStatus == ModelPreviewMeshRenderStatus.Visible).ToArray();
                if (visible.Length == 0)
                {
                    Console.WriteLine($"== {modDirectory.Name}: NO VISIBLE (meshes={result.Meshes.Count})");
                    continue;
                }

                var torso = Centroids(visible, ModelPreviewCustomizationSlot.Torso);
                var legs = Centroids(visible, new HashSet<ModelPreviewCustomizationSlot> { ModelPreviewCustomizationSlot.LeftLeg, ModelPreviewCustomizationSlot.RightLeg });
                var rotation = ModelPreviewCharacterOrientation.GetRequiredRotation(visible);

                Console.WriteLine($"== {modDirectory.Name}");
                Console.WriteLine($"   visible={visible.Length} meshes={result.Meshes.Count} skipped={result.SkippedStreams} err={result.Error}");
                Console.WriteLine($"   rotation={rotation} torso={Fmt(torso)} legs={Fmt(legs)}");

                foreach (var slotGroup in visible.GroupBy(m => m.CustomizationSlot).OrderBy(g => g.Key))
                {
                    var (minX, minY, minZ, maxX, maxY, maxZ, cx, cy, cz) = Bounds(slotGroup.ToArray());
                    Console.WriteLine($"   slot={slotGroup.Key,12} count={slotGroup.Count(),3} X=[{minX,7:F2},{maxX,7:F2}] Y=[{minY,7:F2},{maxY,7:F2}] Z=[{minZ,7:F2},{maxZ,7:F2}] center=({cx,6:F2},{cy,6:F2},{cz,6:F2})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"== {modDirectory.Name}: EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static (double X, double Y, double Z)? Centroids(IReadOnlyList<ModelPreviewMesh> meshes, ModelPreviewCustomizationSlot slot)
        => Centroids(meshes, new HashSet<ModelPreviewCustomizationSlot> { slot });

    private static (double X, double Y, double Z)? Centroids(IReadOnlyList<ModelPreviewMesh> meshes, IReadOnlySet<ModelPreviewCustomizationSlot>? slots)
    {
        var count = 0L;
        double x = 0, y = 0, z = 0;
        foreach (var mesh in meshes)
        {
            if (slots is not null && !slots.Contains(mesh.CustomizationSlot))
                continue;
            for (var i = 0; i < mesh.Positions.Length; i += 3)
            {
                x += mesh.Positions[i]; y += mesh.Positions[i + 1]; z += mesh.Positions[i + 2];
                count++;
            }
        }
        return count == 0 ? null : (x / count, y / count, z / count);
    }

    private static (double, double, double, double, double, double, double, double, double) Bounds(IReadOnlyList<ModelPreviewMesh> meshes)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        var count = 0L; double cx = 0, cy = 0, cz = 0;
        foreach (var mesh in meshes)
            for (var i = 0; i < mesh.Positions.Length; i += 3)
            {
                var x = mesh.Positions[i]; var y = mesh.Positions[i + 1]; var z = mesh.Positions[i + 2];
                minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
                cx += x; cy += y; cz += z; count++;
            }
        return (minX, minY, minZ, maxX, maxY, maxZ, cx / count, cy / count, cz / count);
    }

    private static string Fmt((double X, double Y, double Z)? v) => v is null ? "-" : $"({v.Value.X:F2},{v.Value.Y:F2},{v.Value.Z:F2})";

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (DirectoryInfo? current = new(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return current;
        throw new DirectoryNotFoundException("repo root");
    }
}
