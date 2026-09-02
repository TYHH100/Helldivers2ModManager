using Helldivers2ModManager.Models;
using K4os.Compression.LZ4;
using Microsoft.Extensions.Logging;
using System.Collections.Frozen;
using System.IO;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using static Helldivers2ModManager.Services.VersionCheckShared;
using Helldivers2ModManager.Services.Infrastructure;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 从游戏归档读取 Unit 参考数据。
/// DSAR/归档与补丁结构的解析依据 HD2SDK-CommunityEdition 和 hd2-repatcher 的公开研究；
/// 来源：https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition、
/// https://github.com/RaidingForPants/hd2-repatcher。
/// </summary>
internal sealed class GameUnitReferenceReader
{
    private readonly ILogger _logger;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;

    public GameUnitReferenceReader(ILogger logger, SettingsService settingsService, LocalizationService localizationService)
    {
        _logger = logger;
        _settingsService = settingsService;
        _localizationService = localizationService;
    }
    private const byte DsarCompressionNone = 0;
    private const byte DsarCompressionLz4 = 3;
    private const byte DsarChunkStart = 2;
    internal const int MaxBundleResourceBytes = 64 * 1024 * 1024;
    private const long BonesTypeId = 0x18DEAD01056B72E9;
    private const long AnimationTypeId = unchecked((long)0x931E336D7646CC26UL);
    private const long StateMachineTypeId = unchecked((long)0xA486D4045106165CUL);
    private const long HelldiverAvatarUnitId = 5556372446766824087;
    private const int MaxAnimationsPerPreview = 256;
    private readonly SemaphoreSlim _gameReferenceSemaphore = new(1, 1);
    private GameUnitReferenceIndex? _gameReferenceIndex;
    // 原版贴图有界读取的最近 item 解码缓存（起始 chunk uoff → 解码字节）。
    // 仅在 _gameReferenceSemaphore 持有期间读写；与音频基线的 last-item 缓存同策略。
    private (ulong BundleOffset, byte[] Data)? _lastSliceItemCache;

    internal sealed record DsarChunk(
        ulong UncompressedOffset,
        ulong CompressedOffset,
        int UncompressedSize,
        int CompressedSize,
        byte Compression,
        byte Flags);

    internal sealed class BundleInfo : IDisposable
    {
        public required string Path { get; init; }
        public required DsarChunk[] Chunks { get; init; }
        public required Dictionary<ulong, int> ChunkByOffset { get; init; }

        private FileStream? _stream;

        /// <summary>
        /// 返回索引生命周期内复用的只读流：批量解析 Unit 引用/动画资源时，
        /// 避免每个资源都重新打开一次文件（FileShare.ReadWrite 不阻塞游戏自身读取）。
        /// 所有调用点都在 _gameReferenceSemaphore 保护下，无需额外加锁。
        /// </summary>
        public FileStream OpenReadStream()
        {
            _stream ??= new FileStream(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                81920,
                FileOptions.RandomAccess);
            return _stream;
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    internal sealed record PackageItem(
        ulong ArchiveOffset,
        ulong BundleOffset,
        byte BundleIndex);

    internal sealed record GameUnitLocator(
        string PackageName,
        PackageItem[] Items,
        ulong ResourceOffset,
        uint ResourceSize,
        uint GpuSize);

    internal sealed class GameUnitReferenceIndex : IDisposable
    {
        public required string CacheKey { get; init; }
        public required BundleInfo[] Bundles { get; init; }
        public required IReadOnlyDictionary<long, List<GameUnitLocator>> UnitLocators { get; init; }
        public required IReadOnlyDictionary<(long FileId, long TypeId), List<GameUnitLocator>> AnimationResourceLocators { get; init; }
        /// <summary>全量贴图定位表（实测单游戏归档 8 万+ 条，按 FileID 首见者收录）。
        /// 模型预览解析"模组未携带的原版贴图"时查询；与 Unit 定位表共用同一次扫描。</summary>
        public required IReadOnlyDictionary<long, GameTextureLocator> TextureLocators { get; init; }
        public Dictionary<long, GameUnitReferenceData> ResolvedReferences { get; } = [];
        public Dictionary<long, IReadOnlyList<string>> PackageNames { get; } = [];
        public HashSet<long> AmbiguousUnitIds { get; } = [];

        /// <summary>
        /// 头像（Helldiver）动画资源引用与动画库缓存：同一索引生命周期内只解析一次，
        /// 所有骨架共享同一头像库，避免每个骨架重复 LZ4 解码。
        /// </summary>
        public ModelPreviewAnimationResourceReference? HelldiverAnimationReference { get; set; }
        public ModelPreviewAnimationLibrary? HelldiverAnimationLibrary { get; set; }

        /// <summary>
        /// 释放缓存的 Bundle 只读流。旧索引被新索引替换时调用（仅在
        /// _gameReferenceSemaphore 持有期间发生，此时没有其他读取者）。
        /// </summary>
        public void Dispose()
        {
            foreach (var bundle in Bundles)
                bundle.Dispose();
        }
    }

    internal sealed class GameUnitReferenceLookup
    {
        public Dictionary<long, GameUnitReferenceData> References { get; } = [];
        public Dictionary<long, IReadOnlyList<string>> PackageNames { get; } = [];
        public HashSet<long> MissingUnitIds { get; } = [];
        public HashSet<long> AmbiguousUnitIds { get; } = [];
        public string? ErrorMessage { get; init; }
    }

    internal sealed class GameUnitReferenceData
    {
        public required long FileId { get; init; }
        public required uint Version { get; init; }
        public required byte[] LodGroupData { get; init; }
        public required uint[] MeshIds { get; init; }
        public uint GpuSize { get; init; }
        public required string PackageName { get; init; }
        public string Signature => $"{Version:X8}:{Convert.ToHexString(SHA256.HashData(LodGroupData))}";
    }

    internal async Task<GameUnitReferenceLookup> GetGameUnitReferencesAsync(
        IReadOnlyCollection<long> unitIds,
        CancellationToken cancellationToken = default)
    {
        var dataDirectory = GetConfiguredGameDataDirectory();
        if (dataDirectory is null)
        {
            return new GameUnitReferenceLookup
            {
                ErrorMessage = _localizationService["VersionCheckRepair.GameDataUnavailable"]
            };
        }

        var cacheKey = GetBundleIndexCacheKey(dataDirectory);

        await _gameReferenceSemaphore.WaitAsync(cancellationToken);
        try
        {
            // 索引构建是数十秒级的 LZ4 扫描，必须在后台线程执行（continuation 可能回到 UI 线程）。
            await Task.Run(
                () => EnsureReferenceIndexCore(dataDirectory, cacheKey),
                cancellationToken);

            // 解析 Unit 引用包含 LZ4 解码（CPU 密集），放到后台线程执行，避免阻塞调用线程（UI）。
            return await Task.Run(
                () => ResolveGameUnitReferences(_gameReferenceIndex!, unitIds),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
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

    internal async Task<ModelPreviewAnimationLibrary?> FindCompatibleGameAnimationLibraryAsync(
        IReadOnlyCollection<uint> transformNameHashes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transformNameHashes);
        if (transformNameHashes.Count == 0)
            return null;

        _ = await GetGameUnitReferencesAsync([], cancellationToken);
        await _gameReferenceSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_gameReferenceIndex is null)
                return null;
            return await Task.Run(
                () => FindCompatibleGameAnimationLibrary(
                    _gameReferenceIndex,
                    transformNameHashes,
                    cancellationToken),
                cancellationToken);
        }
        finally
        {
            _gameReferenceSemaphore.Release();
        }
    }

    private static ModelPreviewAnimationLibrary? FindCompatibleGameAnimationLibrary(
        GameUnitReferenceIndex index,
        IReadOnlyCollection<uint> transformNameHashes,
        CancellationToken cancellationToken)
    {
        var transformHashes = transformNameHashes.Where(static hash => hash != 0).ToHashSet();
        if (transformHashes.Count == 0)
            return null;

        // 头像动画库对所有骨架相同：同一索引生命周期内只解析一次
        // （LZ4 解码头像 Unit + bones + state machine + 最多 256 个动画 clip，
        // 多个骨架重复调用会重复全部解码）。
        var reference = index.HelldiverAnimationReference;
        if (reference is null)
        {
            var references = ResolveGameUnitAnimationReferences(
                index,
                [HelldiverAvatarUnitId],
                cancellationToken);
            if (references.TryGetValue(HelldiverAvatarUnitId, out var resolved))
            {
                index.HelldiverAnimationReference = resolved;
                reference = resolved;
            }
        }
        if (reference is null)
            return null;

        var library = index.HelldiverAnimationLibrary;
        if (library is null)
        {
            library = ReadGameAnimationLibrary(
                index,
                reference.Value.BonesId,
                reference.Value.StateMachineId,
                cancellationToken);
            if (library is not null)
                index.HelldiverAnimationLibrary = library;
        }
        if (library is null)
            return null;

        var animationHashes = library.BoneHashes.Where(static hash => hash != 0).ToHashSet();
        var matchingBones = animationHashes.Count(transformHashes.Contains);
        return matchingBones >= ModelPreviewAnimationCompatibility.MinimumMatchingBones &&
               matchingBones >= animationHashes.Count * ModelPreviewAnimationCompatibility.MinimumBoneCoverage &&
               matchingBones >= transformHashes.Count * ModelPreviewAnimationCompatibility.MinimumBoneCoverage
            ? library
            : null;
    }

    private static IReadOnlyDictionary<long, ModelPreviewAnimationResourceReference>
        ResolveGameUnitAnimationReferences(
            GameUnitReferenceIndex index,
            IReadOnlyCollection<long> unitIds,
            CancellationToken cancellationToken)
    {
        var references = new Dictionary<long, ModelPreviewAnimationResourceReference>();
        foreach (var unitId in unitIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!index.UnitLocators.TryGetValue(unitId, out var locators))
                continue;

            foreach (var locator in locators)
            {
                var unitData = TryReadGameResource(index.Bundles, locator);
                if (unitData is null || unitData.Length < 0x28)
                    continue;

                var bonesId = BinaryPrimitives.ReadUInt64LittleEndian(unitData.AsSpan(0x08, sizeof(ulong)));
                var stateMachineId = BinaryPrimitives.ReadUInt64LittleEndian(unitData.AsSpan(0x20, sizeof(ulong)));
                if (bonesId == 0 || stateMachineId == 0)
                    continue;

                references[unitId] = new ModelPreviewAnimationResourceReference(bonesId, stateMachineId);
                break;
            }
        }
        return references;
    }

    private static ModelPreviewAnimationLibrary? ReadGameAnimationLibrary(
        GameUnitReferenceIndex index,
        ulong bonesId,
        ulong stateMachineId,
        CancellationToken cancellationToken)
    {
        var bonesData = TryReadIndexedGameResource(index, unchecked((long)bonesId), BonesTypeId);
        var stateMachineData = TryReadIndexedGameResource(index, unchecked((long)stateMachineId), StateMachineTypeId);
        if (bonesData is null || stateMachineData is null)
            return null;

        var boneHashes = ModelPreviewAnimationLibraryParser.ParseBoneHashes(bonesData);
        var references = ModelPreviewAnimationLibraryParser.ParseStateMachineAnimations(stateMachineData);
        var animations = new List<ModelPreviewAnimationOption>(Math.Min(references.Count, MaxAnimationsPerPreview));
        foreach (var reference in references.Take(MaxAnimationsPerPreview))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var animationData = TryReadIndexedGameResource(
                index,
                unchecked((long)reference.AnimationId),
                AnimationTypeId);
            if (animationData is null ||
                !ModelPreviewAnimationParser.TryParse(
                    animationData,
                    reference.AnimationId,
                    out var clip,
                    out _) ||
                clip is null || clip.BoneCount > boneHashes.Count)
            {
                continue;
            }

            animations.Add(new ModelPreviewAnimationOption
            {
                AnimationId = reference.AnimationId,
                StateNameHash = reference.StateNameHash,
                LayerIndex = reference.LayerIndex,
                Clip = clip
            });
        }

        return animations.Count == 0
            ? null
            : new ModelPreviewAnimationLibrary
            {
                BonesId = bonesId,
                StateMachineId = stateMachineId,
                BoneHashes = boneHashes,
                Animations = animations
            };
    }

    /// <summary>
    /// 在游戏索引上解析指定 Unit 的参考数据（读缓存字典 + 按需 LZ4 解码）。
    /// 必须在 _gameReferenceSemaphore 持有期间调用，且不访问 UI 线程。
    /// </summary>
    internal GameUnitReferenceLookup ResolveGameUnitReferences(
        GameUnitReferenceIndex index,
        IReadOnlyCollection<long> unitIds)
    {
        var lookup = new GameUnitReferenceLookup();
        foreach (var unitId in unitIds)
        {
            if (index.PackageNames.TryGetValue(unitId, out var cachedPackageNames))
                lookup.PackageNames[unitId] = cachedPackageNames;

            if (index.AmbiguousUnitIds.Contains(unitId))
            {
                lookup.AmbiguousUnitIds.Add(unitId);
                continue;
            }

            if (index.ResolvedReferences.TryGetValue(unitId, out var cached))
            {
                lookup.References[unitId] = cached;
                continue;
            }

            if (!index.UnitLocators.TryGetValue(unitId, out var locators))
            {
                lookup.MissingUnitIds.Add(unitId);
                continue;
            }

            var candidates = new List<GameUnitReferenceData>();
            foreach (var locator in locators)
            {
                var candidate = TryReadGameUnitReference(
                    index.Bundles,
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
            else
            {
                var packageNames = candidates
                    .Select(candidate => candidate.PackageName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                index.PackageNames[unitId] = packageNames;
                lookup.PackageNames[unitId] = packageNames;

                if (distinctCandidates.Count > 1)
                {
                    index.AmbiguousUnitIds.Add(unitId);
                    lookup.AmbiguousUnitIds.Add(unitId);
                    continue;
                }

                var resolved = distinctCandidates[0];
                index.ResolvedReferences[unitId] = resolved;
                lookup.References[unitId] = resolved;
            }
        }

        return lookup;
    }

    internal DirectoryInfo? GetConfiguredGameDataDirectory()
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

    internal GameUnitReferenceIndex BuildGameUnitReferenceIndex(
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
        var animationResourceLocators = new Dictionary<(long FileId, long TypeId), List<GameUnitLocator>>();
        var textureLocators = new Dictionary<long, GameTextureLocator>();
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

            IndexPackageUnitLocators(
                packageName,
                items,
                tocData,
                locators,
                animationResourceLocators,
                textureLocators);
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
        // 定位表构建完成后不再变化，冻结为只读表（FrozenDictionary 查找更快）
        return new GameUnitReferenceIndex
        {
            CacheKey = cacheKey,
            Bundles = bundles,
            UnitLocators = locators.ToFrozenDictionary(),
            AnimationResourceLocators = animationResourceLocators.ToFrozenDictionary(),
            TextureLocators = textureLocators.ToFrozenDictionary()
        };
    }

    private static void IndexPackageUnitLocators(
        string packageName,
        PackageItem[] items,
        byte[] tocData,
        Dictionary<long, List<GameUnitLocator>> locators,
        Dictionary<(long FileId, long TypeId), List<GameUnitLocator>> animationResourceLocators,
        Dictionary<long, GameTextureLocator> textureLocators)
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
            var fileId = BinaryPrimitives.ReadInt64LittleEndian(tocData.AsSpan(entryOffset, 8));
            var resourceOffset = BinaryPrimitives.ReadUInt64LittleEndian(tocData.AsSpan(entryOffset + 16, 8));
            var resourceSize = BinaryPrimitives.ReadUInt32LittleEndian(tocData.AsSpan(entryOffset + 56, 4));
            var gpuSize = BinaryPrimitives.ReadUInt32LittleEndian(tocData.AsSpan(entryOffset + 64, 4));
            var locator = new GameUnitLocator(
                packageName,
                items,
                resourceOffset,
                resourceSize,
                gpuSize);
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
            else if (typeId == TextureTypeId)
            {
                // 全量贴图定位表：同一 FileID 可能被多个共享包收录，按首见者收录
                // （同 ID 贴图内容一致，无需消歧）。附带完整伴生偏移，读取时按
                // base/.gpu_resources/.stream 三组包 item 寻址。
                textureLocators.TryAdd(fileId, new GameTextureLocator(
                    packageName,
                    i + 1,
                    resourceOffset,
                    resourceSize,
                    BinaryPrimitives.ReadUInt64LittleEndian(tocData.AsSpan(entryOffset + 32, 8)),
                    gpuSize,
                    BinaryPrimitives.ReadUInt64LittleEndian(tocData.AsSpan(entryOffset + 24, 8)),
                    BinaryPrimitives.ReadUInt32LittleEndian(tocData.AsSpan(entryOffset + 60, 4))));
            }
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

    private static byte[]? TryReadIndexedGameResource(
        GameUnitReferenceIndex index,
        long fileId,
        long typeId)
    {
        if (!index.AnimationResourceLocators.TryGetValue((fileId, typeId), out var locators))
            return null;

        foreach (var locator in locators)
        {
            var data = TryReadGameResource(index.Bundles, locator);
            if (data is not null)
                return data;
        }

        return null;
    }

    private static byte[]? TryReadGameResource(BundleInfo[] bundles, GameUnitLocator locator)
    {
        var item = locator.Items.LastOrDefault(candidate => candidate.ArchiveOffset <= locator.ResourceOffset);
        if (item is null || locator.ResourceSize == 0 || locator.ResourceSize > MaxBundleResourceBytes)
            return null;

        try
        {
            var bundleOffset = checked(item.BundleOffset + locator.ResourceOffset - item.ArchiveOffset);
            var data = ReadBundleResource(bundles[item.BundleIndex], bundleOffset, MaxBundleResourceBytes);
            if (data.Length < locator.ResourceSize)
                return null;
            return data.Length == locator.ResourceSize
                ? data
                : data.AsSpan(0, checked((int)locator.ResourceSize)).ToArray();
        }
        catch
        {
            // Duplicate package entries are common; the caller can try another locator.
            return null;
        }
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
        return new BundleInfo
        {
            Path = path,
            Chunks = chunks,
            ChunkByOffset = chunkByOffset
        };
    }

    internal static DsarChunk[] ReadDsarChunkTable(FileStream stream)
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
        // 表项是连续存储的：只定位一次，顺序读取（chunk 数可达数十万，
        // 每次显式 seek 会产生等量的系统调用开销）。
        stream.Position = 0x20L;
        for (var i = 0; i < chunkCount; i++)
        {
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

        // 复用 BundleInfo 缓存的只读流（不要 using 释放——流属于索引生命周期）
        var stream = bundle.OpenReadStream();
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

    internal static byte[] DecodeDsarChunk(FileStream stream, DsarChunk chunk)
    {
        // 缓冲随后被 ReadExactly/LZ4 完全填充，免初始化清零（GC.AllocateUninitializedArray）
        var encoded = GC.AllocateUninitializedArray<byte>(chunk.CompressedSize);
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

        var decoded = GC.AllocateUninitializedArray<byte>(chunk.UncompressedSize);
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

    private const long TextureTypeId = unchecked((long)0xCD4238C6A0C69E32UL);
    private const int TextureHeaderOffset = 0xC0;
    private const int TextureHeaderSize = 148;

    /// <summary>游戏包内一条贴图资源的寻址信息（与模组补丁 TOC 条目同构）。
    /// 在 BuildGameUnitReferenceIndex 的既有扫描中顺带收集，查询零额外扫描。</summary>
    internal sealed record GameTextureLocator(
        string PackageName,
        int TocEntryIndex,
        ulong MainOffset,
        uint MainSize,
        ulong GpuOffset,
        uint GpuSize,
        ulong StreamOffset,
        uint StreamSize);

    internal sealed record GameOriginalTexture(
        TexturePreviewData Preview,
        GameTextureLocator Locator,
        int Width,
        int Height,
        int MipCount,
        int DxgiFormat);

    /// <summary>
    /// 从游戏归档按需解析原版贴图。模组材质引用模组未携带的贴图时，这些 ID 分散在
    /// 共享基础包与其他装备包里（实测同名包只能解析少数），因此按全量贴图定位表
    /// （在 Unit 索引扫描中顺带构建）查找。只做有界读取：DDS 头 + 所选 mip 的字节，
    /// 任何条目都不整体载入；解析失败按"无法解析"降级，不影响预览主流程。
    /// </summary>
    internal async Task<IReadOnlyDictionary<ulong, GameOriginalTexture>> ReadOriginalTexturesAsync(
        IReadOnlyList<ulong> textureFileIds,
        int maxPreviewPixels,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<ulong, GameOriginalTexture>();
        var dataDirectory = GetConfiguredGameDataDirectory();
        if (dataDirectory is null || textureFileIds.Count == 0)
            return results;

        // LZ4 解码与 BCn 解码都是 CPU 密集工作，统一放到后台线程（调用方在 UI 线程 await）。
        return await Task.Run(async () =>
        {
            try
            {
                var cacheKey = GetBundleIndexCacheKey(dataDirectory);
                await _gameReferenceSemaphore.WaitAsync(cancellationToken);
                try
                {
                    // 信号量持有期间读取：索引重建与 Dispose（Bundle 流）同样在该信号量下进行。
                    var index = EnsureReferenceIndexCore(dataDirectory, cacheKey);
                    foreach (var fileId in textureFileIds.Distinct())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!index.TextureLocators.TryGetValue(unchecked((long)fileId), out var locator))
                            continue;
                        var preview = TryDecodeOriginalTexture(dataDirectory, locator, maxPreviewPixels, cancellationToken);
                        if (preview is not null)
                            results[fileId] = preview;
                    }
                }
                finally
                {
                    _gameReferenceSemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read original textures from the game archive");
            }

            return results;
        }, cancellationToken);
    }

    private string GetBundleIndexCacheKey(DirectoryInfo dataDirectory)
    {
        var bundleFiles = dataDirectory.GetFiles("bundles*.nxa", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return dataDirectory.FullName + "|" + string.Join(
            "|",
            bundleFiles.Select(f => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}:{1}:{2}", f.Name, f.Length, f.LastWriteTimeUtc.Ticks)));
    }

    /// <summary>必须在持有 _gameReferenceSemaphore 时调用（索引重建与 Dispose 都在该信号量下）。</summary>
    private GameUnitReferenceIndex EnsureReferenceIndexCore(DirectoryInfo dataDirectory, string cacheKey)
    {
        if (_gameReferenceIndex is null ||
            !string.Equals(_gameReferenceIndex.CacheKey, cacheKey, StringComparison.Ordinal))
        {
            var newIndex = BuildGameUnitReferenceIndex(dataDirectory, cacheKey);
            var oldIndex = _gameReferenceIndex;
            _gameReferenceIndex = newIndex;
            // 旧索引的 Bundle 流不再被引用（semaphore 保护下无并发读取者），立即释放
            oldIndex?.Dispose();
        }

        return _gameReferenceIndex!;
    }

    private GameOriginalTexture? TryDecodeOriginalTexture(
        DirectoryInfo dataDirectory,
        GameTextureLocator locator,
        int maxPreviewPixels,
        CancellationToken cancellationToken)
    {
        if (locator.MainSize < TextureHeaderOffset + TextureHeaderSize)
            return null;

        var bundlesPath = Path.Combine(dataDirectory.FullName, "bundles.nxa");
        if (!File.Exists(bundlesPath))
            return null;
        var bundleIndex = GameBundleIndexCache.GetOrCreate(dataDirectory, bundlesPath, _logger);
        if (!bundleIndex.TryGetPackage(locator.PackageName, out var tocItems) || tocItems is not { Length: > 0 })
            return null;

        // TOC 条目的 MainOffset 指向 base 包的地址空间；DDS 头位于主资源 0xC0 处。
        var header = TryReadPackageRange(bundleIndex, tocItems, checked((long)locator.MainOffset + TextureHeaderOffset), TextureHeaderSize);
        if (header is null || header.Length < TextureHeaderSize ||
            !header.AsSpan(0, 4).SequenceEqual("DDS "u8))
        {
            return null;
        }

        var height = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12, 4));
        var width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16, 4));
        var mipCount = Math.Max(BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(28, 4)), 1);
        var dxgiFormat = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(128, 4));
        if (width <= 0 || height <= 0)
            return null;

        var format = PatchResourceInspectionService.GetCompressionFormat(dxgiFormat);
        if (format == BCnEncoder.Shared.CompressionFormat.Unknown)
            return null;

        // 游戏贴图可能不声明 mip 数（实测存在 mipCount=0 的条目）；按物理 mip 链上限
        // 走查，最终由伴生 declaredSize 的边界校验兜底，防止读出链外数据。
        var effectiveMipCount = Math.Max(
            mipCount,
            System.Numerics.BitOperations.Log2((uint)Math.Max(width, height)) + 1);
        var plan = PatchResourceInspectionService.PlanTopMip(format, width, height, effectiveMipCount, maxPreviewPixels);
        if (plan is not { } mipPlan)
            return null;

        var useStream = locator.StreamSize > 0;
        if (!bundleIndex.TryGetPackage(locator.PackageName + (useStream ? ".stream" : ".gpu_resources"), out var payloadItems) ||
            payloadItems is not { Length: > 0 })
        {
            return null;
        }

        // 只读所选 mip 的字节；声明的伴生尺寸必须覆盖 mip 范围（与模组补丁路径同一校验）。
        var declaredSize = useStream ? locator.StreamSize : locator.GpuSize;
        var payloadOffset = useStream ? locator.StreamOffset : locator.GpuOffset;
        if ((ulong)mipPlan.SkipBytes + (ulong)mipPlan.ByteCount > declaredSize)
            return null;

        var payload = TryReadPackageRange(
            bundleIndex,
            payloadItems,
            checked((long)(payloadOffset + mipPlan.SkipBytes)),
            checked((int)mipPlan.ByteCount));
        if (payload is null)
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        return PatchResourceInspectionService.DecodeMipPayload(payload, mipPlan, dxgiFormat) is { } preview
            ? new GameOriginalTexture(preview, locator, width, height, mipCount, dxgiFormat)
            : null;
    }

    /// <summary>
    /// 读取包地址空间的一段字节；偏移可横跨包内的多个 bundle item（与音频基线的
    /// 跨 item 连续读取同一寻址模型）。失败返回 null，不抛出。
    /// </summary>
    private byte[]? TryReadPackageRange(
        GameBundleIndex bundleIndex,
        PackageItem[] items,
        long offset,
        int length)
    {
        if (offset < 0 || length <= 0 || length > MaxBundleResourceBytes)
            return null;

        var result = new byte[length];
        var filled = 0;
        while (filled < length)
        {
            var position = offset + filled;
            PackageItem? containing = null;
            var containingIndex = -1;
            for (var i = items.Length - 1; i >= 0; i--)
            {
                if (items[i].ArchiveOffset <= (ulong)position)
                {
                    containing = items[i];
                    containingIndex = i;
                    break;
                }
            }
            if (containing is null)
                return null;

            var itemStart = (long)containing.ArchiveOffset;
            var intraOffset = (int)(position - itemStart);
            var needBytes = length - filled;
            var bundle = bundleIndex.Bundles[containing.BundleIndex];
            if (!bundle.ChunkByOffset.TryGetValue(containing.BundleOffset, out var chunkIndex))
                return null;
            var nextItemBundleOffset = containingIndex + 1 < items.Length
                ? items[containingIndex + 1].BundleOffset
                : ulong.MaxValue;

            // 包内一个 item 可承载多个资源：资源按 chunk 对齐，以 Start 标记（flags&2）
            // chunk 分界（实测数据验证）。先定位 intra 落在的 chunk，再从该 chunk 起连续
            // 解码到覆盖 intra+need——不能在 Start 标记处截断，否则 item 内后继资源的
            // 读取会落空。边界取下一个 item 的 BundleOffset（同 bundle 的 chunk 起点）。
            long startIntra = 0;
            var reachedEnd = false;
            while (startIntra + bundle.Chunks[chunkIndex].UncompressedSize <= intraOffset)
            {
                if (bundle.Chunks[chunkIndex].UncompressedOffset == nextItemBundleOffset)
                {
                    reachedEnd = true;
                    break;
                }
                startIntra += bundle.Chunks[chunkIndex].UncompressedSize;
                chunkIndex++;
                if (chunkIndex >= bundle.Chunks.Length)
                {
                    reachedEnd = true;
                    break;
                }
            }
            if (reachedEnd)
                return null;

            var startChunkUoff = bundle.Chunks[chunkIndex].UncompressedOffset;
            var required = intraOffset - (int)startIntra + needBytes;
            byte[] itemData;
            if (_lastSliceItemCache is { } cache && cache.BundleOffset == startChunkUoff)
            {
                itemData = cache.Data;
            }
            else
            {
                try
                {
                    // 复用 BundleInfo 缓存的只读流（不要释放——流属于索引生命周期）
                    var stream = bundle.OpenReadStream();
                    using var output = new MemoryStream();
                    while (output.Length < required && chunkIndex < bundle.Chunks.Length)
                    {
                        var chunk = bundle.Chunks[chunkIndex];
                        if (output.Length > 0 && chunk.UncompressedOffset == nextItemBundleOffset)
                            break;
                        var decoded = DecodeDsarChunk(stream, chunk);
                        output.Write(decoded);
                        if (output.Length > MaxBundleResourceBytes)
                            return null;
                        chunkIndex++;
                    }

                    itemData = output.ToArray();
                }
                catch (Exception)
                {
                    return null;
                }

                _lastSliceItemCache = (startChunkUoff, itemData);
            }

            var intraInData = intraOffset - (int)startIntra;
            if (itemData.Length <= intraInData)
                return null;
            var take = Math.Min(itemData.Length - intraInData, needBytes);
            if (take <= 0)
                return null;
            Buffer.BlockCopy(itemData, intraInData, result, filled, take);
            filled += take;
        }

        return result;
    }
}
