using System.Collections.ObjectModel;
using System.Text;

namespace Helldivers2ModManager.Models;

/// <summary>
/// 模组版本兼容性状态枚举
/// </summary>
internal enum ModVersionStatus
{
    /// <summary>
    /// 未检查
    /// </summary>
    Unknown,
    /// <summary>
    /// 兼容 - 模组的 Unit 版本与游戏当前版本匹配
    /// </summary>
    Compatible,
    /// <summary>
    /// 不兼容 - 模组的 Unit 版本与游戏当前版本不匹配，可能存在兼容性问题
    /// </summary>
    Incompatible,
    /// <summary>
    /// 检查中
    /// </summary>
    Checking,
    /// <summary>
    /// 检查失败（文件无法读取/解析）
    /// </summary>
    Error
}

/// <summary>
/// 单个补丁文件中提取的 Unit 版本信息
/// </summary>
internal sealed class PatchUnitInfo
{
    /// <summary>
    /// 补丁文件名
    /// </summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>
    /// Unit 资源 ID
    /// </summary>
    public long FileId { get; init; }

    /// <summary>
    /// Unit 版本号（从二进制数据偏移 0x2C 处读取的 4 字节 uint32）
    /// </summary>
    public uint Version { get; init; }

    /// <summary>
    /// Unit 资源数据大小（字节）
    /// </summary>
    public int DataSize { get; init; }
}

/// <summary>
/// 补丁文件健康状态
/// </summary>
internal enum PatchHealthStatus
{
    /// <summary>
    /// 健康 - 文件结构完整，数据正常
    /// </summary>
    Healthy,
    /// <summary>
    /// 警告 - 文件结构基本正常，但存在潜在问题
    /// </summary>
    Warning,
    /// <summary>
    /// 损坏 - 文件结构异常，可能无法正常使用
    /// </summary>
    Corrupted,
    /// <summary>
    /// 无 Unit 资源 - 文件不包含可检查的 Unit 资源
    /// </summary>
    NoUnitResources
}

/// <summary>
/// Unit 资源深度检查结果
/// 参考 hd2-repatcher 的 update_patch_file() 实现：
/// - 验证 Unit 内部结构（LOD Group、Joint List）
/// - 检查 Layout Format 格式（version &lt; 0xA4CD36 时检查布局偏移）
/// </summary>
internal sealed class UnitResourceDetail
{
    /// <summary>
    /// 所在补丁文件名
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// TOC 中的条目序号（1-based）。
    /// </summary>
    public int EntryIndex { get; set; }

    /// <summary>
    /// Unit 资源 ID
    /// </summary>
    public long FileId { get; set; }

    /// <summary>
    /// Unit 版本号
    /// </summary>
    public uint Version { get; set; }

    /// <summary>
    /// Unit 数据大小（字节）
    /// </summary>
    public int DataSize { get; set; }

    /// <summary>
    /// Unit 内部记录的结束偏移（头部 +0x60）。
    /// </summary>
    public int EndingOffset { get; set; }

    /// <summary>
    /// 根据 EndingOffset 计算出的完整 Unit 大小（EndingOffset + 8）。
    /// </summary>
    public int ExpectedDataSize { get; set; }

    /// <summary>
    /// TOC 声明大小是否与 Unit 内部大小一致。
    /// </summary>
    public bool DeclaredSizeMatchesInternal { get; set; } = true;

    /// <summary>
    /// Unit 内部大小大于 TOC 声明大小，数据会被解析器截断。
    /// </summary>
    public bool IsTruncated { get; set; }

    /// <summary>
    /// LOD Group 偏移量（从 Unit 数据起始的偏移）
    /// </summary>
    public int LODGroupOffset { get; set; }

    /// <summary>
    /// Joint List 偏移量（从 Unit 数据起始的偏移）
    /// </summary>
    public int JointListOffset { get; set; }

    /// <summary>
    /// LOD Group 数据大小（字节），由 joint_list_offset - lod_group_offset 计算得出
    /// </summary>
    public int LODGroupSize { get; set; }

    /// <summary>
    /// LOD Group 数据是否在有效边界内
    /// </summary>
    public bool LODGroupInBounds { get; set; }

    /// <summary>
    /// Unit 数据是否在文件数据的有效边界内
    /// </summary>
    public bool UnitDataInBounds { get; set; }

    /// <summary>
    /// 是否执行了 Layout Format 检查（version &lt; 0xA4CD36 时执行）
    /// </summary>
    public bool LayoutFormatChecked { get; set; }

    /// <summary>
    /// Layout Format 检查是否通过
    /// </summary>
    public bool LayoutFormatValid { get; set; }

    /// <summary>
    /// Layout 中的 item_format 异常数量（format &gt; 16 的条目数）
    /// </summary>
    public int LayoutFormatIssueCount { get; set; }

    /// <summary>
    /// 针对该 Unit 的警告信息
    /// </summary>
    public string? Warning { get; set; }
}

/// <summary>
/// 补丁文件详细分析结果
/// 参考 hd2-repatcher 的 update_patch_file() 实现：
/// - 验证文件头结构（魔数、类型数、文件数）
/// - 分析文件条目偏移边界
/// - 检查伴生文件（.gpu_resources / .stream）是否存在
/// </summary>
internal sealed class PatchFileAnalysis
{
    /// <summary>
    /// 补丁文件名
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// 文件大小（字节）
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// 健康状态
    /// </summary>
    public PatchHealthStatus HealthStatus { get; set; }

    /// <summary>
    /// 文件头中的类型数
    /// </summary>
    public int NumTypes { get; set; }

    /// <summary>
    /// 文件头中的资源文件数
    /// </summary>
    public int NumFiles { get; set; }

    /// <summary>
    /// 类型条目总数（来自类型条目的统计）
    /// </summary>
    public int TotalResources { get; set; }

    /// <summary>
    /// 当前补丁文件声明的完整资源类型分布。
    /// </summary>
    public List<ResourceTypeDistribution> ResourceTypes { get; set; } = [];

    /// <summary>
    /// 类型表声明、文件总数和实际条目类型分布是否一致。
    /// </summary>
    public bool TypeDistributionValid { get; set; } = true;

    public int TypeDistributionIssueCount { get; set; }

    /// <summary>
    /// 文件头结构是否有效
    /// </summary>
    public bool HeaderValid { get; set; }

    /// <summary>
    /// 文件条目偏移是否在有效边界内
    /// </summary>
    public bool FileEntriesInBounds { get; set; }

    /// <summary>
    /// 所有主数据 offset/size 是否都在补丁文件边界内。
    /// </summary>
    public bool MainDataBoundsValid { get; set; } = true;

    public int MainDataIssueCount { get; set; }

    /// <summary>
    /// TOC entry_index 是否为连续的 1..N。
    /// </summary>
    public bool EntryIndicesValid { get; set; } = true;

    public int EntryIndexIssueCount { get; set; }

    /// <summary>
    /// 对应的 .gpu_resources 文件是否存在
    /// </summary>
    public bool HasGpuResources { get; set; }

    /// <summary>
    /// 至少一个条目声明了非零 GPU 数据，因此伴生文件是必需的。
    /// </summary>
    public bool RequiresGpuResources { get; set; }

    /// <summary>
    /// 对应的 .stream 文件是否存在
    /// </summary>
    public bool HasStream { get; set; }

    /// <summary>
    /// 至少一个条目声明了非零 stream 数据，因此伴生文件是必需的。
    /// </summary>
    public bool RequiresStream { get; set; }

    /// <summary>
    /// GPU 数据引用是否都在 .gpu_resources 文件边界内
    /// </summary>
    public bool GpuResourceBoundsValid { get; set; } = true;

    /// <summary>
    /// GPU 数据边界异常数量
    /// </summary>
    public int GpuResourceIssueCount { get; set; }

    /// <summary>
    /// GPU offset 非 64 字节对齐的条目数量。
    /// </summary>
    public int GpuAlignmentIssueCount { get; set; }

    /// <summary>
    /// stream 数据引用是否都在伴生文件边界内。
    /// </summary>
    public bool StreamBoundsValid { get; set; } = true;

    public int StreamIssueCount { get; set; }

    public int StreamAlignmentIssueCount { get; set; }

    /// <summary>
    /// 该文件中 Unit 资源的深度检查结果
    /// </summary>
    public List<UnitResourceDetail> UnitDetails { get; set; } = [];

    /// <summary>
    /// 错误/警告信息
    /// </summary>
    public string? Message { get; set; }
}

/// <summary>
/// 资源类型分布信息
/// </summary>
internal sealed class ResourceTypeDistribution
{
    /// <summary>
    /// 资源类型 ID
    /// </summary>
    public long TypeId { get; set; }

    /// <summary>
    /// 该类型的资源数量
    /// </summary>
    public int ResourceCount { get; set; }
}

/// <summary>
/// 模组详细分析结果容器
/// </summary>
internal sealed class ModDetailedAnalysis
{
    /// <summary>
    /// 每个补丁文件的详细分析
    /// </summary>
    public List<PatchFileAnalysis> PatchFiles { get; set; } = [];

    /// <summary>
    /// 资源类型分布
    /// </summary>
    public List<ResourceTypeDistribution> ResourceTypes { get; set; } = [];

    /// <summary>
    /// 是否存在结构性问题
    /// </summary>
    public bool HasStructuralIssues { get; set; }

    /// <summary>
    /// 是否存在伴生文件缺失
    /// </summary>
    public bool HasCompanionFileIssues { get; set; }

    /// <summary>
    /// 是否存在 Unit 内部结构问题
    /// </summary>
    public bool HasUnitStructuralIssues { get; set; }

    /// <summary>
    /// 是否存在 GPU 数据边界问题
    /// </summary>
    public bool HasGpuResourceIssues { get; set; }

    /// <summary>
    /// 是否存在 stream 数据边界或对齐问题。
    /// </summary>
    public bool HasStreamResourceIssues { get; set; }

    /// <summary>
    /// 补丁文件总数
    /// </summary>
    public int TotalPatchFiles { get; set; }

    /// <summary>
    /// 含 Unit 资源的文件数
    /// </summary>
    public int FilesWithUnits { get; set; }

    /// <summary>
    /// 健康文件数
    /// </summary>
    public int HealthyFileCount { get; set; }

    /// <summary>
    /// 警告文件数
    /// </summary>
    public int WarningFileCount { get; set; }

    /// <summary>
    /// 损坏文件数
    /// </summary>
    public int CorruptedFileCount { get; set; }
}

/// <summary>
/// 模组版本检测结果
/// </summary>
internal sealed class ModVersionCheckResult
{
    /// <summary>
    /// 兼容性状态
    /// </summary>
    public ModVersionStatus Status { get; set; } = ModVersionStatus.Unknown;

    /// <summary>
    /// 游戏当前 Unit 版本号
    /// </summary>
    public uint GameVersion { get; set; }

    /// <summary>
    /// 最后检查时间
    /// </summary>
    public DateTime LastChecked { get; set; }

    /// <summary>
    /// 该模组中包含的 Unit 版本信息列表
    /// </summary>
    public ObservableCollection<PatchUnitInfo> PatchUnits { get; set; } = [];

    /// <summary>
    /// 错误信息（当 Status 为 Error 时）
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 模组详细分析结果（文件结构、Unit 内部结构、伴生文件等深度检查）
    /// </summary>
    public ModDetailedAnalysis? DetailedAnalysis { get; set; }
}

internal enum PatchRepairKind
{
    UnitTocSize,
    EntryIndex,
    TypeResourceCount,
    ResourceTypeId,
    TypeAlignment,
    MainDataOffset
}

/// <summary>
/// A fixed-width metadata write that can be verified without moving resource payloads.
/// </summary>
internal sealed class PatchRepairAction
{
    public required PatchRepairKind Kind { get; init; }
    public required string PatchFilePath { get; init; }
    public required long Offset { get; init; }
    public required int Width { get; init; }
    public required ulong OldValue { get; init; }
    public required ulong NewValue { get; init; }
    public int EntryIndex { get; init; }
    public long FileId { get; init; }
}

internal sealed class ModRepairPlan
{
    public List<PatchRepairAction> Actions { get; init; } = [];
    public List<string> BlockingReasons { get; init; } = [];
    public int ActionCount => Actions.Count;
    public int FileCount => Actions.Select(a => a.PatchFilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public bool CanRepair => Actions.Count > 0 && BlockingReasons.Count == 0;
}

internal sealed class ModRepairResult
{
    public bool Success { get; init; }
    public int AppliedActionCount { get; init; }
    public List<string> BackupPaths { get; init; } = [];
    public string? ErrorMessage { get; init; }
}

internal enum AssistedLodStrategy
{
    UseGameReference,
    PreserveMod
}

internal sealed class AssistedUnitRepairAction
{
    public required string PatchFilePath { get; init; }
    public int EntryIndex { get; init; }
    public long FileId { get; init; }
    public uint CurrentVersion { get; init; }
    public uint ReferenceVersion { get; init; }
    public int CurrentLodSize { get; init; }
    public int ReferenceLodSize { get; init; }
    public uint CurrentGpuSize { get; init; }
    public uint ReferenceGpuSize { get; init; }
    public bool MeshIdsDiffer { get; init; }
    public string CurrentMeshSignature { get; init; } = string.Empty;
    public bool StrongCustomModelSignal { get; init; }
    public AssistedLodStrategy LodStrategy { get; init; }
    public bool LodDataDiffers { get; init; }
    public string FriendlyName { get; init; } = string.Empty;
}

internal sealed class AssistedModRepairPlan
{
    public List<AssistedUnitRepairAction> Actions { get; init; } = [];
    public List<string> BlockingReasons { get; init; } = [];
    public int MatchedReferenceCount { get; init; }
    public int MissingReferenceCount { get; init; }
    public bool IsAutomatic { get; init; }
    public int AutomaticStrongCustomCount { get; init; }
    public int AutomaticPreserveUnitCount { get; init; }
    public int AutomaticGameLodUnitCount { get; init; }
    public int ActionCount => Actions.Count;
    public int FileCount => Actions.Select(a => a.PatchFilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public bool CanRepair => Actions.Count > 0 && BlockingReasons.Count == 0;
}

/// <summary>
/// 用于在 UI 中显示版本兼容性检查信息的简单数据类
/// </summary>
internal sealed class CompatibleCheckInfo
{
    public ModVersionStatus VersionStatus { get; set; }
    public uint GameUnitVersion { get; set; }
    public DateTime LastChecked { get; set; }
    public List<PatchUnitInfo> PatchUnits { get; set; } = [];
    public string ModName { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public ModDetailedAnalysis? DetailedAnalysis { get; set; }

    public override string ToString()
    {
        var statusText = VersionStatus switch
        {
            ModVersionStatus.Compatible => "Compatible",
            ModVersionStatus.Incompatible => "Incompatible",
            ModVersionStatus.Unknown => "Unconfirmed",
            ModVersionStatus.Checking => "Checking",
            ModVersionStatus.Error => "Error",
            _ => "Unknown"
        };

        var sb = new StringBuilder();
        sb.AppendLine(string.Format("Mod: {0}", ModName));
        sb.AppendLine(string.Format("Status: {0}", statusText));
        sb.AppendLine(string.Format("Game Unit Version: 0x{0:X8} ({0})", GameUnitVersion));
        sb.AppendLine(string.Format("Last Checked: {0}", LastChecked.ToString("yyyy-MM-dd HH:mm:ss")));
        sb.AppendLine();

        if (PatchUnits.Count > 0)
        {
            sb.AppendLine("Patch Unit Versions:");
            var distinctVersions = PatchUnits.Select(p => p.Version).Distinct().ToList();
            foreach (var version in distinctVersions)
            {
                var count = PatchUnits.Count(p => p.Version == version);
                var match = version == GameUnitVersion ? "(OK)" : "(MISMATCH)";
                sb.AppendLine(string.Format("  {0} 0x{1:X8} ({1}) - {2} file(s)", match, version, count));
            }
            sb.AppendLine();
            sb.AppendLine("Details (FileId - Version):");
            foreach (var unit in PatchUnits)
            {
                var match = unit.Version == GameUnitVersion ? "(OK)" : "(MISMATCH)";
                sb.AppendLine(string.Format("  {0} {1}  0x{2:X16}  0x{3:X8}", match, unit.FileName, unit.FileId, unit.Version));
            }
        }
        else
        {
            sb.AppendLine("No detectable unit resources in this mod.");
        }

        // ---- Detailed Analysis Section ----
        if (DetailedAnalysis is { } analysis)
        {
            sb.AppendLine();
            sb.AppendLine("=== Deep Analysis ===");
            sb.AppendLine(string.Format("Patch Files: {0} total, {1} with Unit resources", analysis.TotalPatchFiles, analysis.FilesWithUnits));
            sb.AppendLine(string.Format("File Health: {0} healthy, {1} warnings, {2} corrupted",
                analysis.HealthyFileCount, analysis.WarningFileCount, analysis.CorruptedFileCount));

            if (analysis.HasStructuralIssues)
                sb.AppendLine("! WARNING: Structural issues detected in some files");

            if (analysis.HasCompanionFileIssues)
                sb.AppendLine("! WARNING: Some companion files (.gpu_resources / .stream) are missing");

            if (analysis.HasUnitStructuralIssues)
                sb.AppendLine("! WARNING: Unit internal structure issues detected");

            if (analysis.HasGpuResourceIssues)
                sb.AppendLine("! WARNING: GPU resource bounds issues detected");

            // Per-file details
            foreach (var pf in analysis.PatchFiles)
            {
                sb.AppendLine();
                sb.AppendLine(string.Format("--- {0} ---", pf.FileName));
                sb.AppendLine(string.Format("  Size: {0} bytes | Health: {1}", pf.FileSize, pf.HealthStatus));
                sb.AppendLine(string.Format("  Header: {0} | Entries in bounds: {1}",
                    pf.HeaderValid ? "Valid" : "INVALID",
                    pf.FileEntriesInBounds ? "Yes" : "NO"));
                sb.AppendLine(string.Format("  Types: {0} | Files: {1} | Total Resources: {2}",
                    pf.NumTypes, pf.NumFiles, pf.TotalResources));
                sb.AppendLine(string.Format("  GPU Resources: {0} | Stream: {1}",
                    pf.HasGpuResources ? "Present" : "Missing",
                    pf.HasStream ? "Present" : "Missing"));
                sb.AppendLine(string.Format("  GPU Bounds: {0} | Issues: {1}",
                    pf.GpuResourceBoundsValid ? "Valid" : "INVALID",
                    pf.GpuResourceIssueCount));

                if (pf.UnitDetails.Count > 0)
                {
                    sb.AppendLine("  Unit Internal Structure:");
                    foreach (var unit in pf.UnitDetails)
                    {
                        sb.AppendLine(string.Format("    [0x{0:X16}] v{1:X8} size={2}",
                            unit.FileId, unit.Version, unit.DataSize));
                        sb.AppendLine(string.Format("      LOD: offset={0} size={1} in_bounds={2}",
                            unit.LODGroupOffset, unit.LODGroupSize, unit.LODGroupInBounds ? "Yes" : "NO"));
                        if (unit.LayoutFormatChecked)
                        {
                            sb.AppendLine(string.Format("      Layout: checked={0} valid={1} issues={2}",
                                unit.LayoutFormatChecked, unit.LayoutFormatValid ? "Yes" : "NO",
                                unit.LayoutFormatIssueCount));
                        }
                        if (!string.IsNullOrEmpty(unit.Warning))
                            sb.AppendLine(string.Format("      Warning: {0}", unit.Warning));
                    }
                }

                if (!string.IsNullOrEmpty(pf.Message))
                    sb.AppendLine(string.Format("  Note: {0}", pf.Message));
            }
        }

        if (!string.IsNullOrEmpty(ErrorMessage))
        {
            sb.AppendLine();
            sb.AppendLine(string.Format("Error: {0}", ErrorMessage));
        }

        return sb.ToString();
    }
}
