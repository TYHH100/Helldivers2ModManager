using System.Buffers.Binary;
using System.IO;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services;

/// <summary>
/// Inspects the subtitle/text content of a set of mod patch files for the preview page.
/// Walks the patch TOC the same way <see cref="ModTypeDetectionService"/> does and parses every
/// TEXT_BANK resource (string id table + NUL-terminated UTF-8 strings, see
/// <see cref="TextBankFormat"/>). When the game's own package is available the entries are
/// compared against the original text via <see cref="GameAudioBaseline"/>. All CPU/IO work runs
/// on a worker thread; callers await the returned task.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class TextBankInspectionService
{
    private const int PatchHeaderMagic = unchecked((int)0xF0000011);
    private const int HeaderSize = 72;
    private const int TypeEntrySize = 32;
    private const int FileEntrySize = 80;
    private const int MaxTypes = 1000;
    private const int MaxFiles = 100000;
    private const int MaxTextBanksPerPatch = 16;

    // ToC resource type id (structure reference: hd2-audio-modder's const.py — unlicensed/ARR
    // project; format constants referenced only, no source code copied. See README 第三方声明).
    internal const ulong TextBankTypeId = 0x0D972BAB10B40FD3UL; // 979299457696010195

    private const int MaxCachedBaselines = 4;

    private readonly ILogger<TextBankInspectionService> _logger;
    private readonly SettingsService _settingsService;
    private readonly Dictionary<string, GameAudioBaseline?> _baselineCache = new(StringComparer.Ordinal);

    public TextBankInspectionService(ILogger<TextBankInspectionService> logger, SettingsService settingsService)
    {
        _logger = logger;
        _settingsService = settingsService;
    }

    public Task<TextInventoryResult> InspectAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        CancellationToken cancellationToken)
        => InspectAsync(modDirectory, patchFiles, ResolveBaseline, cancellationToken);

    internal Task<TextInventoryResult> InspectAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        Func<string, GameAudioBaseline?>? baselineResolver,
        CancellationToken cancellationToken)
        => Task.Run(() => Inspect(modDirectory, patchFiles, baselineResolver, cancellationToken), cancellationToken);

    private TextInventoryResult Inspect(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        Func<string, GameAudioBaseline?>? baselineResolver,
        CancellationToken cancellationToken)
    {
        var groups = new List<TextBankGroup>();
        var patchCount = 0;
        string? error = null;

        foreach (var patch in patchFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                patch.Refresh();
                if (!patch.Exists || patch.Length < HeaderSize)
                    continue;
                if (TryInspectPatch(modDirectory, patch, baselineResolver, cancellationToken) is { } patchGroups)
                {
                    groups.AddRange(patchGroups);
                    patchCount++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Text inspection failed for patch {Patch}", patch.FullName);
                error ??= ex.Message;
            }
        }

        return new TextInventoryResult(groups, patchCount, error);
    }

    private List<TextBankGroup>? TryInspectPatch(
        DirectoryInfo modDirectory,
        FileInfo patch,
        Func<string, GameAudioBaseline?>? baselineResolver,
        CancellationToken cancellationToken)
    {
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
        List<(ulong FileId, long TocDataOffset, uint TocDataSize)> textBanks = [];
        for (var i = 0; i < numFiles; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadAt(patchFile, fileEntriesOffset + i * FileEntrySize, entry))
                return null;

            var fileId = BinaryPrimitives.ReadUInt64LittleEndian(entry);
            var typeId = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            var tocDataOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
            var tocDataSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[56..]);
            if (typeId == TextBankTypeId &&
                tocDataSize >= TextBankFormat.MinHeaderBytes &&
                tocDataSize <= TextBankFormat.MaxBankBytes &&
                tocDataOffset >= 0 &&
                tocDataOffset + tocDataSize <= patchFile.Length &&
                textBanks.Count < MaxTextBanksPerPatch)
            {
                textBanks.Add((fileId, tocDataOffset, tocDataSize));
            }
        }

        if (textBanks.Count == 0)
            return null;

        // 有文本资源才加载游戏基线（9ba626afa44a3aa3 承载绝大部分游戏文本）。
        var baseline = baselineResolver?.Invoke(GetPackageBaseName(patch.Name));

        var relativePath = Path.GetRelativePath(modDirectory.FullName, patch.FullName);
        var result = new List<TextBankGroup>();
        foreach (var (fileId, tocDataOffset, tocDataSize) in textBanks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = new byte[tocDataSize];
            if (!TryReadAt(patchFile, tocDataOffset, data))
                continue;
            if (!TextBankFormat.TryParse(data, out var language, out var entries))
            {
                _logger.LogDebug("Text bank 0x{FileId:X16} in {Patch} failed structural validation", fileId, patch.FullName);
                continue;
            }

            var textEntries = new List<TextEntry>(entries.Count);
            foreach (var (stringId, text) in entries)
            {
                string? original = null;
                bool? matches = null;
                if (baseline is not null)
                {
                    var lookup = baseline.TryGetTextEntry(fileId, stringId, out original);
                    matches = lookup switch
                    {
                        GameAudioOriginalLookup.Found => text == original,
                        GameAudioOriginalLookup.ResourceMissing => false,
                        _ => null,
                    };
                }
                textEntries.Add(new TextEntry(relativePath, fileId, language, stringId, text, original, matches));
            }

            if (textEntries.Count > 0)
                result.Add(new TextBankGroup(relativePath, fileId, language, textEntries));
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>按补丁文件名解析出游戏包名（去掉 .patch_N 后缀），并带缓存地加载原版基线。</summary>
    private GameAudioBaseline? ResolveBaseline(string packageBaseName)
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
}
