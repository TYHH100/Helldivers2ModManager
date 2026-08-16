using System.Buffers.Binary;
using System.IO;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 生成换甲产物 patch 三件套（主文件 + .gpu_resources + .stream）。
/// TOC 布局参考 VersionCheckService / HD2SDK-CommunityEdition / hd2-repatcher：
/// Header(72) + TypeEntry(32) + FileEntry(80)；main 数据紧接 TOC；
/// stream 与 gpu 数据按 64 字节对齐（与真实模组 patch 一致，游戏按 FileEntry
/// 偏移读取，布局顺序无关紧要）。
/// </summary>
internal sealed class PatchWriter
{
    private const int PatchHeaderMagic = unchecked((int)0xF0000011);
    private const int HeaderSize = 72;
    private const int TypeEntrySize = 32;
    private const int FileEntrySize = 80;
    private const int MainAlignment = 16;
    private const int GpuAlignment = 64;
    private const long MaxOutputBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>一个待写入的资源条目；GpuData/StreamData 为空表示无对应伴生载荷。</summary>
    public sealed record ResourceEntry(
        long FileId,
        long TypeId,
        byte[] MainData,
        byte[]? GpuData = null,
        byte[]? StreamData = null);

    /// <summary>
    /// 写出三件套。templateHeader 为来源 patch 的前 72 字节（游戏不校验其中的
    /// 内容相关字段，hd2-repatcher 更新工具只更新 numTypes/numFiles），仅覆盖
    /// numTypes/numFiles 两个字段。
    /// </summary>
    public void WritePatchFiles(
        string patchPath,
        byte[] templateHeader,
        IReadOnlyList<ResourceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(patchPath);
        ArgumentNullException.ThrowIfNull(templateHeader);
        ArgumentNullException.ThrowIfNull(entries);
        if (templateHeader.Length != HeaderSize)
            throw new ArgumentException("The patch header template must be exactly 72 bytes.", nameof(templateHeader));
        if (entries.Count == 0)
            throw new ArgumentException("The patch must contain at least one resource entry.", nameof(entries));
        if (entries.Count > 100000)
            throw new ArgumentException("The patch contains too many resource entries.", nameof(entries));

        // FileId 必须唯一（同一资源出现两次会互相覆盖且不可恢复）
        var fileIds = new HashSet<long>();
        foreach (var entry in entries)
        {
            if (!fileIds.Add(entry.FileId))
                throw new InvalidDataException($"Duplicate resource FileId 0x{entry.FileId:X16} in the patch output.");
            if (entry.MainData.Length == 0)
                throw new InvalidDataException($"Resource 0x{entry.FileId:X16} has empty main data.");
        }

        var typeCounts = entries
            .GroupBy(static entry => entry.TypeId)
            .ToDictionary(static group => group.Key, static group => group.Count());

        // ---- 布局计算 ----
        var numTypes = typeCounts.Count;
        var numFiles = entries.Count;
        var tocSize = checked(HeaderSize + (long)numTypes * TypeEntrySize + (long)numFiles * FileEntrySize);
        if (tocSize > MaxOutputBytes)
            throw new InvalidDataException("The patch TOC exceeds the output size limit.");

        var mainOffsets = new long[numFiles];
        var streamOffsets = new long[numFiles];
        var gpuOffsets = new long[numFiles];
        var mainCursor = tocSize;
        for (var i = 0; i < numFiles; i++)
        {
            mainOffsets[i] = mainCursor;
            mainCursor = checked(mainCursor + entries[i].MainData.Length);
        }

        // stream / gpu 数据偏移相对各自伴生文件（与真实模组一致：GpuOffset 相对
        // .gpu_resources 文件，StreamOffset 相对 .stream 文件），各自从 0 开始、64 对齐。
        var streamCursor = 0L;
        var streamTotal = 0L;
        for (var i = 0; i < numFiles; i++)
        {
            var data = entries[i].StreamData;
            if (data is { Length: > 0 })
            {
                streamOffsets[i] = Align(streamCursor, GpuAlignment);
                streamCursor = checked(streamOffsets[i] + data.Length);
                streamTotal += data.Length;
            }
        }

        var gpuCursor = 0L;
        var gpuTotal = 0L;
        for (var i = 0; i < numFiles; i++)
        {
            var data = entries[i].GpuData;
            if (data is { Length: > 0 })
            {
                gpuOffsets[i] = Align(gpuCursor, GpuAlignment);
                gpuCursor = checked(gpuOffsets[i] + data.Length);
                gpuTotal += data.Length;
            }
        }

        var mainTotal = mainCursor - tocSize;
        if (mainTotal > MaxOutputBytes || streamTotal > MaxOutputBytes || gpuTotal > MaxOutputBytes)
            throw new InvalidDataException("The patch output exceeds the size limit.");

        // ---- 主文件：Header + TypeEntry + FileEntry + main 数据 ----
        var directory = Path.GetDirectoryName(patchPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var gpuPath = patchPath + ".gpu_resources";
        var streamPath = patchPath + ".stream";

        // 与模组生态/部署逻辑一致：三件套总是齐全，无数据的伴生文件以 0 字节存在
        using (var patch = new FileStream(patchPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
        using (var gpu = new FileStream(gpuPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
        using (var stream = new FileStream(streamPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
        {
            // Header：模板 + numTypes/numFiles
            patch.Write(templateHeader, 0, HeaderSize);
            var headerSpan = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(headerSpan.AsSpan(0, 4), numTypes);
            BinaryPrimitives.WriteInt32LittleEndian(headerSpan.AsSpan(4, 4), numFiles);
            patch.Position = 4;
            patch.Write(headerSpan, 0, 8);
            patch.Position = HeaderSize;

            // TypeEntry（按 TypeId 排序保证输出确定性）
            foreach (var (typeId, count) in typeCounts.OrderBy(static pair => pair.Key))
            {
                var typeEntry = new byte[TypeEntrySize];
                BinaryPrimitives.WriteInt64LittleEndian(typeEntry.AsSpan(8, 8), typeId);
                BinaryPrimitives.WriteUInt64LittleEndian(typeEntry.AsSpan(16, 8), (ulong)count);
                BinaryPrimitives.WriteInt32LittleEndian(typeEntry.AsSpan(24, 4), MainAlignment);
                BinaryPrimitives.WriteInt32LittleEndian(typeEntry.AsSpan(28, 4), GpuAlignment);
                patch.Write(typeEntry, 0, TypeEntrySize);
            }

            // FileEntry
            for (var i = 0; i < numFiles; i++)
            {
                var entry = entries[i];
                var fileEntry = new byte[FileEntrySize];
                BinaryPrimitives.WriteInt64LittleEndian(fileEntry.AsSpan(0, 8), entry.FileId);
                BinaryPrimitives.WriteInt64LittleEndian(fileEntry.AsSpan(8, 8), entry.TypeId);
                BinaryPrimitives.WriteInt64LittleEndian(fileEntry.AsSpan(16, 8), mainOffsets[i]);
                BinaryPrimitives.WriteInt64LittleEndian(fileEntry.AsSpan(24, 8), streamOffsets[i]);
                BinaryPrimitives.WriteInt64LittleEndian(fileEntry.AsSpan(32, 8), gpuOffsets[i]);
                BinaryPrimitives.WriteInt32LittleEndian(fileEntry.AsSpan(56, 4), entry.MainData.Length);
                BinaryPrimitives.WriteInt32LittleEndian(fileEntry.AsSpan(60, 4), entry.StreamData?.Length ?? 0);
                BinaryPrimitives.WriteInt32LittleEndian(fileEntry.AsSpan(64, 4), entry.GpuData?.Length ?? 0);
                BinaryPrimitives.WriteInt32LittleEndian(fileEntry.AsSpan(76, 4), i + 1);
                patch.Write(fileEntry, 0, FileEntrySize);
            }

            // main 数据（按条目顺序紧接 TOC）
            for (var i = 0; i < numFiles; i++)
                patch.Write(entries[i].MainData, 0, entries[i].MainData.Length);

            // stream / gpu 数据（各自文件内 64 字节对齐；FileStream 定位会自动补零）
            for (var i = 0; i < numFiles; i++)
            {
                var data = entries[i].StreamData;
                if (data is { Length: > 0 })
                {
                    stream.Position = streamOffsets[i];
                    stream.Write(data, 0, data.Length);
                }
            }
            for (var i = 0; i < numFiles; i++)
            {
                var data = entries[i].GpuData;
                if (data is { Length: > 0 })
                {
                    gpu.Position = gpuOffsets[i];
                    gpu.Write(data, 0, data.Length);
                }
            }
        }
    }

    private static long Align(long value, long alignment) =>
        (value + alignment - 1) / alignment * alignment;
}
