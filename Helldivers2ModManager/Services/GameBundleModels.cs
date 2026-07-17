namespace Helldivers2ModManager.Services;

internal sealed record DsarChunk(
    ulong UncompressedOffset,
    ulong CompressedOffset,
    int UncompressedSize,
    int CompressedSize,
    byte Compression,
    byte Flags);

internal sealed record BundleInfo(
    string Path,
    DsarChunk[] Chunks,
    Dictionary<ulong, int> ChunkByOffset);

internal sealed record PackageItem(
    ulong ArchiveOffset,
    ulong BundleOffset,
    byte BundleIndex);
