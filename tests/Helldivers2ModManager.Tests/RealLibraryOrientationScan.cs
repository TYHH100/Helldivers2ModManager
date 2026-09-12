using System.IO;
using System.Text;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// 临时全库诊断（正式库只读扫描）：对真实游戏库 G:\Temp\HD2ModManager\Mods\Mods
/// 的全部模组输出朝向判定结果与判定输入指标。全程只读（文件枚举 + FileShare.Read
/// 打开），报告写到开发仓库的 .workbuddy/tmp，不触碰用户库目录。
/// </summary>
[TestClass]
public sealed class RealLibraryOrientationScan
{
    private const string LibraryPath = @"G:\Temp\HD2ModManager\Mods\Mods";
    private const string ReportPath = @"D:\TYHH10-git\Helldivers2ModManager\.workbuddy\tmp\reallib_orientation_scan.txt";

    [TestMethod]
    public async Task Scan_RealLibrary()
    {
        Assert.IsTrue(Directory.Exists(LibraryPath), $"Library not found: {LibraryPath}");
        var service = new PatchResourceInspectionService();
        await using var report = new StreamWriter(ReportPath, append: false, Encoding.UTF8);

        foreach (var modPath in Directory.EnumerateDirectories(LibraryPath).OrderBy(static p => p))
        {
            var modDirectory = new DirectoryInfo(modPath);
            var patchFiles = modDirectory
                .GetFiles("*", SearchOption.AllDirectories)
                .Where(static f => f.Name.Contains(".patch_", StringComparison.OrdinalIgnoreCase) &&
                                   !f.Name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase) &&
                                   !f.Name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (patchFiles.Length == 0)
            {
                await report.WriteLineAsync($"{modDirectory.Name} | NO-PATCHES");
                continue;
            }

            try
            {
                var preview = await service.PreviewModelAsync(modDirectory, patchFiles);
                if (preview.Meshes.Count == 0)
                {
                    await report.WriteLineAsync($"{modDirectory.Name} | NO-MESHES");
                    continue;
                }

                var rotation = ModelPreviewCharacterOrientation.GetRequiredRotation(preview.Meshes);
                var stats = CollectAxisStats(preview.Meshes);
                var skinned = preview.Meshes.Count(static m => m.Skinning is not null);
                var slots = string.Join(",", preview.Meshes
                    .Where(static m => m.CustomizationSlot != ModelPreviewCustomizationSlot.Unknown)
                    .GroupBy(static m => m.CustomizationSlot)
                    .Select(static g => $"{g.Key}:{g.Sum(static m => m.Positions.Length / 3)}"));
                await report.WriteLineAsync(
                    $"{modDirectory.Name} | rot={rotation} | meshes={preview.Meshes.Count} skinned={skinned} | " +
                    $"std=({stats.stdX:0.00},{stats.stdY:0.00},{stats.stdZ:0.00}) | " +
                    $"span=({stats.spanX:0.00},{stats.spanY:0.00},{stats.spanZ:0.00}) | " +
                    $"slots=[{slots}] | err={preview.Error ?? "none"}");
            }
            catch (Exception ex)
            {
                await report.WriteLineAsync($"{modDirectory.Name} | EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static (double spanX, double spanY, double spanZ, double stdX, double stdY, double stdZ) CollectAxisStats(
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
            return (0, 0, 0, 0, 0, 0);
        var meanX = sumX / count; var meanY = sumY / count; var meanZ = sumZ / count;
        return (
            maxX - minX, maxY - minY, maxZ - minZ,
            Math.Sqrt(Math.Max(0, sumX2 / count - meanX * meanX)),
            Math.Sqrt(Math.Max(0, sumY2 / count - meanY * meanY)),
            Math.Sqrt(Math.Max(0, sumZ2 / count - meanZ * meanZ)));
    }
}
