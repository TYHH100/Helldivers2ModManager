using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services;

internal enum GameAudioOriginalLookup
{
    Found,
    /// <summary>bank 存在但缺少该 source id（模组新增媒体）。</summary>
    ResourceMissing,
    /// <summary>基线无法回答（布局/解析失败、资源不存在或比对预算耗尽）。</summary>
    Unavailable,
}

/// <summary>
/// Read-only access to one original game audio package (e.g. <c>9ba626afa44a3aa3</c>) so mod
/// entries can be compared with the game's own media. Supports both game layouts: legacy
/// installs (plain <c>data/&lt;base&gt;</c> + <c>.stream</c> files) and slim installs (resources
/// inside the LZ4 DSAR bundles, reusing the readers from <see cref="GameUnitReferenceReader"/>).
///
/// 内存与 IO 约束（语音包单模组可达近万条目，这里失控会拖垮整机）：
/// - 基线只驻留 TOC 元数据与每媒体 32 字节 SHA-256，不保留任何媒体字节数组；
/// - 游戏侧读取有硬预算（条数 + 字节数），耗尽后一律回答"未知"而不是继续读；
/// - legacy 布局持有常开只读句柄（避免逐条目 open/close 的随机 IO 风暴）；
/// - slim 布局缓存最近一个解码的 bundle item，条目按偏移有序时近似顺序读。
/// </summary>
internal sealed class GameAudioBaseline : IDisposable
{
    private const int HeaderSize = 72;
    private const int TypeEntrySize = 32;
    private const int FileEntrySize = 80;
    private const int PatchHeaderMagic = unchecked((int)0xF0000011);
    private const int MaxTypes = 1000;
    private const int MaxFiles = 100000;
    private const long MaxBankBytes = 64L * 1024 * 1024;
    private const int MaxMediaBytes = 32 * 1024 * 1024;
    private const int MaxBundleRangeBytes = 64 * 1024 * 1024;

    /// <summary>单个基线（一个游戏包）的比对预算：超过任一限额即停止比对。</summary>
    internal const int MaxComparisons = 6000;
    internal const long MaxReadBudgetBytes = 96L * 1024 * 1024;

    private delegate byte[]? RangeReader(long offset, int length);

    private readonly RangeReader _readTocRange;
    private readonly RangeReader? _readStreamRange;
    private readonly Dictionary<ulong, TocRecord> _tocEntries;
    private readonly Dictionary<ulong, BankMetadata> _banks = [];
    private readonly List<FileStream> _ownedStreams = [];
    // slim 布局最近一次解码的 bundle item 缓存（bundle 内偏移 → 解码字节）。
    private (ulong BundleOffset, byte[] Data)? _lastItemCache;
    private int _remainingComparisons = MaxComparisons;
    private long _remainingReadBytes = MaxReadBudgetBytes;

    internal enum EntryKind
    {
        Bank,
        Stream,
        TextBank,
    }

    private readonly record struct TocRecord(ulong FileId, EntryKind Kind, long TocDataOffset, uint TocDataSize, long StreamOffset, uint StreamSize);

    /// <summary>bank 的 DIDX 元数据 + 逐媒体惰性哈希。不持有媒体字节。</summary>
    private sealed class BankMetadata(AudioBankChunkReader.BankChunks chunks)
    {
        public AudioBankChunkReader.BankChunks Chunks { get; } = chunks;
        public Dictionary<uint, byte[]> Hashes { get; } = [];
        public bool Complete { get; set; }
    }

    private GameAudioBaseline(RangeReader readTocRange, RangeReader? readStreamRange, Dictionary<ulong, TocRecord> tocEntries)
    {
        _readTocRange = readTocRange;
        _readStreamRange = readStreamRange;
        _tocEntries = tocEntries;
    }

    public void Dispose()
    {
        foreach (var stream in _ownedStreams)
        {
            try
            {
                stream.Dispose();
            }
            catch (Exception)
            {
                // 释放路径不做故障放大。
            }
        }
        _ownedStreams.Clear();
    }

    /// <summary>查询 bank 媒体的原始尺寸；不消耗读取预算。</summary>
    public GameAudioOriginalLookup TryGetBankMediaSize(ulong bankFileId, uint sourceId, out uint size)
    {
        size = 0;
        if (!_tocEntries.TryGetValue(bankFileId, out var record) || record.Kind != EntryKind.Bank)
            return GameAudioOriginalLookup.Unavailable;
        var metadata = GetBankMetadata(record);
        if (metadata is null)
            return GameAudioOriginalLookup.Unavailable;
        foreach (var media in metadata.Chunks.Didx)
        {
            if (media.Id == sourceId)
            {
                size = media.Size;
                return GameAudioOriginalLookup.Found;
            }
        }

        return GameAudioOriginalLookup.ResourceMissing;
    }

    /// <summary>查询流媒体的原始尺寸；不消耗读取预算。</summary>
    public GameAudioOriginalLookup TryGetStreamMediaSize(ulong streamFileId, out uint size)
    {
        size = 0;
        if (!_tocEntries.TryGetValue(streamFileId, out var record) || record.Kind != EntryKind.Stream)
            return GameAudioOriginalLookup.Unavailable;
        size = record.StreamSize;
        return GameAudioOriginalLookup.Found;
    }

    /// <summary>计算 bank 媒体的原始 SHA-256（消耗比对预算；结果按 source id 缓存）。</summary>
    public byte[]? TryGetBankMediaHash(ulong bankFileId, uint sourceId)
    {
        if (!_tocEntries.TryGetValue(bankFileId, out var record) || record.Kind != EntryKind.Bank)
            return null;
        var metadata = GetBankMetadata(record);
        if (metadata is null)
            return null;
        if (metadata.Hashes.TryGetValue(sourceId, out var cached))
            return cached;

        AudioBankChunkReader.DidxMedia? media = null;
        foreach (var candidate in metadata.Chunks.Didx)
        {
            if (candidate.Id == sourceId)
            {
                media = candidate;
                break;
            }
        }
        if (media is not { } found || found.Size == 0 || found.Size > MaxMediaBytes)
            return null;
        if (metadata.Complete)
        {
            // slim 布局：哈希在元数据加载时一次性全量算完（避免重复解码 bundle item），
            // 查询零 IO，不消耗比对预算。
            return metadata.Hashes.GetValueOrDefault(sourceId);
        }
        if (!TryAcquireBudget(found.Size))
            return null;

        byte[]? hash;

        // legacy 的 DataOffset 来自文件句柄遍历（文件绝对偏移）；slim 的 Parse 来自内存 bank（相对偏移）。
        // legacy 不加 TocDataOffset+16；slim 的哈希已在 GetBankMetadata 里按相对坐标算完。
        var slice = _persistentTocStream is not null
            ? ReadStreamRange(_persistentTocStream, metadata.Chunks.DataOffset + found.Offset, (int)found.Size)
            : _readTocRange(record.TocDataOffset + 16 + metadata.Chunks.DataOffset + found.Offset, (int)found.Size);
        hash = slice is null ? null : SHA256.HashData(slice);
        if (hash is not null)
            metadata.Hashes[sourceId] = hash;
        return hash;
    }

    /// <summary>计算流媒体的原始 SHA-256（消耗比对预算；每次直读，语音流条目小且按偏移有序）。</summary>
    public byte[]? TryGetStreamMediaHash(ulong streamFileId)
    {
        if (_readStreamRange is null)
            return null;
        if (!_tocEntries.TryGetValue(streamFileId, out var record) || record.Kind != EntryKind.Stream)
            return null;
        if (record.StreamSize == 0 || record.StreamSize > MaxMediaBytes)
            return null;
        if (!TryAcquireBudget(record.StreamSize))
            return null;
        var slice = _readStreamRange(record.StreamOffset, (int)record.StreamSize);
        return slice is null ? null : SHA256.HashData(slice);
    }

    private readonly Dictionary<ulong, IReadOnlyDictionary<uint, string>?> _textBanks = [];

    /// <summary>包内 TEXT_BANK 资源的 file id（文本预览与诊断用）。</summary>
    internal IEnumerable<ulong> TextBankFileIds =>
        _tocEntries.Values.Where(static record => record.Kind == EntryKind.TextBank).Select(static record => record.FileId);

    /// <summary>解析并返回指定文本库的全部字符串 id（诊断用）。</summary>
    internal IReadOnlyCollection<uint> GetTextBankIds(ulong fileId)
    {
        if (!_tocEntries.TryGetValue(fileId, out var record))
            return [];
        return LoadTextBank(record)?.Keys.ToArray() ?? [];
    }

    /// <summary>查询原版文本条目（惰性整库解析并缓存；文本库远小于音频媒体，驻留代价可接受）。
    /// 返回 Found 时 <paramref name="text"/> 为原版文本；ResourceMissing = 文本库存在但缺该 ID（新增条目）。</summary>
    public GameAudioOriginalLookup TryGetTextEntry(ulong textBankFileId, uint stringId, out string? text)
    {
        text = null;
        if (!_tocEntries.TryGetValue(textBankFileId, out var record) || record.Kind != EntryKind.TextBank)
            return GameAudioOriginalLookup.Unavailable;

        if (!_textBanks.TryGetValue(textBankFileId, out var entries))
        {
            entries = LoadTextBank(record);
            _textBanks[textBankFileId] = entries;
        }
        if (entries is null)
            return GameAudioOriginalLookup.Unavailable;
        if (entries.TryGetValue(stringId, out var original))
        {
            text = original;
            return GameAudioOriginalLookup.Found;
        }
        return GameAudioOriginalLookup.ResourceMissing;
    }

    private IReadOnlyDictionary<uint, string>? LoadTextBank(TocRecord record)
    {
        if (record.TocDataSize < TextBankFormat.MinHeaderBytes ||
            record.TocDataSize > TextBankFormat.MaxBankBytes ||
            record.TocDataOffset < 0)
            return null;
        // 文本库没有 16 字节前缀，toc_data 就是完整格式。
        var data = _readTocRange(record.TocDataOffset, (int)record.TocDataSize);
        if (data is null)
            return null;
        return TextBankFormat.TryParse(data, out _, out var entries) ? entries : null;
    }

    private bool TryAcquireBudget(uint size)
    {
        if (Volatile.Read(ref _remainingComparisons) <= 0)
            return false;
        lock (_banks)
        {
            if (_remainingComparisons <= 0 || size > _remainingReadBytes)
                return false;
            _remainingComparisons--;
            _remainingReadBytes -= size;
            return true;
        }
    }

    /// <summary>加载 bank 的 chunk 元数据。legacy 只走 chunk 头；slim 需解码整个 bank item
    /// （≤64MB，瞬时缓冲，用完即弃），并顺手算完全部媒体哈希。</summary>
    private BankMetadata? GetBankMetadata(TocRecord record)
    {
        if (_banks.TryGetValue(record.FileId, out var cached))
            return cached;
        if (record.TocDataSize <= 16 || record.TocDataSize > MaxBankBytes)
            return null;

        BankMetadata? metadata = null;
        if (_persistentTocStream is { } tocStream)
        {
            // legacy：常开句柄上做 chunk 头遍历，不读 DATA 体。
            var chunks = AudioBankChunkReader.ReadFromStream(tocStream, record.TocDataOffset + 16, record.TocDataSize - 16);
            if (chunks is not null)
                metadata = new BankMetadata(chunks.Value);
        }
        else
        {
            // slim：bank 数据在 bundle 里，读取即解码；一次性建立尺寸+哈希表后立即释放字节。
            var bankData = _readTocRange(record.TocDataOffset + 16, (int)record.TocDataSize - 16);
            if (bankData is not null && AudioBankChunkReader.Parse(bankData) is { } chunks)
            {
                metadata = new BankMetadata(chunks);
                var dataEnd = (long)chunks.DataOffset + chunks.DataSize;
                foreach (var media in chunks.Didx)
                {
                    if (media.Size == 0 || media.Size > MaxMediaBytes ||
                        chunks.DataOffset + media.Offset + media.Size > dataEnd)
                        continue;
                    metadata.Hashes[media.Id] = SHA256.HashData(bankData.AsSpan((int)(chunks.DataOffset + media.Offset), (int)media.Size));
                }
                metadata.Complete = true;
            }
        }

        if (metadata is not null)
            _banks[record.FileId] = metadata;
        return metadata;
    }

    private FileStream? _persistentTocStream;

    /// <summary>Locates and loads the baseline for one package. Returns null when the game data
    /// folder does not provide it (unknown layout, package missing, or parse failure).</summary>
    public static GameAudioBaseline? TryLoad(DirectoryInfo dataDirectory, string packageBaseName, ILogger logger)
    {
        try
        {
            var legacyToc = Path.Combine(dataDirectory.FullName, packageBaseName);
            if (File.Exists(legacyToc))
            {
                var tocStream = OpenSharedRead(legacyToc);
                FileStream? streamStream = null;
                var legacyStream = legacyToc + ".stream";
                if (File.Exists(legacyStream))
                    streamStream = OpenSharedRead(legacyStream);

                var legacyBaseline = TryParseToc(
                    (offset, length) => ReadStreamRange(tocStream, offset, length),
                    streamStream is null ? null : (offset, length) => ReadStreamRange(streamStream, offset, length),
                    logger);
                if (legacyBaseline is null)
                {
                    tocStream.Dispose();
                    streamStream?.Dispose();
                    return null;
                }
                legacyBaseline._persistentTocStream = tocStream;
                legacyBaseline._ownedStreams.Add(tocStream);
                if (streamStream is not null)
                    legacyBaseline._ownedStreams.Add(streamStream);
                return legacyBaseline;
            }

            var bundlesPath = Path.Combine(dataDirectory.FullName, "bundles.nxa");
            if (!File.Exists(bundlesPath))
                return null;

            var index = GameBundleIndexCache.GetOrCreate(dataDirectory, bundlesPath, logger);
            if (!index.TryGetPackage(packageBaseName, out var tocItems) || tocItems is not { Length: > 0 })
                return null;
            index.TryGetPackage(packageBaseName + ".stream", out var streamItems);
            var hasStreamItems = streamItems is { Length: > 0 };

            // 构造阶段（读 TOC 头/条目）走无缓存静态路径；构造完成后委托切换到实例方法，
            // 后续媒体读取才能命中 bundle item 缓存。
            GameAudioBaseline? created = null;
            var baseline = TryParseToc(
                (offset, length) => created is not null
                    ? created.ReadSlimRange(stream: false, offset, length)
                    : ReadSlimRangeNoCache(index, tocItems!, offset, length),
                hasStreamItems
                    ? (offset, length) => created is not null
                        ? created.ReadSlimRange(stream: true, offset, length)
                        : ReadSlimRangeNoCache(index, streamItems!, offset, length)
                    : null,
                logger);
            if (baseline is not null)
            {
                baseline._slimIndex = index;
                baseline._slimTocItems = tocItems!;
                baseline._slimStreamItems = hasStreamItems ? streamItems : null;
                created = baseline;
            }
            return baseline;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to load game audio baseline for {Base}", packageBaseName);
            return null;
        }
    }

    private GameBundleIndex? _slimIndex;
    private GameUnitReferenceReader.PackageItem[]? _slimTocItems;
    private GameUnitReferenceReader.PackageItem[]? _slimStreamItems;

    private byte[]? ReadSlimRange(bool stream, long offset, int length)
    {
        var items = stream ? _slimStreamItems : _slimTocItems;
        return _slimIndex is null || items is null ? null : ReadSlimRangeCore(_slimIndex, items, offset, length, this);
    }

    private static byte[]? ReadSlimRangeNoCache(GameBundleIndex index, GameUnitReferenceReader.PackageItem[] items, long offset, int length)
        => ReadSlimRangeCore(index, items, offset, length, null);

    private static FileStream OpenSharedRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        81920,
        FileOptions.RandomAccess);

    private static GameAudioBaseline? TryParseToc(RangeReader readToc, RangeReader? readStream, ILogger logger)
    {
        var headerBytes = readToc(0, HeaderSize);
        if (headerBytes is null || headerBytes.Length < HeaderSize)
            return null;
        if (BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(0, 4)) != PatchHeaderMagic)
            return null;

        var numTypes = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(4, 4));
        var numFiles = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(8, 4));
        if (numTypes < 0 || numFiles < 0 || numTypes > MaxTypes || numFiles > MaxFiles)
            return null;

        var entriesOffset = HeaderSize + (long)numTypes * TypeEntrySize;
        var entriesLength = (long)numFiles * FileEntrySize;
        if (entriesLength > MaxBundleRangeBytes)
            return null;
        var entriesBytes = readToc(entriesOffset, (int)entriesLength);
        if (entriesBytes is null)
            return null;

        var entries = new Dictionary<ulong, TocRecord>(numFiles);
        for (var i = 0; i < numFiles; i++)
        {
            var entry = entriesBytes.AsSpan(i * FileEntrySize, FileEntrySize);
            var fileId = BinaryPrimitives.ReadUInt64LittleEndian(entry);
            var typeId = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            var tocDataOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
            var streamOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[24..]);
            var tocDataSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[56..]);
            var streamSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[60..]);

            EntryKind? kind = typeId switch
            {
                AudioBankInspectionService.WwiseBankTypeId => EntryKind.Bank,
                AudioBankInspectionService.WwiseStreamTypeId => EntryKind.Stream,
                TextBankInspectionService.TextBankTypeId => EntryKind.TextBank,
                _ => null,
            };
            if (kind is not { } parsedKind)
                continue;
            entries[fileId] = new TocRecord(fileId, parsedKind, tocDataOffset, tocDataSize, streamOffset, streamSize);
        }

        if (entries.Count == 0)
            return null;
        logger.LogDebug("Game audio baseline loaded: {Entries} bank/stream resources", entries.Count);
        return new GameAudioBaseline(readToc, readStream, entries);
    }

    private static byte[]? ReadStreamRange(FileStream stream, long offset, int length)
    {
        try
        {
            if (offset < 0 || length < 0 || offset > stream.Length - length)
                return null;
            var buffer = new byte[length];
            stream.Position = offset;
            var read = 0;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer, read, buffer.Length - read);
                if (count <= 0)
                    return null;
                read += count;
            }
            return buffer;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[]? ReadSlimRangeCore(
        GameBundleIndex index,
        GameUnitReferenceReader.PackageItem[] items,
        long offset,
        int length,
        GameAudioBaseline? baseline)
    {
        if (offset < 0 || length <= 0 || length > MaxBundleRangeBytes)
            return null;

        var result = new byte[length];
        var filled = 0;
        while (filled < length)
        {
            var position = offset + filled;
            GameUnitReferenceReader.PackageItem? containing = null;
            var containingIndex = -1;
            for (var i = items.Length - 1; i >= 0; i--)
            {
                if ((long)items[i].ArchiveOffset <= position)
                {
                    containing = items[i];
                    containingIndex = i;
                    break;
                }
            }
            if (containing is null)
                return null;

            var itemStart = (long)containing.ArchiveOffset;
            var itemEnd = containingIndex + 1 < items.Length ? (long)items[containingIndex + 1].ArchiveOffset : itemStart + MaxBundleRangeBytes;
            var intraOffset = (int)(position - itemStart);
            var wantBytes = (int)Math.Min(itemEnd - itemStart, (long)intraOffset + (length - filled));
            if (wantBytes <= intraOffset)
                return null;

            byte[] itemData;
            if (baseline is not null &&
                baseline._lastItemCache is { } cache &&
                cache.BundleOffset == containing.BundleOffset)
            {
                itemData = cache.Data;
            }
            else
            {
                try
                {
                    itemData = GameUnitReferenceReader.ReadBundleResource(
                        index.Bundles[containing.BundleIndex],
                        containing.BundleOffset,
                        MaxBundleRangeBytes);
                }
                catch (Exception)
                {
                    return null;
                }
                if (baseline is not null)
                    baseline._lastItemCache = (containing.BundleOffset, itemData);
            }

            if (itemData.Length <= intraOffset)
                return null;
            var take = Math.Min(itemData.Length - intraOffset, length - filled);
            Buffer.BlockCopy(itemData, intraOffset, result, filled, take);
            filled += take;
        }

        return result;
    }
}

/// <summary>
/// Decoded bundles.nxa index with a package-name → items lookup. The index can take seconds to
/// decompress, so one instance is cached per data directory and reused across mods.
/// </summary>
internal sealed class GameBundleIndex
{
    private const int RecordStride = 0x18;
    private readonly byte[] _indexData;
    private readonly int _packageCount;
    private readonly Dictionary<string, GameUnitReferenceReader.PackageItem[]?> _packageLookup = new(StringComparer.OrdinalIgnoreCase);

    public GameUnitReferenceReader.BundleInfo[] Bundles { get; }

    private GameBundleIndex(byte[] indexData, int packageCount, GameUnitReferenceReader.BundleInfo[] bundles)
    {
        _indexData = indexData;
        _packageCount = packageCount;
        Bundles = bundles;
    }

    public static GameBundleIndex Load(string bundlesPath, ILogger logger)
    {
        var indexData = GameUnitReferenceReader.DecodeDsarFile(bundlesPath);
        if (indexData.Length < 0x18)
            throw new InvalidDataException("The game bundle index is too small.");
        var bundleCount = BinaryPrimitives.ReadUInt32LittleEndian(indexData.AsSpan(0x0C, 4));
        var packageCount = BinaryPrimitives.ReadUInt32LittleEndian(indexData.AsSpan(0x10, 4));
        if (bundleCount is 0 or > 256 || packageCount > 1_000_000)
            throw new InvalidDataException("The game bundle index has suspicious counts.");

        var bundles = new GameUnitReferenceReader.BundleInfo[bundleCount];
        for (var i = 0; i < bundleCount; i++)
        {
            var bundlePath = Path.Combine(Path.GetDirectoryName(bundlesPath)!, $"bundles.{i:00}.nxa");
            bundles[i] = GameUnitReferenceReader.LoadBundleInfo(bundlePath);
        }

        logger.LogInformation("Game bundle index ready: {Bundles} bundles, {Packages} packages", bundleCount, packageCount);
        return new GameBundleIndex(indexData, (int)packageCount, bundles);
    }

    public bool TryGetPackage(string packageName, out GameUnitReferenceReader.PackageItem[]? items)
    {
        if (_packageLookup.TryGetValue(packageName, out var cached))
        {
            items = cached;
            return cached is not null;
        }

        items = ScanForPackage(packageName);
        _packageLookup[packageName] = items;
        return items is not null;
    }

    private GameUnitReferenceReader.PackageItem[]? ScanForPackage(string packageName)
    {
        // 百万级包记录：逐记录做字节比较（不做每记录的字符串分配/UTF8 解码），
        // 查询结果按包名缓存，扫描本身毫秒级。
        var targetName = Encoding.UTF8.GetBytes(packageName);
        for (var packageIndex = 0; packageIndex < _packageCount; packageIndex++)
        {
            var recordOffset = 0x18 + packageIndex * RecordStride;
            if (recordOffset + RecordStride > _indexData.Length)
                break;
            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(_indexData.AsSpan(recordOffset + 8, 4));
            var itemCount = BinaryPrimitives.ReadUInt32LittleEndian(_indexData.AsSpan(recordOffset + 12, 4));
            var itemsOffset = BinaryPrimitives.ReadUInt32LittleEndian(_indexData.AsSpan(recordOffset + 16, 4));
            if (itemCount == 0 || itemCount > 100_000)
                continue;
            if (nameOffset + (ulong)targetName.Length + 1 > (ulong)_indexData.Length ||
                (ulong)itemsOffset + itemCount * 0x10UL > (ulong)_indexData.Length)
                continue;

            if (!_indexData.AsSpan((int)nameOffset, targetName.Length).SequenceEqual(targetName) ||
                _indexData[(int)nameOffset + targetName.Length] != 0)
                continue;

            var items = new GameUnitReferenceReader.PackageItem[itemCount];
            for (var i = 0; i < itemCount; i++)
            {
                var itemOffset = itemsOffset + i * 0x10;
                var bundleIndex = _indexData[itemOffset + 15];
                if (bundleIndex >= Bundles.Length)
                    return null;
                items[i] = new GameUnitReferenceReader.PackageItem(
                    BinaryPrimitives.ReadUInt64LittleEndian(_indexData.AsSpan((int)itemOffset, 8)),
                    BinaryPrimitives.ReadUInt32LittleEndian(_indexData.AsSpan((int)itemOffset + 8, 4)),
                    bundleIndex);
            }
            return items;
        }

        return null;
    }
}

/// <summary>Per-data-directory cache of the decoded bundle index and loaded baselines.</summary>
internal static class GameBundleIndexCache
{
    private static readonly object Gate = new();
    private static GameBundleIndex? _index;
    private static string _indexKey = string.Empty;

    public static GameBundleIndex GetOrCreate(DirectoryInfo dataDirectory, string bundlesPath, ILogger logger)
    {
        var key = Path.Combine(dataDirectory.FullName, new FileInfo(bundlesPath).LastWriteTimeUtc.Ticks.ToString());
        lock (Gate)
        {
            if (_index is not null && _indexKey == key)
                return _index;

            _index?.DisposeBundles();
            _index = GameBundleIndex.Load(bundlesPath, logger);
            _indexKey = key;
            return _index;
        }
    }

    private static void DisposeBundles(this GameBundleIndex index)
    {
        foreach (var bundle in index.Bundles)
            bundle.Dispose();
    }
}
