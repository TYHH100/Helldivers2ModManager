using System.Buffers.Binary;
using System.Text;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services.Parsing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// Lua 脚本静态还原引擎的回归测试。夹具是真实世界模组（Bingus Shared Loader v3）的两层
/// LuaJIT 字节码——2026-09-12 安全审查时提取，结构与 ljd 交叉验证一致。安全契约：解析器
/// 全程只做 bytes → 结构 → 文本 的静态转换，测试同样断言"失败必须优雅、成功必须忠实"。
/// </summary>
[TestClass]
public sealed class LuaScriptInspectionServiceTests
{
    private const ulong BingusScriptType = LuaScriptInspectionService.BingusLuaScriptTypeId;

    private static byte[] OuterLoader() => Convert.FromBase64String(LuaJitRealWorldFixture.OuterLoaderBase64);
    private static byte[] InnerWwise() => Convert.FromBase64String(LuaJitRealWorldFixture.InnerWwiseBase64);

    private static LuaScriptInspectionService CreateService() =>
        new(NullLogger<LuaScriptInspectionService>.Instance);

    [TestMethod]
    public void Parse_InnerWwiseDump_RestoresFullPrototypeTree()
    {
        var data = InnerWwise();

        var ok = LuaJitDumpDecoder.TryParse(data, 0, data.Length, new LuaJitDumpDecoder.Limits(), null, out var model, out var error);

        Assert.IsTrue(ok, "parse failed: " + error);
        Assert.IsNotNull(model);
        Assert.AreEqual(LuaJitDumpDecoder.DumpVersionLuaJit21, model.Version);
        Assert.IsTrue(model.Stripped);
        // ljd 交叉验证：根原型 vararg、99 个 KGC 常量、整树 36 个原型。
        Assert.IsTrue(model.Root.IsVararg);
        Assert.AreEqual(99, model.Root.ComplexConstants.Count);
        Assert.AreEqual(36, model.ProtoCount);
        Assert.AreEqual(0, model.EmbeddedDumpCandidates.Count, "inner dump 不应再嵌套字节码");

        var strings = LuaJitDumpDisassembler.CollectStrings(model);
        CollectionAssert.Contains(strings, "core/wwise/lua/wwise_visualization");
        CollectionAssert.Contains(strings, "core/wwise/lua/wwise_bank_reference");
        CollectionAssert.Contains(strings, "WwiseFlowCallbacks");
        CollectionAssert.Contains(strings, "wwise_load_bank");
        CollectionAssert.Contains(strings, "wwise_trigger_event");

        var listing = LuaJitDumpDisassembler.Render(model);
        StringAssert.Contains(listing, "PROTO #0");
        StringAssert.Contains(listing, "GGET");
        StringAssert.Contains(listing, "TSETS");
        StringAssert.Contains(listing, "\"WwiseFlowCallbacks\"");
    }

    [TestMethod]
    public void Parse_OuterLoaderDump_DetectsEmbeddedLoadstringDump()
    {
        var data = OuterLoader();

        var ok = LuaJitDumpDecoder.TryParse(data, 0, data.Length, new LuaJitDumpDecoder.Limits(), null, out var model, out var error);

        Assert.IsTrue(ok, "parse failed: " + error);
        Assert.IsNotNull(model);
        Assert.AreEqual(4, model.ProtoCount);
        // loader 的核心混淆：把真正的 wwise 回调脚本作为字符串常量再 loadstring——
        // 解析器必须把这一层挖出来，否则静态字符串搜索只能看到表象。
        Assert.AreEqual(1, model.EmbeddedDumpCandidates.Count);

        var embedded = model.EmbeddedDumpCandidates[0];
        Assert.AreEqual(InnerWwise().Length, embedded.Length, "内嵌 dump 应与独立提取的 inner 完全一致");
        CollectionAssert.AreEqual(InnerWwise(), embedded);

        var okNested = LuaJitDumpDecoder.TryParse(embedded, 0, embedded.Length, new LuaJitDumpDecoder.Limits(), null, out var nested, out var nestedError);
        Assert.IsTrue(okNested, "nested parse failed: " + nestedError);
        Assert.IsNotNull(nested);
        Assert.AreEqual(36, nested.ProtoCount);
    }

    [TestMethod]
    public void InspectResource_FullBingusPayload_ProducesCopyReadyReport()
    {
        // 还原 2026-09-12 审查时的完整资源体：8 字节包装头（长度 + 标志）+ 外层 dump。
        var loader = OuterLoader();
        var payload = new byte[8 + loader.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)loader.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 2);
        Array.Copy(loader, 0, payload, 8, loader.Length);

        var entry = CreateService().InspectResource(
            "data/9ba626afa44a3aa3.patch_0",
            0x7251FDD9BB62480A,
            BingusScriptType,
            resourceOffset: 0xC0,
            resourceSize: (uint)payload.Length,
            payload);

        Assert.IsNotNull(entry);
        Assert.AreEqual("bytecode", entry.Kind);
        Assert.AreEqual(2, entry.BlockCount, "外层 + loadstring 内嵌层");
        Assert.AreEqual(4 + 36, entry.TotalPrototypeCount);

        var report = entry.Report;
        // 安全声明必须醒目且明确"未执行"。
        StringAssert.Contains(report, "静态");
        StringAssert.Contains(report, "未执行");
        // loader 主体与内嵌层的关键字符串都能被审阅者看到。
        StringAssert.Contains(report, "@vanilla_wwise_callbacks");
        StringAssert.Contains(report, "CowboyBingusModLoader");
        StringAssert.Contains(report, "mods/cowboybingus/better_stratagem_bounce");
        StringAssert.Contains(report, "BingusSharedLoader.log");
        StringAssert.Contains(report, "wwise_load_bank");
        // 报告是独立自洽的：标题里带来源资源标识，便于粘贴给外部模型时定位。
        StringAssert.Contains(report, "0x7251FDD9BB62480A");
    }

    [TestMethod]
    public void InspectResource_PlainLuaSource_IsShownVerbatimWithoutExecution()
    {
        var source = """
            -- untrusted example mod
            local function hook(unit)
                return stingray.Unit.world_pose(unit, 0)
            end

            return hook
            """;
        var data = Encoding.UTF8.GetBytes(source);

        var entry = CreateService().InspectResource(
            "data/test.patch_0", 0x1234, BingusScriptType, 0, (uint)data.Length, data);

        Assert.IsNotNull(entry);
        Assert.AreEqual("source", entry.Kind);
        StringAssert.Contains(entry.Report, "stingray.Unit.world_pose");
        StringAssert.Contains(entry.Report, "未执行");
    }

    [TestMethod]
    public void InspectResource_TruncatedBytecode_FailsGracefullyWithoutGuessing()
    {
        var data = InnerWwise();
        var truncated = new byte[120];
        Array.Copy(data, truncated, truncated.Length);

        var entry = CreateService().InspectResource(
            "data/test.patch_0", 0x5678, BingusScriptType, 0, (uint)truncated.Length, truncated);

        // 脚本类型资源解析失败必须给出透明失败条目，而不是假装成功或直接消失。
        Assert.IsNotNull(entry);
        Assert.AreEqual("failed", entry.Kind);
        StringAssert.Contains(entry.Report, "未能还原");
        StringAssert.Contains(entry.Report, "十六进制");
    }

    [TestMethod]
    public void InspectResource_NonLuaPayload_ReturnsNull()
    {
        var data = new byte[4096];
        new Random(20260912).NextBytes(data);

        var entry = CreateService().InspectResource(
            "data/test.patch_0", 0x99, 0xCD4238C6A0C69E32UL /* Texture */, 0, (uint)data.Length, data);

        Assert.IsNull(entry, "普通二进制资源不应产生脚本条目");
    }

    [TestMethod]
    public void TryParse_HostileSizeField_FailsWithoutThrowing()
    {
        var data = OuterLoader();
        // 破坏第一个原型的 size ULEB（偏移 5）：改成超长 ULEB 会让结构校验失败。
        data[5] = 0xFF;
        data[6] = 0xFF;
        data[7] = 0xFF;
        data[8] = 0xFF;
        data[9] = 0x0F;

        var ok = LuaJitDumpDecoder.TryParse(data, 0, data.Length, new LuaJitDumpDecoder.Limits(), null, out _, out var error);

        Assert.IsFalse(ok);
        Assert.IsNotNull(error);
    }

    [TestMethod]
    public void TryParse_GarbageAfterMagic_FailsCleanly()
    {
        var data = new byte[] { 0x1B, 0x4C, 0x4A, 0x02, 0x02, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        var ok = LuaJitDumpDecoder.TryParse(data, 0, data.Length, new LuaJitDumpDecoder.Limits(), null, out _, out _);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void LooksLikePlainLua_DistinguishesSourceFromBinary()
    {
        var source = Encoding.UTF8.GetBytes("local x = 1\nif x then print('hi') end\n-- comment\n");
        Assert.IsTrue(LuaScriptInspectionService.LooksLikePlainLua(source, out var text));
        StringAssert.Contains(text, "print('hi')");

        var binary = new byte[512];
        new Random(42).NextBytes(binary);
        Assert.IsFalse(LuaScriptInspectionService.LooksLikePlainLua(binary, out _));

        Assert.IsFalse(LuaScriptInspectionService.LooksLikePlainLua([], out _));
    }

    [TestMethod]
    public async Task InspectAsync_PatchWithScriptResource_FindsAndRestoresIt()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "lua_inspect_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var loader = OuterLoader();
            var payload = new byte[8 + loader.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)loader.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 2);
            Array.Copy(loader, 0, payload, 8, loader.Length);

            var patchPath = Path.Combine(tempRoot, "9ba626afa44a3aa3.patch_0");
            WritePatchWithResource(patchPath, fileId: 0x7251FDD9BB62480A, typeId: BingusScriptType, payload);

            var result = await CreateService().InspectAsync(
                new DirectoryInfo(tempRoot),
                [new FileInfo(patchPath)],
                CancellationToken.None);

            Assert.IsNull(result.Error, "unexpected inspection error: " + result.Error);
            Assert.AreEqual(1, result.PatchCount);
            Assert.AreEqual(1, result.Groups.Count);
            var entry = AssertIsSingle(result.Groups[0].Entries);
            Assert.AreEqual("bytecode", entry.Kind);
            Assert.AreEqual(2, entry.BlockCount);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExtractAsync_WritesAllScriptArtifactsWithoutExecutingAnything()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "lua_extract_tests_" + Guid.NewGuid().ToString("N"));
        var destRoot = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var loader = OuterLoader();
            var payload = new byte[8 + loader.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)loader.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 2);
            Array.Copy(loader, 0, payload, 8, loader.Length);

            var patchPath = Path.Combine(tempRoot, "9ba626afa44a3aa3.patch_0");
            WritePatchWithResource(patchPath, fileId: 0x7251FDD9BB62480A, typeId: BingusScriptType, payload);

            var result = await CreateService().ExtractAsync(
                new DirectoryInfo(tempRoot),
                [new FileInfo(patchPath)],
                destRoot,
                CancellationToken.None);

            Assert.IsNull(result.Error, "unexpected extraction error: " + result.Error);
            Assert.IsTrue(result.FileCount > 0);
            Assert.IsTrue(Directory.Exists(destRoot), "目标目录应被创建");

            // README 安全声明必须存在。
            var readme = Path.Combine(destRoot, "README.txt");
            Assert.IsTrue(File.Exists(readme));
            StringAssert.Contains(File.ReadAllText(readme), "未执行");

            // 资源目录：payload + 外层块 + 嵌套块，全部落盘。
            var resourceDirs = Directory.GetDirectories(Path.Combine(destRoot, "9ba626afa44a3aa3.patch_0"));
            Assert.AreEqual(1, resourceDirs.Length);
            var resourceDir = resourceDirs[0];
            Assert.IsTrue(File.Exists(Path.Combine(resourceDir, "payload.bin")));
            Assert.IsTrue(File.Exists(Path.Combine(resourceDir, "block1.luajit")));
            Assert.IsTrue(File.Exists(Path.Combine(resourceDir, "block1.decompiled.lua")));
            Assert.IsTrue(File.Exists(Path.Combine(resourceDir, "block1.listing.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(resourceDir, "block1.strings.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(resourceDir, "block1.nested1.luajit")), "loadstring 内嵌层必须单独提取");
            Assert.IsTrue(File.Exists(Path.Combine(resourceDir, "block1.nested1.decompiled.lua")));
            Assert.IsTrue(File.Exists(Path.Combine(resourceDir, "report.txt")));

            // 嵌套层 = 已知 36 原型的 wwise 复刻；还原源码包含关键调用。
            var nestedSource = File.ReadAllText(Path.Combine(resourceDir, "block1.nested1.decompiled.lua"));
            StringAssert.Contains(nestedSource, "WwiseFlowCallbacks");

            var listing = File.ReadAllText(Path.Combine(resourceDir, "block1.listing.txt"));
            StringAssert.Contains(listing, "PROTO #0");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static T AssertIsSingle<T>(IReadOnlyList<T> list)
    {
        Assert.AreEqual(1, list.Count);
        return list[0];
    }

    /// <summary>写入一个最小合法的 HD2 patch 文件：TOC 只有一个资源条目。</summary>
    private static void WritePatchWithResource(string path, ulong fileId, ulong typeId, byte[] payload)
    {
        const int headerSize = 72;
        const int fileEntrySize = 80;
        var dataOffset = headerSize + fileEntrySize; // numTypes = 0：entry 表紧跟 header，资源体在 entry 之后

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        Span<byte> header = stackalloc byte[headerSize];
        BinaryPrimitives.WriteInt32LittleEndian(header, unchecked((int)0xF0000011));
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 0); // numTypes
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], 1); // numFiles
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], (ulong)payload.Length); // 对齐真实补丁头部的总长字段
        stream.Write(header);

        Span<byte> entry = stackalloc byte[fileEntrySize];
        BinaryPrimitives.WriteUInt64LittleEndian(entry, fileId);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], typeId);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], (ulong)dataOffset); // mainOffset
        BinaryPrimitives.WriteUInt32LittleEndian(entry[56..], (uint)payload.Length); // mainSize
        stream.Write(entry);

        stream.Write(payload);
    }
}
