using System.Buffers.Binary;
using System.IO;
using System.Text;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services;

/// <summary>
/// Inspects the audio content of a set of mod patch files for the preview page.
/// Walks the patch TOC the same way <see cref="ModTypeDetectionService"/> does, then, for every
/// WWISE_BANK resource, splits the embedded Wwise bank into its chunks and turns each DIDX media
/// entry into an <see cref="AudioEntry"/>; WWISE_STREAM resources become one entry each. Only the
/// WEM headers (bounded 128-byte reads) are touched during inspection — the media payloads stay
/// on disk and are read on demand by the playback service. All CPU/IO work runs on a worker
/// thread; callers await the returned task.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class AudioBankInspectionService
{
    private const int PatchHeaderMagic = unchecked((int)0xF0000011);
    private const int HeaderSize = 72;
    private const int TypeEntrySize = 32;
    private const int FileEntrySize = 80;
    private const int MaxTypes = 1000;
    private const int MaxFiles = 100000;
    private const int MaxBanksPerPatch = 64;
    private const int MaxMediaEntriesPerPatch = 65536;
    private const int MaxDepNameBytes = 512;
    private const int WemHeaderProbeBytes = 128;
    private const int BankDataPrefixSize = 16;

    // ToC resource type ids (structure reference: hd2-audio-modder's const.py — unlicensed/ARR
    // project; format constants referenced only, no source code copied. See README 第三方声明).
    internal const ulong WwiseBankTypeId = 0x535A7BD3E650D799UL;   // 6006249203084351385
    internal const ulong WwiseStreamTypeId = 0x504B55235D21440EUL; // 5785811756662211598
    internal const ulong WwiseDepTypeId = 0xAF32095C82F2B070UL;    // 12624162998411505776

    private const int MaxOriginalCompareBytes = 32 * 1024 * 1024;
    private const int MaxCachedBaselines = 16;

    private readonly ILogger<AudioBankInspectionService> _logger;
    private readonly SettingsService _settingsService;
    private readonly Dictionary<string, GameAudioBaseline?> _baselineCache = new(StringComparer.Ordinal);

    public AudioBankInspectionService(ILogger<AudioBankInspectionService> logger, SettingsService settingsService)
    {
        _logger = logger;
        _settingsService = settingsService;
    }

    /// <summary>按补丁文件名解析出游戏包名（去掉 .patch_N 后缀），并带缓存地加载原版基线。</summary>
    private GameAudioBaseline? ResolveBaselineFromSettings(string packageBaseName)
    {
        if (_settingsService is null ||
            !_settingsService.Initialized ||
            string.IsNullOrWhiteSpace(_settingsService.GameDirectory))
            return null;
        var dataDirectory = new DirectoryInfo(Path.Combine(_settingsService.GameDirectory, "data"));
        if (!dataDirectory.Exists)
            return null;

        var key = dataDirectory.FullName + "|" + packageBaseName;
        if (_baselineCache.TryGetValue(key, out var cached))
            return cached;

        var baseline = GameAudioBaseline.TryLoad(dataDirectory, packageBaseName, _logger);
        while (_baselineCache.Count >= MaxCachedBaselines)
        {
            // 基线持有 legacy 布局的常开只读句柄，逐出时必须释放。
            var oldestKey = _baselineCache.Keys.First();
            if (_baselineCache.Remove(oldestKey, out var evicted))
                evicted?.Dispose();
        }
        _baselineCache[key] = baseline;
        return baseline;
    }

    /// <summary>"9ba626afa44a3aa3.patch_23" → "9ba626afa44a3aa3"；已是游戏包名则原样返回。</summary>
    private static string GetPackageBaseName(string patchFileName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            patchFileName,
            @"^(?<base>[0-9A-Fa-f]{16})\.patch_\d+$");
        return match.Success ? match.Groups["base"].Value : Path.GetFileNameWithoutExtension(patchFileName);
    }

    public Task<AudioInventoryResult> InspectAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        CancellationToken cancellationToken)
        => InspectAsync(modDirectory, patchFiles, ResolveBaselineFromSettings, cancellationToken);

    internal Task<AudioInventoryResult> InspectAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        Func<string, GameAudioBaseline?>? baselineResolver,
        CancellationToken cancellationToken)
        => Task.Run(() => Inspect(modDirectory, patchFiles, baselineResolver, cancellationToken), cancellationToken);

    private AudioInventoryResult Inspect(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        Func<string, GameAudioBaseline?>? baselineResolver,
        CancellationToken cancellationToken)
    {
        var groups = new List<AudioBankGroup>();
        var patchCount = 0;
        var uncomparedEntries = 0;
        string? error = null;

        foreach (var patch in patchFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                patch.Refresh();
                if (!patch.Exists || patch.Length < HeaderSize)
                    continue;
                if (TryInspectPatch(modDirectory, patch, baselineResolver, cancellationToken) is { PatchGroups: var groupList, Uncompared: var uncompared })
                {
                    groups.AddRange(groupList);
                    uncomparedEntries += uncompared;
                    patchCount++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Audio inspection failed for patch {Patch}", patch.FullName);
                error ??= ex.Message;
            }
        }

        return new AudioInventoryResult(groups, patchCount, error, uncomparedEntries);
    }

    private readonly record struct PatchInspection(
        List<AudioBankGroup> PatchGroups,
        int Uncompared);

    private PatchInspection? TryInspectPatch(
        DirectoryInfo modDirectory,
        FileInfo patch,
        Func<string, GameAudioBaseline?>? baselineResolver,
        CancellationToken cancellationToken)
    {
        var streamPath = patch.FullName + ".stream";
        using var streamFile = File.Exists(streamPath)
            ? new FileStream(streamPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.RandomAccess)
            : null;

        using var patchFile = new FileStream(
            patch.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.RandomAccess);

        Span<byte> header = stackalloc byte[HeaderSize];
        if (!TryReadAt(patchFile, 0, header) || BinaryPrimitives.ReadInt32LittleEndian(header) != PatchHeaderMagic)
            return null;

        var numTypes = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        var numFiles = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (numTypes < 0 || numFiles < 0 || numTypes > MaxTypes || numFiles > MaxFiles)
            return null;

        var fileEntriesOffset = HeaderSize + (long)numTypes * TypeEntrySize;
        if (fileEntriesOffset + (long)numFiles * FileEntrySize > patchFile.Length)
            return null;

        Span<byte> entry = stackalloc byte[FileEntrySize];
        var bankChunks = new List<BankChunkInfo>();
        var depNames = new Dictionary<ulong, string>();
        var streamEntries = new List<StreamEntryInfo>();

        for (var i = 0; i < numFiles; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadAt(patchFile, fileEntriesOffset + i * FileEntrySize, entry))
                return null;

            var fileId = BinaryPrimitives.ReadUInt64LittleEndian(entry);
            var typeId = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            var tocDataOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
            var streamFileOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[24..]);
            var tocDataSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[56..]);
            var streamSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[60..]);

            if (typeId == WwiseBankTypeId)
            {
                if (bankChunks.Count >= MaxBanksPerPatch)
                    continue;
                if (tocDataSize <= BankDataPrefixSize ||
                    tocDataOffset < 0 ||
                    tocDataOffset + tocDataSize > patchFile.Length)
                    continue;
                var chunks = ReadBankChunks(patchFile, tocDataOffset + BankDataPrefixSize, tocDataSize - BankDataPrefixSize);
                if (chunks is not null)
                    bankChunks.Add(new BankChunkInfo(fileId, chunks.Value.DataOffset, chunks.Value.DataSize, chunks.Value.Didx));
            }
            else if (typeId == WwiseStreamTypeId)
            {
                if (streamEntries.Count >= MaxMediaEntriesPerPatch)
                    continue;
                if (streamFile is null || streamFileOffset < 0 || streamFileOffset + streamSize > streamFile.Length)
                    continue;
                streamEntries.Add(new StreamEntryInfo(fileId, streamFileOffset, streamSize));
            }
            else if (typeId == WwiseDepTypeId)
            {
                if (tocDataOffset < 0 || tocDataOffset + 8 + tocDataSize > patchFile.Length || tocDataSize > MaxDepNameBytes)
                    continue;
                var nameBuffer = new byte[Math.Min(tocDataSize, MaxDepNameBytes)];
                if (TryReadAt(patchFile, tocDataOffset + 8, nameBuffer))
                {
                    var name = Encoding.UTF8.GetString(nameBuffer).TrimEnd('\0');
                    if (name.Length > 0)
                        depNames[fileId] = name;
                }
            }
        }

        if (bankChunks.Count == 0 && streamEntries.Count == 0)
            return null;

        // 有音频资源才解析游戏基线（避免为纯模型补丁无谓加载游戏包）。
        var baseline = baselineResolver?.Invoke(GetPackageBaseName(patch.Name));
        // 按偏移排序：游戏 .stream 伴生文件的比对读取近似顺序 IO（机械盘友好，避免随机寻道风暴）。
        streamEntries.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));

        var relativePath = Path.GetRelativePath(modDirectory.FullName, patch.FullName);
        var result = new List<AudioBankGroup>();
        var mergedStreamIds = new HashSet<ulong>();
        foreach (var bank in bankChunks)
        {
            var entries = new List<AudioEntry>();
            if (bank.DataOffset >= 0)
            {
                foreach (var media in bank.Didx)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entries.Count >= MaxMediaEntriesPerPatch)
                        break;
                    var available = Math.Min((long)media.Size, bank.DataOffset + bank.DataSize - (bank.DataOffset + media.Offset));
                    if (available <= 0)
                        continue;
                    var dataOffset = bank.DataOffset + media.Offset;
                    var (channels, sampleRate, issue) = ProbeWemHeader(patchFile, dataOffset, available);
                    entries.Add(new AudioEntry(
                        media.Id,
                        AudioEntryOrigin.BankMedia,
                        relativePath,
                        depNames.GetValueOrDefault(bank.FileId),
                        bank.FileId,
                        patch.FullName,
                        dataOffset,
                        available,
                        channels,
                        sampleRate,
                        issue,
                        issue == AudioEntryIssue.None
                            ? CompareWithOriginal(baseline, patchFile, dataOffset, available, media.Id, bank.FileId)
                            : null));
                }
            }

            // A patch usually holds a single bank plus all of its streamed voice lines; merge
            // those streams into the bank group so they share one display group. Patches with
            // several banks keep their streams in a separate group below.
            var bankName = depNames.GetValueOrDefault(bank.FileId);
            if (bankChunks.Count == 1)
            {
                foreach (var streamEntry in streamEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    mergedStreamIds.Add(streamEntry.FileId);
                    var (channels, sampleRate, issue) = ProbeWemHeader(streamFile!, streamEntry.Offset, streamEntry.Size);
                    entries.Add(new AudioEntry(
                        streamEntry.FileId,
                        AudioEntryOrigin.StreamMedia,
                        relativePath,
                        bankName,
                        bank.FileId,
                        streamPath,
                        streamEntry.Offset,
                        streamEntry.Size,
                        channels,
                        sampleRate,
                        issue,
                        issue == AudioEntryIssue.None
                            ? CompareWithOriginal(baseline, streamFile!, streamEntry.Offset, streamEntry.Size, streamEntry.FileId, 0)
                            : null));
                }
            }

            if (entries.Count > 0)
                result.Add(new AudioBankGroup(relativePath, bankName, bank.FileId, entries));
        }

        if (streamEntries.Count > mergedStreamIds.Count)
        {
            var entries = new List<AudioEntry>();
            foreach (var streamEntry in streamEntries)
            {
                if (mergedStreamIds.Contains(streamEntry.FileId))
                    continue;
                cancellationToken.ThrowIfCancellationRequested();
                var (channels, sampleRate, issue) = ProbeWemHeader(streamFile!, streamEntry.Offset, streamEntry.Size);
                entries.Add(new AudioEntry(
                    streamEntry.FileId,
                    AudioEntryOrigin.StreamMedia,
                    relativePath,
                    null,
                    0,
                    streamPath,
                    streamEntry.Offset,
                    streamEntry.Size,
                    channels,
                    sampleRate,
                    issue,
                    issue == AudioEntryIssue.None
                        ? CompareWithOriginal(baseline, streamFile!, streamEntry.Offset, streamEntry.Size, streamEntry.FileId, 0)
                        : null));
            }
            if (entries.Count > 0)
                result.Add(new AudioBankGroup(relativePath, null, 0, entries));
        }

        var uncomparedCount = 0;
        if (baseline is not null)
        {
            foreach (var group in result)
                uncomparedCount += group.Entries.Count(static entry => entry.MatchesOriginal is null);
        }
        return new PatchInspection(result, uncomparedCount);
    }

    /// <summary>Splits the embedded Wwise bank into chunk records without loading the big ones.</summary>
    private static (long DataOffset, uint DataSize, List<DidxMedia> Didx)? ReadBankChunks(FileStream patchFile, long start, uint length)
    {
        long? dataOffset = null;
        uint dataSize = 0;
        List<DidxMedia>? didx = null;
        var position = start;
        var end = start + length;

        Span<byte> chunkHeader = stackalloc byte[8];
        while (position + 8 <= end)
        {
            if (!TryReadAt(patchFile, position, chunkHeader))
                return null;
            var tag = BinaryPrimitives.ReadInt32LittleEndian(chunkHeader);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            var bodyOffset = position + 8;
            if (bodyOffset + size > end)
                return null;

            if (tag == BankDataTag)
            {
                dataOffset = bodyOffset;
                dataSize = size;
            }
            else if (tag == DidxTag && size >= 12 && size % 12 == 0)
            {
                var count = (int)(size / 12);
                if (count > MaxMediaEntriesPerPatch)
                    return null;
                var buffer = new byte[size];
                if (!TryReadAt(patchFile, bodyOffset, buffer))
                    return null;
                didx = new List<DidxMedia>(count);
                for (var i = 0; i < count; i++)
                {
                    didx.Add(new DidxMedia(
                        BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i * 12)),
                        BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i * 12 + 4)),
                        BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i * 12 + 8))));
                }
            }

            position = bodyOffset + size;
        }

        return didx is null || dataOffset is null ? null : (dataOffset.Value, dataSize, didx);
    }

    /// <summary>与游戏原版比对；null = 未知（无基线/预算耗尽/读取失败），false = 已替换或新增，true = 原版。
    /// 先查原版尺寸：大小不同直接判定已替换（零 IO）——语音模组的被替换条目几乎都命中这条快速路径。</summary>
    private static bool? CompareWithOriginal(
        GameAudioBaseline? baseline,
        FileStream source,
        long offset,
        long size,
        ulong sourceId,
        ulong bankFileId)
    {
        if (baseline is null || size <= 0 || size > MaxOriginalCompareBytes)
            return null;
        try
        {
            var lookup = bankFileId != 0
                ? baseline.TryGetBankMediaSize(bankFileId, (uint)sourceId, out var originalSize)
                : baseline.TryGetStreamMediaSize(sourceId, out originalSize);
            if (lookup == GameAudioOriginalLookup.ResourceMissing)
                return false;
            if (lookup != GameAudioOriginalLookup.Found)
                return null;
            if (originalSize != (ulong)size)
                return false;

            var originalHash = bankFileId != 0
                ? baseline.TryGetBankMediaHash(bankFileId, (uint)sourceId)
                : baseline.TryGetStreamMediaHash(sourceId);
            if (originalHash is null)
                return null;
            var modHash = HashFileSlice(source, offset, size);
            return modHash is not null && modHash.AsSpan().SequenceEqual(originalHash);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>流式哈希文件切片（不整块分配内存；条目上限 32MB，语音条目通常几十 KB）。</summary>
    private static byte[]? HashFileSlice(FileStream stream, long offset, long size)
    {
        if (offset < 0 || size <= 0 || offset > stream.Length - size)
            return null;
        var buffer = new byte[64 * 1024];
        stream.Position = offset;
        using var sha = System.Security.Cryptography.SHA256.Create();
        long remaining = size;
        while (remaining > 0)
        {
            var take = (int)Math.Min(buffer.Length, remaining);
            var read = 0;
            while (read < take)
            {
                var count = stream.Read(buffer, read, take - read);
                if (count <= 0)
                    return null;
                read += count;
            }
            sha.TransformBlock(buffer, 0, take, null, 0);
            remaining -= take;
        }
        sha.TransformFinalBlock([], 0, 0);
        return sha.Hash!;
    }

    /// <summary>Bounded WEM header probe: verifies RIFF/WAVE + Vorbis fmt and reads channels/rate.</summary>
    private static (int Channels, int SampleRate, AudioEntryIssue Issue) ProbeWemHeader(FileStream backingFile, long offset, long available)
    {
        try
        {
            // Anything smaller than a minimal RIFF/WAVE+fmt header can never be a WEM.
            if (offset < 0 || available < 44 || offset >= backingFile.Length)
                return (0, 0, available < 44 ? AudioEntryIssue.NotRiff : AudioEntryIssue.ReadFailed);

            Span<byte> header = stackalloc byte[WemHeaderProbeBytes];
            var probeLength = (int)Math.Min(WemHeaderProbeBytes, Math.Min(available, backingFile.Length - offset));
            if (probeLength < 44 || !TryReadAt(backingFile, offset, header[..probeLength]))
                return (0, 0, AudioEntryIssue.ReadFailed);

            if (BinaryPrimitives.ReadInt32LittleEndian(header) != RiffTag ||
                BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != WaveTag)
                return (0, 0, AudioEntryIssue.NotRiff);

            var declaredRiffSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (declaredRiffSize > available - 8)
                return (0, 0, AudioEntryIssue.Truncated);

            // find the fmt chunk (RIFF chunk walk, bounded by the probe)
            var position = 12;
            while (position + 8 <= probeLength)
            {
                var tag = BinaryPrimitives.ReadInt32LittleEndian(header[position..]);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(header[(position + 4)..]);
                var body = position + 8;
                if (tag == FmtTag)
                {
                    if (body + 16 > probeLength)
                        return (0, 0, AudioEntryIssue.NotVorbis);
                    var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(header[body..]);
                    var channels = BinaryPrimitives.ReadUInt16LittleEndian(header[(body + 2)..]);
                    var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(header[(body + 4)..]);
                    if (formatTag is not (VorbisFormatTag or OggVorbisType2FormatTag or OggVorbisType3FormatTag))
                        return (0, 0, AudioEntryIssue.NotVorbis);
                    return (channels, (int)sampleRate, AudioEntryIssue.None);
                }

                if (body + size > probeLength)
                    break; // fmt chunk sits beyond our small probe; treat as unsupported rather than failing
                position = body + (int)size + (int)(size & 1);
            }

            return (0, 0, AudioEntryIssue.NotVorbis);
        }
        catch (Exception)
        {
            return (0, 0, AudioEntryIssue.ReadFailed);
        }
    }

    private static bool TryReadAt(FileStream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > stream.Length - buffer.Length)
            return false;
        stream.Position = offset;
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count <= 0)
                return false;
            read += count;
        }
        return true;
    }

    private const int BankDataTag = unchecked((int)0x41544144); // "DATA"
    private const int DidxTag = unchecked((int)0x58444944);    // "DIDX"
    private const int RiffTag = unchecked((int)0x46464952);    // "RIFF"
    private const int WaveTag = unchecked((int)0x45564157);    // "WAVE"
    private const int FmtTag = unchecked((int)0x20746D66);     // "fmt "
    private const ushort VorbisFormatTag = 0xFFFF;
    private const ushort OggVorbisType2FormatTag = 0x6771;
    private const ushort OggVorbisType3FormatTag = 0x674F;

    private readonly record struct DidxMedia(uint Id, uint Offset, uint Size);
    private readonly record struct BankChunkInfo(ulong FileId, long DataOffset, uint DataSize, List<DidxMedia> Didx);
    private readonly record struct StreamEntryInfo(ulong FileId, long Offset, uint Size);
}
