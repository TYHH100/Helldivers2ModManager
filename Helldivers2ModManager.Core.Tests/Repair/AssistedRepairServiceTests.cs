using System.Buffers.Binary;
using System.Text;
using Helldivers2ModManager.Core.GameData;
using Helldivers2ModManager.Core.PatchKit;
using Helldivers2ModManager.Core.Repair;
using Helldivers2ModManager.Core.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Repair;

[TestClass]
public sealed class AssistedRepairServiceTests
{
    [TestMethod]
    public async Task PreserveStrategy_ShouldUpdateOnlyUnitVersion()
    {
        var root = Directory.CreateTempSubdirectory("hd2mm-assisted-");
        try
        {
            var mod = root.CreateSubdirectory("mod");
            await File.WriteAllBytesAsync(Path.Combine(mod.FullName, "0123456789abcdef.patch_0"), CreateUnitPatch(1));
            await File.WriteAllBytesAsync(Path.Combine(mod.FullName, "0123456789abcdef.patch_0.gpu_resources"), new byte[64]);
            await File.WriteAllBytesAsync(Path.Combine(mod.FullName, "0123456789abcdef.patch_0.stream"), new byte[64]);
            var service = CreateService(mod);
            var plan = await service.CreatePlanAsync(mod, AssistedLodStrategy.PreserveMod);
            Assert.AreEqual(0, plan.BlockingReasons.Count, string.Join("|", plan.BlockingReasons));
            Assert.AreEqual(1, plan.Actions.Count);
            Assert.AreEqual(AssistedLodStrategy.PreserveMod, plan.Actions[0].LodStrategy);
            Assert.IsTrue(plan.Actions[0].LodDataDiffers);

            var result = await service.RepairAsync(mod, AssistedLodStrategy.PreserveMod);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            var repaired = await new PatchFileParser().ParseFileAsync(new FileInfo(Path.Combine(mod.FullName, "0123456789abcdef.patch_0")));
            Assert.IsNotNull(repaired.Snapshot);
            Assert.AreEqual(10800438u, repaired.Snapshot.Units.Single().Version);
            Assert.AreEqual(1, Directory.GetFiles(mod.FullName, "*.hd2mm-backup").Length);
        }
        finally { root.Delete(true); }
    }

    [TestMethod]
    public async Task GameLodStrategy_ShouldReplaceLodAndAdjustOffsets()
    {
        var root = Directory.CreateTempSubdirectory("hd2mm-assisted-lod-");
        try
        {
            var mod = root.CreateSubdirectory("mod");
            var path = Path.Combine(mod.FullName, "0123456789abcdef.patch_0");
            await File.WriteAllBytesAsync(path, CreateUnitPatch(10800438));
            await File.WriteAllBytesAsync(path + ".gpu_resources", new byte[64]);
            await File.WriteAllBytesAsync(path + ".stream", new byte[64]);
            var service = CreateService(mod);
            var plan = await service.CreatePlanAsync(mod, AssistedLodStrategy.UseGameReference);
            Assert.AreEqual(0, plan.BlockingReasons.Count, string.Join("|", plan.BlockingReasons));
            Assert.AreEqual(1, plan.Actions.Count);
            Assert.IsTrue(plan.Actions[0].LodDataDiffers);

            var result = await service.RepairAsync(mod, AssistedLodStrategy.UseGameReference);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual(392, new FileInfo(path).Length);
            var parsed = await new PatchFileParser().ParseFileAsync(new FileInfo(path));
            Assert.IsNotNull(parsed.Snapshot);
            Assert.AreEqual(10800438u, parsed.Snapshot.Units.Single().Version);
        }
        finally { root.Delete(true); }
    }

    [TestMethod]
    public void LegacyEmissiveMigration_ShouldBuildCurrentSchema()
    {
        var source = new byte[512];
        BinaryPrimitives.WriteUInt64LittleEndian(source.AsSpan(0x18), 0xD3701FC725106C09UL);
        BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(0x40), 3);
        uint[] semantics = [0x1D57DCF3, 0xCA6F2CF1, 0x848BA63B];
        int semanticCount = semantics.Length;
        (uint Id, uint Offset)[] variables =
        [
            (0xA3351311, 0), (0x43695F7B, 4), (0x64AAB07B, 8),
            (0x6FD0B9E7, 12), (0x60E7D2A1, 16), (0x4A7CD0EF, 20),
            (0x4A6796C6, 24), (0xBD16A396, 28), (0x32C02400, 56),
            (0xC012EFE1, 36), (0xA83F44CD, 40), (0x6DDBAE8F, 44),
            (0x4B564F57, 48), (0x9ED04DA2, 52),
        ];
        int variableCount = variables.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(0x68), (uint)variableCount);
        BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(0x78), 60);
        for (var index = 0; index < semanticCount; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(0x88 + index * sizeof(uint)), semantics[index]);
        var textureIds = 0x94;
        for (var index = 0; index < semanticCount; index++)
            BinaryPrimitives.WriteUInt64LittleEndian(source.AsSpan(textureIds + index * (int)sizeof(ulong)), (ulong)(index * 11L + 100));
        var descriptors = textureIds + semanticCount * (int)sizeof(ulong);
        var values = descriptors + variableCount * 20;
        for (var index = 0; index < variableCount; index++)
        {
            var offset = descriptors + index * 20;
            BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(offset + 8), variables[index].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(offset + 12), variables[index].Offset);
            BinaryPrimitives.WriteSingleLittleEndian(source.AsSpan(checked(values + (int)variables[index].Offset)), index + 1);
        }

        Assert.IsTrue(AssistedRepairService.TryBuildLegacyEmissiveMaterialMigration(source, out var migrated));
        Assert.AreEqual(480, migrated.Length);
        Assert.AreEqual(0x11Fu, BinaryPrimitives.ReadUInt32LittleEndian(migrated.AsSpan(0)));
        Assert.AreEqual(0xC6042E3403385D40UL, BinaryPrimitives.ReadUInt64LittleEndian(migrated.AsSpan(0x18)));
        Assert.AreEqual(4u, BinaryPrimitives.ReadUInt32LittleEndian(migrated.AsSpan(0x40)));
        uint[] expectedSemantics = [0x1D57DCF3, 0xCA6F2CF1, 0x848BA63B, 0xCBDE381B];
        for (var index = 0; index < expectedSemantics.Length; index++)
            Assert.AreEqual(expectedSemantics[index], BinaryPrimitives.ReadUInt32LittleEndian(migrated.AsSpan(0x88 + index * sizeof(uint))));
        Assert.AreEqual(0x12D4692531C1FD35UL, BinaryPrimitives.ReadUInt64LittleEndian(migrated.AsSpan(0xB0)));
        for (var index = 0; index < semanticCount; index++)
            Assert.AreEqual((ulong)(index * 11L + 100), BinaryPrimitives.ReadUInt64LittleEndian(migrated.AsSpan(0x98 + index * (int)sizeof(ulong))));
        var targetDescriptors = 0xB8;
        var targetValues = targetDescriptors + 12 * 20;
        for (var index = 0; index < 12; index++)
        {
            var offset = targetDescriptors + index * 20;
            var id = BinaryPrimitives.ReadUInt32LittleEndian(migrated.AsSpan(offset + 8));
            var valueOffset = BinaryPrimitives.ReadUInt32LittleEndian(migrated.AsSpan(offset + 12));
            float expectedValue = id switch
            {
                0x529A4AAF => 0.144f,
                0x32C02400 => 1f,
                _ => Array.FindIndex(variables, item => item.Id == id) + 1,
            };
            Assert.AreEqual(expectedValue, BinaryPrimitives.ReadSingleLittleEndian(migrated.AsSpan(targetValues + (int)valueOffset)),
                $"Index={index}, Id={id:X8}, Offset={valueOffset}");
        }
    }

    [TestMethod]
    public async Task CompanionRecovery_ShouldUseExactCopy_AndValidateStagedFile()
    {
        var root = Directory.CreateTempSubdirectory("hd2mm-companion-");
        try
        {
            var mod = root.CreateSubdirectory("mod");
            var target = Path.Combine(mod.FullName, "base", "0123456789abcdef.patch_0");
            var source = Path.Combine(mod.FullName, "option", "0123456789abcdef.patch_0");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllBytesAsync(target, CreateUnitPatch(10800438));
            await File.WriteAllBytesAsync(source, await File.ReadAllBytesAsync(target));
            await File.WriteAllBytesAsync(source + ".gpu_resources", new byte[64]);
            await File.WriteAllBytesAsync(source + ".stream", new byte[64]);
            using var archive = new GameArchiveService(Microsoft.Extensions.Logging.Abstractions.NullLogger<GameArchiveService>.Instance);
            var service = new CompanionRecoveryService(archive, new PatchStructureAnalyzer());

            var plan = await service.CreatePlanAsync(mod, root.CreateSubdirectory("data"));
            Assert.AreEqual(2, plan.MissingCount, string.Join("|", plan.Items.Select(item => item.Reason)));
            Assert.AreEqual(2, plan.RecoverableCount);
            Assert.IsTrue(plan.Items.All(item => item.SourcePath is not null), string.Join("|", plan.Items.Select(item => item.Reason)));

            var recovered = await service.RecoverAsync(mod, root.CreateSubdirectory("data"));
            Assert.IsTrue(recovered.Success, recovered.ErrorMessage);
            Assert.IsTrue(File.Exists(target + ".gpu_resources"));
            Assert.IsTrue(File.Exists(target + ".stream"));
        }
        finally { root.Delete(true); }
    }

    private static byte[] CreateGameLod()
    {
        var data = new byte[32];
        
        return data;
    }

    private static TestAssistedRepairService CreateService(DirectoryInfo mod)
    {
        return new TestAssistedRepairService(
            new MetadataRepairService(new PatchStructureAnalyzer()),
            new PatchStructureAnalyzer(),
            0x123456789ABCDEF0L,
            CreateGameLod());
    }

    private sealed class TestAssistedRepairService(
        MetadataRepairService safeRepair,
        PatchStructureAnalyzer analyzer,
        long unitId,
        byte[] lodData)
        : AssistedRepairService(safeRepair, analyzer)
    {
        protected override Task<GameUnitReferenceLookup> ResolveReferencesAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
        {
            Assert.AreEqual(1, ids.Count);
            Assert.AreEqual(unitId, ids.Single());
            return Task.FromResult(new GameUnitReferenceLookup(
                new Dictionary<long, GameUnitReference> { [unitId] = new(unitId, 10800438, lodData, Array.Empty<uint>(), 64, "packages/test") },
                new Dictionary<long, IReadOnlyList<string>>(),
                new HashSet<long>(),
                new HashSet<long>(),
                null));
        }
    }

    private static byte[] CreateUnitPatch(uint version)
    {
        const int headerSize = 72;
        const int typeTableSize = 32;
        const int entrySize = 80;
        int mainOffset = headerSize + typeTableSize + entrySize;
        int mainSize = 192;
        byte[] bytes = new byte[mainOffset + mainSize];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0), unchecked((int)0xF0000011));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(headerSize + 8), PatchFileParser.UnitTypeId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(headerSize + 16), 1);
        int entry = headerSize + typeTableSize;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry), 0x123456789ABCDEF0UL);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry + 8), PatchFileParser.UnitTypeId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry + 16), (ulong)mainOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 56), (uint)mainSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 60), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 64), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 76), 1);

        int unit = mainOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(unit + 0x2C), version);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(unit + 0x30), 0x68);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(unit + 0x34), 0x78);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(unit + 0x5C), 0x78);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(unit + 0x60), 0xB8);
        
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(unit + 0x78), 0);
        return bytes;
    }
}







