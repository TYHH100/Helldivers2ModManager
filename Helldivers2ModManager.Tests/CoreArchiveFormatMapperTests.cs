using Helldivers2ModManager.Adapters;
using CoreMods = Helldivers2ModManager.Core.Mods;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class CoreArchiveFormatMapperTests
{
    [TestMethod]
    public void Map_ShouldCoverAllExportGears()
    {
        Assert.AreEqual(CoreMods.ArchiveExportFormat.Zip, CoreArchiveFormatMapper.Map(false, "32m"));
        Assert.AreEqual(CoreMods.ArchiveExportFormat.SevenZipFast, CoreArchiveFormatMapper.Map(true, "8m"));
        Assert.AreEqual(CoreMods.ArchiveExportFormat.SevenZipStandard, CoreArchiveFormatMapper.Map(true, "32m"));
        Assert.AreEqual(CoreMods.ArchiveExportFormat.SevenZipHigh, CoreArchiveFormatMapper.Map(true, "64m"));
        Assert.AreEqual(CoreMods.ArchiveExportFormat.SevenZipUltra, CoreArchiveFormatMapper.Map(true, "128m"));
    }
}
