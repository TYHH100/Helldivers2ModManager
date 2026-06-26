using System.IO;
using System.Security.Cryptography;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 文件哈希工具类，提供 SHA-256 文件哈希计算功能，用于模组增量更新时的文件比对
/// </summary>
internal static class FileHashUtils
{
	private static LocalizationService? _localizationService;

	internal static void Init(LocalizationService localizationService)
	{
		_localizationService = localizationService;
	}
    /// <summary>
    /// GPU 资源文件的 SHA-256 跳过阈值。
    /// 超过此大小的 .gpu_resources 文件跳过完整 SHA-256 计算，改用文件大小+修改时间的组合作为伪哈希。
    /// 此类文件体积巨大（可达 GB 级）但极少在模组更新中变动，无需逐字节计算。
    /// </summary>
    private const long LargeGpuFileThresholdBytes = 1L * 1024 * 1024 * 1024; // 1 GB

    /// <summary>
    /// 判断是否为超大 GPU 资源文件，若是则通过 out 参数返回基于大小+时间的快速伪哈希。
    /// 伪哈希格式：__gpu_{size}_{lastWriteTicks}，足以检测文件是否发生变化。
    /// </summary>
    private static bool IsLargeGpuResourceFile(FileInfo file, out string fastHash)
    {
        if (string.Equals(file.Extension, ".gpu_resources", StringComparison.OrdinalIgnoreCase) &&
            file.Length > LargeGpuFileThresholdBytes)
        {
            fastHash = $"__gpu_{file.Length}_{file.LastWriteTimeUtc.Ticks}";
            return true;
        }
        fastHash = string.Empty;
        return false;
    }

    /// <summary>
    /// 计算单个文件的 SHA-256 哈希值，返回小写十六进制字符串
    /// 使用异步流式读取，对大文件友好
    /// </summary>
    /// <param name="file">要计算哈希的文件</param>
    /// <returns>SHA-256 哈希值的十六进制字符串（小写）</returns>
    /// <exception cref="IOException">文件读取失败时抛出</exception>
    public static async Task<string> ComputeFileHashAsync(FileInfo file)
    {
        using var sha256 = SHA256.Create();
        using var stream = file.OpenRead();
        var hashBytes = await sha256.ComputeHashAsync(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// 计算目录中所有文件的 SHA-256 哈希值
    /// 返回以相对路径（使用 '/' 作为分隔符）为键、哈希十六进制字符串为值的字典
    /// </summary>
    /// <param name="directory">目标目录</param>
    /// <param name="progress">进度报告回调：(已检查数, 总数, 当前文件相对路径)</param>
    /// <returns>相对路径 → SHA-256哈希值的字典</returns>
    /// <exception cref="IOException">文件读取或哈希计算失败时抛出，包含失败文件路径信息</exception>
    public static async Task<Dictionary<string, string>> ComputeDirectoryHashesAsync(
        DirectoryInfo directory,
        IProgress<(int checkedCount, int totalCount, string currentFile)>? progress = null)
    {
        // 获取所有文件（包括子目录），按路径排序确保一致性
        var files = directory.GetFiles("*", SearchOption.AllDirectories)
            .OrderBy(f => f.FullName)
            .ToArray();

        var result = new Dictionary<string, string>(files.Length, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < files.Length; i++)
        {
            var file = files[i];

            // 计算相对于目录根部的相对路径，统一使用 '/' 作为目录分隔符
            var relativePath = file.FullName
                .Substring(directory.FullName.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');

            // 报告进度
            progress?.Report((i + 1, files.Length, relativePath));

            // 超大 GPU 资源文件跳过完整 SHA-256（1GB+ 的 .gpu_resources 文件逐字节哈希极慢且几乎不变动）
            if (IsLargeGpuResourceFile(file, out var fastHash))
            {
                result[relativePath] = fastHash;
                continue;
            }

            try
            {
                result[relativePath] = await ComputeFileHashAsync(file);
            }
            catch (Exception ex) when (ex is not IOException)
            {
                throw new IOException(_localizationService?["FileHashUtils.HashError"].Replace("{path}", relativePath).Replace("{message}", ex.Message) ?? $"无法计算文件「{relativePath}」的哈希值: {ex.Message}", ex);
            }
        }

        return result;
    }

    /// <summary>
    /// 带数据库缓存的计算目录哈希值 —— 优先从数据库读取有效的缓存哈希，
	/// 仅在文件元数据（大小/修改时间）发生变化时才重新计算 SHA-256。
	/// 计算完成后自动将新哈希值保存到数据库。
	/// </summary>
	/// <param name="directory">目标目录</param>
	/// <param name="modGuid">mod 的 Guid，用作数据库缓存键</param>
	/// <param name="repo">文件哈希仓储</param>
	/// <param name="storageDirectory">存储目录（用于数据库连接）</param>
	/// <param name="progress">进度报告回调：(已检查数, 总数, 当前文件相对路径, 缓存命中数)</param>
	/// <returns>相对路径 → SHA-256哈希值的字典</returns>
	public static async Task<Dictionary<string, string>> ComputeDirectoryHashesWithCacheAsync(
		DirectoryInfo directory,
		Guid modGuid,
		FileHashRepository repo,
		string storageDirectory,
		IProgress<(int checkedCount, int totalCount, string currentFile, int cacheHits)>? progress = null)
	{
		return await ComputeDirectoryHashesWithCacheInternalAsync(directory, modGuid, repo, storageDirectory, saveToDb: true, progress);
	}

	/// <summary>
	/// 只读缓存版——优先从数据库读取有效缓存哈希，仅在缓存未命中时重新计算，
	/// 但 <b>不</b> 将结果写回数据库。用于更新流程的阶段1（文件即将被替换，无需持久化中间状态）。
	/// </summary>
	/// <param name="directory">目标目录</param>
	/// <param name="modGuid">mod 的 Guid</param>
	/// <param name="repo">文件哈希仓储</param>
	/// <param name="storageDirectory">存储目录</param>
	/// <param name="progress">进度报告回调</param>
	/// <returns>相对路径 → SHA-256哈希值的字典</returns>
	public static async Task<Dictionary<string, string>> ComputeDirectoryHashesReadCacheAsync(
		DirectoryInfo directory,
		Guid modGuid,
		FileHashRepository repo,
		string storageDirectory,
		IProgress<(int checkedCount, int totalCount, string currentFile, int cacheHits)>? progress = null)
	{
		return await ComputeDirectoryHashesWithCacheInternalAsync(directory, modGuid, repo, storageDirectory, saveToDb: false, progress);
	}

	private static async Task<Dictionary<string, string>> ComputeDirectoryHashesWithCacheInternalAsync(
		DirectoryInfo directory,
		Guid modGuid,
		FileHashRepository repo,
		string storageDirectory,
		bool saveToDb,
		IProgress<(int checkedCount, int totalCount, string currentFile, int cacheHits)>? progress)
    {
        var files = directory.GetFiles("*", SearchOption.AllDirectories)
            .OrderBy(f => f.FullName)
            .ToArray();

        var result = new Dictionary<string, string>(files.Length, StringComparer.OrdinalIgnoreCase);
        var newHashes = new Dictionary<string, (string fileHash, long fileSize, DateTime lastModified)>(StringComparer.OrdinalIgnoreCase);
        int cacheHits = 0;

        for (int i = 0; i < files.Length; i++)
        {
            var file = files[i];

            var relativePath = file.FullName
                .Substring(directory.FullName.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');

            // 报告进度
            progress?.Report((i + 1, files.Length, relativePath, cacheHits));

            var fileSize = file.Length;
            var lastModified = file.LastWriteTimeUtc;

            // 优先检查数据库缓存：文件大小和修改时间均匹配则直接使用缓存哈希
            var cachedHash = repo.GetValidCacheHash(storageDirectory, modGuid, relativePath, fileSize, lastModified);
            if (cachedHash is not null)
            {
                result[relativePath] = cachedHash;
                cacheHits++;
                continue;
            }

            // 缓存未命中或已失效，计算 SHA-256 哈希

            // 超大 GPU 资源文件跳过完整 SHA-256（1GB+ 的 .gpu_resources 文件逐字节哈希极慢且几乎不变动）
            if (IsLargeGpuResourceFile(file, out var fastHash))
            {
                result[relativePath] = fastHash;
                newHashes[relativePath] = (fastHash, fileSize, lastModified);
                continue;
            }

            try
            {
                var hash = await ComputeFileHashAsync(file);
                result[relativePath] = hash;

                // 记录需要保存到数据库的新哈希值
                newHashes[relativePath] = (hash, fileSize, lastModified);
            }
            catch (Exception ex) when (ex is not IOException)
            {
                throw new IOException(_localizationService?["FileHashUtils.HashError"].Replace("{path}", relativePath).Replace("{message}", ex.Message) ?? $"无法计算文件「{relativePath}」的哈希值: {ex.Message}", ex);
            }
        }

        // 最后报告一次最终进度（包含最终缓存命中数）
        progress?.Report((files.Length, files.Length, "", cacheHits));

        // 仅在 saveToDb=true 时将新计算的哈希值保存到数据库缓存
        // saveToDb=false 用于更新流程阶段1（文件即将被替换，无需持久化中间状态）
        if (saveToDb && newHashes.Count > 0)
        {
            await repo.UpsertModHashesAsync(storageDirectory, modGuid, newHashes);
        }

        return result;
    }

    /// <summary>
    /// 比较两组文件哈希字典，识别出变更、新增和需要删除的文件
    /// </summary>
    /// <param name="currentHashes">当前版本的文件哈希字典（相对路径 → 哈希值）</param>
    /// <param name="newHashes">新版本的文件哈希字典（相对路径 → 哈希值）</param>
    /// <returns>比较结果，包含需要更新和删除的文件路径列表</returns>
    public static HashCompareResult CompareHashes(
        Dictionary<string, string> currentHashes,
        Dictionary<string, string> newHashes)
    {
        var changedFiles = new List<string>();  // 哈希值变化的文件（含新增）
        var deletedFiles = new List<string>();  // 新版本中不存在的文件（需删除）
        var unchangedCount = 0;                  // 未发生变化的文件数

        // 遍历新版本文件，找出变更和新增的
        foreach (var (relativePath, newHash) in newHashes)
        {
            if (currentHashes.TryGetValue(relativePath, out var currentHash))
            {
                if (!string.Equals(currentHash, newHash, StringComparison.OrdinalIgnoreCase))
                {
                    // 哈希值不同 → 文件已变更
                    changedFiles.Add(relativePath);
                }
                else
                {
                    // 哈希值相同 → 文件未变更，跳过
                    unchangedCount++;
                }
            }
            else
            {
                // 新版本有但当前版本没有 → 新增文件
                changedFiles.Add(relativePath);
            }
        }

        // 找出新版本中不存在的文件（需要删除）
        foreach (var (relativePath, _) in currentHashes)
        {
            if (!newHashes.ContainsKey(relativePath))
            {
                deletedFiles.Add(relativePath);
            }
        }

        return new HashCompareResult
        {
            ChangedFiles = changedFiles,
            DeletedFiles = deletedFiles,
            UnchangedCount = unchangedCount,
            TotalNewFiles = newHashes.Count,
            TotalCurrentFiles = currentHashes.Count,
        };
    }
}

/// <summary>
/// 文件哈希比对结果，描述两个版本之间文件的差异
/// </summary>
internal sealed class HashCompareResult
{
    /// <summary>需要更新的文件相对路径列表（含变更和新增）</summary>
    public required List<string> ChangedFiles { get; init; }

    /// <summary>需要删除的文件相对路径列表（新版本中不存在）</summary>
    public required List<string> DeletedFiles { get; init; }

    /// <summary>未发生变化的文件数量</summary>
    public int UnchangedCount { get; init; }

    /// <summary>新版本的文件总数</summary>
    public int TotalNewFiles { get; init; }

    /// <summary>当前版本的文件总数</summary>
    public int TotalCurrentFiles { get; init; }

    /// <summary>是否存在任何变更（包括新增、修改、删除）</summary>
    public bool HasChanges => ChangedFiles.Count > 0 || DeletedFiles.Count > 0;
}
