using System.Buffers.Binary;
using System.Text;
using Helldivers2ModManager.Core.Mods;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Mods;

[TestClass]
public sealed class ModTypeDetectionServiceTests
{
    [TestMethod]
    public async Task DetectAsync_ShouldClassifyAudioPathHint()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hd2mm-detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var payload = "content/audio/object_terminal_sound"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(root, "0123456789abcdef.patch_0"), CreatePatch(payload));
            var detection = await new ModTypeDetectionService().DetectAsync(new DirectoryInfo(root));

            Assert.AreEqual(ModType.Audio, detection.Type);
            Assert.AreEqual(1, detection.PatchesScanned);
            Assert.IsTrue(detection.PathHints.Single().Contains("content/audio", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void AggregateTypes_ShouldPrioritizeSpecificTagsAndDropFallbacks()
    {
        var types = ModTypeDetectionService.AggregateTypes(
            ModType.SupportWeapon,
            [ModType.Model, ModType.Ui, ModType.Texture]);

        Assert.AreEqual(ModType.SupportWeapon, types[0]);
        CollectionAssert.Contains(types.ToList(), ModType.SupportWeapon);
        CollectionAssert.DoesNotContain(types.ToList(), ModType.Ui);
        CollectionAssert.DoesNotContain(types.ToList(), ModType.Model);
    }

    private static byte[] CreatePatch(byte[] payload)
    {
        const int headerSize = 72;
        const int typeEntrySize = 32;
        const int fileEntrySize = 80;
        var mainOffset = headerSize + typeEntrySize + fileEntrySize;
        var bytes = new byte[mainOffset + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0), unchecked((int)0xF0000011));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(headerSize + 8), 0x0000000000000001UL);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(headerSize + 16), 1);

        var entry = headerSize + typeEntrySize;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry), 0x123456789ABCDEF0UL);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry + 8), 0x0000000000000001UL);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry + 16), (ulong)mainOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 56), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 76), 1);
        payload.CopyTo(bytes, mainOffset);
        return bytes;
    }
}
