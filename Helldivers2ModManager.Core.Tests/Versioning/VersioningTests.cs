using System.Buffers.Binary;
using Helldivers2ModManager.Core.PatchKit;
using Helldivers2ModManager.Core.Repair;
using Helldivers2ModManager.Core.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Versioning;

[TestClass]
public sealed class VersioningTests
{
    [TestMethod]
    public void MostCommonVersion_ShouldPreferHigherVersionOnTie()
    {
        Assert.AreEqual(10800438u, VersionCompatibilityEvaluator.MostCommonVersion([1, 10800438, 10800437, 10800438]));
        Assert.IsNull(VersionCompatibilityEvaluator.MostCommonVersion([]));
    }

    [TestMethod]
    public void Evaluate_ShouldFollowBatchRules()
    {
        Assert.AreEqual(ModVersionStatus.Compatible, VersionCompatibilityEvaluator.Evaluate(false, [7], 7, [7]));
        Assert.AreEqual(ModVersionStatus.Incompatible, VersionCompatibilityEvaluator.Evaluate(false, [6], 7, [6]));
        Assert.AreEqual(ModVersionStatus.Unknown, VersionCompatibilityEvaluator.Evaluate(false, [], null, []));
        Assert.AreEqual(ModVersionStatus.Incompatible, VersionCompatibilityEvaluator.Evaluate(true, [7], 7, [7]));
    }

    [TestMethod]
    public async Task Analyzer_ShouldReportHealthyUnitAndCacheResult()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "0123456789abcdef.patch_0");
            await File.WriteAllBytesAsync(path, CreatePatch());
            await File.WriteAllBytesAsync(path + ".gpu_resources", new byte[64]);
            await File.WriteAllBytesAsync(path + ".stream", new byte[64]);
            var analyzer = new PatchStructureAnalyzer();
            var first = await analyzer.AnalyzeAsync(directory);
            Assert.AreEqual(1, first.TotalPatchFiles);
            Assert.AreEqual(PatchHealthStatus.Healthy, first.PatchFiles.Single().HealthStatus, string.Join("|", first.PatchFiles.Single().UnitDetails) + string.Join("|", first.HasStructuralIssues ? "structural" : ""));
            Assert.IsTrue(first.PatchFiles.Single().UnitDetails.Single().DeclaredSizeMatchesInternal);
            var second = await analyzer.AnalyzeAsync(directory);
            Assert.AreEqual(second.TotalPatchFiles, first.TotalPatchFiles);
            Assert.AreEqual(second.PatchFiles.Single().UnitDetails.Single(), first.PatchFiles.Single().UnitDetails.Single());
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    [TestMethod]
    public async Task MetadataRepair_ShouldProveAndRepairUnitTocSize()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var bytes = CreatePatch(mainSize: 120u);
            var path = Path.Combine(directory.FullName, "0123456789abcdef.patch_0");
            await File.WriteAllBytesAsync(path, CreatePatch(120u, 100));
            await File.WriteAllBytesAsync(path + ".gpu_resources", new byte[64]);
            await File.WriteAllBytesAsync(path + ".stream", new byte[64]);
            var service = new MetadataRepairService(new PatchStructureAnalyzer());
            var plan = await service.CreatePlanAsync(directory);
            Assert.IsTrue(plan.CanRepair, string.Join(", ", plan.BlockingReasons) + " / actions=" + string.Join(",", plan.Actions.Select(a => a.Kind + ":" + a.NewValue)));
            var action = plan.Actions.Single();
            Assert.AreEqual(PatchRepairKind.UnitTocSize, action.Kind);
            Assert.AreEqual(108ul, action.NewValue);

            var result = await service.RepairAsync(directory);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual(1, result.AppliedActionCount);
            Assert.AreEqual(108u, BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(path).AsSpan(72 + 32 + 56)));
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    private static DirectoryInfo CreateTemporaryDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("hd2mm-versioning-");
        return directory;
    }

    private static byte[] CreatePatch(uint mainSize = 108, int endingOffset = -1)
    {
        const int headerSize = 72;
        const int typeTableSize = 32;
        const int tocEntrySize = 80;
        var mainOffset = headerSize + typeTableSize + tocEntrySize;
        var bytes = new byte[mainOffset + (endingOffset < 0 ? mainSize : (uint)endingOffset + 8)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0), unchecked((int)0xF0000011));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(headerSize + 8), PatchFileParser.UnitTypeId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(headerSize + 16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerSize + 24), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerSize + 28), 1);
        var entry = headerSize + typeTableSize;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry), 0x123456789ABCDEF0UL);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry + 8), PatchFileParser.UnitTypeId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry + 16), (ulong)mainOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 56), mainSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 60), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 64), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 76), 1);
        var unit = mainOffset;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(unit + 0x30), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(unit + 0x34), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(unit + 0x2C), 10800438);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(unit + 0x5C), 0x68);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(unit + 0x68), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(unit + 0x60), endingOffset < 0 ? (int)mainSize - 8 : endingOffset);
        return bytes;
    }
}









