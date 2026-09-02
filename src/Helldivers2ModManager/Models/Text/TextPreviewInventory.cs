using System.IO;

namespace Helldivers2ModManager.Models;

/// <summary>
/// One string entry parsed out of a patch's TEXT_BANK resource. The text is decoded during
/// inspection (entries are small UTF-8 strings; the whole bank is capped well below the
/// audio media limits), so no on-demand slice reading is needed for display.
/// </summary>
internal sealed record TextEntry(
    string PatchRelativePath,
    ulong TextBankFileId,
    int Language,
    uint StringId,
    string Text,
    /// <summary>游戏原版同 ID 文本；null = 原版不存在（新增）或无法比对。</summary>
    string? OriginalText = null,
    bool? MatchesOriginal = null)
{
    public bool IsNewEntry => MatchesOriginal == false && OriginalText is null;
}

/// <summary>A display group: one TEXT_BANK resource inside one patch.</summary>
internal sealed record TextBankGroup(
    string PatchRelativePath,
    ulong TextBankFileId,
    int Language,
    IReadOnlyList<TextEntry> Entries);

internal sealed record TextInventoryResult(
    IReadOnlyList<TextBankGroup> Groups,
    int PatchCount,
    string? Error)
{
    public static readonly TextInventoryResult Empty = new([], 0, null);
}
