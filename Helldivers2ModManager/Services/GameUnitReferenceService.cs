using K4os.Compression.LZ4;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Helldivers2ModManager.Core.Compatibility;

namespace Helldivers2ModManager.Services;

internal sealed class GameUnitReferenceService
{
    private const byte DsarCompressionNone = 0;
    private const byte DsarCompressionLz4 = 3;
    private const byte DsarChunkStart = 2;
    private const int MaxBundleResourceBytes = 64 * 1024 * 1024;
    private const long UnitTypeId = unchecked((long)16187218042980615487UL);
    private const int PatchHeaderMagic = unchecked((int)0xF0000011);
    private const int HeaderSize = 72;
    private const int TypeEntrySize = 32;
    private const int FileEntrySize = 80;
    private readonly ILogger _logger;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly SemaphoreSlim _gameReferenceSemaphore = new(1, 1);
    private GameUnitReferenceIndex? _gameReferenceIndex;

    public GameUnitReferenceService(
        ILogger logger,
        SettingsService settingsService,
        LocalizationService localizationService)
    {
        _logger = logger;
        _settingsService = settingsService;
        _localizationService = localizationService;
    }

    private sealed record GameUnitLocator(
        string PackageName,
        PackageItem[] Items,
        ulong ResourceOffset,
        uint ResourceSize,
        uint GpuSize);

    private sealed class GameUnitReferenceIndex
    {
        public required string CacheKey { get; init; }
        public required BundleInfo[] Bundles { get; init; }
        public required Dictionary<long, List<GameUnitLocator>> UnitLocators { get; init; }
        public Dictionary<long, GameUnitReferenceData> ResolvedReferences { get; } = [];
        public HashSet<long> AmbiguousUnitIds { get; } = [];
    }

    internal async Task<GameUnitReferenceLookup> GetGameUnitReferencesAsync(
        IReadOnlyCollection<long> unitIds)
    {
        var dataDirectory = GetConfiguredGameDataDirectory();
        if (dataDirectory is null)
        {
            return new GameUnitReferenceLookup
            {
                ErrorMessage = _localizationService["VersionCheckRepair.GameDataUnavailable"]
            };
        }

        var bundleFiles = dataDirectory.GetFiles("bundles*.nxa", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var cacheKey = dataDirectory.FullName + "|" + string.Join(
            "|",
            bundleFiles.Select(f => $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"));

        await _gameReferenceSemaphore.WaitAsync();
        try
        {
            if (_gameReferenceIndex is null ||
                !string.Equals(_gameReferenceIndex.CacheKey, cacheKey, StringComparison.Ordinal))
            {
                _gameReferenceIndex = await Task.Run(() =>
                    BuildGameUnitReferenceIndex(dataDirectory, cacheKey));
            }

            var lookup = new GameUnitReferenceLookup();
            foreach (var unitId in unitIds)
            {
                if (_gameReferenceIndex.AmbiguousUnitIds.Contains(unitId))
                {
                    lookup.AmbiguousUnitIds.Add(unitId);
                    continue;
                }

                if (_gameReferenceIndex.ResolvedReferences.TryGetValue(unitId, out var cached))
                {
                    lookup.References[unitId] = cached;
                    continue;
                }

                if (!_gameReferenceIndex.UnitLocators.TryGetValue(unitId, out var locators))
                {
                    lookup.MissingUnitIds.Add(unitId);
                    continue;
                }

                var candidates = new List<GameUnitReferenceData>();
                foreach (var locator in locators)
                {
                    var candidate = TryReadGameUnitReference(
                        _gameReferenceIndex.Bundles,
                        unitId,
                        locator);
                    if (candidate is not null)
                        candidates.Add(candidate);
                }

                var distinctCandidates = candidates
                    .GroupBy(c => c.Signature, StringComparer.Ordinal)
                    .Select(g => g.First())
                    .ToList();
                if (distinctCandidates.Count == 0)
                {
                    lookup.MissingUnitIds.Add(unitId);
                }
                else if (distinctCandidates.Count > 1)
                {
                    _gameReferenceIndex.AmbiguousUnitIds.Add(unitId);
                    lookup.AmbiguousUnitIds.Add(unitId);
                }
                else
                {
                    var resolved = distinctCandidates[0];
                    _gameReferenceIndex.ResolvedReferences[unitId] = resolved;
                    lookup.References[unitId] = resolved;
                }
            }

            return lookup;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index current game Unit resources");
            return new GameUnitReferenceLookup { ErrorMessage = ex.Message };
        }
        finally
        {
            _gameReferenceSemaphore.Release();
        }
    }

    internal async Task<GameReferenceSnapshot> GetCoreGameReferencesAsync(
        string gameDataDirectory,
        IReadOnlyCollection<long> unitIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuredDirectory = GetConfiguredGameDataDirectory();
        if (configuredDirectory is null ||
            !string.Equals(
                Path.GetFullPath(gameDataDirectory).TrimEnd(Path.DirectorySeparatorChar),
                configuredDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return new GameReferenceSnapshot(
                ReferenceSource.Unavailable,
                null,
                new Dictionary<long, GameUnitReference>(),
                "Reference.GameDataUnavailable");
        }

        var lookup = await GetGameUnitReferencesAsync(unitIds).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(lookup.ErrorMessage))
        {
            return new GameReferenceSnapshot(
                ReferenceSource.Unavailable,
                null,
                new Dictionary<long, GameUnitReference>(),
                "Reference.GameDataReadFailed");
        }

        var fingerprintSeed = _gameReferenceIndex?.CacheKey ?? configuredDirectory.FullName;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSeed)));
        var references = lookup.References.ToDictionary(
            static pair => pair.Key,
            static pair => new GameUnitReference(pair.Key, pair.Value.Version, pair.Value.Signature));
        return new GameReferenceSnapshot(
            ReferenceSource.CurrentGameFiles,
            fingerprint,
            references);
    }

    private DirectoryInfo? GetConfiguredGameDataDirectory()
    {
        if (_settingsService is null ||
            !_settingsService.Initialized ||
            string.IsNullOrWhiteSpace(_settingsService.GameDirectory))
        {
            return null;
        }

        var directory = new DirectoryInfo(Path.Combine(_settingsService.GameDirectory, "data"));
        return directory.Exists &&
               File.Exists(Path.Combine(directory.FullName, "bundles.nxa"))
            ? directory
            : null;
    }

    private GameUnitReferenceIndex BuildGameUnitReferenceIndex(
        DirectoryInfo dataDirectory,
        string cacheKey)
    {
        var bundleIndexData = DecodeDsarFile(Path.Combine(dataDirectory.FullName, "bundles.nxa"));
        if (bundleIndexData.Length < 0x18)
            throw new InvalidDataException("The game bundle index is too small.");

        var bundleCount = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(0x0C, 4));
        var packageCount = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(0x10, 4));
        if (bundleCount is 0 or > 256 || packageCount > 1_000_000)
            throw new InvalidDataException("The game bundle index has suspicious counts.");

        var bundles = new BundleInfo[bundleCount];
        for (var i = 0; i < bundles.Length; i++)
        {
            var bundlePath = Path.Combine(dataDirectory.FullName, $"bundles.{i:00}.nxa");
            bundles[i] = LoadBundleInfo(bundlePath);
        }

        var locators = new Dictionary<long, List<GameUnitLocator>>();
        for (var packageIndex = 0; packageIndex < packageCount; packageIndex++)
        {
            var recordOffset = checked(0x18 + packageIndex * 0x18);
            if ((long)recordOffset + 0x18 > bundleIndexData.Length)
                throw new InvalidDataException("The package table exceeds the bundle index.");

            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(recordOffset + 8, 4));
            var itemCount = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(recordOffset + 12, 4));
            var itemsOffset = BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(recordOffset + 16, 4));
            if (itemCount == 0 || itemCount > 100_000)
                continue;

            var packageName = ReadNullTerminatedString(bundleIndexData, nameOffset);
            if (string.IsNullOrEmpty(packageName) ||
                packageName.Contains(".gpu_resources", StringComparison.OrdinalIgnoreCase) ||
                packageName.Contains(".stream", StringComparison.OrdinalIgnoreCase) ||
                packageName.Contains(".patch", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if ((ulong)itemsOffset + itemCount * 0x10UL > (ulong)bundleIndexData.Length)
                continue;

            var items = new PackageItem[itemCount];
            var itemsValid = true;
            for (var i = 0; i < items.Length; i++)
            {
                var itemOffset = checked((int)itemsOffset + i * 0x10);
                var bundleIndex = bundleIndexData[itemOffset + 15];
                if (bundleIndex >= bundles.Length)
                {
                    itemsValid = false;
                    break;
                }

                items[i] = new PackageItem(
                    BinaryPrimitives.ReadUInt64LittleEndian(bundleIndexData.AsSpan(itemOffset, 8)),
                    BinaryPrimitives.ReadUInt32LittleEndian(bundleIndexData.AsSpan(itemOffset + 8, 4)),
                    bundleIndex);
            }
            if (!itemsValid)
                continue;

            byte[] tocData;
            try
            {
                tocData = ReadBundleResource(
                    bundles[items[0].BundleIndex],
                    items[0].BundleOffset,
                    MaxBundleResourceBytes);
            }
            catch
            {
                continue;
            }

            IndexPackageUnitLocators(packageName, items, tocData, locators);
            if (packageIndex > 0 && packageIndex % 1000 == 0)
            {
                _logger.LogInformation(
                    "Indexed {Current}/{Total} game packages, Unit IDs={UnitCount}",
                    packageIndex,
                    packageCount,
                    locators.Count);
            }
        }

        _logger.LogInformation(
            "Game Unit reference index ready: packages={PackageCount}, Unit IDs={UnitCount}",
            packageCount,
            locators.Count);
        return new GameUnitReferenceIndex
        {
            CacheKey = cacheKey,
            Bundles = bundles,
            UnitLocators = locators
        };
    }

    private static void IndexPackageUnitLocators(
        string packageName,
        PackageItem[] items,
        byte[] tocData,
        Dictionary<long, List<GameUnitLocator>> locators)
    {
        if (tocData.Length < HeaderSize ||
            BinaryPrimitives.ReadInt32LittleEndian(tocData.AsSpan(0, 4)) != PatchHeaderMagic)
        {
            return;
        }

        var numTypes = BinaryPrimitives.ReadInt32LittleEndian(tocData.AsSpan(4, 4));
        var numFiles = BinaryPrimitives.ReadInt32LittleEndian(tocData.AsSpan(8, 4));
        if (numTypes < 0 || numFiles < 0 || numTypes > 1000 || numFiles > 100000)
            return;

        var entryStart = HeaderSize + (long)numTypes * TypeEntrySize;
        if (entryStart + (long)numFiles * FileEntrySize > tocData.Length)
            return;

        for (var i = 0; i < numFiles; i++)
        {
            var entryOffset = checked((int)(entryStart + (long)i * FileEntrySize));
            var typeId = BinaryPrimitives.ReadInt64LittleEndian(tocData.AsSpan(entryOffset + 8, 8));
            if (typeId != UnitTypeId)
                continue;

            var fileId = BinaryPrimitives.ReadInt64LittleEndian(tocData.AsSpan(entryOffset, 8));
            var resourceOffset = BinaryPrimitives.ReadUInt64LittleEndian(tocData.AsSpan(entryOffset + 16, 8));
            var resourceSize = BinaryPrimitives.ReadUInt32LittleEndian(tocData.AsSpan(entryOffset + 56, 4));
            var gpuSize = BinaryPrimitives.ReadUInt32LittleEndian(tocData.AsSpan(entryOffset + 64, 4));
            if (!locators.TryGetValue(fileId, out var entries))
            {
                entries = [];
                locators[fileId] = entries;
            }

            entries.Add(new GameUnitLocator(
                packageName,
                items,
                resourceOffset,
                resourceSize,
                gpuSize));
        }
    }

    private static GameUnitReferenceData? TryReadGameUnitReference(
        BundleInfo[] bundles,
        long unitId,
        GameUnitLocator locator)
    {
        var item = locator.Items.LastOrDefault(i => i.ArchiveOffset <= locator.ResourceOffset);
        if (item is null)
            return null;

        var resourceBundleOffset = checked(item.BundleOffset + locator.ResourceOffset - item.ArchiveOffset);
        byte[] unitData;
        try
        {
            unitData = ReadBundleResource(
                bundles[item.BundleIndex],
                resourceBundleOffset,
                MaxBundleResourceBytes);
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

        return new GameUnitReferenceData
        {
            FileId = unitId,
            Version = version,
            LodGroupData = unitData.AsSpan(
                checked((int)lodGroupOffset),
                checked((int)(nextSectionOffset - lodGroupOffset))).ToArray(),
            MeshIds = ReadUnitMeshIds(unitData, declaredLength),
            GpuSize = locator.GpuSize,
            PackageName = locator.PackageName
        };
    }

    internal static uint[] ReadUnitMeshIds(byte[] unitData, int declaredLength)
    {
        if (declaredLength < 0x68)
            return [];

        var meshInfoOffset = BinaryPrimitives.ReadUInt32LittleEndian(unitData.AsSpan(0x64, 4));
        if (meshInfoOffset > declaredLength - 4)
            return [];

        var meshCount = BinaryPrimitives.ReadInt32LittleEndian(unitData.AsSpan((int)meshInfoOffset, 4));
        if (meshCount < 0 || meshCount > 4096)
            return [];

        var meshIdOffset = checked((long)meshInfoOffset + 4L + meshCount * 4L);
        if (meshIdOffset + meshCount * 4L > declaredLength)
            return [];

        var result = new uint[meshCount];
        for (var i = 0; i < meshCount; i++)
            result[i] = BinaryPrimitives.ReadUInt32LittleEndian(unitData.AsSpan((int)meshIdOffset + i * 4, 4));
        return result;
    }

    internal static byte[] DecodeDsarFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var chunks = ReadDsarChunkTable(stream);
        using var output = new MemoryStream();
        foreach (var chunk in chunks)
        {
            var decoded = DecodeDsarChunk(stream, chunk);
            if (output.Length + decoded.Length > 256L * 1024 * 1024)
                throw new InvalidDataException("The bundle index is unexpectedly large.");
            output.Write(decoded);
        }
        return output.ToArray();
    }

    internal static BundleInfo LoadBundleInfo(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var chunks = ReadDsarChunkTable(stream);
        var chunkByOffset = new Dictionary<ulong, int>(chunks.Length);
        for (var i = 0; i < chunks.Length; i++)
        {
            if (!chunkByOffset.TryAdd(chunks[i].UncompressedOffset, i))
                throw new InvalidDataException($"Bundle {Path.GetFileName(path)} contains duplicate chunk offsets.");
        }
        return new BundleInfo(path, chunks, chunkByOffset);
    }

    private static DsarChunk[] ReadDsarChunkTable(FileStream stream)
    {
        var header = new byte[0x20];
        stream.Position = 0;
        stream.ReadExactly(header);
        var chunkCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
        if (chunkCount < 0 || chunkCount > 10_000_000 ||
            0x20L + (long)chunkCount * 0x20 > stream.Length)
        {
            throw new InvalidDataException("The DSAR chunk table is invalid.");
        }

        var chunks = new DsarChunk[chunkCount];
        var chunkBuffer = new byte[0x20];
        for (var i = 0; i < chunkCount; i++)
        {
            stream.Position = 0x20L + (long)i * 0x20;
            stream.ReadExactly(chunkBuffer);
            var chunk = new DsarChunk(
                BinaryPrimitives.ReadUInt64LittleEndian(chunkBuffer.AsSpan(0, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(chunkBuffer.AsSpan(8, 8)),
                BinaryPrimitives.ReadInt32LittleEndian(chunkBuffer.AsSpan(16, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(chunkBuffer.AsSpan(20, 4)),
                chunkBuffer[24],
                chunkBuffer[25]);
            if (chunk.UncompressedSize < 0 ||
                chunk.CompressedSize < 0 ||
                chunk.CompressedOffset > (ulong)stream.Length ||
                (ulong)chunk.CompressedSize > (ulong)stream.Length - chunk.CompressedOffset)
            {
                throw new InvalidDataException("A DSAR chunk exceeds its file bounds.");
            }
            chunks[i] = chunk;
        }
        return chunks;
    }

    internal static byte[] ReadBundleResource(
        BundleInfo bundle,
        ulong startOffset,
        int maxBytes)
    {
        if (!bundle.ChunkByOffset.TryGetValue(startOffset, out var chunkIndex))
            throw new InvalidDataException($"No bundle chunk starts at 0x{startOffset:X}.");

        using var stream = new FileStream(bundle.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new MemoryStream();
        while (chunkIndex < bundle.Chunks.Length)
        {
            var chunk = bundle.Chunks[chunkIndex];
            if (output.Length > 0 && (chunk.Flags & DsarChunkStart) != 0)
                break;

            var decoded = DecodeDsarChunk(stream, chunk);
            if (output.Length + decoded.Length > maxBytes)
                throw new InvalidDataException("A bundle resource exceeds the allowed size.");
            output.Write(decoded);
            chunkIndex++;
        }
        return output.ToArray();
    }

    private static byte[] DecodeDsarChunk(FileStream stream, DsarChunk chunk)
    {
        var encoded = new byte[chunk.CompressedSize];
        stream.Position = checked((long)chunk.CompressedOffset);
        stream.ReadExactly(encoded);
        if (chunk.Compression == DsarCompressionNone)
        {
            if (encoded.Length != chunk.UncompressedSize)
                throw new InvalidDataException("An uncompressed DSAR chunk has inconsistent sizes.");
            return encoded;
        }

        if (chunk.Compression != DsarCompressionLz4)
            throw new InvalidDataException($"Unsupported DSAR compression type {chunk.Compression}.");

        var decoded = new byte[chunk.UncompressedSize];
        var decodedLength = LZ4Codec.Decode(encoded, decoded);
        if (decodedLength != decoded.Length)
            throw new InvalidDataException($"LZ4 decoded {decodedLength} of {decoded.Length} bytes.");
        return decoded;
    }

    internal static string ReadNullTerminatedString(byte[] data, uint offset)
    {
        if (offset >= data.Length)
            return string.Empty;
        var end = Array.IndexOf(data, (byte)0, checked((int)offset));
        return end < 0
            ? string.Empty
            : Encoding.UTF8.GetString(data, checked((int)offset), end - checked((int)offset));
    }
}
