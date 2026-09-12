using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services.Parsing;

/// <summary>
/// Walks a set of mod patch files and statically restores every Lua script it can find:
/// LuaJIT bytecode dumps (including nested dumps embedded in string constants, e.g. via
/// loadstring, plus standalone magic bytes inside any resource payload) and plain-text Lua
/// sources. Output is one display-ready report per resource.
///
/// SECURITY CONTRACT: parsing is strictly static — bytes in, text out. Nothing in this service
/// (or anywhere it calls) compiles, loads, evaluates or executes the restored code, and it must
/// stay that way: mod files are untrusted input of unpredictable behavior.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class LuaScriptInspectionService
{
    private const int PatchHeaderMagic = unchecked((int)0xF0000011);
    private const int HeaderSize = 72;
    private const int TypeEntrySize = 32;
    private const int FileEntrySize = 80;
    private const int MaxTypes = 1000;
    private const int MaxFiles = 100_000;

    private const int MaxScannedResourcesPerPatch = 256;
    private const int MaxScanBytesPerResource = 16 * 1024 * 1024;
    private const int MaxParseAttemptsPerResource = 24;
    private const int MaxBytecodeBlobsPerResource = 8;
    private const int MaxNestingDepth = 4;
    private const int MaxResourceEntriesTotal = 64;
    private const int MaxReportChars = 1_500_000;
    private const int MinPlainLuaLength = 24;
    private const int MaxHexPreviewBytes = 256;

    /// <summary>Bingus / Shared-Mod-Loader 生态的 Lua 脚本资源类型。</summary>
    internal const ulong BingusLuaScriptTypeId = 0xA14E8DFA2CD117E2UL;

    private readonly ILogger<LuaScriptInspectionService> _logger;
    private readonly LuaJitDumpDecoder.Limits _limits = new();

    public LuaScriptInspectionService(ILogger<LuaScriptInspectionService> logger)
    {
        _logger = logger;
    }

    public Task<LuaScriptInventoryResult> InspectAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        CancellationToken cancellationToken)
        => Task.Run(() => Inspect(modDirectory, patchFiles, cancellationToken), cancellationToken);

    internal sealed record LuaExtractionResult(int FileCount, string DestinationDirectory, string? Error)
    {
        public static readonly LuaExtractionResult Failed = new(0, string.Empty, "extraction failed");
    }

    /// <summary>
    /// 把补丁内全部脚本内容按文件提取到 <paramref name="destinationRoot"/> 下（按
    /// “补丁名/resource_资源ID/”分目录）：原始资源体、每层字节码原件、还原 Lua 源码、
    /// 权威反汇编清单。只写文件，绝不执行任何内容。目标目录不存在时创建。
    /// </summary>
    public Task<LuaExtractionResult> ExtractAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        string destinationRoot,
        CancellationToken cancellationToken)
        => Task.Run(() => Extract(modDirectory, patchFiles, destinationRoot, cancellationToken), cancellationToken);

    private LuaExtractionResult Extract(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            var fileCount = 0;
            var anyScript = false;

            foreach (var patch in patchFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                patch.Refresh();
                if (!patch.Exists || patch.Length < HeaderSize)
                    continue;

                var patchScripts = TryCollectForExtract(modDirectory, patch, destinationRoot, ref fileCount, cancellationToken);
                anyScript |= patchScripts;
            }

            if (!anyScript)
                return new LuaExtractionResult(0, destinationRoot, null);

            var readme = Path.Combine(destinationRoot, "README.txt");
            File.WriteAllText(readme, ExtractReadmeText, Encoding.UTF8);
            fileCount++;

            _logger.LogInformation("Lua script extraction wrote {Count} files to {Destination}", fileCount, destinationRoot);
            return new LuaExtractionResult(fileCount, destinationRoot, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lua script extraction failed");
            return new LuaExtractionResult(0, destinationRoot, ex.Message);
        }
    }

    /// <summary>提取功能与预览共用的安全声明。</summary>
    internal const string ExtractReadmeText = """
        Lua 脚本模组提取结果（由 Helldivers2ModManager 静态解析生成）

        ⚠ 安全声明:
        - 本目录全部内容由模组管理器对补丁做纯静态解析写出；
        - 全程未加载、未编译、未执行、未求值任何 Lua 代码；
        - 脚本来自不可信来源，实际行为无法预测：
          请勿将任何文件放入游戏目录或任何 Lua 解释器运行。

        目录内容（每个含脚本资源的资源一个目录）:
          payload.bin          资源原始字节（未做任何修改）
          block<N>.luajit      提取出的 LuaJIT 字节码原件（含嵌套层）
          block<N>.decompiled.lua  尽力还原的 Lua 源码（静态反编译，仅供审阅）
          block<N>.listing.txt 权威字节码反汇编清单（luajit -bl 风格）
          block<N>.strings.txt 字符串常量汇总
          report.txt           与预览页一致的完整还原报告
        """;

    private bool TryCollectForExtract(
        DirectoryInfo modDirectory,
        FileInfo patch,
        string destinationRoot,
        ref int fileCount,
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
            return false;

        var numTypes = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        var numFiles = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (numTypes < 0 || numFiles < 0 || numTypes > MaxTypes || numFiles > MaxFiles)
            return false;

        var fileEntriesOffset = HeaderSize + (long)numTypes * TypeEntrySize;
        if (fileEntriesOffset + (long)numFiles * FileEntrySize > patchFile.Length)
            return false;

        var patchName = Path.GetFileName(patch.FullName);
        var any = false;
        Span<byte> entry = stackalloc byte[FileEntrySize];
        var scanned = 0;
        for (var i = 0; i < numFiles && scanned < MaxScannedResourcesPerPatch; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadAt(patchFile, fileEntriesOffset + i * FileEntrySize, entry))
                return any;

            var fileId = BinaryPrimitives.ReadUInt64LittleEndian(entry);
            var typeId = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            var dataOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[56..]);
            if (dataOffset < 0 || dataSize == 0 || dataOffset > patchFile.Length - dataSize)
                continue;

            var scanSize = (int)Math.Min(dataSize, MaxScanBytesPerResource);
            var data = new byte[scanSize];
            if (!TryReadAt(patchFile, dataOffset, data))
                continue;
            scanned++;

            if (FindDumpCandidates(data).Count == 0 && !LooksLikePlainLua(data, out _))
                continue;

            var resourceDir = Path.Combine(destinationRoot, patchName, $"resource_{dataOffset:X}_0x{fileId:X16}");
            Directory.CreateDirectory(resourceDir);

            // 原始资源体
            File.WriteAllBytes(Path.Combine(resourceDir, "payload.bin"), data);
            fileCount++;
            any = true;

            // 明文 Lua 源码
            if (FindDumpCandidates(data).Count == 0)
            {
                if (LooksLikePlainLua(data, out var text))
                {
                    File.WriteAllText(Path.Combine(resourceDir, "source.lua"), text, Encoding.UTF8);
                    fileCount++;
                }

                continue;
            }

            // 字节码块（含嵌套）：解析→写原件/还原源码/清单/字符串
            var blockIndex = 0;
            var covered = new List<(int Start, int End)>();
            foreach (var start in FindDumpCandidates(data))
            {
                if (blockIndex >= MaxBytecodeBlobsPerResource)
                    break;
                if (covered.Any(c => start >= c.Start && start < c.End))
                    continue;
                if (!LuaJitDumpDecoder.TryParse(data, start, data.Length - start, _limits, null, out var model, out _) || model is null)
                    continue;

                covered.Add((start, start + model.ConsumedBytes));

                blockIndex++;
                var blockBase = Path.Combine(resourceDir, $"block{blockIndex}");
                var dumpSpan = data.AsSpan(start, model.ConsumedBytes);
                File.WriteAllBytes(blockBase + ".luajit", dumpSpan.ToArray());
                fileCount++;

                if (LuaJitDecompiler.TryDecompile(model, out var luaSource, out _))
                {
                    File.WriteAllText(blockBase + ".decompiled.lua", luaSource, Encoding.UTF8);
                    fileCount++;
                }

                File.WriteAllText(blockBase + ".listing.txt", LuaJitDumpDisassembler.Render(model), Encoding.UTF8);
                fileCount++;

                var strings = LuaJitDumpDisassembler.CollectStrings(model);
                if (strings.Count > 0)
                {
                    File.WriteAllText(
                        blockBase + ".strings.txt",
                        string.Join(Environment.NewLine, strings),
                        Encoding.UTF8);
                    fileCount++;
                }

                // 嵌套字节码（loadstring 内嵌）
                var nestedIndex = 0;
                foreach (var embedded in model.EmbeddedDumpCandidates)
                {
                    if (!LuaJitDumpDecoder.TryParse(embedded, 0, embedded.Length, _limits, null, out var nested, out _) || nested is null)
                        continue;
                    nestedIndex++;
                    var nestedBase = $"{blockBase}.nested{nestedIndex}";
                    File.WriteAllBytes(nestedBase + ".luajit", embedded);
                    fileCount++;
                    if (LuaJitDecompiler.TryDecompile(nested, out var nestedSource, out _))
                    {
                        File.WriteAllText(nestedBase + ".decompiled.lua", nestedSource, Encoding.UTF8);
                        fileCount++;
                    }

                    File.WriteAllText(nestedBase + ".listing.txt", LuaJitDumpDisassembler.Render(nested), Encoding.UTF8);
                    fileCount++;
                }
            }

            // 与预览页一致的完整报告
            if (InspectResource(patchName, fileId, typeId, dataOffset, dataSize, data) is { } reportEntry)
            {
                File.WriteAllText(Path.Combine(resourceDir, "report.txt"), reportEntry.Report, Encoding.UTF8);
                fileCount++;
            }
        }

        return any;
    }


    private LuaScriptInventoryResult Inspect(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        CancellationToken cancellationToken)
    {
        var groups = new List<LuaScriptPatchGroup>();
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
                if (TryInspectPatch(modDirectory, patch, cancellationToken) is { } group && group.Entries.Count > 0)
                {
                    groups.Add(group);
                    patchCount++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Lua script inspection failed for patch {Patch}", patch.FullName);
                error ??= ex.Message;
            }
        }

        return new LuaScriptInventoryResult(groups, patchCount, error);
    }

    private LuaScriptPatchGroup? TryInspectPatch(
        DirectoryInfo modDirectory,
        FileInfo patch,
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

        var relativePath = Path.GetRelativePath(modDirectory.FullName, patch.FullName);
        Span<byte> entry = stackalloc byte[FileEntrySize];
        var entries = new List<LuaScriptEntry>();
        var scanned = 0;
        for (var i = 0; i < numFiles && scanned < MaxScannedResourcesPerPatch && entries.Count < MaxResourceEntriesTotal; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadAt(patchFile, fileEntriesOffset + i * FileEntrySize, entry))
                return null;

            var fileId = BinaryPrimitives.ReadUInt64LittleEndian(entry);
            var typeId = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            var dataOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[56..]);
            if (dataOffset < 0 || dataSize == 0 || dataOffset > patchFile.Length - dataSize)
                continue;

            // 脚本都很小；超大资源（贴图/模型）只扫描前段。
            var scanSize = (int)Math.Min(dataSize, MaxScanBytesPerResource);
            var data = new byte[scanSize];
            if (!TryReadAt(patchFile, dataOffset, data))
                continue;
            scanned++;

            if (InspectResource(relativePath, fileId, typeId, dataOffset, dataSize, data) is { } item)
                entries.Add(item);
        }

        return entries.Count > 0 ? new LuaScriptPatchGroup(relativePath, entries) : null;
    }

    /// <summary>
    /// 静态解析一个资源负载。命中字节码或明文 Lua 时返回一个条目（报告内含全部块），
    /// 完全没有 Lua 痕迹时返回 null；脚本类型资源解析失败时返回 failed 条目（含十六进制预览），
    /// 不猜测、不误报。
    /// </summary>
    internal LuaScriptEntry? InspectResource(
        string patchRelativePath,
        ulong fileId,
        ulong typeId,
        long resourceOffset,
        uint resourceSize,
        byte[] data)
    {
        var candidates = FindDumpCandidates(data);
        if (candidates.Count == 0)
        {
            if (TryBuildPlainTextEntry(patchRelativePath, fileId, typeId, resourceOffset, resourceSize, data) is { } plain)
                return plain;
            if (typeId == BingusLuaScriptTypeId)
                return BuildFailedEntry(patchRelativePath, fileId, typeId, resourceOffset, resourceSize, data, "资源声明为脚本类型，但未找到可识别的 Lua 字节码或明文源码");
            return null;
        }

        var report = new StringBuilder(64 * 1024);
        AppendReportHeader(report, patchRelativePath, fileId, typeId, resourceOffset, resourceSize, data.Length, "bytecode");

        var covered = new List<(int Start, int End)>();
        var blockIndex = 0;
        var totalProtos = 0;
        var attempts = 0;
        string? lastParseError = null;
        foreach (var start in candidates)
        {
            if (blockIndex >= MaxBytecodeBlobsPerResource || attempts >= MaxParseAttemptsPerResource)
                break;
            if (IsCovered(covered, start))
                continue;
            attempts++;

            if (!LuaJitDumpDecoder.TryParse(data, start, data.Length - start, _limits, sourceName: null, out var model, out var parseError) || model is null)
            {
                // 魔数误报或损坏的 dump：不猜测，跳过（若最终一无所获再以 failed 呈现）。
                lastParseError = parseError;
                continue;
            }

            covered.Add((start, start + model.ConsumedBytes));
            blockIndex++;
            totalProtos += model.ProtoCount;
            AppendDumpBlock(report, model, $"块 {blockIndex}: LuaJIT 字节码 @0x{start:X}（资源内偏移）");
            RenderNestedCandidates(model, report, ref blockIndex, ref totalProtos, depth: 1);

            if (report.Length > MaxReportChars)
            {
                report.AppendLine("… [报告超长，已截断]");
                break;
            }
        }

        if (blockIndex == 0)
        {
            // 有魔数但全部解析失败：透明呈现失败信息，绝不输出猜测性的“还原”。
            return BuildFailedEntry(
                patchRelativePath,
                fileId,
                typeId,
                resourceOffset,
                resourceSize,
                data,
                $"找到 {candidates.Count} 处 LuaJIT 魔数，但结构校验全部失败（最后错误: {lastParseError ?? "unknown"}）");
        }

        report.AppendLine();
        report.AppendLine("──────── 安全声明 ────────");
        report.AppendLine("以上内容为纯静态解析结果；模组管理器全程未加载、未执行、未求值任何 Lua 代码。");
        report.AppendLine("脚本来自不可信来源，实际行为无法预测：请勿将还原代码粘贴进任何 Lua 解释器运行。");

        return new LuaScriptEntry(
            patchRelativePath,
            fileId,
            typeId,
            resourceOffset,
            resourceSize,
            blockIndex,
            totalProtos,
            $"{patchRelativePath} · 0x{fileId:X16}",
            report.ToString(),
            "bytecode");
    }

    private void RenderNestedCandidates(
        LuaJitDumpDecoder.LuaDumpModel model,
        StringBuilder report,
        ref int blockIndex,
        ref int totalProtos,
        int depth)
    {
        if (depth > MaxNestingDepth || model.EmbeddedDumpCandidates.Count == 0)
            return;

        foreach (var embedded in model.EmbeddedDumpCandidates)
        {
            if (blockIndex >= MaxBytecodeBlobsPerResource)
                return;
            if (!LuaJitDumpDecoder.TryParse(embedded, 0, embedded.Length, _limits, sourceName: null, out var nested, out _) || nested is null)
            {
                report.AppendLine($"> 嵌套提示: 发现一个 {embedded.Length} 字节的内嵌字节码字符串，但结构校验未通过（按不可解析数据处理）");
                continue;
            }

            blockIndex++;
            totalProtos += nested.ProtoCount;
            AppendDumpBlock(report, nested, $"块 {blockIndex}: 嵌套 LuaJIT 字节码（来自上层 dump 的字符串常量，{embedded.Length} 字节）");
            RenderNestedCandidates(nested, report, ref blockIndex, ref totalProtos, depth + 1);
        }
    }

    private void AppendDumpBlock(StringBuilder report, LuaJitDumpDecoder.LuaDumpModel model, string title)
    {
        report.AppendLine();
        report.AppendLine($"■ {title}");

        // 源码级还原（尽力而为）：结构可靠的调用直接给可读 Lua 源码；
        // 无法验证的结构整体回退，绝不输出猜测性代码。权威参考始终是下方的字节码清单。
        if (LuaJitDecompiler.TryDecompile(model, out var luaSource, out var bailReason))
        {
            report.AppendLine("──── Lua 源码级还原（尽力而为：变量名来自寄存器，可能存在与原始源码的表述差异；权威参考为下方清单）────");
            report.AppendLine();
            report.AppendLine(luaSource);
            report.AppendLine();
        }
        else
        {
            report.AppendLine($"──── Lua 源码级还原不可用（{bailReason ?? "未知原因"}），以下仅提供权威字节码清单 ────");
            report.AppendLine();
        }

        report.Append(LuaJitDumpDisassembler.Render(model));
        var strings = LuaJitDumpDisassembler.CollectStrings(model);
        if (strings.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("──── 字符串常量汇总（去重，按遍历序）────");
            foreach (var s in strings)
                report.AppendLine("  " + LuaJitDumpDisassembler.QuoteString(s));
        }

        if (report.Length > MaxReportChars)
            report.AppendLine("… [报告超长，已截断]");
    }

    private static void AppendReportHeader(
        StringBuilder report,
        string patchRelativePath,
        ulong fileId,
        ulong typeId,
        long resourceOffset,
        uint resourceSize,
        int scannedBytes,
        string kind)
    {
        report.AppendLine("════════════════════════════════════════════════");
        report.AppendLine("Lua 脚本静态还原报告（仅供人工 / 模型审阅）");
        report.AppendLine("════════════════════════════════════════════════");
        report.AppendLine("来源补丁 : " + patchRelativePath);
        report.AppendLine($"资源     : fileId=0x{fileId:X16}  typeId=0x{typeId:X16}");
        report.AppendLine($"数据位置 : 偏移 0x{resourceOffset:X}，声明 {resourceSize} 字节，已解析前 {scannedBytes} 字节");
        report.AppendLine("还原方式 : " + (kind == "bytecode" ? "LuaJIT 字节码静态反汇编（luajit -bl 风格清单）" : "明文 Lua 源码原样呈现"));
        report.AppendLine();
        report.AppendLine("⚠ 安全声明: 本文本由模组管理器静态解析生成，全程未加载、未编译、未执行、未求值任何 Lua 代码。");
        report.AppendLine("⚠ 脚本来自不可信来源，实际行为无法预测：请勿在任何 Lua 环境中运行还原出的代码。");
    }

    private static LuaScriptEntry BuildFailedEntry(
        string patchRelativePath,
        ulong fileId,
        ulong typeId,
        long resourceOffset,
        uint resourceSize,
        byte[] data,
        string reason)
    {
        var report = new StringBuilder(4 * 1024);
        AppendReportHeader(report, patchRelativePath, fileId, typeId, resourceOffset, resourceSize, data.Length, "bytecode");
        report.AppendLine();
        report.AppendLine("■ 解析结果: 未能还原");
        report.AppendLine("原因: " + reason);
        report.AppendLine();
        report.AppendLine("──── 资源头十六进制预览 ────");
        var preview = Math.Min(data.Length, MaxHexPreviewBytes);
        for (var row = 0; row < preview; row += 16)
        {
            var end = Math.Min(row + 16, preview);
            report.Append("  ").AppendFormat(CultureInfo.InvariantCulture, "X8", row).Append("  ");
            for (var i = row; i < end; i++)
                report.Append(data[i].ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
            report.AppendLine();
        }

        report.AppendLine();
        report.AppendLine("⚠ 无法校验其结构的内容不猜测语义；如需进一步分析请结合其他工具离线进行。");

        return new LuaScriptEntry(
            patchRelativePath,
            fileId,
            typeId,
            resourceOffset,
            resourceSize,
            0,
            0,
            $"{patchRelativePath} · 0x{fileId:X16}",
            report.ToString(),
            "failed");
    }

    private static LuaScriptEntry? TryBuildPlainTextEntry(
        string patchRelativePath,
        ulong fileId,
        ulong typeId,
        long resourceOffset,
        uint resourceSize,
        byte[] data)
    {
        if (data.Length < MinPlainLuaLength)
            return null;
        if (!LooksLikePlainLua(data, out var text))
            return null;

        var report = new StringBuilder(8 * 1024);
        AppendReportHeader(report, patchRelativePath, fileId, typeId, resourceOffset, resourceSize, data.Length, "source");
        report.AppendLine();
        report.AppendLine("■ 明文 Lua 源码（原样呈现，未做任何解释或转换）");
        report.AppendLine();
        report.AppendLine(text.Length <= MaxReportChars ? text : text[..MaxReportChars] + "\n… [源码超长，已截断]");
        report.AppendLine();
        report.AppendLine("──────── 安全声明 ────────");
        report.AppendLine("以上为资源中的原始文本；请勿在任何 Lua 环境中运行。");

        return new LuaScriptEntry(
            patchRelativePath,
            fileId,
            typeId,
            resourceOffset,
            resourceSize,
            1,
            0,
            $"{patchRelativePath} · 0x{fileId:X16}",
            report.ToString(),
            "source");
    }

    internal static bool LooksLikePlainLua(byte[] data, out string text)
    {
        text = string.Empty;
        // 严格 UTF-8：包含非法序列即按二进制处理。
        var decoder = new UTF8Encoding(false, true);
        string candidate;
        try
        {
            candidate = decoder.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        var printable = 0;
        foreach (var c in candidate)
        {
            if (c is '\n' or '\r' or '\t' || (!char.IsControl(c) && c != '\uFFFD'))
                printable++;
        }

        if (candidate.Length == 0 || printable < candidate.Length * 0.95)
            return false;

        var keywords = 0;
        foreach (var keyword in (ReadOnlySpan<string>)["function", "local", "end", "then", "require", "return", "pairs", "ipairs", "print"])
        {
            if (candidate.Contains(keyword, StringComparison.Ordinal))
                keywords++;
        }

        if (keywords < 2 && !candidate.Contains("--", StringComparison.Ordinal))
            return false;

        text = candidate;
        return true;
    }

    /// <summary>资源内所有 LuaJIT 魔数位置（升序）。解析器自行验证、自终止，无需预判长度。</summary>
    private static List<int> FindDumpCandidates(byte[] data)
    {
        var result = new List<int>();
        const int MaxCandidates = 64;
        for (var i = 0; i <= data.Length - 5 && result.Count < MaxCandidates; i++)
        {
            if (data[i] == 0x1B && data[i + 1] == 0x4C && data[i + 2] == 0x4A &&
                data[i + 3] is LuaJitDumpDecoder.DumpVersionLuaJit20 or LuaJitDumpDecoder.DumpVersionLuaJit21 &&
                data[i + 4] <= 0x0F)
            {
                result.Add(i);
            }
        }

        return result;
    }

    private static bool IsCovered(List<(int Start, int End)> covered, int start)
    {
        foreach (var (s, e) in covered)
        {
            if (start >= s && start < e)
                return true;
        }

        return false;
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
