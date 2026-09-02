using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Helldivers2ModManager.Services;

/// <summary>
/// Parser for the game's TEXT_BANK binary format (structure reference: hd2-audio-modder's
/// core.py TextBank — unlicensed/ARR project; format constants referenced only, no source
/// code copied. See README 第三方声明).
///
/// Layout (little-endian, no 16-byte prefix — unlike WWISE_BANK toc_data):
///   0x00: magic 0x3E85F3AE, 0x04: version (=1),
///   0x08: entry count, 0x0C: language id,
///   then count × uint32 string ids, count × uint32 absolute data offsets,
///   then NUL-terminated UTF-8 strings addressed by those offsets.
/// </summary>
internal static class TextBankFormat
{
    internal const uint Magic = 0x3E85F3AE;
    internal const uint Version1 = 1;
    internal const int MinHeaderBytes = 16;
    /// <summary>单个文本库 toc_data 的硬上限（完整语言文本库实测约 1-2MB，留足余量防失控）。</summary>
    internal const int MaxBankBytes = 16 * 1024 * 1024;
    internal const int MaxEntries = 65536;

    // 语言字段存的是语言名哈希（常量表参考自 hd2-audio-modder 的 xlocale.py，同 ARR 出处约定）。
    private static readonly IReadOnlyDictionary<uint, string> LanguageNames = new Dictionary<uint, string>
    {
        [0x03F97B57u] = "English (US)",
        [0x6F4515CBu] = "English (UK)",
        [4271961631u] = "Français",
        [1861586415u] = "Português (Brasil)",
        [1244441033u] = "Português (Europa)",
        [260593578u] = "Polski",
        [2427891497u] = "日本語",
        [2663028010u] = "繁體中文",
        [1497550071u] = "简体中文",
        [291057413u] = "Nederlands",
        [3151476177u] = "한국어",
        [830498882u] = "Español (Castellano)",
        [3854981686u] = "Español (LatinoAmérica)",
        [3124347884u] = "Deutsch",
        [3808107213u] = "Italiano",
        [3317373165u] = "Русский",
    };

    /// <summary>语言哈希 → 可读名称；未知值回退为十六进制。</summary>
    public static string GetLanguageName(int language) =>
        LanguageNames.TryGetValue(unchecked((uint)language), out var name) ? name : $"0x{language:X8}";

    /// <summary>Bounded parse: <paramref name="data"/> must be exactly the toc_data payload.
    /// Returns null on any structural violation (bad magic/version, truncated tables,
    /// out-of-range offsets); never throws.</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out int language, out Dictionary<uint, string> entries)
    {
        language = 0;
        entries = [];
        if (data.Length < MinHeaderBytes)
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != Version1)
            return false;

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        language = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if (count == 0)
            return true;
        if (count > MaxEntries)
            return false;

        var tableBytes = (long)count * 8;
        if (MinHeaderBytes + tableBytes > data.Length)
            return false;

        var ids = data[MinHeaderBytes..];
        var offsets = ids.Slice((int)(count * 4));
        var parsed = new Dictionary<uint, string>((int)count);
        for (var i = 0; i < count; i++)
        {
            var stringId = BinaryPrimitives.ReadUInt32LittleEndian(ids.Slice(i * 4, 4));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(offsets.Slice(i * 4, 4));
            if (offset < MinHeaderBytes + tableBytes || offset >= (ulong)data.Length)
                return false;

            var start = (int)offset;
            var stop = start;
            while (stop < data.Length && data[stop] != 0)
                stop++;
            if (stop >= data.Length)
                return false;

            string text;
            try
            {
                text = Encoding.UTF8.GetString(data[start..stop]);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
            parsed[stringId] = text;
        }

        entries = parsed;
        return true;
    }

    /// <summary>Serializes a text bank back to its binary form (round-trip helper for tests).</summary>
    public static byte[] Write(int language, IReadOnlyCollection<KeyValuePair<uint, string>> entries)
    {
        // 布局是"头部 + 全部 id + 全部偏移 + 全部文本"，先算好每个文本的绝对偏移再一次性写出。
        var textBytes = new List<byte[]>(entries.Count);
        var offset = MinHeaderBytes + entries.Count * 8;
        var offsets = new uint[entries.Count];
        var index = 0;
        foreach (var entry in entries)
        {
            offsets[index++] = (uint)offset;
            var bytes = Encoding.UTF8.GetBytes(entry.Value);
            textBytes.Add(bytes);
            offset += bytes.Length + 1;
        }

        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[MinHeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], Version1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)language);
        stream.Write(header);

        Span<byte> scratch = stackalloc byte[4];
        foreach (var entry in entries)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(scratch, entry.Key);
            stream.Write(scratch);
        }
        foreach (var entryOffset in offsets)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(scratch, entryOffset);
            stream.Write(scratch);
        }
        foreach (var bytes in textBytes)
        {
            stream.Write(bytes);
            stream.WriteByte(0);
        }
        return stream.ToArray();
    }
}
