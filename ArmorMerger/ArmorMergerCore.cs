using System.IO;

namespace ArmorMerger;

/// <summary>
/// 护甲合并核心逻辑。
/// 功能：分析 patch 文件中的 Unit，合并多个 Unit 的 GPU 数据。
/// </summary>
internal static class ArmorMergerCore
{
    /// <summary>
    /// 分析 patch 文件，列出所有 Unit 信息。
    /// </summary>
    public static IReadOnlyList<UnitInfo> Analyze(string patchPath)
    {
        if (!File.Exists(patchPath))
            throw new FileNotFoundException("Patch 文件不存在", patchPath);

        var patch = PatchFile.Load(patchPath);
        var gpuPath = patchPath + ".gpu_resources";
        var hasGpu = File.Exists(gpuPath);

        var result = new List<UnitInfo>();
        for (var i = 0; i < patch.Files.Count; i++)
        {
            var entry = patch.Files[i];
            result.Add(new UnitInfo
            {
                Index = i,
                FileId = $"0x{entry.FileId:X16}",
                TypeId = $"0x{entry.TypeId:X16}",
                TocOffset = entry.TocOffset,
                TocSize = entry.TocSize,
                GpuOffset = entry.GpuOffset,
                GpuSize = entry.GpuSize,
                StreamOffset = entry.StreamOffset,
                StreamSize = entry.StreamSize,
                EntryIndex = entry.EntryIndex
            });
        }

        return result;
    }

    /// <summary>
    /// 将多个 Unit 合并成一个新 patch + gpu_resources。
    /// 读取源 patch 的 TOC 数据和 GPU 数据，重新打包。
    /// </summary>
    /// <param name="sourcePatchPath">源 patch 文件路径</param>
    /// <param name="unitIndices">要合并的 Unit 索引列表</param>
    /// <param name="outputDirectory">输出目录</param>
    /// <param name="outputName">输出文件名（不含扩展名）</param>
    /// <returns>生成的文件路径列表</returns>
    public static IReadOnlyList<string> MergeUnits(
        string sourcePatchPath,
        IReadOnlyList<int> unitIndices,
        string outputDirectory,
        string outputName)
    {
        if (!File.Exists(sourcePatchPath))
            throw new FileNotFoundException("Patch 文件不存在", sourcePatchPath);

        var sourcePatch = PatchFile.Load(sourcePatchPath);
        var sourceGpuPath = sourcePatchPath + ".gpu_resources";
        var sourceStreamPath = sourcePatchPath + ".stream";

        if (unitIndices.Count == 0)
            throw new ArgumentException("至少需要选择一个 Unit");

        // 读取源 patch 完整字节
        var sourceBytes = File.ReadAllBytes(sourcePatchPath);

        // 收集选中 Unit 的 TOC 数据
        var tocDataList = new List<byte[]>();
        var newEntries = new List<PatchFileEntry>();
        uint currentTocOffset = 0;

        foreach (var index in unitIndices)
        {
            if (index < 0 || index >= sourcePatch.Files.Count)
                throw new ArgumentOutOfRangeException(nameof(unitIndices), $"Unit 索引 {index} 超出范围 (0-{sourcePatch.Files.Count - 1})");

            var sourceEntry = sourcePatch.Files[index];
            var tocData = new byte[sourceEntry.TocSize];
            // TocOffset 是 patch 文件中的绝对偏移
            Array.Copy(sourceBytes, (long)sourceEntry.TocOffset, tocData, 0, sourceEntry.TocSize);
            tocDataList.Add(tocData);

            newEntries.Add(new PatchFileEntry
            {
                FileId = sourceEntry.FileId,
                TypeId = sourceEntry.TypeId,
                TocOffset = currentTocOffset,
                StreamOffset = 0,
                GpuOffset = 0, // 后面计算
                TocSize = sourceEntry.TocSize,
                StreamSize = 0,
                GpuSize = sourceEntry.GpuSize, // 保持原始 GPU 大小
                EntryIndex = (uint)(newEntries.Count + 1)
            });

            currentTocOffset += sourceEntry.TocSize;
        }

        // 合并 GPU 数据
        var gpuDataList = new List<byte[]>();
        var hasGpu = File.Exists(sourceGpuPath);
        if (hasGpu)
        {
            using var gpuStream = File.OpenRead(sourceGpuPath);
            uint currentGpuOffset = 0;

            for (var i = 0; i < newEntries.Count; i++)
            {
                var sourceEntry = sourcePatch.Files[unitIndices[i]];
                var gpuData = new byte[sourceEntry.GpuSize];
                gpuStream.Seek((long)sourceEntry.GpuOffset, SeekOrigin.Begin);
                gpuStream.ReadExactly(gpuData, 0, (int)sourceEntry.GpuSize);
                gpuDataList.Add(gpuData);

                // 更新 GpuOffset
                newEntries[i].GpuOffset = currentGpuOffset;
                currentGpuOffset += sourceEntry.GpuSize;
            }
        }

        // 创建新 patch
        var newPatch = new PatchFile
        {
            NumTypes = sourcePatch.NumTypes,
            NumFiles = newEntries.Count,
            Types = [new TypeEntry
            {
                TypeId = sourcePatch.Types[0].TypeId,
                ResourceCount = newEntries.Count
            }],
            Files = newEntries
        };

        // 输出
        Directory.CreateDirectory(outputDirectory);
        var patchOutputPath = Path.Combine(outputDirectory, outputName + ".patch_0");
        newPatch.Save(patchOutputPath, tocDataList);

        var resultFiles = new List<string> { patchOutputPath };

        // 输出合并后的 gpu_resources
        if (hasGpu && gpuDataList.Count > 0)
        {
            var gpuOutputPath = patchOutputPath + ".gpu_resources";
            using var gpuOutputStream = File.Create(gpuOutputPath);
            foreach (var gpuData in gpuDataList)
                gpuOutputStream.Write(gpuData, 0, gpuData.Length);
            resultFiles.Add(gpuOutputPath);
        }

        // 复制 stream 文件（如果存在）
        if (File.Exists(sourceStreamPath))
        {
            var streamOutputPath = patchOutputPath + ".stream";
            File.Copy(sourceStreamPath, streamOutputPath, true);
            resultFiles.Add(streamOutputPath);
        }

        return resultFiles;
    }

    /// <summary>
    /// 将 patch 文件中指定 Unit 的 GPU 数据替换为另一个 patch 的 GPU 数据。
    /// 用于：保持 TOC 结构不变，只替换 GPU 资源。
    /// </summary>
    public static IReadOnlyList<string> ReplaceGpuData(
        string basePatchPath,
        string gpuSourcePatchPath,
        string outputDirectory,
        string outputName)
    {
        if (!File.Exists(basePatchPath))
            throw new FileNotFoundException("基础 Patch 文件不存在", basePatchPath);
        if (!File.Exists(gpuSourcePatchPath))
            throw new FileNotFoundException("GPU 源 Patch 文件不存在", gpuSourcePatchPath);

        var basePatch = PatchFile.Load(basePatchPath);
        var gpuSourcePatch = PatchFile.Load(gpuSourcePatchPath);

        // 复制基础 patch 的 TOC
        Directory.CreateDirectory(outputDirectory);
        var patchOutputPath = Path.Combine(outputDirectory, outputName + ".patch_0");
        File.Copy(basePatchPath, patchOutputPath, true);

        // 替换 gpu_resources
        var gpuSourcePath = gpuSourcePatchPath + ".gpu_resources";
        if (File.Exists(gpuSourcePath))
        {
            var gpuOutputPath = patchOutputPath + ".gpu_resources";
            File.Copy(gpuSourcePath, gpuOutputPath, true);
        }

        return [patchOutputPath, patchOutputPath + ".gpu_resources"];
    }

    /// <summary>
    /// 从分体版创建合并版（基于参考文件的模式）。
    /// 合并版使用 Unit[15] 的 TOC 结构，但 GPU 数据是合并后的。
    /// </summary>
    /// <param name="splitPatchPath">分体版 patch 文件路径</param>
    /// <param name="mergedGpuPatchPath">合并版参考 patch（用于获取合并后的 GPU 数据）</param>
    /// <param name="outputDirectory">输出目录</param>
    /// <param name="outputName">输出文件名</param>
    public static IReadOnlyList<string> CreateMergedFromSplit(
        string splitPatchPath,
        string mergedGpuPatchPath,
        string outputDirectory,
        string outputName)
    {
        if (!File.Exists(splitPatchPath))
            throw new FileNotFoundException("分体版 Patch 文件不存在", splitPatchPath);
        if (!File.Exists(mergedGpuPatchPath))
            throw new FileNotFoundException("合并版参考 Patch 文件不存在", mergedGpuPatchPath);

        // 1. 从分体版读取 Unit[15] 的 TOC 数据（合并后的结构）
        var splitPatch = PatchFile.Load(splitPatchPath);
        var splitBytes = File.ReadAllBytes(splitPatchPath);

        // 查找 FileId = 0xBFDC0F01475D16C8 的 Unit（合并后的结构）
        var targetIndex = -1;
        for (var i = 0; i < splitPatch.Files.Count; i++)
        {
            if (splitPatch.Files[i].FileId == unchecked((long)0xBFDC0F01475D16C8))
            {
                targetIndex = i;
                break;
            }
        }

        if (targetIndex < 0)
            throw new InvalidDataException("在分体版中未找到合并目标 Unit (FileId=0xBFDC0F01475D16C8)");

        var targetEntry = splitPatch.Files[targetIndex];

        // 2. 从合并版参考获取 GPU 数据
        var mergedGpuPath = mergedGpuPatchPath + ".gpu_resources";
        if (!File.Exists(mergedGpuPath))
            throw new FileNotFoundException("合并版 GPU 资源文件不存在", mergedGpuPath);

        var gpuData = File.ReadAllBytes(mergedGpuPath);

        // 3. 创建新的合并版 patch
        var newEntry = new PatchFileEntry
        {
            FileId = targetEntry.FileId,
            TypeId = targetEntry.TypeId,
            TocOffset = 0,
            StreamOffset = 0,
            GpuOffset = 0,
            TocSize = targetEntry.TocSize,
            StreamSize = 0,
            GpuSize = (uint)gpuData.Length,
            EntryIndex = 1
        };

        var newPatch = new PatchFile
        {
            NumTypes = 1,
            NumFiles = 1,
            Types =
            [
                new TypeEntry
                {
                    TypeId = splitPatch.Types[0].TypeId,
                    ResourceCount = 1
                }
            ],
            Files = [newEntry]
        };

        // 读取 Unit 的 TOC 数据
        var tocData = new byte[targetEntry.TocSize];
        // TocOffset 是 patch 文件中的绝对偏移
        Array.Copy(splitBytes, (long)targetEntry.TocOffset, tocData, 0, targetEntry.TocSize);

        // 输出
        Directory.CreateDirectory(outputDirectory);
        var patchOutputPath = Path.Combine(outputDirectory, outputName + ".patch_0");
        newPatch.Save(patchOutputPath, [tocData]);

        var gpuOutputPath = patchOutputPath + ".gpu_resources";
        File.WriteAllBytes(gpuOutputPath, gpuData);

        return [patchOutputPath, gpuOutputPath];
    }
}

/// <summary>
/// Unit 信息。
/// </summary>
internal sealed class UnitInfo
{
    public int Index { get; set; }
    public string FileId { get; set; } = string.Empty;
    public string TypeId { get; set; } = string.Empty;
    public ulong TocOffset { get; set; }
    public uint TocSize { get; set; }
    public ulong GpuOffset { get; set; }
    public uint GpuSize { get; set; }
    public ulong StreamOffset { get; set; }
    public uint StreamSize { get; set; }
    public uint EntryIndex { get; set; }

    public override string ToString()
        => $"[{Index}] FileId={FileId}, TocSize={TocSize}, GpuSize={GpuSize}";
}
