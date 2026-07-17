using System.Buffers.Binary;
using Helldivers2ModManager.Core.Compatibility;

namespace Helldivers2ModManager.Infrastructure.Compatibility;

public sealed class StingrayPatchScanner : IPatchScanner
{
    private const int HeaderSize = 72;
    private const int TypeEntrySize = 32;
    private const int FileEntrySize = 80;
    private const int MaximumTypes = 1_000;
    private const int MaximumFiles = 100_000;
    private const long UnitTypeId = unchecked((long)16187218042980615487UL);

    public async Task<PatchScanResult> ScanAsync(string patchPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patchPath);
        var units = new List<PatchUnitObservation>();
        var issues = new List<string>();

        await using var stream = new FileStream(
            patchPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (stream.Length < HeaderSize)
            return new PatchScanResult(units, ["Patch.HeaderTruncated"]);

        var header = new byte[HeaderSize];
        if (!await ReadExactlyAtAsync(stream, 0, header, cancellationToken).ConfigureAwait(false))
            return new PatchScanResult(units, ["Patch.HeaderTruncated"]);

        var typeCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        var fileCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
        if (typeCount is < 0 or > MaximumTypes || fileCount is < 0 or > MaximumFiles)
            return new PatchScanResult(units, ["Patch.EntryCountInvalid"]);

        var fileTableOffset = HeaderSize + ((long)typeCount * TypeEntrySize);
        var fileTableLength = (long)fileCount * FileEntrySize;
        if (!RangeIsInBounds(fileTableOffset, fileTableLength, stream.Length))
            return new PatchScanResult(units, ["Patch.FileTableOutOfBounds"]);

        var entry = new byte[FileEntrySize];
        var versionBytes = new byte[sizeof(uint)];
        for (var index = 0; index < fileCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryOffset = fileTableOffset + ((long)index * FileEntrySize);
            if (!await ReadExactlyAtAsync(stream, entryOffset, entry, cancellationToken).ConfigureAwait(false))
            {
                issues.Add("Patch.FileEntryTruncated");
                break;
            }

            var typeId = BinaryPrimitives.ReadInt64LittleEndian(entry.AsSpan(8, 8));
            if (typeId != UnitTypeId)
                continue;

            var fileId = BinaryPrimitives.ReadInt64LittleEndian(entry.AsSpan(0, 8));
            var dataOffset = BinaryPrimitives.ReadInt64LittleEndian(entry.AsSpan(16, 8));
            var dataSize = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(56, 4));
            if (dataSize < 0x30 || !RangeIsInBounds(dataOffset, dataSize, stream.Length))
            {
                issues.Add($"Patch.UnitDataOutOfBounds:{fileId}");
                continue;
            }

            if (!await ReadExactlyAtAsync(stream, dataOffset + 0x2C, versionBytes, cancellationToken).ConfigureAwait(false))
            {
                issues.Add($"Patch.UnitVersionTruncated:{fileId}");
                continue;
            }

            units.Add(new PatchUnitObservation(
                fileId,
                BinaryPrimitives.ReadUInt32LittleEndian(versionBytes),
                patchPath,
                dataOffset,
                dataSize));
        }

        return new PatchScanResult(units, issues);
    }

    private static bool RangeIsInBounds(long offset, long length, long streamLength) =>
        offset >= 0 && length >= 0 && offset <= streamLength && length <= streamLength - offset;

    private static async Task<bool> ReadExactlyAtAsync(
        FileStream stream,
        long offset,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        if (!RangeIsInBounds(offset, buffer.Length, stream.Length))
            return false;

        stream.Position = offset;
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return false;
            totalRead += read;
        }

        return true;
    }
}
