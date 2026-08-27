using System.Buffers.Binary;
using Helldivers2ModManager.Core.GameData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.GameData;

[TestClass]
public sealed class GameArchiveServiceTests
{
    [TestMethod]
    public async Task ResolveUnitsAsync_ShouldDecodeSyntheticUncompressedArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2mm-game-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bundleData = CreateBundleWithCurrentUnit();
            var mainOffset = BinaryPrimitives.ReadUInt64LittleEndian(bundleData.AsSpan(TocEntryOffset + 16, 8));
            WriteUncompressedDsar(Path.Combine(root, "bundles.nxa"), CreateBundleIndex(root));
            WriteUncompressedDsar(
                Path.Combine(root, "bundles.00.nxa"),
                new[]
                {
                    ((ulong)0, new ArraySegment<byte>(bundleData, 0, (int)mainOffset).ToArray()),
                    ((ulong)mainOffset, new ArraySegment<byte>(bundleData, (int)mainOffset, bundleData.Length - (int)mainOffset).ToArray()),
                });

            using var service = new GameArchiveService(NullLogger<GameArchiveService>.Instance);
            var result = await service.ResolveUnitsAsync(new DirectoryInfo(root), [0x123456789ABCDEF0L]);

            Assert.IsNull(result.ErrorMessage, result.ErrorMessage);
            Assert.AreEqual(0, result.MissingUnitIds.Count);
            Assert.IsTrue(result.References.TryGetValue(0x123456789ABCDEF0L, out var reference));
            Assert.IsNotNull(reference);
            Assert.AreEqual(10800438u, reference!.Version);
            Assert.AreEqual("packages/test", reference.PackageName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private const int TocEntryOffset = 104;

    private static byte[] CreateBundleIndex(string root)
    {
        var name = "packages/test"u8.ToArray();
        var data = new byte[0x80];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x0C), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x10), 1);
        
        name.CopyTo(data, 0x30);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x20), 0x30);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x24), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x28), 0x40);
        return data;
    }

    private static byte[] CreateBundleWithCurrentUnit()
    {
        const int typeTableSize = 32;
        const int tocEntrySize = 80;
        var mainOffset = 72 + typeTableSize + tocEntrySize;
        var bytes = new byte[mainOffset + 128];

        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0), unchecked((int)0xF0000011));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);

        const ulong unitTypeId = 0xE0A48D0BE9A7453FUL;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(72 + 8), unitTypeId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(72 + 16), 1);

        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(TocEntryOffset), 0x123456789ABCDEF0UL);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(TocEntryOffset + 8), unitTypeId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(TocEntryOffset + 16), (ulong)mainOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(TocEntryOffset + 56), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(TocEntryOffset + 76), 1);

        var unit = mainOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(unit + 0x2C), 10800438);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(unit + 0x30), 0x68);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(unit + 0x34), 0x6C);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(unit + 0x60), 120);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(unit + 0x68), 0);
        return bytes;
    }

    private static void WriteUncompressedDsar(string path, byte[] payload)
    {
        WriteUncompressedDsar(path, [((ulong)0, payload)]);
    }

    private static void WriteUncompressedDsar(string path, IReadOnlyList<(ulong Offset, byte[] Payload)> chunks)
    {
        using var stream = File.Create(path);
        var header = new byte[0x20];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), chunks.Count);
        stream.Write(header);
        var compressedOffset = 0x20L + chunks.Count * 0x20L;
        foreach (var (offset, payload) in chunks)
        {
            var entry = new byte[0x20];
            BinaryPrimitives.WriteUInt64LittleEndian(entry, offset);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(8, 8), (ulong)compressedOffset);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(16, 4), payload.Length);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(20, 4), payload.Length);
            entry[24] = 0;
            entry[25] = 2;
            stream.Write(entry);
            compressedOffset += payload.Length;
        }

        foreach (var (_, payload) in chunks)
        {
            stream.Write(payload);
        }
    }
    [TestMethod]
    public async Task BuildCompanionRecipeAsync_ShouldMatchExactGameData()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2mm-companion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
            var bundleData = CreateCompanionPatch(payload);
            var mainOffset = BinaryPrimitives.ReadUInt64LittleEndian(bundleData.AsSpan(TocEntryOffset + 16, 8));
            var companionOffset = (ulong)bundleData.Length;
            var patchPath = Path.Combine(root, "example.patch_0");
            File.WriteAllBytes(patchPath, CreateCompanionPatch(payload));

            WriteUncompressedDsar(Path.Combine(root, "bundles.nxa"), CreateCompanionBundleIndex(companionOffset));
            WriteUncompressedDsar(
                Path.Combine(root, "bundles.00.nxa"),
                new[]
                {
                    ((ulong)0, bundleData.AsSpan(0, (int)mainOffset).ToArray()),
                    ((ulong)mainOffset, bundleData.AsSpan((int)mainOffset, bundleData.Length - (int)mainOffset).ToArray()),
                    ((ulong)companionOffset, payload),
                });

            using var service = new GameArchiveService(NullLogger<GameArchiveService>.Instance);
            var result = await service.BuildCompanionRecipeAsync(
                new DirectoryInfo(root),
                new FileInfo(patchPath),
                GameCompanionKind.GpuResources,
                includePayloads: true);

            Assert.IsNull(result.ErrorMessage, result.ErrorMessage);
            Assert.IsNotNull(result.Recipe);
            Assert.AreEqual("Current game bundles (1 exact segment(s))", result.Recipe!.Description);
            Assert.AreEqual(16L, result.Recipe.Length);
            var segment = result.Recipe.Segments.Single();
            Assert.IsInstanceOfType(segment, typeof(GameCompanionSegment));
            Assert.AreEqual(0UL, segment.TargetOffset);
            Assert.AreEqual(16u, segment.Size);
            Assert.AreEqual("packages/test.gpu_resources", segment.PackageName);
            CollectionAssert.AreEqual(payload, segment.Payload);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreateCompanionBundleIndex(ulong companionOffset)
    {
        var data = new byte[0xE0];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x0C), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x10), 2);

        "packages/test"u8.CopyTo(data.AsSpan(0x60));
        "packages/test.gpu_resources"u8.CopyTo(data.AsSpan(0x78));
        WritePackageRecord(data, 0x18, 0x60, 0xC0);
        WritePackageRecord(data, 0x30, 0x78, 0xD0);
        WriteArchiveItem(data, 0xC0, 0, 0);
        WriteArchiveItem(data, 0xD0, (uint)companionOffset, 0);
        return data;
    }

    private static void WritePackageRecord(byte[] data, int offset, uint nameOffset, uint itemsOffset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 8), nameOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 16), itemsOffset);
    }

    private static void WriteArchiveItem(byte[] data, int offset, uint bundleOffset, ulong archiveOffset)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), archiveOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 8), bundleOffset);
        data[offset + 15] = 0;
    }

    private static byte[] CreateCompanionPatch(byte[] payload)
    {
        const int mainOffset = 184;
        var bytes = new byte[mainOffset + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0), unchecked((int)0xF0000011));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);

        const ulong gpuResourceTypeId = 0xD1A11BBD865C3F55UL;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(80), gpuResourceTypeId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(88), 1);

        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(TocEntryOffset), 0x123456789ABCDEF0UL);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(TocEntryOffset + 8), gpuResourceTypeId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(TocEntryOffset + 16), mainOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(TocEntryOffset + 56), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(TocEntryOffset + 64), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(TocEntryOffset + 76), 1);
        payload.CopyTo(bytes, mainOffset);
        return bytes;
    }
}
