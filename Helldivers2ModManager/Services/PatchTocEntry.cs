namespace Helldivers2ModManager.Services;

internal readonly record struct PatchTocEntry(
    long FileId,
    long TypeId,
    ulong TocOffset,
    ulong StreamOffset,
    ulong GpuOffset,
    uint TocSize,
    uint StreamSize,
    uint GpuSize,
    uint EntryIndex);
