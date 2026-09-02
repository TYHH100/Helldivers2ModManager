using System.Buffers.Binary;
using System.Text;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class TextBankInspectionServiceTests
{
    private const uint PatchMagic = 0xF0000011;
    private const ulong TextBankType = TextBankInspectionService.TextBankTypeId;

    private string _tempRoot = string.Empty;

    [TestInitialize]
    public void TestInitialize()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "text_inspect_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        // 临时补丁必须清理，避免测试目录堆积。
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [TestMethod]
    public void TextBankFormat_RoundTrip_ParsesEntriesAndLanguage()
    {
        var data = TextBankFormat.Write(language: 3,
        [
            new(1u, "Hello <i=1>world</i>"),
            new(0xDEADBEEFu, "字幕文本"),
        ]);

        var ok = TextBankFormat.TryParse(data, out var language, out var entries);
        Assert.IsTrue(ok);
        Assert.AreEqual(3, language);
        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual("Hello <i=1>world</i>", entries[1u]);
        Assert.AreEqual("字幕文本", entries[0xDEADBEEFu]);
    }

    [TestMethod]
    public void TextBankFormat_RejectsCorruptData()
    {
        Assert.IsFalse(TextBankFormat.TryParse([], out _, out _));

        var badMagic = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(badMagic, 0xDEADBEEF);
        Assert.IsFalse(TextBankFormat.TryParse(badMagic, out _, out _));

        // 声明 10 条目但表被截断。
        var truncated = TextBankFormat.Write(0, [new(1u, "a"), new(2u, "b")]);
        Array.Resize(ref truncated, 20);
        Assert.IsFalse(TextBankFormat.TryParse(truncated, out _, out _));
    }

    [TestMethod]
    public async Task InspectAsync_TextBankPatch_ParsesEntries()
    {
        var patchPath = Path.Combine(_tempRoot, "9ba626afa44a3aa3.patch_0");
        WriteTextPatch(patchPath, (0x1122334455667788UL, 2, [(1u, "Modified line"), (2u, "Another line")]));

        var service = CreateService();
        var result = await service.InspectAsync(
            new DirectoryInfo(_tempRoot),
            [new FileInfo(patchPath)],
            CancellationToken.None);

        Assert.IsNull(result.Error, "unexpected inspection error: " + result.Error);
        Assert.AreEqual(1, result.PatchCount);
        Assert.AreEqual(1, result.Groups.Count);
        var group = result.Groups[0];
        Assert.AreEqual(0x1122334455667788UL, group.TextBankFileId);
        Assert.AreEqual(2, group.Language);
        Assert.AreEqual(2, group.Entries.Count);
        Assert.AreEqual(1u, group.Entries[0].StringId);
        Assert.AreEqual("Modified line", group.Entries[0].Text);
        Assert.IsNull(group.Entries[0].MatchesOriginal, "无基线时不应标记替换状态");
    }

    [TestMethod]
    public async Task InspectAsync_WithGameBaseline_MarksReplacedAndNewEntries()
    {
        // 游戏原版包：字符串 1 与模组相同，字符串 2 不同，字符串 3 是模组新增。
        var dataDir = Path.Combine(_tempRoot, "data");
        Directory.CreateDirectory(dataDir);
        var modDir = Path.Combine(_tempRoot, "mods", "字幕模组");
        Directory.CreateDirectory(modDir);
        var patchPath = Path.Combine(modDir, "9ba626afa44a3aa3.patch_0");

        WriteTextPatch(patchPath, (0xA1B2C3D4E5F60718UL, 0, [(1u, "same"), (2u, "modded"), (3u, "brand new")]));
        WriteTextPatch(Path.Combine(dataDir, "9ba626afa44a3aa3"), (0xA1B2C3D4E5F60718UL, 0, [(1u, "same"), (2u, "original")]));

        var service = CreateService();
        var result = await service.InspectAsync(
            new DirectoryInfo(modDir),
            [new FileInfo(patchPath)],
            baseName => GameAudioBaseline.TryLoad(new DirectoryInfo(dataDir), baseName, NullLoggerFactory.Instance.CreateLogger<GameAudioBaseline>()),
            CancellationToken.None);

        Assert.IsNull(result.Error, result.Error);
        Assert.AreEqual(1, result.Groups.Count);
        var entries = result.Groups[0].Entries.ToDictionary(static e => e.StringId);
        Assert.AreEqual(true, entries[1u].MatchesOriginal, "相同文本应标记为原版");
        Assert.AreEqual("same", entries[1u].OriginalText);
        Assert.AreEqual(false, entries[2u].MatchesOriginal, "不同文本应标记为已替换");
        Assert.AreEqual("original", entries[2u].OriginalText);
        Assert.AreEqual(false, entries[3u].MatchesOriginal, "新增条目应标记为已替换");
        Assert.IsNull(entries[3u].OriginalText, "新增条目没有原版文本");
        Assert.IsTrue(entries[3u].IsNewEntry);
    }

    [TestMethod]
    public async Task InspectAsync_PatchWithoutTextBanks_ReturnsNothing()
    {
        var patchPath = Path.Combine(_tempRoot, "0000000000000009.patch_0");
        // 只有类型头、没有文件条目的补丁。
        using (var patch = new MemoryStream())
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, PatchMagic);
            patch.Write(buffer);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0);
            patch.Write(buffer);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0);
            patch.Write(buffer);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0);
            patch.Write(buffer);
            patch.Write(new byte[56]);
            File.WriteAllBytes(patchPath, patch.ToArray());
        }

        var service = CreateService();
        var result = await service.InspectAsync(
            new DirectoryInfo(_tempRoot),
            [new FileInfo(patchPath)],
            CancellationToken.None);

        Assert.AreEqual(0, result.Groups.Count);
        Assert.AreEqual(0, result.PatchCount);
    }

    [TestMethod]
    public async Task InspectAsync_CorruptTextBank_IsSkippedNotFatal()
    {
        var patchPath = Path.Combine(_tempRoot, "000000000000000A.patch_0");
        var corruptBank = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(corruptBank, 0xDEADBEEF); // 错误魔数
        WriteTextPatch(patchPath, textBank: null, rawBankData: (0x1UL, 0, corruptBank));

        var service = CreateService();
        var result = await service.InspectAsync(
            new DirectoryInfo(_tempRoot),
            [new FileInfo(patchPath)],
            CancellationToken.None);

        Assert.IsNull(result.Error);
        Assert.AreEqual(0, result.Groups.Count);
    }

    private static TextBankInspectionService CreateService() =>
        new(NullLoggerFactory.Instance.CreateLogger<TextBankInspectionService>(), null!);

    /// <summary>写入一个只含 TEXT_BANK 资源的最小补丁。</summary>
    private static void WriteTextPatch(
        string path,
        (ulong FileId, int Language, (uint Id, string Text)[] Entries)? textBank,
        (ulong FileId, int Language, byte[] RawBankData)? rawBankData = null,
        byte[]? rawBankOnly = null)
    {
        byte[] bankData;
        ulong fileId;
        if (textBank is { } bank)
        {
            bankData = TextBankFormat.Write(bank.Language,
                bank.Entries.Select(e => new KeyValuePair<uint, string>(e.Id, e.Text)).ToArray());
            fileId = bank.FileId;
        }
        else if (rawBankData is { } raw)
        {
            bankData = raw.RawBankData;
            fileId = raw.FileId;
        }
        else
        {
            bankData = rawBankOnly!;
            fileId = 0x1UL;
        }

        var numFiles = 1;
        using var patch = new MemoryStream();
        Span<byte> buffer = stackalloc byte[8];

        BinaryPrimitives.WriteUInt32LittleEndian(buffer, PatchMagic);
        patch.Write(buffer[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 1);
        patch.Write(buffer[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)numFiles);
        patch.Write(buffer[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0);
        patch.Write(buffer[..4]);
        patch.Write(new byte[56]);

        BinaryPrimitives.WriteUInt64LittleEndian(buffer, 0);
        patch.Write(buffer);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, TextBankType);
        patch.Write(buffer);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, 1);
        patch.Write(buffer);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 16);
        patch.Write(buffer[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 64);
        patch.Write(buffer[..4]);

        var tocDataOffset = patch.Length + 80L * numFiles + 8;
        patch.Write(BuildTocEntry(fileId, TextBankType, tocDataOffset, (uint)bankData.Length));
        patch.Write(new byte[8]);
        patch.Write(bankData);

        File.WriteAllBytes(path, patch.ToArray());
    }

    private static byte[] BuildTocEntry(ulong fileId, ulong typeId, long tocDataOffset, uint tocDataSize)
    {
        var entry = new byte[80];
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(0), fileId);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(8), typeId);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(16), (ulong)tocDataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(56), tocDataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(68), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(72), 64);
        return entry;
    }
}
