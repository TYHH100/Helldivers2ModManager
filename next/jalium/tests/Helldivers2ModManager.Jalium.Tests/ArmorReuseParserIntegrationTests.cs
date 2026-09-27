using System.Buffers.Binary;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Tests;

[TestClass]
public sealed class ArmorReuseParserIntegrationTests
{
    [TestMethod]
    public async Task LinkedVersionCheckParserFindsUnitUsedByArmorReuseScan()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-unit-parser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var patchPath = Path.Combine(directory, "0011223344556677.patch_0");
            var bytes = new byte[72 + 32 + 80 + 0x30];
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0), unchecked((int)0xF0000011));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);
            const int entry = 72 + 32;
            const long unitId = 0x123456789ABCDEF;
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(entry), unitId);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(entry + 8),
                unchecked((long)16187218042980615487UL));
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(entry + 16), 72 + 32 + 80);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry + 56), 0x30);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(72 + 32 + 80 + 0x2C), 10800438);
            await File.WriteAllBytesAsync(patchPath, bytes);

            var settings = new SettingsService(NullLogger<SettingsService>.Instance,
                Path.Combine(directory, "settings.json"), directory);
            var localization = new LocalizationService(NullLogger<LocalizationService>.Instance, directory);
            var parser = new VersionCheckService(NullLogger<VersionCheckService>.Instance,
                settings, localization);
            var units = await parser.ExtractUnitVersionsFromPatchFileAsync(new FileInfo(patchPath));

            Assert.AreEqual(1, units.Count);
            Assert.AreEqual(unitId, units[0].FileId);
            Assert.AreEqual(10800438u, units[0].Version);
        }
        finally
        {
            var root = Path.GetFullPath(Path.GetTempPath());
            var target = Path.GetFullPath(directory);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("hd2mm-jalium-unit-parser-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test cleanup path.");
            Directory.Delete(target, recursive: true);
        }
    }
}
