using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Helldivers2ModManager.Core.Preview;
using K4os.Compression.LZ4;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Core.GameData;

public sealed partial class GameArchiveService : IDisposable
{
    private const byte CompressionNone = 0;
    private const byte CompressionLz4 = 3;
    private const byte ChunkStart = 2;
    private const int HeaderSize = 72;
    private const int TypeEntrySize = 32;
    private const int FileEntrySize = 80;
    private const long UnitTypeId = unchecked((long)0xE0A48D0BE9A7453FUL);
    private const long BonesTypeId = 0x18DEAD01056B72E9;
    private const long AnimationTypeId = unchecked((long)0x931E336D7646CC26UL);
    private const long StateMachineTypeId = unchecked((long)0xA486D4045106165CUL);
    private const long HelldiverAvatarUnitId = 5556372446766824087;
    private const int MaxAnimationsPerPreview = 256;
    private const int MaxBundleResourceBytes = 64 * 1024 * 1024;

    private readonly ILogger<GameArchiveService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private ArchiveIndex? _index;
    private bool _disposed;

    public GameArchiveService(ILogger<GameArchiveService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GameUnitReferenceLookup> ResolveUnitsAsync(
        DirectoryInfo dataDirectory,
        IReadOnlyCollection<long> unitIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(unitIds);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var indexPath = Path.Combine(dataDirectory.FullName, "bundles.nxa");
        if (!dataDirectory.Exists || !File.Exists(indexPath))
        {
            return Error("The game data directory or bundles.nxa is unavailable.");
        }

        var bundleFiles = dataDirectory.GetFiles("bundles*.nxa", SearchOption.TopDirectoryOnly)
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var cacheKey = dataDirectory.FullName + "|" + string.Join("|", bundleFiles.Select(
            file => $"{file.Name}:{file.Length}:{file.LastWriteTimeUtc.Ticks}"));

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_index is null || !string.Equals(_index.CacheKey, cacheKey, StringComparison.Ordinal))
            {
                var newIndex = await Task.Run(() => BuildIndex(dataDirectory, cacheKey), cancellationToken)
                    .ConfigureAwait(false);
                var oldIndex = _index;
                _index = newIndex;
                oldIndex?.Dispose();
            }

            return await Task.Run(() => ResolveUnits(_index!, unitIds), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to resolve game Unit references");
            return Error(exception.Message);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _index?.Dispose();
        _semaphore.Dispose();
    }

    private static GameUnitReferenceLookup Error(string message) => new(
        new Dictionary<long, GameUnitReference>(),
        new Dictionary<long, IReadOnlyList<string>>(),
        new HashSet<long>(),
        new HashSet<long>(),
        message);

    private ArchiveIndex BuildIndex(DirectoryInfo dataDirectory, string cacheKey)
    {
        var indexPath = Path.Combine(dataDirectory.FullName, "bundles.nxa");
        var bundleIndexData = DecodeDsar(indexPath);
        if (bundleIndexData.Length < 0x18)
            throw new InvalidDataException("The game bundle index is too small.");

        var bundleCount = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(0x0C, 4));
        var packageCount = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(0x10, 4));
        if (bundleCount is 0 or > 256 || packageCount > 1_000_000)
            throw new InvalidDataException("The game bundle index has suspicious counts.");

        var bundles = new BundleInfo[bundleCount];
        for (var index = 0; index < bundles.Length; index++)
        {
            bundles[index] = LoadBundle(Path.Combine(dataDirectory.FullName, $"bundles.{index:00}.nxa"));
        }

        var unitLocators = new Dictionary<long, List<UnitLocator>>();
        var animationResourceLocators = new Dictionary<(long FileId, long TypeId), List<UnitLocator>>();
        for (var packageIndex = 0; packageIndex < packageCount; packageIndex++)
        {
            var recordOffset = checked(0x18 + packageIndex * 0x18);
            if (recordOffset + 0x18 > bundleIndexData.Length)
                throw new InvalidDataException("The package table exceeds the bundle index.");

            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(recordOffset + 8, 4));
            var itemCount = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(recordOffset + 12, 4));
            var itemsOffset = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(recordOffset + 16, 4));
            if (itemCount == 0 || itemCount > 100_000)
                continue;

            var packageName = ReadNullTerminatedString(bundleIndexData, nameOffset);
            if (packageName.Length == 0 ||
                packageName.Contains(".gpu_resources", StringComparison.OrdinalIgnoreCase) ||
                packageName.Contains(".stream", StringComparison.OrdinalIgnoreCase) ||
                packageName.Contains(".patch", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if ((ulong)itemsOffset + itemCount * 0x10UL > (ulong)bundleIndexData.Length)
                continue;

            var items = new ArchiveItem[itemCount];
            var valid = true;
            for (var index = 0; index < items.Length; index++)
            {
                var itemOffset = checked((int)(itemsOffset + index * 0x10));
                var bundleIndex = bundleIndexData[itemOffset + 15];
                if (bundleIndex >= bundles.Length)
                {
                    valid = false;
                    break;
                }

                items[index] = new(
                    BinaryPrimitives.ReadUInt64LittleEndian(bundleIndexData.AsSpan(itemOffset, 8)),
                    BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(itemOffset + 8, 4)),
                    bundleIndex);
            }
            if (!valid) continue;

            try
            {
                var tocData = ReadResource(bundles[items[0].BundleIndex], items[0].BundleOffset, MaxBundleResourceBytes);
                IndexPackage(packageName, items, tocData, unitLocators, animationResourceLocators);
            }
            catch
            {
                continue;
            }
        }

        _logger.LogInformation(
            "Game archive index ready: packages={PackageCount}, Unit IDs={UnitIdCount}",
            packageCount,
            unitLocators.Count);

        return new(
            cacheKey,
            bundles,
            unitLocators.ToFrozenDictionary(),
            animationResourceLocators.ToFrozenDictionary(),
            new GameArchiveIndexStatistics((int)bundleCount, (int)packageCount, unitLocators.Count));
    }

    private static void IndexPackage(
        string packageName,
        ArchiveItem[] items,
        byte[] tocData,
        Dictionary<long, List<UnitLocator>> locators,
        Dictionary<(long FileId, long TypeId), List<UnitLocator>> animationResourceLocators)
    {
        if (tocData.Length < HeaderSize ||
            BinaryPrimitives.ReadInt32LittleEndian(tocData.AsSpan(0, 4)) != unchecked((int)0xF0000011))
        {
            return;
        }

        var typeCount = BinaryPrimitives.ReadInt32LittleEndian(tocData.AsSpan(4, 4));
        var fileCount = BinaryPrimitives.ReadInt32LittleEndian(tocData.AsSpan(8, 4));
        if (typeCount is < 0 or > 1000 || fileCount is < 0 or > 100_000)
            return;

        var entryStart = HeaderSize + (long)typeCount * TypeEntrySize;
        if (entryStart + (long)fileCount * FileEntrySize > tocData.Length)
            return;

        for (var index = 0; index < fileCount; index++)
        {
            var offset = checked((int)(entryStart + (long)index * FileEntrySize));
            var fileId = BinaryPrimitives.ReadInt64LittleEndian(tocData.AsSpan(offset, 8));
            var typeId = BinaryPrimitives.ReadInt64LittleEndian(tocData.AsSpan(offset + 8, 8));
            var resourceOffset = BinaryPrimitives.ReadUInt64LittleEndian(tocData.AsSpan(offset + 16, 8));
            var resourceSize = BinaryPrimitives.ReadUInt32LittleEndian(tocData.AsSpan(offset + 56, 4));
            var gpuSize = BinaryPrimitives.ReadUInt32LittleEndian(tocData.AsSpan(offset + 64, 4));
            var locator = new UnitLocator(packageName, items, resourceOffset, resourceSize, gpuSize);
            if (typeId == UnitTypeId)
            {
                if (!locators.TryGetValue(fileId, out var entries))
                {
                    entries = [];
                    locators[fileId] = entries;
                }

                entries.Add(locator);
            }
            else if (typeId is BonesTypeId or StateMachineTypeId or AnimationTypeId)
            {
                var key = (fileId, typeId);
                if (!animationResourceLocators.TryGetValue(key, out var entries))
                {
                    entries = [];
                    animationResourceLocators[key] = entries;
                }

                entries.Add(locator);
            }
        }
    }

    private static GameUnitReferenceLookup ResolveUnits(ArchiveIndex index, IReadOnlyCollection<long> unitIds)
    {
        var references = new Dictionary<long, GameUnitReference>();
        var packageNames = new Dictionary<long, IReadOnlyList<string>>();
        var missing = new HashSet<long>();
        var ambiguous = new HashSet<long>();

        foreach (var unitId in unitIds)
        {
            if (index.PackageNames.TryGetValue(unitId, out var cached))
                packageNames[unitId] = cached;
            if (index.AmbiguousUnitIds.Contains(unitId))
            {
                ambiguous.Add(unitId);
                continue;
            }
            if (index.ResolvedReferences.TryGetValue(unitId, out var cachedReference))
            {
                references[unitId] = cachedReference;
                continue;
            }
            if (!index.UnitLocators.TryGetValue(unitId, out var locators))
            {
                missing.Add(unitId);
                continue;
            }

            var candidates = locators
                .Select(locator => TryReadUnit(index.Bundles, unitId, locator))
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!)
                .ToList();
            var distinct = candidates
                .GroupBy(candidate => candidate.Signature, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            if (distinct.Count == 0)
            {
                missing.Add(unitId);
                continue;
            }

            var names = candidates
                .Select(candidate => candidate.PackageName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            index.PackageNames[unitId] = names;
            packageNames[unitId] = names;

            if (distinct.Count > 1)
            {
                index.AmbiguousUnitIds.Add(unitId);
                ambiguous.Add(unitId);
                continue;
            }

            index.ResolvedReferences[unitId] = distinct[0];
            references[unitId] = distinct[0];
        }

        return new(references, packageNames, missing, ambiguous, null);
    }

    private static GameUnitReference? TryReadUnit(BundleInfo[] bundles, long unitId, UnitLocator locator)
    {
        var item = locator.Items.LastOrDefault(candidate => candidate.ArchiveOffset <= locator.ResourceOffset);
        if (item is null) return null;

        var bundleOffset = checked(item.BundleOffset + locator.ResourceOffset - item.ArchiveOffset);
        byte[] unitData;
        try
        {
            unitData = ReadResource(bundles[item.BundleIndex], bundleOffset, MaxBundleResourceBytes);
        }
        catch
        {
            return null;
        }

        var declaredLength = locator.ResourceSize <= int.MaxValue
            ? Math.Min(unitData.Length, (int)locator.ResourceSize)
            : unitData.Length;
        if (declaredLength < 0x68)
            return null;

        var version = BinaryPrimitives.ReadUInt32LittleEndian(unitData.AsSpan(0x2C, 4));
        var lodGroupOffset = BinaryPrimitives.ReadUInt32LittleEndian(unitData.AsSpan(0x30, 4));
        var nextSectionOffset = BinaryPrimitives.ReadUInt32LittleEndian(unitData.AsSpan(0x34, 4));
        var endingOffset = BinaryPrimitives.ReadUInt32LittleEndian(unitData.AsSpan(0x60, 4));
        if (lodGroupOffset < 0x68 ||
            nextSectionOffset <= lodGroupOffset ||
            nextSectionOffset > declaredLength ||
            endingOffset > declaredLength - 8)
        {
            return null;
        }

        return new(
            unitId,
            version,
            unitData.AsSegment(checked((int)lodGroupOffset), checked((int)(nextSectionOffset - lodGroupOffset))),
            ReadMeshIds(unitData, declaredLength),
            locator.GpuSize,
            locator.PackageName);
    }

    private static uint[] ReadMeshIds(byte[] unitData, int declaredLength)
    {
        if (declaredLength < 0x68) return [];
        var meshInfoOffset = BinaryPrimitives.ReadUInt32LittleEndian(unitData.AsSpan(0x64, 4));
        if (meshInfoOffset > declaredLength - 4) return [];

        var meshCount = BinaryPrimitives.ReadInt32LittleEndian(unitData.AsSpan((int)meshInfoOffset, 4));
        if (meshCount is < 0 or > 4096) return [];
        var meshIdOffset = meshInfoOffset + 4L + meshCount * 4L;
        if (meshIdOffset + meshCount * 4L > declaredLength) return [];

        var result = new uint[meshCount];
        for (var index = 0; index < meshCount; index++)
        {
            result[index] = BinaryPrimitives.ReadUInt32LittleEndian(unitData.AsSpan((int)meshIdOffset + index * 4, 4));
        }
        return result;
    }

    private static byte[] DecodeDsar(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var chunks = ReadChunkTable(stream);
        if (chunks.Length == 0) { stream.Position = 0; var h = new byte[16]; stream.ReadExactly(h); throw new InvalidDataException($"DSAR empty: {Convert.ToHexString(h)} len={stream.Length}."); }
        using var output = new MemoryStream();
            if (chunks[0].UncompressedSize == 0) throw new InvalidDataException($"DSAR chunk empty: uo={chunks[0].UncompressedOffset} co={chunks[0].CompressedOffset} us={chunks[0].UncompressedSize} cs={chunks[0].CompressedSize}.");
        foreach (var chunk in chunks)
        {
            var decoded = DecodeChunk(stream, chunk);
            if (output.Length + decoded.Length > 256L * 1024 * 1024)
                throw new InvalidDataException("The game bundle index is unexpectedly large.");
            output.Write(decoded);
        }
        return output.ToArray();
    }

    private static BundleInfo LoadBundle(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var chunks = ReadChunkTable(stream);
        if (chunks.Length == 0) { stream.Position = 0; var h = new byte[16]; stream.ReadExactly(h); throw new InvalidDataException($"DSAR empty: {Convert.ToHexString(h)} len={stream.Length}."); }
        var byOffset = new Dictionary<ulong, int>(chunks.Length);
        for (var index = 0; index < chunks.Length; index++)
        {
            if (!byOffset.TryAdd(chunks[index].UncompressedOffset, index))
                throw new InvalidDataException($"Bundle {Path.GetFileName(path)} contains duplicate chunk offsets.");
        }
        return new(path, chunks, byOffset);
    }

    private static DsarChunk[] ReadChunkTable(FileStream stream)
    {
        var header = new byte[0x20];
        stream.Position = 0;
        stream.ReadExactly(header);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
        if (count is < 0 or > 10_000_000 || 0x20L + (long)count * 0x20 > stream.Length)
            throw new InvalidDataException("The DSAR chunk table is invalid.");

        var chunks = new DsarChunk[count];
        var buffer = new byte[0x20];
        stream.Position = 0x20;
        for (var index = 0; index < count; index++)
        {
            stream.ReadExactly(buffer);
            var chunk = new DsarChunk(
                BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8, 8)),
                BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(16, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(20, 4)),
                buffer[24],
                buffer[25]);
            if (chunk.UncompressedSize < 0 ||
                chunk.CompressedSize < 0 ||
                chunk.CompressedOffset > (ulong)stream.Length ||
                (ulong)chunk.CompressedSize > (ulong)stream.Length - chunk.CompressedOffset)
            {
                throw new InvalidDataException("A DSAR chunk exceeds its file bounds.");
            }
            chunks[index] = chunk;
        }
        return chunks;
    }

    private static byte[] ReadResource(BundleInfo bundle, ulong startOffset, int maxBytes)
    {
        if (!bundle.ChunkByOffset.TryGetValue(startOffset, out var chunkIndex))
            throw new InvalidDataException($"No bundle chunk starts at 0x{startOffset:X}.");

        var stream = bundle.OpenReadStream();
        using var output = new MemoryStream();
        while (chunkIndex < bundle.Chunks.Length)
        {
            var chunk = bundle.Chunks[chunkIndex];
            if (output.Length > 0 && (chunk.Flags & ChunkStart) != 0)
                break;
            var decoded = DecodeChunk(stream, chunk);
            if (output.Length + decoded.Length > maxBytes)
                throw new InvalidDataException("A bundle resource exceeds the allowed size.");
            output.Write(decoded);
            chunkIndex++;
        }
        return output.ToArray();
    }

    private static byte[] DecodeChunk(FileStream stream, DsarChunk chunk)
    {
        var encoded = GC.AllocateUninitializedArray<byte>(chunk.CompressedSize);
        stream.Position = checked((long)chunk.CompressedOffset);
        stream.ReadExactly(encoded);
        if (chunk.Compression == CompressionNone)
        {
            if (encoded.Length != chunk.UncompressedSize)
                throw new InvalidDataException("An uncompressed DSAR chunk has inconsistent sizes.");
            return encoded;
        }
        if (chunk.Compression != CompressionLz4)
            throw new InvalidDataException($"Unsupported DSAR compression type {chunk.Compression}.");

        var decoded = GC.AllocateUninitializedArray<byte>(chunk.UncompressedSize);
        var decodedLength = LZ4Codec.Decode(encoded, decoded);
        if (decodedLength != decoded.Length)
            throw new InvalidDataException($"LZ4 decoded {decodedLength} of {decoded.Length} bytes.");
        return decoded;
    }

    private static string ReadNullTerminatedString(byte[] data, uint offset)
    {
        if (offset >= data.Length) return string.Empty;
        var end = Array.IndexOf(data, (byte)0, checked((int)offset));
        return end < 0 ? string.Empty : Encoding.UTF8.GetString(data, (int)offset, end - (int)offset);
    }

    private sealed record DsarChunk(
        ulong UncompressedOffset,
        ulong CompressedOffset,
        int UncompressedSize,
        int CompressedSize,
        byte Compression,
        byte Flags);

    private sealed record ArchiveItem(ulong ArchiveOffset, uint BundleOffset, byte BundleIndex);

    private sealed record UnitLocator(
        string PackageName,
        ArchiveItem[] Items,
        ulong ResourceOffset,
        uint ResourceSize,
        uint GpuSize);

    private sealed class BundleInfo(string path, DsarChunk[] chunks, Dictionary<ulong, int> chunkByOffset) : IDisposable
    {
        public DsarChunk[] Chunks { get; } = chunks;
        public Dictionary<ulong, int> ChunkByOffset { get; } = chunkByOffset;
        private FileStream? _stream;

        public FileStream OpenReadStream()
        {
            _stream ??= new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.RandomAccess);
            return _stream;
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    private sealed class ArchiveIndex(
        string cacheKey,
        BundleInfo[] bundles,
        FrozenDictionary<long, List<UnitLocator>> unitLocators,
        FrozenDictionary<(long FileId, long TypeId), List<UnitLocator>> animationResourceLocators,
        GameArchiveIndexStatistics statistics) : IDisposable
    {
        public string CacheKey { get; } = cacheKey;
        public BundleInfo[] Bundles { get; } = bundles;
        public FrozenDictionary<long, List<UnitLocator>> UnitLocators { get; } = unitLocators;
        public FrozenDictionary<(long FileId, long TypeId), List<UnitLocator>> AnimationResourceLocators { get; } = animationResourceLocators;
        public GameArchiveIndexStatistics Statistics { get; } = statistics;
        public Dictionary<long, GameUnitReference> ResolvedReferences { get; } = [];
        public Dictionary<long, IReadOnlyList<string>> PackageNames { get; } = [];
        public HashSet<long> AmbiguousUnitIds { get; } = [];
        public ModelPreviewAnimationResourceReference? HelldiverAnimationReference { get; set; }
        public ModelPreviewAnimationLibrary? HelldiverAnimationLibrary { get; set; }

        public void Dispose()
        {
            foreach (var bundle in Bundles)
                bundle.Dispose();
        }
    }
}
