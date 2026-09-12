using System.IO;
using System.Text;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// 临时全库诊断（回应用户"测试不全面"）：对存储库全部模组输出朝向判定输入
/// （slot 可信度 / 逐轴标准差 / bounds / 蒙皮覆盖）与 GetRequiredRotation 结果，
/// 供逐类审查"会被旋转的模组"是否都是人物、有无误旋转。
/// </summary>
[TestClass]
public sealed class FullLibraryOrientationScan
{
    [TestMethod]
    public async Task Scan_EntireLibrary()
    {
        var root = FindRepositoryRoot();
        var modsRoot = Path.Combine(root.FullName, "Test", "Mods", "Mods");
        var service = new PatchResourceInspectionService();
        var report = new StringBuilder();

        foreach (var modPath in Directory.EnumerateDirectories(modsRoot).OrderBy(static p => p))
        {
            var modDirectory = new DirectoryInfo(modPath);
            var patchFiles = modDirectory
                .GetFiles("*", SearchOption.AllDirectories)
                .Where(static f => f.Name.Contains(".patch_", StringComparison.OrdinalIgnoreCase) &&
                                   !f.Name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase) &&
                                   !f.Name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (patchFiles.Length == 0)
                continue;

            try
            {
                var preview = await service.PreviewModelAsync(modDirectory, patchFiles);
                if (preview.Meshes.Count == 0)
                {
                    report.AppendLine($"{modDirectory.Name} | NO-MESHES");
                    continue;
                }

                var rotation = ModelPreviewCharacterOrientation.GetRequiredRotation(preview.Meshes);
                var stats = CollectAxisStats(preview.Meshes);
                var skinned = preview.Meshes.Count(static m => m.Skinning is not null);
                var torsoPoints = preview.Meshes
                    .Where(static m => m.CustomizationSlot == ModelPreviewCustomizationSlot.Torso)
                    .Sum(static m => m.Positions.Length / 3);
                var totalPoints = preview.Meshes.Sum(static m => m.Positions.Length / 3);
                report.AppendLine(
                    $"{modDirectory.Name} | rot={rotation} | meshes={preview.Meshes.Count} skinned={skinned} | " +
                    $"std=({stats.std.stdX:0.00},{stats.std.stdY:0.00},{stats.std.stdZ:0.00}) | " +
                    $"span=({stats.span.x:0.00},{stats.span.y:0.00},{stats.span.z:0.00}) | " +
                    $"torsoPts={torsoPoints}/{totalPoints} | err={preview.Error ?? "none"}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"{modDirectory.Name} | EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }
        }

        File.WriteAllText(@"D:\TYHH10-git\Helldivers2ModManager\.workbuddy\tmp\library_orientation_scan.txt", report.ToString());
        Console.WriteLine(report.ToString());
        Assert.Inconclusive("全库朝向扫描完成，报告已写入 .workbuddy/tmp/library_orientation_scan.txt（人工审查各模组旋转是否合理）");
    }

    private static ((double x, double y, double z) span, (double stdX, double stdY, double stdZ) std) CollectAxisStats(
        IReadOnlyList<ModelPreviewMesh> meshes)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        double sumX = 0, sumY = 0, sumZ = 0, sumX2 = 0, sumY2 = 0, sumZ2 = 0;
        long count = 0;
        foreach (var mesh in meshes)
        {
            for (var i = 0; i + 2 < mesh.Positions.Length; i += 3)
            {
                var x = mesh.Positions[i];
                var y = mesh.Positions[i + 1];
                var z = mesh.Positions[i + 2];
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
                sumX += x; sumY += y; sumZ += z;
                sumX2 += x * x; sumY2 += y * y; sumZ2 += z * z;
                count++;
            }
        }

        if (count == 0)
            return ((0, 0, 0), (0, 0, 0));
        var meanX = sumX / count; var meanY = sumY / count; var meanZ = sumZ / count;
        return (
            (maxX - minX, maxY - minY, maxZ - minZ),
            (Math.Sqrt(Math.Max(0, sumX2 / count - meanX * meanX)),
             Math.Sqrt(Math.Max(0, sumY2 / count - meanY * meanY)),
             Math.Sqrt(Math.Max(0, sumZ2 / count - meanZ * meanZ))));
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Helldivers2ModManager.sln")))
            directory = directory.Parent;
        Assert.IsNotNull(directory, "Repository root not found.");
        return directory;
    }
}
