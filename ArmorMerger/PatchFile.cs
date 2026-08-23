using System.IO;
using System.Runtime.InteropServices;

namespace ArmorMerger;

/// <summary>
/// Helldivers 2 补丁文件解析器。
/// 格式参考 HD2SDK-CommunityEdition 和 hd2-repatcher。
/// 补丁文件结构：
///   Header (72 bytes): Magic(4) + NumTypes(4) + NumFiles(4) + reserved(60)
///   Type entries (32 bytes * NumTypes)
///   File entries (80 bytes * NumFiles)
///   TOC data (Unit数据区)
///
/// File entry 字段（80 bytes）：
///   FileId(8) + TypeId(8) + TocOffset(8) + StreamOffset(8) + GpuOffset(8)
///   + TocSize(4) + StreamSize(4) + GpuSize(4) + reserved(12) + EntryIndex(4)
/// </summary>
internal sealed class PatchFile
{
    public const int HeaderSize = 72;
    public const int TypeEntrySize = 32;
    public const int FileEntrySize = 80;
    public const int PatchHeaderMagic = unchecked((int)0xF0000011);

    public byte[] RawHeader { get; set; } = new byte[HeaderSize];

    public int NumTypes { get; set; }
    public int NumFiles { get; set; }
    public List<TypeEntry> Types { get; set; } = [];
    public List<PatchFileEntry> Files { get; set; } = [];

    /// <summary>
    /// 从文件加载补丁 TOC（不含数据区）。
    /// </summary>
    public static PatchFile Load(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        return LoadFromBytes(bytes);
    }

    /// <summary>
    /// 从字节数组加载补丁。
    /// </summary>
    public static PatchFile LoadFromBytes(byte[] bytes)
    {
        var patch = new PatchFile();

        // 读取头部
        Array.Copy(bytes, patch.RawHeader, HeaderSize);
        var magic = MemoryMarshal.Read<int>(bytes.AsSpan(0, 4));
        if (magic != PatchHeaderMagic)
            throw new InvalidDataException($"无效的补丁魔数: 0x{magic:X8}，期望 0x{PatchHeaderMagic:X8}");

        patch.NumTypes = MemoryMarshal.Read<int>(bytes.AsSpan(4, 4));
        patch.NumFiles = MemoryMarshal.Read<int>(bytes.AsSpan(8, 4));

        // 读取Type条目
        for (var i = 0; i < patch.NumTypes; i++)
        {
            var offset = HeaderSize + (i * TypeEntrySize);
            var typeId = MemoryMarshal.Read<long>(bytes.AsSpan(offset + 8, 8));
            var resourceCount = MemoryMarshal.Read<ulong>(bytes.AsSpan(offset + 16, 8));
            patch.Types.Add(new TypeEntry
            {
                TypeId = typeId,
                ResourceCount = (int)resourceCount
            });
        }

        // 读取File条目
        var fileEntriesOffset = HeaderSize + (patch.NumTypes * TypeEntrySize);
        for (var i = 0; i < patch.NumFiles; i++)
        {
            var offset = fileEntriesOffset + (i * FileEntrySize);
            var entry = new PatchFileEntry
            {
                FileId = MemoryMarshal.Read<long>(bytes.AsSpan(offset, 8)),
                TypeId = MemoryMarshal.Read<long>(bytes.AsSpan(offset + 8, 8)),
                TocOffset = MemoryMarshal.Read<ulong>(bytes.AsSpan(offset + 16, 8)),
                StreamOffset = MemoryMarshal.Read<ulong>(bytes.AsSpan(offset + 24, 8)),
                GpuOffset = MemoryMarshal.Read<ulong>(bytes.AsSpan(offset + 32, 8)),
                TocSize = MemoryMarshal.Read<uint>(bytes.AsSpan(offset + 56, 4)),
                StreamSize = MemoryMarshal.Read<uint>(bytes.AsSpan(offset + 60, 4)),
                GpuSize = MemoryMarshal.Read<uint>(bytes.AsSpan(offset + 64, 4)),
                EntryIndex = MemoryMarshal.Read<uint>(bytes.AsSpan(offset + 76, 4))
            };
            patch.Files.Add(entry);
        }

        return patch;
    }

    /// <summary>
    /// 读取所有 Unit 的 TOC 数据。
    /// </summary>
    public Dictionary<int, byte[]> ReadAllTocData(string patchFilePath)
    {
        var bytes = File.ReadAllBytes(patchFilePath);
        var fileEntriesOffset = HeaderSize + (NumTypes * TypeEntrySize);
        var dataStartOffset = fileEntriesOffset + (NumFiles * FileEntrySize);
        var result = new Dictionary<int, byte[]>();

        for (var i = 0; i < NumFiles; i++)
        {
            var entry = Files[i];
            var dataOffset = (long)dataStartOffset + (long)entry.TocOffset;
            var data = new byte[entry.TocSize];
            Array.Copy(bytes, dataOffset, data, 0, entry.TocSize);
            result[i] = data;
        }

        return result;
    }

    /// <summary>
    /// 读取指定 Unit 的 TOC 数据。
    /// </summary>
    public byte[] ReadTocData(string patchFilePath, int unitIndex)
    {
        var bytes = File.ReadAllBytes(patchFilePath);
        var entry = Files[unitIndex];
        var fileEntriesOffset = HeaderSize + (NumTypes * TypeEntrySize);
        var dataStartOffset = fileEntriesOffset + (NumFiles * FileEntrySize);
        var dataOffset = (long)dataStartOffset + (long)entry.TocOffset;
        var data = new byte[entry.TocSize];
        Array.Copy(bytes, dataOffset, data, 0, entry.TocSize);
        return data;
    }

    /// <summary>
    /// 读取 GPU 资源文件中指定 Unit 的数据。
    /// </summary>
    public byte[] ReadGpuData(string gpuFilePath, int unitIndex)
    {
        var entry = Files[unitIndex];
        using var stream = File.OpenRead(gpuFilePath);
        var data = new byte[entry.GpuSize];
        stream.Seek((long)entry.GpuOffset, SeekOrigin.Begin);
        stream.ReadExactly(data, 0, (int)entry.GpuSize);
        return data;
    }

    /// <summary>
    /// 保存补丁 TOC 和数据到文件。
    /// </summary>
    public void Save(string filePath, IReadOnlyList<byte[]>? tocDataList = null)
    {
        using var stream = File.Create(filePath);
        Save(stream, tocDataList);
    }

    /// <summary>
    /// 保存补丁到流。
    /// </summary>
    public void Save(Stream stream, IReadOnlyList<byte[]>? tocDataList = null)
    {
        // 写入头部（保留原始数据）
        stream.Write(RawHeader, 0, HeaderSize);

        // 更新NumTypes和NumFiles
        stream.Seek(4, SeekOrigin.Begin);
        WriteInt(stream, NumTypes);
        WriteInt(stream, NumFiles);
        stream.Seek(0, SeekOrigin.End);

        // 写入Type条目
        foreach (var type in Types)
        {
            var typeEntry = new byte[TypeEntrySize];
            MemoryMarshal.Write(typeEntry.AsSpan(8, 8), type.TypeId);
            MemoryMarshal.Write(typeEntry.AsSpan(16, 8), (ulong)type.ResourceCount);
            stream.Write(typeEntry, 0, TypeEntrySize);
        }

        // 写入File条目
        foreach (var file in Files)
        {
            var fileEntry = new byte[FileEntrySize];
            MemoryMarshal.Write(fileEntry.AsSpan(0, 8), file.FileId);
            MemoryMarshal.Write(fileEntry.AsSpan(8, 8), file.TypeId);
            MemoryMarshal.Write(fileEntry.AsSpan(16, 8), file.TocOffset);
            MemoryMarshal.Write(fileEntry.AsSpan(24, 8), file.StreamOffset);
            MemoryMarshal.Write(fileEntry.AsSpan(32, 8), file.GpuOffset);
            MemoryMarshal.Write(fileEntry.AsSpan(56, 4), file.TocSize);
            MemoryMarshal.Write(fileEntry.AsSpan(60, 4), file.StreamSize);
            MemoryMarshal.Write(fileEntry.AsSpan(64, 4), file.GpuSize);
            MemoryMarshal.Write(fileEntry.AsSpan(76, 4), file.EntryIndex);
            stream.Write(fileEntry, 0, FileEntrySize);
        }

        // 写入TOC数据
        if (tocDataList is not null)
        {
            foreach (var data in tocDataList)
                stream.Write(data, 0, data.Length);
        }
    }

    private static void WriteInt(Stream stream, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        stream.Write(bytes, 0, 4);
    }
}

internal sealed class TypeEntry
{
    public long TypeId { get; set; }
    public int ResourceCount { get; set; }
}

/// <summary>
/// 补丁文件中的 File 条目（80 bytes）。
/// </summary>
internal sealed class PatchFileEntry
{
    public long FileId { get; set; }
    public long TypeId { get; set; }
    /// <summary>TOC 数据在 patch 文件中的偏移（相对数据区起始）</summary>
    public ulong TocOffset { get; set; }
    /// <summary>数据在 stream 文件中的偏移</summary>
    public ulong StreamOffset { get; set; }
    /// <summary>数据在 gpu_resources 文件中的偏移</summary>
    public ulong GpuOffset { get; set; }
    /// <summary>TOC 数据在 patch 文件中的大小</summary>
    public uint TocSize { get; set; }
    /// <summary>数据在 stream 文件中的大小</summary>
    public uint StreamSize { get; set; }
    /// <summary>数据在 gpu_resources 文件中的大小</summary>
    public uint GpuSize { get; set; }
    /// <summary>1-based 索引</summary>
    public uint EntryIndex { get; set; }

    public override string ToString()
        => $"FileId=0x{FileId:X16}, TocOff={TocOffset}, TocSize={TocSize}, GpuOff={GpuOffset}, GpuSize={GpuSize}";
}
