using System.IO;

namespace ArmorMerger;

/// <summary>
/// GPU 资源文件 (.gpu_resources) 的结构解析。
/// GPU 资源文件是大型二进制文件，需要有界随机读取。
/// </summary>
internal sealed class GpuResources
{
    /// <summary>
    /// 从 patch 文件路径获取对应的 gpu_resources 文件路径。
    /// </summary>
    public static string GetCompanionPath(string patchFilePath)
        => patchFilePath + ".gpu_resources";

    /// <summary>
    /// 从 patch 文件路径获取对应的 stream 文件路径。
    /// </summary>
    public static string GetStreamPath(string patchFilePath)
        => patchFilePath + ".stream";

    /// <summary>
    /// 验证 gpu_resources 文件是否存在且大小合理。
    /// </summary>
    public static bool ValidateCompanion(string patchFilePath, ulong expectedGpuSize)
    {
        var companionPath = GetCompanionPath(patchFilePath);
        if (!File.Exists(companionPath))
            return false;

        var info = new FileInfo(companionPath);
        return (ulong)info.Length == expectedGpuSize;
    }
}
