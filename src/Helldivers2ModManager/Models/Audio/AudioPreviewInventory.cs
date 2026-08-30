using System.IO;

namespace Helldivers2ModManager.Models;

internal enum AudioEntryOrigin
{
    /// <summary>WEM media inside a Wwise bank's DATA chunk.</summary>
    BankMedia,

    /// <summary>WEM media stored as its own TOC resource inside the .stream companion.</summary>
    StreamMedia,
}

internal enum AudioEntryIssue
{
    None,
    /// <summary>Media does not start with a RIFF/WAVE header.</summary>
    NotRiff,
    /// <summary>RIFF WEM exists but the fmt chunk is not a Wwise/Ogg Vorbis codec.</summary>
    NotVorbis,
    /// <summary>The WEM header declares more data than the mod actually stores (prefetch media).</summary>
    Truncated,
    /// <summary>The backing file could not be read at the recorded offset.</summary>
    ReadFailed,
}

/// <summary>
/// One playable (or recognized-but-unsupported) WEM media entry discovered inside a mod patch.
/// Data is never loaded here: <see cref="DataOffset"/>/<see cref="SizeBytes"/> point into
/// <see cref="BackingFilePath"/> and the playback service reads the slice on demand.
/// </summary>
internal sealed record AudioEntry(
    ulong SourceId,
    AudioEntryOrigin Origin,
    string PatchRelativePath,
    string? BankName,
    ulong BankFileId,
    string BackingFilePath,
    long DataOffset,
    long SizeBytes,
    int Channels,
    int SampleRate,
    AudioEntryIssue Issue,
    bool? MatchesOriginal = null)
{
    public bool IsPlayable => Issue == AudioEntryIssue.None;
}

/// <summary>比较结果的语义说明（见 <see cref="AudioEntry.MatchesOriginal"/>）。</summary>

/// <summary>A display group: one Wwise bank (with any stream media of the same patch merged in),
/// or the stream media of a patch that has no bank.</summary>
internal sealed record AudioBankGroup(
    string PatchRelativePath,
    string? BankName,
    ulong BankFileId,
    IReadOnlyList<AudioEntry> Entries);

internal sealed record AudioInventoryResult(
    IReadOnlyList<AudioBankGroup> Groups,
    int PatchCount,
    string? Error,
    int UncomparedEntries = 0)
{
    public static readonly AudioInventoryResult Empty = new([], 0, null);
}
