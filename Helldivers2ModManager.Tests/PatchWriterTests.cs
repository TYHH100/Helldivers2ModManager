using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Buffers.Binary;
using System.Text;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class PatchWriterTests
{
    private static readonly byte[] TemplateHeader = CreateTemplateHeader();

    private static byte[] CreateTemplateHeader()
    {
        // 模拟真实模组 header：magic + 0 填充 + 常量字段（内容字段不被游戏校验）
        var header = new byte[72];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), unchecked((int)0xF0000011));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), unchecked((int)0xF4F509CE));
        return header;
    }

    private sealed class ParsedEntry
    {
        public required long FileId { get; init; }
        public required long TypeId { get; init; }
        public required long MainOffset { get; init; }
        public required long StreamOffset { get; init; }
        public required long GpuOffset { get; init; }
        public required int MainSize { get; init; }
        public required int StreamSize { get; init; }
        public required int GpuSize { get; init; }
        public required int EntryIndex { get; init; }
    }

    private static (int NumTypes, int NumFiles, List<ParsedEntry> Entries) ParseToc(byte[] patch)
    {
        Assert.AreEqual(unchecked((int)0xF0000011), BinaryPrimitives.ReadInt32LittleEndian(patch.AsSpan(0, 4)));
        var numTypes = BinaryPrimitives.ReadInt32LittleEndian(patch.AsSpan(4, 4));
        var numFiles = BinaryPrimitives.ReadInt32LittleEndian(patch.AsSpan(8, 4));
        var entriesStart = 72 + numTypes * 32;
        var entries = new List<ParsedEntry>(numFiles);
        for (var i = 0; i < numFiles; i++)
        {
            var offset = entriesStart + i * 80;
            entries.Add(new ParsedEntry
            {
                FileId = BinaryPrimitives.ReadInt64LittleEndian(patch.AsSpan(offset + 0, 8)),
                TypeId = BinaryPrimitives.ReadInt64LittleEndian(patch.AsSpan(offset + 8, 8)),
                MainOffset = BinaryPrimitives.ReadInt64LittleEndian(patch.AsSpan(offset + 16, 8)),
                StreamOffset = BinaryPrimitives.ReadInt64LittleEndian(patch.AsSpan(offset + 24, 8)),
                GpuOffset = BinaryPrimitives.ReadInt64LittleEndian(patch.AsSpan(offset + 32, 8)),
                MainSize = BinaryPrimitives.ReadInt32LittleEndian(patch.AsSpan(offset + 56, 4)),
                StreamSize = BinaryPrimitives.ReadInt32LittleEndian(patch.AsSpan(offset + 60, 4)),
                GpuSize = BinaryPrimitives.ReadInt32LittleEndian(patch.AsSpan(offset + 64, 4)),
                EntryIndex = BinaryPrimitives.ReadInt32LittleEndian(patch.AsSpan(offset + 76, 4))
            });
        }
        return (numTypes, numFiles, entries);
    }

    private static void AssertByteRange(byte[] file, long offset, int size, byte[] expected, string label)
    {
        Assert.IsTrue(offset >= 0 && offset + size <= file.Length, $"{label} exceeds file bounds.");
        CollectionAssert.AreEqual(expected, file.AsSpan((int)offset, size).ToArray(), label);
    }

    [TestMethod]
    public void WritePatchFiles_LayoutAndPayloadsMatch_WithGpuAndStream()
    {
        var dir = Directory.CreateTempSubdirectory("hd2mm-patchwriter-");
        try
        {
            var patchPath = Path.Combine(dir.FullName, "9ba626afa44a3aa3.patch_0");
            var unitMain = new byte[0x200];
            BinaryPrimitives.WriteUInt32LittleEndian(unitMain.AsSpan(0x2C, 4), 10800438);
            BinaryPrimitives.WriteInt32LittleEndian(unitMain.AsSpan(0x4C, 4), 0);
            var materialMain = Encoding.UTF8.GetBytes("material-payload");
            var textureMain = new byte[0x80];
            var gpuUnit = new byte[1234];
            var gpuTexture = new byte[5678];
            var streamTexture = new byte[999];
            for (var i = 0; i < gpuUnit.Length; i++) gpuUnit[i] = (byte)(i * 3);
            for (var i = 0; i < gpuTexture.Length; i++) gpuTexture[i] = (byte)(i * 5);
            for (var i = 0; i < streamTexture.Length; i++) streamTexture[i] = (byte)(i * 7);

            var writer = new PatchWriter();
            writer.WritePatchFiles(patchPath, TemplateHeader,
            [
                new PatchWriter.ResourceEntry(unchecked((long)0x1111111111111111UL), unchecked((long)0xE0A48D0BE9A7453FUL), unitMain, gpuUnit),
                new PatchWriter.ResourceEntry(unchecked((long)0x2222222222222222UL), unchecked((long)0xEAC0B497876ADEDFUL), materialMain),
                new PatchWriter.ResourceEntry(unchecked((long)0x3333333333333333UL), unchecked((long)0xCD4238C6A0C69E32UL), textureMain, gpuTexture, streamTexture)
            ]);

            var patch = File.ReadAllBytes(patchPath);
            var gpu = File.ReadAllBytes(patchPath + ".gpu_resources");
            var stream = File.ReadAllBytes(patchPath + ".stream");

            var (numTypes, numFiles, entries) = ParseToc(patch);
            Assert.AreEqual(3, numTypes);
            Assert.AreEqual(3, numFiles);
            Assert.AreEqual(3, entries.Count);

            // 类型计数：Unit/Material/Texture 各 1
            var typeCounts = entries.GroupBy(static e => e.TypeId).ToDictionary(static g => g.Key, static g => g.Count());
            Assert.AreEqual(1, typeCounts[unchecked((long)0xE0A48D0BE9A7453FUL)]);
            Assert.AreEqual(1, typeCounts[unchecked((long)0xEAC0B497876ADEDFUL)]);
            Assert.AreEqual(1, typeCounts[unchecked((long)0xCD4238C6A0C69E32UL)]);

            // FileEntry 偏移/大小与数据一致
            var unitEntry = entries.Single(e => e.FileId == unchecked((long)0x1111111111111111UL));
            Assert.AreEqual(unitMain.Length, unitEntry.MainSize);
            Assert.AreEqual(gpuUnit.Length, unitEntry.GpuSize);
            Assert.AreEqual(0, unitEntry.StreamSize);
            Assert.AreEqual(1, unitEntry.EntryIndex);

            var materialEntry = entries.Single(e => e.FileId == unchecked((long)0x2222222222222222UL));
            Assert.AreEqual(materialMain.Length, materialEntry.MainSize);
            Assert.AreEqual(0L, materialEntry.GpuSize);

            var textureEntry = entries.Single(e => e.FileId == unchecked((long)0x3333333333333333UL));
            Assert.AreEqual(gpuTexture.Length, textureEntry.GpuSize);
            Assert.AreEqual(streamTexture.Length, textureEntry.StreamSize);

            // main 数据紧接 TOC 且字节一致
            var tocEnd = 72 + numTypes * 32 + numFiles * 80;
            AssertByteRange(patch, unitEntry.MainOffset, unitEntry.MainSize, unitMain, "unit main");
            AssertByteRange(patch, materialEntry.MainOffset, materialEntry.MainSize, materialMain, "material main");
            AssertByteRange(patch, textureEntry.MainOffset, textureEntry.MainSize, textureMain, "texture main");
            Assert.AreEqual(tocEnd, unitEntry.MainOffset);

            // stream/gpu 64 字节对齐 + 字节一致
            Assert.AreEqual(0, streamTexture.Length == 0 ? 0 : textureEntry.StreamOffset % 64);
            AssertByteRange(stream, textureEntry.StreamOffset, textureEntry.StreamSize, streamTexture, "stream payload");
            Assert.AreEqual(0L, gpuUnit.Length == 0 ? 0 : unitEntry.GpuOffset % 64);
            Assert.AreEqual(0L, gpuTexture.Length == 0 ? 0 : textureEntry.GpuOffset % 64);
            AssertByteRange(gpu, unitEntry.GpuOffset, unitEntry.GpuSize, gpuUnit, "unit gpu");
            AssertByteRange(gpu, textureEntry.GpuOffset, textureEntry.GpuSize, gpuTexture, "texture gpu");

            // header 模板保留：magic 与常量字段来自模板
            Assert.AreEqual(unchecked((int)0xF0000011), BinaryPrimitives.ReadInt32LittleEndian(patch.AsSpan(0, 4)));
            Assert.AreEqual(unchecked((int)0xF4F509CE), BinaryPrimitives.ReadInt32LittleEndian(patch.AsSpan(16, 4)));
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    [TestMethod]
    public void WritePatchFiles_NoCompanionData_KeepsEmptyCompanionFiles()
    {
        var dir = Directory.CreateTempSubdirectory("hd2mm-patchwriter-");
        try
        {
            var patchPath = Path.Combine(dir.FullName, "abc.patch_0");
            var writer = new PatchWriter();
            writer.WritePatchFiles(patchPath, TemplateHeader,
            [
                new PatchWriter.ResourceEntry(unchecked((long)0xAAAAAAAAAAAAAAAAUL), unchecked((long)0xEAC0B497876ADEDFUL), new byte[16])
            ]);

            Assert.IsTrue(File.Exists(patchPath));
            // 与模组生态一致：三件套总是齐全，无数据的伴生文件以 0 字节存在
            Assert.IsTrue(File.Exists(patchPath + ".gpu_resources"));
            Assert.IsTrue(File.Exists(patchPath + ".stream"));
            Assert.AreEqual(0L, new FileInfo(patchPath + ".gpu_resources").Length);
            Assert.AreEqual(0L, new FileInfo(patchPath + ".stream").Length);
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    [TestMethod]
    public void WritePatchFiles_DuplicateFileId_Throws()
    {
        var dir = Directory.CreateTempSubdirectory("hd2mm-patchwriter-");
        try
        {
            var patchPath = Path.Combine(dir.FullName, "abc.patch_0");
            var writer = new PatchWriter();
            var ex = Assert.ThrowsException<InvalidDataException>(() => writer.WritePatchFiles(patchPath, TemplateHeader,
            [
                new PatchWriter.ResourceEntry(unchecked((long)0xAAAAAAAAAAAAAAAAUL), unchecked((long)0xEAC0B497876ADEDFUL), new byte[16]),
                new PatchWriter.ResourceEntry(unchecked((long)0xAAAAAAAAAAAAAAAAUL), unchecked((long)0xEAC0B497876ADEDFUL), new byte[16])
            ]));
            StringAssert.Contains(ex.Message, "Duplicate");
            Assert.IsFalse(File.Exists(patchPath), "No partial output may remain after a layout failure.");
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    [TestMethod]
    public void WritePatchFiles_EmptyEntries_Throws()
    {
        var writer = new PatchWriter();
        Assert.ThrowsException<ArgumentException>(() =>
            writer.WritePatchFiles(Path.Combine(Path.GetTempPath(), "x.patch_0"), TemplateHeader, []));
    }
}
