using CoreMods = Helldivers2ModManager.Core.Mods;

namespace Helldivers2ModManager.Adapters;

internal static class CoreArchiveFormatMapper
{
    public static CoreMods.ArchiveExportFormat Map(bool isSevenZip, string dictionarySize) =>
        !isSevenZip ? CoreMods.ArchiveExportFormat.Zip : dictionarySize switch
        {
            "8m" => CoreMods.ArchiveExportFormat.SevenZipFast,
            "64m" => CoreMods.ArchiveExportFormat.SevenZipHigh,
            "128m" => CoreMods.ArchiveExportFormat.SevenZipUltra,
            _ => CoreMods.ArchiveExportFormat.SevenZipStandard,
        };
}
