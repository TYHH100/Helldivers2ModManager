using System.Buffers.Binary;
using System.Text;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class AudioBankInspectionServiceTests
{
    private const uint PatchMagic = 0xF0000011;
    private const ulong WwiseBankType = AudioBankInspectionService.WwiseBankTypeId;
    private const ulong WwiseStreamType = AudioBankInspectionService.WwiseStreamTypeId;
    private const ulong WwiseDepType = AudioBankInspectionService.WwiseDepTypeId;

    private string _tempRoot = string.Empty;

    [TestInitialize]
    public void TestInitialize()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "audio_inspect_tests_" + Guid.NewGuid().ToString("N"));
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
    public async Task InspectAsync_BankWithDepAndStreams_GroupsStreamsUnderBank()
    {
        var optionDir = Path.Combine(_tempRoot, "opt1");
        Directory.CreateDirectory(optionDir);
        var patchPath = Path.Combine(optionDir, "9ba626afa44a3aa3.patch_0");

        var goodWem = BuildWem(channels: 2, sampleRate: 48000, payloadSize: 256);
        var junk = Encoding.ASCII.GetBytes("this is not a riff container payload");
        var truncatedWem = BuildWem(channels: 1, sampleRate: 48000, payloadSize: 128);
        // DIDX advertises fewer bytes than the RIFF header declares -> Truncated.
        var truncatedSlice = truncatedWem.AsSpan(0, truncatedWem.Length - 24).ToArray();

        var bank = BuildBank(
            (1001, goodWem),
            (1002, junk),
            (1003, truncatedSlice));
        var streamWem = BuildWem(channels: 1, sampleRate: 44100, payloadSize: 64);

        WritePatch(
            patchPath,
            bankTocData: ("D82F7678", bank.BankData, bank.FileId),
            depName: "content/audio/us/helldiver_soldier_VO",
            streamEntries: [(0x565A620CD813F3UL, streamWem)]);

        var service = new AudioBankInspectionService(NullLoggerFactory.Instance.CreateLogger<AudioBankInspectionService>(), null!);
        var result = await service.InspectAsync(
            new DirectoryInfo(_tempRoot),
            [new FileInfo(patchPath)],
            CancellationToken.None);

        Assert.IsNull(result.Error, "unexpected inspection error: " + result.Error);
        Assert.AreEqual(1, result.Groups.Count);
        var group = result.Groups[0];
        Assert.AreEqual("content/audio/us/helldiver_soldier_VO", group.BankName);
        Assert.AreEqual("opt1" + Path.DirectorySeparatorChar + "9ba626afa44a3aa3.patch_0", group.PatchRelativePath);

        Assert.AreEqual(4, group.Entries.Count);
        var bankGood = group.Entries[0];
        Assert.AreEqual(1001UL, bankGood.SourceId);
        Assert.AreEqual(AudioEntryOrigin.BankMedia, bankGood.Origin);
        Assert.IsTrue(bankGood.IsPlayable, bankGood.Issue.ToString());
        Assert.AreEqual(2, bankGood.Channels);
        Assert.AreEqual(48000, bankGood.SampleRate);
        Assert.AreEqual(goodWem.Length, bankGood.SizeBytes);

        var bankJunk = group.Entries[1];
        Assert.AreEqual(AudioEntryIssue.NotRiff, bankJunk.Issue);
        Assert.IsFalse(bankJunk.IsPlayable);

        Assert.AreEqual(AudioEntryIssue.Truncated, group.Entries[2].Issue);

        var stream = group.Entries[3];
        Assert.AreEqual(0x565A620CD813F3UL, stream.SourceId);
        Assert.AreEqual(AudioEntryOrigin.StreamMedia, stream.Origin);
        Assert.IsTrue(stream.IsPlayable, stream.Issue.ToString());
        Assert.AreEqual(1, stream.Channels);
        Assert.AreEqual(44100, stream.SampleRate);
        Assert.AreEqual(patchPath + ".stream", stream.BackingFilePath);
        Assert.AreEqual(1, result.PatchCount);
    }

    [TestMethod]
    public async Task InspectAsync_PatchWithTwoBanks_KeepsStreamsInSeparateGroup()
    {
        var optionDir = Path.Combine(_tempRoot, "opt2");
        Directory.CreateDirectory(optionDir);
        var patchPath = Path.Combine(optionDir, "0000000000000001.patch_1");

        var bankA = BuildBank((10, BuildWem(1, 48000, 32)));
        var bankB = BuildBank((20, BuildWem(1, 48000, 32)));
        var streamWem = BuildWem(1, 48000, 32);

        WritePatch(
            patchPath,
            bankTocData: null,
            bankTocDataA: ("D82F7678", bankA.BankData, bankA.FileId),
            bankTocDataB: ("D82F7678", bankB.BankData, bankB.FileId),
            depName: null,
            streamEntries: [(0xAAAAUL, streamWem)]);

        var service = new AudioBankInspectionService(NullLoggerFactory.Instance.CreateLogger<AudioBankInspectionService>(), null!);
        var result = await service.InspectAsync(
            new DirectoryInfo(_tempRoot),
            [new FileInfo(patchPath)],
            CancellationToken.None);

        Assert.AreEqual(3, result.Groups.Count);
        Assert.IsTrue(result.Groups.All(g => g.Entries.Count == 1));
        var streamGroup = result.Groups.Single(g => g.Entries[0].Origin == AudioEntryOrigin.StreamMedia);
        Assert.AreEqual(AudioEntryOrigin.StreamMedia, streamGroup.Entries[0].Origin);
    }

    [TestMethod]
    public async Task InspectAsync_PatchWithoutAudioResources_ReturnsNothing()
    {
        var patchPath = Path.Combine(_tempRoot, "0000000000000002.patch_0");
        // 一个只有 Unit 资源的补丁：写空 bank/stream/de 列表，且 toc data 无内容。
        WritePatch(patchPath, bankTocData: null, depName: null, streamEntries: []);

        var service = new AudioBankInspectionService(NullLoggerFactory.Instance.CreateLogger<AudioBankInspectionService>(), null!);
        var result = await service.InspectAsync(
            new DirectoryInfo(_tempRoot),
            [new FileInfo(patchPath)],
            CancellationToken.None);

        Assert.AreEqual(0, result.Groups.Count);
        Assert.AreEqual(0, result.PatchCount);
    }

    [TestMethod]
    public async Task InspectAsync_CorruptHeader_IsReportedAsError()
    {
        var patchPath = Path.Combine(_tempRoot, "0000000000000003.patch_0");
        await File.WriteAllBytesAsync(patchPath, [0x12, 0x34, 0x56, 0x78, 0, 0, 0, 0]);

        var service = new AudioBankInspectionService(NullLoggerFactory.Instance.CreateLogger<AudioBankInspectionService>(), null!);
        var result = await service.InspectAsync(
            new DirectoryInfo(_tempRoot),
            [new FileInfo(patchPath)],
            CancellationToken.None);

        Assert.AreEqual(0, result.Groups.Count);
        Assert.AreEqual(0, result.PatchCount);
    }

    /// <summary>构建一个最小 RIFF/WAVE Vorbis WEM 头（仅供解析器做头校验）。</summary>
    private static byte[] BuildWem(int channels, int sampleRate, int payloadSize)
    {
        using var ms = new MemoryStream();
        Span<byte> buffer = stackalloc byte[8];

        // fmt chunk body (0x42 = 66 bytes, Wwise vorbis)
        var fmt = new byte[66];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(0), 0xFFFF); // WAVE_FORMAT_VORBIS
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(24), 48); // cbSize

        var bodyLength = 8 + fmt.Length + 8 + payloadSize; // fmt chunk + data chunk header + payload
        ms.Write("RIFF"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)(bodyLength + 4));
        ms.Write(buffer[..4]);
        ms.Write("WAVE"u8);

        ms.Write("fmt "u8);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)fmt.Length);
        ms.Write(buffer[..4]);
        ms.Write(fmt);

        ms.Write("data"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)payloadSize);
        ms.Write(buffer[..4]);
        ms.Write(new byte[payloadSize]);

        return ms.ToArray();
    }

    private static (byte[] BankData, ulong FileId) BuildBank(params (uint SourceId, byte[] Payload)[] media)
    {
        var didx = new byte[media.Length * 12];
        var data = new List<byte>();
        var offset = 0;
        for (var i = 0; i < media.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(didx.AsSpan(i * 12), media[i].SourceId);
            BinaryPrimitives.WriteUInt32LittleEndian(didx.AsSpan(i * 12 + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(didx.AsSpan(i * 12 + 8), (uint)media[i].Payload.Length);
            data.AddRange(media[i].Payload);
            offset += media[i].Payload.Length;
        }

        var bkhd = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(bkhd, 154u ^ 0x9211BCACu);

        using var bank = new MemoryStream();
        WriteChunk(bank, "BKHD", bkhd);
        WriteChunk(bank, "DIDX", didx);
        WriteChunk(bank, "DATA", [.. data]);
        return (bank.ToArray(), 0x3543B29A8A90B1BEUL);
    }

    private static void WriteChunk(Stream stream, string tag, byte[] body)
    {
        stream.Write(Encoding.ASCII.GetBytes(tag));
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
        stream.Write(size);
        stream.Write(body);
    }

    private static void WritePatch(
        string path,
        (string Prefix, byte[] BankData, ulong FileId)? bankTocData,
        string? depName,
        (ulong FileId, byte[] Wem)[] streamEntries,
        (string Prefix, byte[] BankData, ulong FileId)? bankTocDataA = null,
        (string Prefix, byte[] BankData, ulong FileId)? bankTocDataB = null)
    {
        // 支持两个 bank 的重载：把可选参数归一化成列表。
        var banks = new List<(string Prefix, byte[] BankData, ulong FileId)>();
        if (bankTocData is { } single)
            banks.Add(single);
        if (bankTocDataA is { } a)
            banks.Add(a);
        if (bankTocDataB is { } b)
            banks.Add(b);

        var numTypes = 1;
        var numFiles = banks.Count + (depName is not null ? 1 : 0) + streamEntries.Length;

        using var patch = new MemoryStream();
        Span<byte> buffer = stackalloc byte[8];

        BinaryPrimitives.WriteUInt32LittleEndian(buffer, PatchMagic);
        patch.Write(buffer[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)numTypes);
        patch.Write(buffer[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)numFiles);
        patch.Write(buffer[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0);
        patch.Write(buffer[..4]);
        patch.Write(new byte[56]); // unk4Data

        // single type header (<QQQII>, 32 bytes): 条目计数按工具格式是 u64。
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, 0);
        patch.Write(buffer);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, WwiseBankType);
        patch.Write(buffer);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, (ulong)Math.Max(1, banks.Count));
        patch.Write(buffer);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 16);
        patch.Write(buffer[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 64);
        patch.Write(buffer[..4]);

        var dataStart = patch.Length + 80L * numFiles + 8;
        var tocDataOffset = dataStart;
        var streamOffset = 0;

        var tocEntries = new List<byte[]>();
        var tocDatas = new List<byte[]>();
        var streamData = new List<byte>();

        foreach (var bank in banks)
        {
            var tocData = new byte[16 + bank.BankData.Length];
            Encoding.ASCII.GetBytes(bank.Prefix).CopyTo(tocData, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(tocData.AsSpan(4), (uint)bank.BankData.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(tocData.AsSpan(8), bank.FileId);
            bank.BankData.CopyTo(tocData, 16);
            tocEntries.Add(BuildTocEntry(bank.FileId, WwiseBankType, tocDataOffset, 0, (uint)tocData.Length, 0));
            tocDatas.Add(tocData);
            tocDataOffset += tocData.Length;
        }

        if (depName is not null)
        {
            var nameBytes = Encoding.UTF8.GetBytes(depName + "\0");
            var depData = new byte[8 + nameBytes.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(depData.AsSpan(0), 0x70654444); // "DDep"-ish tag, parser ignores value
            BinaryPrimitives.WriteUInt32LittleEndian(depData.AsSpan(4), (uint)nameBytes.Length);
            nameBytes.CopyTo(depData, 8);
            tocEntries.Add(BuildTocEntry(banks[0].FileId, WwiseDepType, tocDataOffset, 0, (uint)depData.Length, 0));
            tocDatas.Add(depData);
            tocDataOffset += depData.Length;
        }

        foreach (var (fileId, wem) in streamEntries)
        {
            tocEntries.Add(BuildTocEntry(fileId, WwiseStreamType, tocDataOffset, streamOffset, 16, (uint)wem.Length));
            tocDatas.Add(new byte[16]);
            streamData.AddRange(wem);
            streamOffset += wem.Length;
            tocDataOffset += 16;
        }

        foreach (var entry in tocEntries)
            patch.Write(entry);
        patch.Write(new byte[8]);
        foreach (var data in tocDatas)
            patch.Write(data);

        File.WriteAllBytes(path, patch.ToArray());
        if (streamData.Count > 0)
            File.WriteAllBytes(path + ".stream", [.. streamData]);
    }

    private static byte[] BuildTocEntry(
        ulong fileId,
        ulong typeId,
        long tocDataOffset,
        long streamFileOffset,
        uint tocDataSize,
        uint streamSize)
    {
        var entry = new byte[80];
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(0), fileId);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(8), typeId);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(16), (ulong)tocDataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(24), (ulong)streamFileOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(32), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(40), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(48), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(56), tocDataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(60), streamSize);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(64), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(68), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(72), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(76), 0);
        return entry;
    }
    [TestMethod]
    public async Task PlayAsync_GarbageVorbisPayload_FailsGracefullyWithoutDevice()
    {
        // 合法的 RIFF/Vorbis 头 + 垃圾音频数据：两个 codebook 都应在 VorbisReader
        // 构造阶段失败，PlayAsync 返回错误而不是崩溃，也不会创建音频输出设备。
        var optionDir = Path.Combine(_tempRoot, "opt3");
        Directory.CreateDirectory(optionDir);
        var wemPath = Path.Combine(optionDir, "fake.wem");
        await File.WriteAllBytesAsync(wemPath, BuildWem(1, 48000, 4096));

        var entry = new Helldivers2ModManager.Models.AudioEntry(
            SourceId: 42,
            Origin: Helldivers2ModManager.Models.AudioEntryOrigin.BankMedia,
            PatchRelativePath: "opt3/fake.wem",
            BankName: null,
            BankFileId: 0,
            BackingFilePath: wemPath,
            DataOffset: 0,
            SizeBytes: new FileInfo(wemPath).Length,
            Channels: 1,
            SampleRate: 48000,
            Issue: Helldivers2ModManager.Models.AudioEntryIssue.None);

        using var service = new Helldivers2ModManager.Services.AudioPlaybackService(
            NullLoggerFactory.Instance.CreateLogger<Helldivers2ModManager.Services.AudioPlaybackService>());
        var (success, error) = await service.PlayAsync(entry, CancellationToken.None);

        Assert.IsFalse(success);
        Assert.IsNotNull(error);
    }
    [TestMethod]
    public async Task InspectAsync_WithGameBaseline_MarksReplacedEntries()
    {
        // 游戏原版包：同一 bank 中 1001 与模组完全一致，1002 与模组不同；stream S1 不同。
        var dataDir = Path.Combine(_tempRoot, "data");
        Directory.CreateDirectory(dataDir);
        var modDir = Path.Combine(_tempRoot, "mods", "音效模组");
        Directory.CreateDirectory(modDir);
        var optionDir = Path.Combine(modDir, "opt");
        Directory.CreateDirectory(optionDir);
        var patchPath = Path.Combine(optionDir, "9ba626afa44a3aa3.patch_0");

        var sameWem = BuildWem(1, 48000, 96);
        var moddedWem = BuildWem(1, 48000, 120);
        var anotherModdedWem = BuildWem(2, 44100, 64);
        var modStream = BuildWem(1, 48000, 48);
        var gameStream = BuildWem(1, 48000, 56);

        WritePatch(patchPath, ("D82F7678", BuildBank((1001, sameWem), (1002, moddedWem)).BankData, 0x3543B29A8A90B1BEUL), "content/audio/test", [(0xAAAAUL, modStream)]);
        var gamePatchPath = Path.Combine(dataDir, "9ba626afa44a3aa3");
        WritePatch(gamePatchPath, ("D82F7678", BuildBank((1001, sameWem), (1002, anotherModdedWem)).BankData, 0x3543B29A8A90B1BEUL), "content/audio/test", [(0xAAAAUL, gameStream)]);

        var service = new AudioBankInspectionService(
            NullLoggerFactory.Instance.CreateLogger<AudioBankInspectionService>(),
            null!);
        var result = await service.InspectAsync(
            new DirectoryInfo(modDir),
            [new FileInfo(patchPath)],
            baseName => GameAudioBaseline.TryLoad(new DirectoryInfo(dataDir), baseName, NullLoggerFactory.Instance.CreateLogger<GameAudioBaseline>()),
            CancellationToken.None);

        Assert.IsNull(result.Error, result.Error);
        var entries = result.Groups.SelectMany(static g => g.Entries).ToDictionary(static e => e.SourceId);
        Assert.AreEqual(3, entries.Count);
        Assert.AreEqual(true, entries[1001UL].MatchesOriginal, "1001 应与原版一致");
        Assert.AreEqual(false, entries[1002UL].MatchesOriginal, "1002 应标记为已替换");
        Assert.AreEqual(false, entries[0xAAAAUL].MatchesOriginal, "stream 应标记为已替换");
    }
    [TestMethod]
    public void ShouldSkipAudioPreview_MultiOptionAudioModsOnly()
    {
        // 多选项且检测为音频 → 跳过；单选项或非音频 → 不跳过。
        Assert.IsTrue(Helldivers2ModManager.ViewModels.ModelPreviewPageViewModel.ShouldSkipAudioPreviewCore(3, Helldivers2ModManager.Models.ModType.Audio));
        Assert.IsFalse(Helldivers2ModManager.ViewModels.ModelPreviewPageViewModel.ShouldSkipAudioPreviewCore(1, Helldivers2ModManager.Models.ModType.Audio));
        Assert.IsFalse(Helldivers2ModManager.ViewModels.ModelPreviewPageViewModel.ShouldSkipAudioPreviewCore(3, Helldivers2ModManager.Models.ModType.Model));
        Assert.IsFalse(Helldivers2ModManager.ViewModels.ModelPreviewPageViewModel.ShouldSkipAudioPreviewCore(2, Helldivers2ModManager.Models.ModType.Unknown));
    }
}
