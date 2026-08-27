using System.Buffers.Binary;
using Helldivers2ModManager.Core.PatchKit;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.PatchKit;

[TestClass]
public sealed class PatchFileParserTests
{
    private const ulong UnitTypeId = 0xE0A48D0BE9A7453FUL;

    [TestMethod]
    public async Task ParseAsync_ShouldReadMinimalCurrentUnitPatch()
    {
        await using var patch = new MemoryStream(CreatePatch());
        await using var gpu = new MemoryStream(new byte[64]);
        await using var stream = new MemoryStream(new byte[64]);

        var result = await new PatchFileParser().ParseAsync(patch, gpu, stream, "minimal.patch_0");

        Assert.IsNotNull(result.Snapshot);
        var snapshot = result.Snapshot!;
        Assert.AreEqual(1, snapshot.Header.TypeCount);
        Assert.AreEqual(1, snapshot.Header.FileCount);
        Assert.IsFalse(snapshot.HasErrors);
        Assert.AreEqual(1u, snapshot.Entries.Single().EntryIndex);
        Assert.IsTrue(snapshot.RequiresGpuResources);
        Assert.IsTrue(snapshot.RequiresStream);
        Assert.AreEqual(1, snapshot.Units.Count);
        Assert.AreEqual(10800438u, snapshot.Units[0].Version);
        Assert.AreEqual(0, snapshot.Units[0].Streams.Count);
    }

    [TestMethod]
    public async Task ParseAsync_ShouldRejectInvalidMagic()
    {
        var bytes = CreatePatch();
        bytes[3] = 0x12;
        await using var patch = new MemoryStream(bytes);

        var result = await new PatchFileParser().ParseAsync(patch, null, null, "invalid.patch_0");

        Assert.IsNull(result.Snapshot);
        Assert.AreEqual("InvalidMagic", result.Issues.Single().Code);
    }

    private static byte[] CreatePatch()
    {
        const int headerSize = 72;
        const int typeTableSize = 32;
        const int tocEntrySize = 80;
        const int unitHeaderSize = 0x68;
        var mainOffset = headerSize + typeTableSize + tocEntrySize;
        var bytes = new byte[mainOffset + 108];

        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0), unchecked((int)0xF0000011));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);

        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(headerSize + 8), UnitTypeId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(headerSize + 16), 1);

        var entry = headerSize + typeTableSize;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry), 0x123456789ABCDEF0UL);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry + 8), UnitTypeId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry + 16), (ulong)mainOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry + 24), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry + 32), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 56), 108);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 60), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 64), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 76), 1);

        var unit = mainOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(unit + 0x2C), 10800438);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(unit + 0x5C), unitHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(unit + unitHeaderSize), 0);

        return bytes;
    }
}
