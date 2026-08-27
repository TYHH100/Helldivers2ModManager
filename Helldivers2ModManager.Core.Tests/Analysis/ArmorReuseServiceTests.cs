using Helldivers2ModManager.Core.Analysis;
using Helldivers2ModManager.Core.GameData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace Helldivers2ModManager.Core.Tests.Analysis;

[TestClass]
public sealed class ArmorReuseServiceTests
{
    [TestMethod]
    public async Task AnalyzeAsync_NullGameDirectory_SkipsLookupButPreservesScanCounts()
    {
        var root = Directory.CreateTempSubdirectory("hd2mm-armor-null-game-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "0123456789abcdef.patch_0"), "not-a-patch");
            using var archive = new GameArchiveService(NullLogger<GameArchiveService>.Instance);
            var service = new ArmorReuseService(archive, NullLogger<ArmorReuseService>.Instance, Path.Combine(root.FullName, "missing.json"));
            var mods = new[]
            {
                new AnalysisMod(Guid.NewGuid(), "Mod", true, 0, root),
            };

            var result = await service.AnalyzeAsync(mods, gameDataDirectory: null);

            Assert.AreEqual(1, result.ScannedModCount);
            Assert.AreEqual(1, result.ScannedPatchCount);
            Assert.AreEqual(0, result.ScannedUnitCount);
            Assert.AreEqual(0, result.Records.Count);
        }
        finally { root.Delete(true); }
    }

    [TestMethod]
    public void BuildRecord_ShouldCountOnlyReusedArmorsAsSharedUnits()
    {
        var root = Directory.CreateTempSubdirectory("hd2mm-armor-");
        try
        {
            var namesPath = Path.Combine(root.FullName, "armor-names.json");
            File.WriteAllText(namesPath, JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["1111111111111111"] = "Source",
                ["2222222222222222"] = "Reused One",
                ["3333333333333333"] = "Reused Two",
            }));
            using var archive = new GameArchiveService(NullLogger<GameArchiveService>.Instance);
            var service = new ArmorReuseService(archive, NullLogger<ArmorReuseService>.Instance, namesPath);
            var modId = Guid.NewGuid();
            var sources = new (Guid Id, string Name, string Patch, long UnitId)[]
            {
                (modId, "Mod", "0123456789abcdef.patch_0", 10),
                (modId, "Mod", "0123456789abcdef.patch_0", 11),
            };
            var packages = new Dictionary<long, IReadOnlyList<string>>
            {
                [10] = ["1111111111111111", "2222222222222222"],
                [11] = ["1111111111111111", "3333333333333333"],
            };

            var record = service.BuildRecord(modId, "Mod", sources, packages);

            Assert.IsNotNull(record);
            Assert.AreEqual("1111111111111111", record.SourceArmorId);
            Assert.AreEqual(2, record.ReusedBy.Count);
            Assert.AreEqual(2, record.SharedUnitCount);
        }
        finally { root.Delete(true); }
    }
}
