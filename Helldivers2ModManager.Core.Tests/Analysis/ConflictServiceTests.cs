using Helldivers2ModManager.Core.Analysis;
using System.Buffers.Binary;
using Helldivers2ModManager.Core.PatchKit;
using Helldivers2ModManager.Core.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Analysis;

[TestClass]
public sealed class ConflictServiceTests
{
    [TestMethod]
    public void ConflictRecord_ShouldSelectLastDeployedWinnerAndDetectDefiniteConflict()
    {
        var first = new ConflictParticipant(Guid.NewGuid(), "First", "a.patch_0", 10, 7, 100, 200, 0);
        var second = first with { ModId = Guid.NewGuid(), ModName = "Second", Version = 8, DeploymentOrder = 1 };
        var record = new ConflictRecord(10, "Unit", [first, second]);
        Assert.IsTrue(record.IsDefiniteConflict);
        Assert.AreEqual(second, record.Winner);
    }

    [TestMethod]
    public void BuildCacheKey_ShouldIncludeOptionState()
    {
        var directory = Directory.CreateTempSubdirectory("hd2mm-conflict-key-");
        try
        {
            const string name = "First";
            var id = Guid.NewGuid();
            AnalysisMod Create(IReadOnlyList<bool> enabled, IReadOnlyList<int> selected) =>
                new(id, name, true, 0, directory, null, "1.0", enabled, selected);
            var baseline = ModConflictService.BuildCacheKey([Create([true], [1])]);
            Assert.AreNotEqual(baseline, ModConflictService.BuildCacheKey([Create([false], [1])]));
            Assert.AreNotEqual(baseline, ModConflictService.BuildCacheKey([Create([true], [2])]));
            Assert.AreEqual(baseline, ModConflictService.BuildCacheKey([Create([true], [1])]));
        }
        finally { Directory.Delete(directory.FullName, true); }
    }

    [TestMethod]
    public async Task ConflictService_ShouldDetectSameUnitInTwoMods()
    {
        var directory = Directory.CreateTempSubdirectory("hd2mm-conflict-");
        try
        {
            var bytes = CreateHealthyPatch();
            var first = directory.CreateSubdirectory("first");
            var second = directory.CreateSubdirectory("second");
            foreach (var mod in new[] { first, second })
            {
                await File.WriteAllBytesAsync(Path.Combine(mod.FullName, "0123456789abcdef.patch_0"), bytes);
                await File.WriteAllBytesAsync(Path.Combine(mod.FullName, "0123456789abcdef.patch_0.gpu_resources"), new byte[64]);
                await File.WriteAllBytesAsync(Path.Combine(mod.FullName, "0123456789abcdef.patch_0.stream"), new byte[64]);
            }
            var mods = new[]
            {
                new AnalysisMod(Guid.NewGuid(), "First", true, 0, first),
                new AnalysisMod(Guid.NewGuid(), "Second", false, 1, second),
            };
            var result = await new ModConflictService(new PatchStructureAnalyzer()).AnalyzeAsync(mods);
            Assert.AreEqual(1, result.ScannedModCount);
            Assert.AreEqual(0, result.Conflicts.Count);
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    [TestMethod]
    public async Task AnalyzeAsync_NullGameDirectory_ScansConflictWithoutArchiveLookup()
    {
        var root = Directory.CreateTempSubdirectory("hd2mm-conflict-null-game-");
        try
        {
            var bytes = CreateHealthyPatch();
            var first = root.CreateSubdirectory("first");
            var second = root.CreateSubdirectory("second");
            foreach (var mod in new[] { first, second })
            {
                await File.WriteAllBytesAsync(Path.Combine(mod.FullName, "0123456789abcdef.patch_0"), bytes);
                await File.WriteAllBytesAsync(Path.Combine(mod.FullName, "0123456789abcdef.patch_0.gpu_resources"), new byte[64]);
                await File.WriteAllBytesAsync(Path.Combine(mod.FullName, "0123456789abcdef.patch_0.stream"), new byte[64]);
            }

            var mods = new[]
            {
                new AnalysisMod(Guid.NewGuid(), "First", true, 0, first),
                new AnalysisMod(Guid.NewGuid(), "Second", true, 1, second),
            };
            var result = await new ModConflictService(new PatchStructureAnalyzer()).AnalyzeAsync(mods, gameDataDirectory: null);

            Assert.AreEqual(2, result.ScannedModCount);
            Assert.AreEqual(2, result.ScannedPatchCount);
            Assert.AreEqual(2, result.ScannedUnitCount);
            Assert.AreEqual(1, result.Conflicts.Count);
            Assert.AreEqual(string.Empty, result.Conflicts[0].FriendlyName);
        }
        finally { Directory.Delete(root.FullName, true); }
    }
    private static byte[] CreateHealthyPatch()
    {
        const int headerSize = 72;
        const int typeTableSize = 32;
        const int tocEntrySize = 80;
        var mainOffset = headerSize + typeTableSize + tocEntrySize;
        var bytes = new byte[mainOffset + 108];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0), unchecked((int)0xF0000011));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(headerSize + 8), PatchFileParser.UnitTypeId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(headerSize + 16), 1);
        var entry = headerSize + typeTableSize;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry), 0x123456789ABCDEF0UL);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry + 8), PatchFileParser.UnitTypeId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry + 16), (ulong)mainOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 56), 108);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 60), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 64), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 76), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(mainOffset + 0x30), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(mainOffset + 0x34), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(mainOffset + 0x2C), 10800438);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(mainOffset + 0x5C), 0x68);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(mainOffset + 0x60), 100);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(mainOffset + 0x68), 0);
        return bytes;
    }
}
