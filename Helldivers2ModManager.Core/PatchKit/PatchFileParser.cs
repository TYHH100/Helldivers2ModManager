using System.Buffers.Binary;

namespace Helldivers2ModManager.Core.PatchKit;

public sealed class PatchFileParser
{
    private const int HeaderSize = 72;
    private const int TypeEntrySize = 32;
    private const int FileEntrySize = 80;
    private const int StreamInfoSize = 0x1B0;
    private const int UnitHeaderSize = 0x68;
    private const uint OriginalUnitVersion = 1;
    private const uint LegacyVerifiedUnitVersion = 10800437;
    private const uint CurrentVerifiedUnitVersion = 10800438;
    private const uint LayoutCheckVersionThreshold = 0xA4CD36;
    internal const ulong UnitTypeId = 0xE0A48D0BE9A7453FUL;

    public async Task<PatchParseResult> ParseFileAsync(
        FileInfo patchFile,
        PatchKitOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patchFile);
        options ??= PatchKitOptions.Default;

        if (!patchFile.Exists)
        {
            return PatchParseResult.Failed(new PatchParseIssue(PatchParseSeverity.Error, "PatchNotFound", patchFile.FullName));
        }

        await using var patchStream = Open(patchFile);
        await using var gpuStream = File.Exists(patchFile.FullName + ".gpu_resources")
            ? Open(new FileInfo(patchFile.FullName + ".gpu_resources"))
            : null;
        await using var streamResource = File.Exists(patchFile.FullName + ".stream")
            ? Open(new FileInfo(patchFile.FullName + ".stream"))
            : null;

        return await ParseAsync(
            patchStream,
            gpuStream,
            streamResource,
            patchFile.FullName,
            options,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PatchParseResult> ParseAsync(
        Stream patchStream,
        Stream? gpuResources,
        Stream? streamResource,
        string path,
        PatchKitOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patchStream);
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= PatchKitOptions.Default;

        if (!patchStream.CanSeek)
        {
            return Failed("PatchNotSeekable", "The patch stream must support seeking.");
        }

        var issues = new List<PatchParseIssue>();
        var length = patchStream.Length;
        if (length < HeaderSize)
        {
            return Failed("PatchTooSmall", $"Length {length} is smaller than the {HeaderSize}-byte header.");
        }

        var header = new byte[HeaderSize];
        if (!await ReadAtAsync(patchStream, 0, header, cancellationToken).ConfigureAwait(false))
        {
            return Failed("HeaderReadFailed", "Unable to read the complete patch header.");
        }

        if (BinaryPrimitives.ReadInt32LittleEndian(header) != unchecked((int)0xF0000011))
        {
            return Failed("InvalidMagic", "The patch magic is not 0xF0000011.");
        }

        var typeCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        var fileCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (typeCount < 0 || typeCount > options.MaxTypes || fileCount < 0 || fileCount > options.MaxFiles)
        {
            return Failed("SuspiciousHeader", $"Type/file counts are outside configured limits ({typeCount}, {fileCount}).");
        }

        var fileEntriesOffsetLong = HeaderSize + (long)typeCount * TypeEntrySize;
        var fileEntriesLengthLong = (long)fileCount * FileEntrySize;
        if (fileEntriesOffsetLong > length || fileEntriesLengthLong > length - fileEntriesOffsetLong)
        {
            return Failed("TocOutOfRange", "The declared type/file tables exceed the patch boundary.");
        }

        var typeBytes = new byte[typeCount * TypeEntrySize];
        if (typeCount > 0 && !await ReadAtAsync(patchStream, HeaderSize, typeBytes, cancellationToken).ConfigureAwait(false))
        {
            return Failed("TypeTableReadFailed", "Unable to read the complete type table.");
        }

        var types = new List<PatchTypeEntry>(typeCount);
        var distributions = new Dictionary<ulong, int>();
        ulong totalDeclaredResources = 0;
        var typeDistributionIssues = 0;
        for (var index = 0; index < typeCount; index++)
        {
            var offset = index * TypeEntrySize;
            var entry = new PatchTypeEntry(
                index + 1,
                BinaryPrimitives.ReadUInt64LittleEndian(typeBytes.AsSpan(offset)),
                BinaryPrimitives.ReadUInt64LittleEndian(typeBytes.AsSpan(offset + 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(typeBytes.AsSpan(offset + 16)));

            types.Add(entry);
            totalDeclaredResources = checked(totalDeclaredResources + entry.ResourceCount);
            if (entry.ResourceCount > int.MaxValue || !distributions.TryAdd(entry.TypeId, (int)entry.ResourceCount))
            {
                typeDistributionIssues++;
                issues.Add(new(PatchParseSeverity.Error, "InvalidTypeTable", $"Type entry {index + 1} has an invalid or duplicate declaration."));
            }
        }

        var entries = new List<PatchTocEntry>(fileCount);
        var actualCounts = new Dictionary<ulong, int>();
        var entryIndexIssues = 0;
        var entryBuffer = new byte[FileEntrySize];
        for (var index = 0; index < fileCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryOffset = fileEntriesOffsetLong + (long)index * FileEntrySize;
            if (!await ReadAtAsync(patchStream, entryOffset, entryBuffer, cancellationToken).ConfigureAwait(false))
            {
                return Failed("TocReadFailed", $"Unable to read TOC entry {index + 1}.");
            }

            var entry = new PatchTocEntry(
                index + 1,
                BinaryPrimitives.ReadUInt64LittleEndian(entryBuffer.AsSpan(0)),
                BinaryPrimitives.ReadUInt64LittleEndian(entryBuffer.AsSpan(8)),
                BinaryPrimitives.ReadUInt64LittleEndian(entryBuffer.AsSpan(16)),
                BinaryPrimitives.ReadUInt64LittleEndian(entryBuffer.AsSpan(24)),
                BinaryPrimitives.ReadUInt64LittleEndian(entryBuffer.AsSpan(32)),
                BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer.AsSpan(56)),
                BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer.AsSpan(60)),
                BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer.AsSpan(64)),
                BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer.AsSpan(76)));
            entries.Add(entry);
            actualCounts[entry.TypeId] = actualCounts.GetValueOrDefault(entry.TypeId) + 1;
            if (entry.EntryIndex != (uint)entry.Index)
            {
                entryIndexIssues++;
            }
        }

        if (totalDeclaredResources != (ulong)fileCount)
        {
            typeDistributionIssues++;
            issues.Add(new(PatchParseSeverity.Error, "ResourceCountMismatch",
                $"Declared resources={totalDeclaredResources}, files={fileCount}."));
        }

        foreach (var pair in distributions)
        {
            if (actualCounts.GetValueOrDefault(pair.Key) != pair.Value)
            {
                typeDistributionIssues++;
                issues.Add(new(PatchParseSeverity.Error, "TypeDistributionMismatch", $"Type 0x{pair.Key:X16} declaration differs from entries."));
            }
        }

        foreach (var pair in actualCounts)
        {
            if (!distributions.TryGetValue(pair.Key, out var declared) || declared != pair.Value)
            {
                typeDistributionIssues++;
                issues.Add(new(PatchParseSeverity.Error, "UndeclaredType", $"Type 0x{pair.Key:X16} is present in the TOC but not declared."));
            }
        }

        var minimumMainOffset = (ulong)(fileEntriesOffsetLong + fileEntriesLengthLong);
        var mainDataIssues = CountMainDataIssues(entries, minimumMainOffset, length);

        var gpuInfo = Describe(gpuResources);
        var streamInfo = Describe(streamResource);
        var gpuRangeIssues = 0;
        var gpuAlignmentIssues = 0;
        var streamRangeIssues = 0;
        var streamAlignmentIssues = 0;
        foreach (var entry in entries)
        {
            var gpuLength = gpuInfo?.Length;
            if (entry.GpuSize > 0 && (!gpuLength.HasValue || !entry.GpuInRange(gpuLength.Value)))
            {
                gpuRangeIssues++;
            }
            if (entry.GpuOffset % 64 != 0)
            {
                gpuAlignmentIssues++;
            }
            var streamLength = streamInfo?.Length;
            if (entry.StreamSize > 0 && (!streamLength.HasValue || !entry.StreamInRange(streamLength.Value)))
            {
                streamRangeIssues++;
            }
            if (entry.StreamOffset % 64 != 0)
            {
                streamAlignmentIssues++;
            }
        }

        if (entryIndexIssues != 0)
        {
            issues.Add(new(PatchParseSeverity.Warning, "EntryIndexMismatch", $"{entryIndexIssues} entries do not have a contiguous 1..N index."));
        }
        if (mainDataIssues != 0)
        {
            issues.Add(new(PatchParseSeverity.Error, "MainDataOutOfBounds", $"{mainDataIssues} main-data ranges are invalid or overlap."));
        }
        if (gpuRangeIssues != 0)
        {
            issues.Add(new(PatchParseSeverity.Error, "GpuOutOfRange", $"{gpuRangeIssues} GPU references are outside the companion."));
        }
        if (streamRangeIssues != 0)
        {
            issues.Add(new(PatchParseSeverity.Error, "StreamOutOfRange", $"{streamRangeIssues} stream references are outside the companion."));
        }
        if (gpuAlignmentIssues != 0 || streamAlignmentIssues != 0)
        {
            issues.Add(new(PatchParseSeverity.Warning, "CompanionMisaligned",
                $"GPU={gpuAlignmentIssues}, stream={streamAlignmentIssues} non-64-byte-aligned references."));
        }

        var units = await ReadUnitsAsync(
            patchStream,
            gpuResources,
            entries,
            fileEntriesOffsetLong + fileEntriesLengthLong,
            options,
            issues,
            cancellationToken).ConfigureAwait(false);

        var snapshot = new PatchFileSnapshot(
            path,
            length,
            new PatchHeader(typeCount, fileCount),
            types,
            entries,
            true,
            entryIndexIssues,
            typeDistributionIssues,
            mainDataIssues,
            gpuInfo,
            streamInfo,
            entries.Any(entry => entry.GpuSize > 0),
            entries.Any(entry => entry.StreamSize > 0),
            gpuRangeIssues,
            gpuAlignmentIssues,
            streamRangeIssues,
            streamAlignmentIssues,
            units,
            issues);

        return new(snapshot, issues);
    }

    private static async Task<IReadOnlyList<PatchUnitSnapshot>> ReadUnitsAsync(
        Stream patchStream,
        Stream? gpuResources,
        IReadOnlyList<PatchTocEntry> entries,
        long minimumMainOffset,
        PatchKitOptions options,
        ICollection<PatchParseIssue> issues,
        CancellationToken cancellationToken)
    {
        var units = new List<PatchUnitSnapshot>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.TypeId != UnitTypeId || !entry.MainInRange(patchStream.Length) || entry.MainSize < UnitHeaderSize)
            {
                continue;
            }

            var header = new byte[UnitHeaderSize];
            if (!await ReadAtAsync(patchStream, (long)entry.MainOffset, header, cancellationToken).ConfigureAwait(false))
            {
                issues.Add(new(PatchParseSeverity.Warning, "UnitHeaderUnavailable", $"TOC entry {entry.Index} could not be read."));
                continue;
            }

            var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x2C));
            var supportedVersion = version is OriginalUnitVersion or LegacyVerifiedUnitVersion or CurrentVerifiedUnitVersion;
            var listOffset = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x5C));
            var lodGroupOffset = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x30));
            var jointListOffset = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x34));
            var endingOffset = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x60));
            var expectedDataSize = endingOffset > 0 && endingOffset <= int.MaxValue - 8
                ? endingOffset + 8
                : 0;
            var lodGroupSize = jointListOffset - lodGroupOffset;
            var lodGroupInBounds =
                (lodGroupOffset == 0 && jointListOffset == 0) ||
                (lodGroupOffset >= 0 && lodGroupSize > 0 &&
                 (long)lodGroupOffset + lodGroupSize <= entry.MainSize);
            var streams = new List<PatchGpuStreamSnapshot>();
            var declaredStreamCount = 0;
            if (supportedVersion && listOffset > 0 && listOffset + 4 <= entry.MainSize)
            {
                var countBuffer = new byte[4];
                if (await ReadAtAsync(patchStream, (long)entry.MainOffset + listOffset, countBuffer, cancellationToken).ConfigureAwait(false))
                {
                    var count = BinaryPrimitives.ReadInt32LittleEndian(countBuffer);
                    declaredStreamCount = count;
                    if (count >= 0 && count <= options.MaxStreamsPerUnit &&
                        listOffset + 4L + count * 4L <= entry.MainSize &&
                        count * 4 <= options.MaxRandomReadBytes)
                    {
                        var offsets = new byte[count * 4];
                        if (count == 0 ||
                            await ReadAtAsync(patchStream, (long)entry.MainOffset + listOffset + 4, offsets, cancellationToken).ConfigureAwait(false))
                        {
                            for (var streamIndex = 0; streamIndex < count; streamIndex++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var relativeOffset = BinaryPrimitives.ReadInt32LittleEndian(offsets.AsSpan(streamIndex * 4));
                                var stream = await ReadStreamInfoAsync(
                                    patchStream,
                                    entry,
                                    streamIndex,
                                    relativeOffset,
                                    listOffset,
                                    version == CurrentVerifiedUnitVersion,
                                    options.MaxComponentsPerStream,
                                    cancellationToken).ConfigureAwait(false);
                                if (stream is not null)
                                {
                                    streams.Add(stream);
                                }
                            }
                        }
                    }
                    else
                    {
                        issues.Add(new(PatchParseSeverity.Warning, "UnsupportedStreamList", $"TOC entry {entry.Index} declares an unusable stream count ({count})."));
                    }
                }
                else
                {
                    issues.Add(new(PatchParseSeverity.Warning, "StreamListUnavailable", $"TOC entry {entry.Index} stream-list count could not be read."));
                }
            }
            else if (supportedVersion)
            {
                issues.Add(new(PatchParseSeverity.Warning, "InvalidStreamListOffset", $"TOC entry {entry.Index} has stream-list offset {listOffset}."));
            }
            var layoutFormatChecked = false;
            var layoutFormatValid = true;
            var layoutFormatIssues = 0;
            if (version < LayoutCheckVersionThreshold)
            {
                layoutFormatChecked = true;
                (layoutFormatValid, layoutFormatIssues) = await ValidateLegacyLayoutAsync(
                    patchStream,
                    entry,
                    listOffset,
                    options.MaxRandomReadBytes,
                    cancellationToken).ConfigureAwait(false);
                if (!layoutFormatValid)
                {
                    issues.Add(new(PatchParseSeverity.Error, "LegacyLayoutInvalid",
                        $"TOC entry {entry.Index} has {layoutFormatIssues} invalid legacy layout item(s)."));
                }
            }

            var gpuStructureChecked = supportedVersion;
            var unknownComponents = streams.Sum(static stream => stream.Components.Count(static component => !component.KnownFormat));
            var knownComponentIssues = streams.Count(stream =>
                stream.Components.Count == 0 ||
                stream.Components.Sum(static component => component.Size) != stream.VertexStride ||
                !stream.VertexBufferInGpuRange ||
                !stream.IndexBufferInGpuRange);
            if (streams.Count != 0 && streams.Count != declaredStreamCount)
            {
                knownComponentIssues++;
            }

            units.Add(new(
                entry.Index,
                entry.FileId,
                entry.TypeId,
                entry.MainOffset,
                entry.MainSize,
                entry.GpuOffset,
                entry.GpuSize,
                version,
                supportedVersion,
                listOffset,
                streams.Count,
                streams,
                new PatchUnitStructureSnapshot(
                    lodGroupOffset,
                    jointListOffset,
                    endingOffset,
                    expectedDataSize,
                    expectedDataSize == entry.MainSize,
                    expectedDataSize > entry.MainSize,
                    lodGroupInBounds,
                    layoutFormatChecked,
                    layoutFormatValid,
                    layoutFormatIssues,
                    gpuStructureChecked,
                    knownComponentIssues == 0,
                    unknownComponents)));
        }

        return units;
    }
        private static async Task<(bool Valid, int IssueCount)> ValidateLegacyLayoutAsync(
            Stream patchStream,
            PatchTocEntry entry,
            int streamListOffset,
            long maxRandomReadBytes,
            CancellationToken cancellationToken)
        {
            if (streamListOffset < 0 || (long)streamListOffset + 4 > entry.MainSize)
            {
                return (false, 1);
            }

            var countBuffer = new byte[4];
            if (!await ReadAtAsync(patchStream, (long)entry.MainOffset + streamListOffset, countBuffer, cancellationToken).ConfigureAwait(false))
            {
                return (false, 1);
            }

            var layoutCount = BinaryPrimitives.ReadInt32LittleEndian(countBuffer);
            if (layoutCount < 0 || layoutCount > 100 || (long)streamListOffset + 4L + layoutCount * 4L > entry.MainSize)
            {
                return (false, 1);
            }

            if (layoutCount == 0)
            {
                return (true, 0);
            }

            if ((long)layoutCount * 4 > maxRandomReadBytes)
            {
                return (false, 1);
            }

            var offsets = new byte[layoutCount * 4];
            if (!await ReadAtAsync(patchStream, (long)entry.MainOffset + streamListOffset + 4, offsets, cancellationToken).ConfigureAwait(false))
            {
                return (false, 1);
            }

            const int layoutHeaderAndItemsSize = 8 + 16 * 20;
            var issueCount = 0;
            for (var index = 0; index < layoutCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativeOffset = BinaryPrimitives.ReadInt32LittleEndian(offsets.AsSpan(index * 4));
                var layoutStart = (long)streamListOffset + relativeOffset;
                if (relativeOffset < 0 || layoutStart < 0 || layoutStart + layoutHeaderAndItemsSize > entry.MainSize)
                {
                    issueCount++;
                    continue;
                }

                var layout = new byte[layoutHeaderAndItemsSize];
                if (!await ReadAtAsync(patchStream, (long)entry.MainOffset + layoutStart, layout, cancellationToken).ConfigureAwait(false))
                {
                    issueCount++;
                    continue;
                }

                issueCount += Enumerable.Range(0, 16).Count(item =>
                    BinaryPrimitives.ReadInt32LittleEndian(layout.AsSpan(8 + item * 20 + 4)) > 16);
            }

            return (issueCount == 0, issueCount);
        }

    private static async Task<PatchGpuStreamSnapshot?> ReadStreamInfoAsync(
        Stream patchStream,
        PatchTocEntry entry,
        int streamIndex,
        int relativeOffset,
        int listOffset,
        bool usesCurrentFormats,
        int maxComponents,
        CancellationToken cancellationToken)
    {
        if (relativeOffset < 0)
        {
            return null;
        }

        var absoluteOffset = listOffset + relativeOffset;
        if (absoluteOffset < 0 || absoluteOffset + StreamInfoSize > entry.MainSize)
        {
            return null;
        }

        var buffer = new byte[StreamInfoSize];
        if (!await ReadAtAsync(patchStream, (long)entry.MainOffset + absoluteOffset, buffer, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var componentCountRaw = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0x148));
        if (componentCountRaw > (ulong)maxComponents)
        {
            return null;
        }

        var components = new List<PatchVertexComponent>((int)componentCountRaw);
        var offset = 0;
        for (var componentIndex = 0; componentIndex < (int)componentCountRaw; componentIndex++)
        {
            var baseOffset = 0x08 + componentIndex * 20;
            var semantic = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(baseOffset));
            var format = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(baseOffset + 4));
            var known = TryGetComponentSize(format, usesCurrentFormats, out var size);
            components.Add(new(componentIndex, semantic, format, offset, size, known));
            offset += size;
        }

        var vertexBufferInGpu = IsBufferInGpu(buffer, 0x1A0, entry.GpuSize);
        var indexBufferInGpu = IsBufferInGpu(buffer, 0x1A8, entry.GpuSize);

        return new(
            streamIndex,
            relativeOffset,
            absoluteOffset,
            components,
            componentCountRaw,
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0x160)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0x164)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0x188)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0x18C)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0x1A0)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0x1A4)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0x1A8)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0x1AC)),
            vertexBufferInGpu,
            indexBufferInGpu);
    }

    private static bool IsBufferInGpu(byte[] buffer, int offset, uint gpuSize)
    {
        var start = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 4));
        return start <= gpuSize && size <= gpuSize - start;
    }

    private static bool TryGetComponentSize(uint format, bool usesCurrentFormats, out int size)
    {
        size = usesCurrentFormats
            ? format switch
            {
                0 => 4,
                1 => 8,
                2 => 12,
                4 or 28 or 30 or 33 => 4,
                35 => 8,
                _ => 0,
            }
            : format switch
            {
                0 => 4,
                1 => 8,
                2 => 12,
                4 or 24 or 25 or 26 or 29 => 4,
                20 => 16,
                31 => 8,
                _ => 0,
            };
        return size != 0;
    }

    private static int CountMainDataIssues(IReadOnlyList<PatchTocEntry> entries, ulong minimumOffset, long length)
    {
        var issues = 0;
        var ranges = new List<(ulong Start, ulong End)>();
        foreach (var entry in entries)
        {
            if (!entry.MainInRange(length) || (entry.MainSize > 0 && entry.MainOffset < minimumOffset))
            {
                issues++;
                continue;
            }
            if (entry.MainSize > 0)
            {
                ranges.Add((entry.MainOffset, entry.MainOffset + entry.MainSize));
            }
        }

        ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        for (var index = 1; index < ranges.Count; index++)
        {
            if (ranges[index].Start < ranges[index - 1].End)
            {
                issues++;
            }
        }

        return issues;
    }

    private static PatchCompanionInfo? Describe(Stream? stream) =>
        stream is null || !stream.CanSeek ? null : new(true, stream.Length);

    private static async Task<bool> ReadAtAsync(Stream stream, long offset, Memory<byte> target, CancellationToken cancellationToken)
    {
        if (offset < 0)
        {
            return false;
        }

        try
        {
            stream.Seek(offset, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(target, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    private static FileStream Open(FileInfo file) =>
        new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess);

    private static PatchParseResult Failed(string code, string detail) =>
        new(null, [new PatchParseIssue(PatchParseSeverity.Error, code, detail)]);
}




