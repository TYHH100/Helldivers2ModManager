using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class TempPreviewRenderer
{
    [TestMethod]
    public async Task RenderAngles()
    {
        var root = FindRepositoryRoot();
        var outDir = Path.Combine(Path.GetTempPath(), "hd2preview");
        Directory.CreateDirectory(outDir);

        var cases = new[]
        {
            ("Plum", Path.Combine(root.FullName, "Test", "Mods", "Mods", "【学園制服】Plum 替换 CW-9+CE-27+I-92")),
            ("VrcTell", Path.Combine(root.FullName, "Test", "Mods", "Mods", "715 VRC_Tell 替换RE-1861 肩章轻甲_1a9657a3")),
            ("Mizuki", Path.Combine(root.FullName, "Test", "Mods", "Mods", "VRC_瑞希 寄染赛车服 替换 CM-10全套 + EX00全套 +CM17头+无畏头_02508ace", "无尾巴"))
        };

        foreach (var (name, modPath) in cases)
        {
            var dir = new DirectoryInfo(modPath);
            var patches = dir.EnumerateFiles("*.patch_*", SearchOption.AllDirectories)
                .Where(f => !f.Name.Contains(".gpu_resources") && !f.Name.Contains(".stream"))
                .OrderBy(f => f.FullName)
                .ToArray();
            var result = await new PatchResourceInspectionService().PreviewModelAsync(dir, patches);
            var visible = ModelPreviewMeshSelector.Select(result.Meshes).VisibleMeshes
                .Where(m => m.RenderStatus == ModelPreviewMeshRenderStatus.Visible)
                .ToArray();
            Console.WriteLine($"{name}: visible={visible.Length} rotation={ModelPreviewCharacterOrientation.GetRequiredRotation(visible)}");

            // Render raw (no presentation transform) with default camera to confirm "lying flat".
            Render(name, "raw_default", visible, rotation: ModelPreviewPresentationRotation.None, yaw: 35, pitch: 15, outDir);

            // Render with the app's presentation transform from several yaw angles.
            foreach (var (yawName, yaw) in new[] { ("y0", 0d), ("y35", 35d), ("y90", 90d), ("yn90", -90d), ("y180", 180d) })
            {
                Render(name, yawName, visible, rotation: ModelPreviewCharacterOrientation.GetRequiredRotation(visible), yaw, 15, outDir);
            }
        }
        Console.WriteLine($"Rendered to {outDir}");
    }

    private static void Render(
        string name,
        string tag,
        IReadOnlyList<ModelPreviewMesh> meshes,
        ModelPreviewPresentationRotation rotation,
        double yaw,
        double pitch,
        string outDir)
    {
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var group = BuildGroup(meshes, rotation);
                var camera = CreateCamera(yaw, pitch, distance: 8);
                var viewport = new Viewport3D
                {
                    Width = 500,
                    Height = 700,
                    Camera = camera,
                    ClipToBounds = true
                };
                viewport.Children.Add(new ModelVisual3D { Content = group });
                var lights = new Model3DGroup();
                lights.Children.Add(new AmbientLight(Color.FromRgb(90, 90, 90)));
                lights.Children.Add(new DirectionalLight(Color.FromRgb(255, 255, 255), new Vector3D(-1, -1, -2)));
                lights.Children.Add(new DirectionalLight(Color.FromRgb(120, 120, 120), new Vector3D(1, 0, 1)));
                viewport.Children.Add(new ModelVisual3D { Content = lights });

                viewport.Measure(new Size(500, 700));
                viewport.Arrange(new Rect(0, 0, 500, 700));
                viewport.UpdateLayout();

                var rtb = new RenderTargetBitmap(500, 700, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(viewport);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using var stream = File.Create(Path.Combine(outDir, $"{name}_{tag}.png"));
                encoder.Save(stream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Render {name}_{tag} failed: {ex}");
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));
    }

    private static PerspectiveCamera CreateCamera(double yawDeg, double pitchDeg, double distance)
    {
        var yaw = yawDeg * Math.PI / 180;
        var pitch = pitchDeg * Math.PI / 180;
        var horizontal = distance * Math.Cos(pitch);
        var position = new Point3D(
            horizontal * Math.Cos(yaw),
            distance * Math.Sin(pitch),
            horizontal * Math.Sin(yaw));
        var camera = new PerspectiveCamera
        {
            FieldOfView = 45,
            Position = position,
            LookDirection = new Vector3D(-position.X, -position.Y, -position.Z),
            UpDirection = new Vector3D(0, 1, 0),
            NearPlaneDistance = 0.01,
            FarPlaneDistance = 10000
        };
        return camera;
    }

    private static Model3DGroup BuildGroup(IReadOnlyList<ModelPreviewMesh> meshes, ModelPreviewPresentationRotation rotation)
    {
        var group = new Model3DGroup();
        var brush = new SolidColorBrush(Color.FromRgb(200, 200, 205));
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();

        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var source in meshes)
        {
            var geometry = CreateGeometry(source);
            foreach (var p in geometry.Positions)
            {
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); minZ = Math.Min(minZ, p.Z);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); maxZ = Math.Max(maxZ, p.Z);
            }
            var model = new GeometryModel3D(geometry, material) { BackMaterial = material };
            model.Freeze();
            group.Children.Add(model);
        }

        var center = new Vector3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        group.Transform = ModelPreviewPageViewModel.CreatePresentationTransform(center, rotation);
        group.Transform.Freeze();
        group.Freeze();
        return group;
    }

    private static MeshGeometry3D CreateGeometry(ModelPreviewMesh source)
    {
        var hasNormals = source.Normals is { Length: > 0 } && source.Normals.Length == source.Positions.Length;
        var geometry = new MeshGeometry3D
        {
            Positions = new Point3DCollection(source.VertexCount),
            Normals = new Vector3DCollection(hasNormals ? source.VertexCount : 0),
            TextureCoordinates = new PointCollection(0),
            TriangleIndices = new Int32Collection(source.TriangleIndices.Length)
        };
        for (var i = 0; i < source.Positions.Length; i += 3)
            geometry.Positions.Add(new Point3D(source.Positions[i], source.Positions[i + 1], source.Positions[i + 2]));
        if (hasNormals)
            for (var i = 0; i < source.Normals!.Length; i += 3)
                geometry.Normals.Add(new Vector3D(source.Normals[i], source.Normals[i + 1], source.Normals[i + 2]));
        foreach (var index in source.TriangleIndices)
            geometry.TriangleIndices.Add(index);
        geometry.Freeze();
        return geometry;
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (DirectoryInfo? current = new(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return current;
        throw new DirectoryNotFoundException("repo root");
    }
}
