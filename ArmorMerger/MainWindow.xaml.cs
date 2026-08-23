using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Windows;

namespace ArmorMerger;

public partial class MainWindow : Window
{
    private readonly StringBuilder _log = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Log(string message)
    {
        _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        LogTextBox.Text = _log.ToString();
        LogTextBox.ScrollToEnd();
    }

    private void Clear()
    {
        HelmetPatchPath.Text = string.Empty;
        ArmorPatchPath.Text = string.Empty;
        ModDirectoryPath.Text = string.Empty;
        OutputDirectoryPath.Text = string.Empty;
        OutputName.Text = "merged_armor";
        _log.Clear();
        LogTextBox.Text = string.Empty;
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => Clear();

    // --- 文件浏览 ---

    private void HelmetPatchBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择头盔 Patch 文件",
            Filter = "Patch 文件 (*.patch_*)|*.patch_*|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            HelmetPatchPath.Text = dialog.FileName;
            Log($"已选择头盔 Patch: {dialog.FileName}");
            if (string.IsNullOrEmpty(OutputDirectoryPath.Text))
                OutputDirectoryPath.Text = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
        }
    }

    private void ArmorPatchBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择护甲 Patch 文件",
            Filter = "Patch 文件 (*.patch_*)|*.patch_*|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            ArmorPatchPath.Text = dialog.FileName;
            Log($"已选择护甲 Patch: {dialog.FileName}");
            if (string.IsNullOrEmpty(OutputDirectoryPath.Text))
                OutputDirectoryPath.Text = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
        }
    }

    private void ModDirectoryBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 Mod 目录"
        };
        if (dialog.ShowDialog() == true)
        {
            ModDirectoryPath.Text = dialog.FolderName;
            Log($"已选择 Mod 目录: {dialog.FolderName}");
            AutoDetectPatchFiles(dialog.FolderName);
            if (string.IsNullOrEmpty(OutputDirectoryPath.Text))
                OutputDirectoryPath.Text = dialog.FolderName;
        }
    }

    private void OutputDirectoryBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择输出目录"
        };
        if (dialog.ShowDialog() == true)
        {
            OutputDirectoryPath.Text = dialog.FolderName;
        }
    }

    // --- 自动检测 ---

    private void AutoDetectPatchFiles(string directory)
    {
        try
        {
            var patchFiles = Directory.GetFiles(directory, "*.patch_*")
                .OrderBy(f => f)
                .ToArray();

            if (patchFiles.Length == 0)
            {
                Log("警告: 目录中未找到 .patch_* 文件");
                return;
            }

            Log($"在目录中找到 {patchFiles.Length} 个 patch 文件:");
            foreach (var file in patchFiles)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var size = new FileInfo(file).Length;
                var gpuPath = file + ".gpu_resources";
                var gpuSize = File.Exists(gpuPath) ? new FileInfo(gpuPath).Length : 0;
                Log($"  {name} (patch={size:N0}, gpu={gpuSize:N0})");
            }
        }
        catch (Exception ex)
        {
            Log($"扫描目录失败: {ex.Message}");
        }
    }

    // --- 分析 ---

    private void Analyze_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var helmetPath = HelmetPatchPath.Text;
            var armorPath = ArmorPatchPath.Text;

            if (!string.IsNullOrEmpty(helmetPath))
            {
                Log($"\n=== 分析头盔 Patch ===");
                AnalyzePatch(helmetPath);
            }

            if (!string.IsNullOrEmpty(armorPath))
            {
                Log($"\n=== 分析护甲 Patch ===");
                AnalyzePatch(armorPath);
            }

            if (string.IsNullOrEmpty(helmetPath) && string.IsNullOrEmpty(armorPath))
            {
                Log("请先选择头盔或护甲的 patch 文件");
            }
        }
        catch (Exception ex)
        {
            Log($"分析失败: {ex.Message}");
        }
    }

    private void AnalyzePatch(string patchPath)
    {
        Log($"文件: {patchPath}");
        var info = new FileInfo(patchPath);
        Log($"大小: {info.Length:N0} bytes");

        var units = ArmorMergerCore.Analyze(patchPath);
        Log($"Unit 数量: {units.Count}");

        Log($"  {"Idx",-5} {"FileId",-20} {"TocSize",-10} {"GpuSize",-12} {"GpuOff",-12}");
        Log($"  {"---",-5} {"------",-20} {"-------",-10} {"-------",-12} {"------",-12}");
        foreach (var unit in units)
        {
            Log($"  {unit.Index,-5} {unit.FileId,-20} {unit.TocSize,-10} {unit.GpuSize,-12} {unit.GpuOffset,-12}");
        }

        var gpuPath = patchPath + ".gpu_resources";
        if (File.Exists(gpuPath))
        {
            var gpuInfo = new FileInfo(gpuPath);
            Log($"GPU Resources: {gpuInfo.Length:N0} bytes");
        }
        else
        {
            Log("GPU Resources: 未找到");
        }
    }

    // --- 合并 ---

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var helmetPath = HelmetPatchPath.Text;
            var armorPath = ArmorPatchPath.Text;
            var outputDir = OutputDirectoryPath.Text;
            var outputName = OutputName.Text;

            if (string.IsNullOrEmpty(helmetPath) && string.IsNullOrEmpty(armorPath))
            {
                MessageBox.Show("请先选择头盔或护甲的 patch 文件", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(outputDir))
            {
                outputDir = Path.GetDirectoryName(armorPath ?? helmetPath) ?? string.Empty;
                OutputDirectoryPath.Text = outputDir;
            }

            if (string.IsNullOrEmpty(outputName))
            {
                outputName = "merged_armor";
                OutputName.Text = outputName;
            }

            // 合并两个 patch 的 Unit
            var sourcePath = !string.IsNullOrEmpty(armorPath) ? armorPath : helmetPath;
            var otherPath = !string.IsNullOrEmpty(armorPath) ? helmetPath : armorPath;

            var sourceUnits = ArmorMergerCore.Analyze(sourcePath);
            var otherUnits = string.IsNullOrEmpty(otherPath) ? [] : ArmorMergerCore.Analyze(otherPath);

            Log($"\n=== 合并 Patch ===");
            Log($"源 Patch: {sourcePath} ({sourceUnits.Count} units)");
            if (!string.IsNullOrEmpty(otherPath))
                Log($"附加 Patch: {otherPath} ({otherUnits.Count} units)");

            // 合并策略：取源 patch 的所有 Unit + 附加 patch 的所有 Unit
            var allIndices = Enumerable.Range(0, sourceUnits.Count).ToList();
            if (!string.IsNullOrEmpty(otherPath))
            {
                // 附加 patch 的 Unit 需要单独处理（因为是不同的文件）
                // 先合并源 patch，再合并附加 patch
                var result1 = ArmorMergerCore.MergeUnits(
                    sourcePath, allIndices, outputDir, outputName + "_src");

                var otherIndices = Enumerable.Range(0, otherUnits.Count).ToList();
                var result2 = ArmorMergerCore.MergeUnits(
                    otherPath, otherIndices, outputDir, outputName + "_other");

                Log($"已生成两个合并文件:");
                foreach (var f in result1) Log($"  {f}");
                foreach (var f in result2) Log($"  {f}");

                Log($"\n提示: 两个 patch 已分别合并。如需进一步处理，请使用「导出 Mod 包」功能。");
            }
            else
            {
                var result = ArmorMergerCore.MergeUnits(
                    sourcePath, allIndices, outputDir, outputName);

                Log($"合并完成! 生成了 {result.Count} 个文件:");
                foreach (var file in result)
                {
                    if (File.Exists(file))
                    {
                        var fileInfo = new FileInfo(file);
                        Log($"  {file} ({fileInfo.Length:N0} bytes)");
                    }
                }
            }

            MessageBox.Show("合并完成!", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"合并失败: {ex.Message}");
            MessageBox.Show($"合并失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- 导出 Mod 包 ---

    private void ExportMod_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var helmetPath = HelmetPatchPath.Text;
            var armorPath = ArmorPatchPath.Text;
            var outputDir = OutputDirectoryPath.Text;
            var outputName = OutputName.Text;

            if (string.IsNullOrEmpty(helmetPath) && string.IsNullOrEmpty(armorPath))
            {
                MessageBox.Show("请先选择头盔或护甲的 patch 文件", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(outputDir))
            {
                outputDir = Path.GetDirectoryName(armorPath ?? helmetPath) ?? string.Empty;
                OutputDirectoryPath.Text = outputDir;
            }

            if (string.IsNullOrEmpty(outputName))
            {
                outputName = "merged_armor";
                OutputName.Text = outputName;
            }

            // 创建 Mod 包目录
            var modDir = Path.Combine(outputDir, outputName);
            Directory.CreateDirectory(modDir);

            Log($"\n=== 导出 Mod 包 ===");
            Log($"Mod 目录: {modDir}");

            // 合并 patch
            var sourcePath = !string.IsNullOrEmpty(armorPath) ? armorPath : helmetPath;
            var sourceUnits = ArmorMergerCore.Analyze(sourcePath);
            var allIndices = Enumerable.Range(0, sourceUnits.Count).ToList();

            var resultFiles = ArmorMergerCore.MergeUnits(
                sourcePath, allIndices, modDir, "merged");

            Log($"生成了 {resultFiles.Count} 个 patch 文件");

            // 生成 manifest.json
            var manifest = new
            {
                Version = 1,
                Guid = Guid.NewGuid().ToString(),
                Name = outputName,
                Description = $"由 ArmorMerger 工具生成的合并护甲包",
                Options = new object[]
                {
                    new
                    {
                        Name = "合并护甲",
                        Description = "头盔已隐藏，护甲已合并",
                        Include = new[] { "." }
                    }
                }
            };

            var manifestPath = Path.Combine(modDir, "manifest.json");
            var manifestJson = System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(manifestPath, manifestJson, Encoding.UTF8);
            Log($"已生成 manifest.json");

            Log($"\nMod 包导出完成!");
            MessageBox.Show($"Mod 包已导出到:\n{modDir}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"导出失败: {ex.Message}");
            MessageBox.Show($"导出失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
