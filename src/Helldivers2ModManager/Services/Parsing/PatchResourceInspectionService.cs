using Helldivers2ModManager.Models;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using Microsoft.Extensions.DependencyInjection;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Helldivers2ModManager.Services;

/// <summary>
/// Reads patch TOC and Unit GPU stream metadata for the resource viewer. All reads are
/// bounded and random-access; companion GPU files are never loaded as a whole.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class PatchResourceInspectionService
{
    private const int PatchHeaderMagic = unchecked((int)0xF0000011);
    private const ulong UnitTypeId = 0xE0A48D0BE9A7453FUL;
    private const ulong TextureTypeId = 0xCD4238C6A0C69E32UL;
    private const ulong MaterialTypeId = 0xEAC0B497876ADEDFUL;
    private const uint OriginalUnitVersion = 1;
    private const uint LegacyVerifiedUnitVersion = 10800437;
    private const uint CurrentVerifiedUnitVersion = 10800438;
    private const int HeaderSize = 72;
    private const int TypeEntrySize = 32;
    private const int FileEntrySize = 80;
    private const int StreamInfoSize = 0x1B0;
    private const int TextureHeaderOffset = 0xC0;
    private const int TextureHeaderSize = 148;
    private const long MaxPreviewPixels = 4_194_304; // 2048 x 2048
    // Only the Model Preview page requests this through its explicit source-resolution
    // checkbox. The standard viewer path continues to use MaxPreviewPixels above.
    private const long MaxExplicitSourcePreviewPixels = 67_108_864; // 8K x 8K BGRA = 256 MiB
    private const uint MaxEncodedImageBytes = 64 * 1024 * 1024;
    // A single high-detail character section can legitimately exceed the earlier
    // per-stream limit. The ModelPreviewResult global budgets below still bound the
    // complete preview, while these limits avoid discarding a face/body section before
    // that global admission control is reached.
    private const uint MaxPreviewVerticesPerStream = 500_000;
    private const uint MaxPreviewIndicesPerStream = 1_500_000;
    private const long MaxPreviewVertexBytes = 64 * 1024 * 1024;
    private const long MaxReferenceScanBytes = 32 * 1024 * 1024;
    private static readonly int MaxPatchReadParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
    public PatchResourceInspectionService()
    {
    }

    public Task<PatchResourceInspectionResult> InspectAsync(
        DirectoryInfo modDirectory,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => InspectCoreAsync(modDirectory, GetAllPatchFiles(modDirectory), includeGpuStreams: true, cancellationToken),
            cancellationToken);

    /// <summary>
    /// Inspects an explicit patch set. Model preview passes the same selected files that
    /// deployment uses, so an unselected accessory or material variant cannot leak into
    /// the render merely because it lives below the mod directory.
    /// </summary>
    public async Task<PatchResourceInspectionResult> InspectAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        CancellationToken cancellationToken = default)
        => await Task.Run(
            () => InspectCoreAsync(modDirectory, patchFiles, includeGpuStreams: true, cancellationToken),
            cancellationToken);

    private static async Task<PatchResourceInspectionResult> InspectCoreAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        bool includeGpuStreams,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(modDirectory);
        ArgumentNullException.ThrowIfNull(patchFiles);
        var result = new PatchResourceInspectionResult();
        if (!RefreshDirectoryExists(modDirectory))
        {
            result.Error = "Mod directory no longer exists.";
            return result;
        }

        result.PatchFileCount = patchFiles.Count;

        var patchResults = new PatchResourceInspectionResult[patchFiles.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, patchFiles.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxPatchReadParallelism,
                CancellationToken = cancellationToken
            },
            async (patchOrder, token) =>
            {
                var patchResult = new PatchResourceInspectionResult();
                var patchFile = patchFiles[patchOrder];
                try
                {
                    await InspectPatchAsync(
                        patchFile,
                        Path.GetRelativePath(modDirectory.FullName, patchFile.FullName),
                        patchOrder,
                        patchResult,
                        cancellationToken: token,
                        includeGpuStreams: includeGpuStreams);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    patchResult.Error = ex.Message;
                }

                patchResults[patchOrder] = patchResult;
            });

        foreach (var patchResult in patchResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.TocEntries.AddRange(patchResult.TocEntries);
            result.GpuStreams.AddRange(patchResult.GpuStreams);
            result.Textures.AddRange(patchResult.Textures);
            result.Error ??= patchResult.Error;
        }

        return result;
    }

    // Kept for the existing metadata-only regression seam; production callers use the
    // cancellation-aware public overloads above.
    private static Task<PatchResourceInspectionResult> InspectAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        bool includeGpuStreams) =>
        InspectCoreAsync(modDirectory, patchFiles, includeGpuStreams, CancellationToken.None);

    public Task<ModelPreviewResult> PreviewModelAsync(
        DirectoryInfo modDirectory,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => PreviewModelCoreAsync(modDirectory, GetAllPatchFiles(modDirectory), cancellationToken),
            cancellationToken);

    /// <summary>
    /// Builds a preview from the current deployment selection rather than recursively
    /// merging every patch that happens to be stored in the mod directory.
    /// </summary>
    public Task<ModelPreviewResult> PreviewModelAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => PreviewModelCoreAsync(modDirectory, patchFiles, cancellationToken),
            cancellationToken);

    private async Task<ModelPreviewResult> PreviewModelCoreAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(modDirectory);
        ArgumentNullException.ThrowIfNull(patchFiles);
        var result = new ModelPreviewResult();
        if (!RefreshDirectoryExists(modDirectory))
        {
            result.Error = "Mod directory no longer exists.";
            return result;
        }

        // The first pass only supplies TOC, texture metadata, and material resources. GPU
        // stream samples are not needed to resolve materials and would otherwise be read a
        // second time while the actual preview meshes are built.
        var inspection = await InspectCoreAsync(modDirectory, patchFiles, includeGpuStreams: false, cancellationToken);
        result.Textures.AddRange(ModelPreviewTextureIndex.Create(inspection.Textures).Values);
        result.PatchFileCount = inspection.PatchFileCount;
        result.Error = inspection.Error;
        IReadOnlyDictionary<(string PatchPath, ulong UnitId), ModelPreviewMaterialLayout> unitMaterialLayouts;
        try
        {
            unitMaterialLayouts = await BuildUnitMaterialLayoutsAsync(inspection, cancellationToken);
        }
        catch (Exception ex)
        {
            result.Error ??= ex.Message;
            unitMaterialLayouts = new Dictionary<(string PatchPath, ulong UnitId), ModelPreviewMaterialLayout>();
        }

        var pureBlackBaseColorTextureIds = await FindPureBlackBaseColorTextureIdsAsync(
            modDirectory,
            result.Textures,
            unitMaterialLayouts.Values,
            cancellationToken);

        var patchResults = new ModelPreviewResult[patchFiles.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, patchFiles.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxPatchReadParallelism,
                CancellationToken = cancellationToken
            },
            async (patchOrder, token) =>
            {
                var patchResult = new ModelPreviewResult();
                var patchFile = patchFiles[patchOrder];
                try
                {
                    await InspectPatchAsync(
                        patchFile,
                        Path.GetRelativePath(modDirectory.FullName, patchFile.FullName),
                        patchOrder,
                        new PatchResourceInspectionResult(),
                        patchResult,
                        unitMaterialLayouts,
                        cancellationToken: token,
                        includeGpuStreams: true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    patchResult.Error = ex.Message;
                }

                patchResults[patchOrder] = patchResult;
            });

        // 部署把选中的补丁按选项顺序重排为连续补丁链，游戏逐链应用；同一 Unit FileID 被
        // 多个选中补丁修改时，链中靠后的补丁才是生效版本。"移除/摘下"类选项依赖该语义：
        // 选项补丁用空壳或精简 Unit 覆盖本体补丁里的完整网格，覆盖后的旧网格必须丢弃，
        // 否则被移除的部件在预览里永远消失不了（含空壳补丁解码不出网格的情况）。
        var effectiveUnitPatchOrders = new Dictionary<ulong, int>();
        foreach (var entry in inspection.TocEntries)
        {
            if (entry.TypeId != UnitTypeId)
                continue;
            if (!effectiveUnitPatchOrders.TryGetValue(entry.FileId, out var currentOrder) || entry.PatchOrder > currentOrder)
                effectiveUnitPatchOrders[entry.FileId] = entry.PatchOrder;
        }

        for (var patchOrder = 0; patchOrder < patchResults.Length; patchOrder++)
        {
            var patchResult = patchResults[patchOrder];
            cancellationToken.ThrowIfCancellationRequested();
            result.SkippedStreams += patchResult.SkippedStreams;
            result.Error ??= patchResult.Error;
            // 补丁链覆盖判定：本补丁的 Unit 已被更靠后的补丁覆盖时，其网格不再参与合并。
            var effectiveMeshes = patchResult.Meshes
                .Where(mesh => !effectiveUnitPatchOrders.TryGetValue(mesh.UnitId, out var effectiveOrder) ||
                               effectiveOrder == patchOrder)
                .ToList();
            var preferredMeshes = ModelPreviewMaterialVariantSelector.SelectPreferredVariants(
                effectiveMeshes,
                textureId => result.Textures.FirstOrDefault(texture => texture.TextureId == textureId) is { } texture
                    ? (long)texture.Width * texture.Height
                    : 0);
            foreach (var mesh in ModelPreviewMaterialVariantSelector.FilterPureBlackPlaceholders(
                         preferredMeshes,
                         pureBlackBaseColorTextureIds))
            {
                if (!result.TryAddMesh(mesh))
                    result.SkippedStreams++;
            }
        }

        return result;
    }

    public async Task<TexturePreviewData?> PreviewTextureAsync(
        DirectoryInfo modDirectory,
        TextureInspectionItem texture,
        int maxPreviewPixels = (int)MaxPreviewPixels,
        CancellationToken cancellationToken = default)
        => await Task.Run(
            () => PreviewTextureCoreAsync(modDirectory, texture, maxPreviewPixels, cancellationToken),
            cancellationToken);

    private static async Task<TexturePreviewData?> PreviewTextureCoreAsync(
        DirectoryInfo modDirectory,
        TextureInspectionItem texture,
        int maxPreviewPixels,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(modDirectory);
        maxPreviewPixels = Math.Clamp(maxPreviewPixels, 256, (int)MaxExplicitSourcePreviewPixels);
        var patchPath = Path.IsPathFullyQualified(texture.PatchPath)
            ? texture.PatchPath
            : Path.Combine(modDirectory.FullName, texture.PatchPath);
        if (!File.Exists(patchPath))
            return null;

        var payloadPath = texture.PayloadSource.Equals("stream", StringComparison.OrdinalIgnoreCase)
            ? patchPath + ".stream"
            : patchPath + ".gpu_resources";
        if (!File.Exists(payloadPath))
            return null;

        await using var payloadStream = OpenRead(new FileInfo(payloadPath));
        var payloadOffset = texture.PayloadSource.Equals("stream", StringComparison.OrdinalIgnoreCase)
            ? texture.StreamOffset
            : texture.GpuOffset;
        var payloadSize = texture.PayloadSource.Equals("stream", StringComparison.OrdinalIgnoreCase)
            ? texture.StreamSize
            : texture.GpuSize;
        if (!IsRangeInBounds(payloadOffset, payloadSize, payloadStream.Length) ||
            texture.Width <= 0 || texture.Height <= 0)
            return null;

        if (texture.PayloadKind == "PNG")
        {
            if (payloadSize > MaxEncodedImageBytes)
                return null;
            var png = new byte[(int)payloadSize];
            return await ReadAtAsync(payloadStream, (long)payloadOffset, png, cancellationToken)
                ? new TexturePreviewData { Width = texture.Width, Height = texture.Height, EncodedImageBytes = png, Description = "PNG" }
                : null;
        }

        await using var patchStream = OpenRead(new FileInfo(patchPath));
        var header = new byte[TextureHeaderSize];
        if (!await ReadAtAsync(patchStream, (long)texture.MainOffset + TextureHeaderOffset, header, cancellationToken) ||
            !header.AsSpan(0, 4).SequenceEqual("DDS "u8))
            return null;

        var format = texture.DxgiFormat switch
        {
            28 or 29 => CompressionFormat.Rgba,
            71 or 72 => CompressionFormat.Bc1WithAlpha,
            77 or 78 => CompressionFormat.Bc3,
            83 or 84 => CompressionFormat.Bc5,
            98 or 99 => CompressionFormat.Bc7,
            _ => CompressionFormat.Unknown
        };
        if (format == CompressionFormat.Unknown)
            return null;

        var previewWidth = texture.Width;
        var previewHeight = texture.Height;
        ulong mipOffset = 0;
        var mipLevel = 0;
        while ((long)previewWidth * previewHeight > maxPreviewPixels && mipLevel + 1 < texture.MipCount)
        {
            var previousMipSize = GetTopMipByteCount(format, previewWidth, previewHeight);
            if (previousMipSize <= 0)
                return null;
            mipOffset += (ulong)previousMipSize;
            previewWidth = Math.Max(1, previewWidth / 2);
            previewHeight = Math.Max(1, previewHeight / 2);
            mipLevel++;
        }

        var previewByteCount = GetTopMipByteCount(format, previewWidth, previewHeight);
        byte[] payload;
        string previewDescription;
        if ((long)previewWidth * previewHeight <= maxPreviewPixels && previewByteCount > 0 &&
            mipOffset <= payloadSize && (ulong)previewByteCount <= (ulong)payloadSize - mipOffset &&
            previewByteCount <= int.MaxValue && payloadOffset <= (ulong)long.MaxValue - mipOffset)
        {
            payload = new byte[(int)previewByteCount];
            if (!await ReadAtAsync(payloadStream, (long)(payloadOffset + mipOffset), payload, cancellationToken))
                return null;
            previewDescription = $"DXGI {texture.DxgiFormat}, mip {mipLevel}";
        }
        else
        {
            var sampled = await ReadSampledTopMipAsync(
                payloadStream, payloadOffset, payloadSize, format, texture.Width, texture.Height, maxPreviewPixels, cancellationToken);
            if (sampled is null)
                return null;

            (payload, previewWidth, previewHeight, var sampleFactor) = sampled.Value;
            previewDescription = $"DXGI {texture.DxgiFormat}, sampled 1/{sampleFactor}";
        }

        // The payload starts at the selected mip level, so its dimensions must match
        // the chosen mip rather than the source texture's top-level dimensions.
        cancellationToken.ThrowIfCancellationRequested();
        var pixels = new BcDecoder().DecodeRaw(payload, previewWidth, previewHeight, format);
        var bgra = new byte[pixels.Length * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            bgra[i * 4] = pixels[i].b;
            bgra[i * 4 + 1] = pixels[i].g;
            bgra[i * 4 + 2] = pixels[i].r;
            bgra[i * 4 + 3] = pixels[i].a;
        }

        return new TexturePreviewData
        {
            Width = previewWidth,
            Height = previewHeight,
            BgraPixels = bgra,
            Description = previewDescription
        };
    }

    private static async Task<(byte[] Payload, int Width, int Height, int Factor)?> ReadSampledTopMipAsync(
        FileStream stream,
        ulong payloadOffset,
        uint payloadSize,
        CompressionFormat format,
        int width,
        int height,
        int maxPreviewPixels,
        CancellationToken cancellationToken)
    {
        var topMipByteCount = GetTopMipByteCount(format, width, height);
        if (topMipByteCount <= 0 || (ulong)topMipByteCount > payloadSize)
            return null;

        var factor = 1;
        var sampledWidth = width;
        var sampledHeight = height;
        while ((long)sampledWidth * sampledHeight > maxPreviewPixels)
        {
            if (factor > int.MaxValue / 2)
                return null;
            factor *= 2;
            sampledWidth = Math.Max(1, (int)(((long)width + factor - 1) / factor));
            sampledHeight = Math.Max(1, (int)(((long)height + factor - 1) / factor));
        }

        if (format == CompressionFormat.Rgba)
        {
            var sampled = new byte[checked(sampledWidth * sampledHeight * 4)];
            var sourceRow = new byte[checked(width * 4)];
        for (var targetY = 0; targetY < sampledHeight; targetY++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceY = MapSampleCoordinate(targetY, sampledHeight, height);
                var sourceRowOffset = (long)sourceY * sourceRow.Length;
                if (payloadOffset > (ulong)long.MaxValue - (ulong)sourceRowOffset ||
                    !await ReadAtAsync(stream, (long)payloadOffset + sourceRowOffset, sourceRow, cancellationToken))
                    return null;

                var targetRowOffset = targetY * sampledWidth * 4;
                for (var targetX = 0; targetX < sampledWidth; targetX++)
                {
                    var sourceX = MapSampleCoordinate(targetX, sampledWidth, width);
                    sourceRow.AsSpan(sourceX * 4, 4).CopyTo(sampled.AsSpan(targetRowOffset + targetX * 4, 4));
                }
            }
            return (sampled, sampledWidth, sampledHeight, factor);
        }

        var blockSize = GetBlockByteCount(format);
        if (blockSize == 0)
            return null;

        var sourceBlockWidth = Math.Max(1, (width + 3) / 4);
        var sourceBlockHeight = Math.Max(1, (height + 3) / 4);
        var sampledBlockWidth = Math.Max(1, (sampledWidth + 3) / 4);
        var sampledBlockHeight = Math.Max(1, (sampledHeight + 3) / 4);
        var sampledBlocks = new byte[checked(sampledBlockWidth * sampledBlockHeight * blockSize)];
        var sourceBlockRow = new byte[checked(sourceBlockWidth * blockSize)];
        for (var targetBlockY = 0; targetBlockY < sampledBlockHeight; targetBlockY++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceBlockY = MapSampleCoordinate(targetBlockY, sampledBlockHeight, sourceBlockHeight);
            var sourceRowOffset = (long)sourceBlockY * sourceBlockRow.Length;
            if (payloadOffset > (ulong)long.MaxValue - (ulong)sourceRowOffset ||
                !await ReadAtAsync(stream, (long)payloadOffset + sourceRowOffset, sourceBlockRow, cancellationToken))
                return null;

            var targetRowOffset = targetBlockY * sampledBlockWidth * blockSize;
            for (var targetBlockX = 0; targetBlockX < sampledBlockWidth; targetBlockX++)
            {
                var sourceBlockX = MapSampleCoordinate(targetBlockX, sampledBlockWidth, sourceBlockWidth);
                sourceBlockRow.AsSpan(sourceBlockX * blockSize, blockSize)
                    .CopyTo(sampledBlocks.AsSpan(targetRowOffset + targetBlockX * blockSize, blockSize));
            }
        }
        return (sampledBlocks, sampledWidth, sampledHeight, factor);
    }

    private static int MapSampleCoordinate(int targetCoordinate, int targetSize, int sourceSize) =>
        Math.Min(sourceSize - 1, (int)(((2L * targetCoordinate + 1) * sourceSize) / (2L * targetSize)));

    private static int GetBlockByteCount(CompressionFormat format) => format switch
    {
        CompressionFormat.Bc1 or CompressionFormat.Bc1WithAlpha => 8,
        CompressionFormat.Bc3 or CompressionFormat.Bc5 or CompressionFormat.Bc7 => 16,
        _ => 0
    };

    private static long GetTopMipByteCount(CompressionFormat format, int width, int height)
    {
        var blockWidth = Math.Max(1, (width + 3) / 4);
        var blockHeight = Math.Max(1, (height + 3) / 4);
        return format switch
        {
            CompressionFormat.Bc1 or CompressionFormat.Bc1WithAlpha => (long)blockWidth * blockHeight * 8,
            CompressionFormat.Bc3 or CompressionFormat.Bc5 or CompressionFormat.Bc7 => (long)blockWidth * blockHeight * 16,
            CompressionFormat.Rgba => (long)width * height * 4,
            _ => 0
        };
    }

    /// <summary>DDS DXGI 格式 → BCn 解码格式；模组补丁与游戏归档共用同一套贴图资源格式。</summary>
    internal static CompressionFormat GetCompressionFormat(int dxgiFormat) => dxgiFormat switch
    {
        28 or 29 => CompressionFormat.Rgba,
        71 or 72 => CompressionFormat.Bc1WithAlpha,
        77 or 78 => CompressionFormat.Bc3,
        83 or 84 => CompressionFormat.Bc5,
        98 or 99 => CompressionFormat.Bc7,
        _ => CompressionFormat.Unknown
    };

    internal static long GetTopMipByteSize(CompressionFormat format, int width, int height) =>
        GetTopMipByteCount(format, width, height);

    internal readonly record struct TextureMipPlan(
        int Width,
        int Height,
        int MipLevel,
        ulong SkipBytes,
        long ByteCount);

    /// <summary>按预览像素上限选定 mip；调用方据此只读取所选 mip 的字节，绝不整体载入。</summary>
    internal static TextureMipPlan? PlanTopMip(
        CompressionFormat format,
        int sourceWidth,
        int sourceHeight,
        int sourceMipCount,
        int maxPreviewPixels)
    {
        var previewWidth = sourceWidth;
        var previewHeight = sourceHeight;
        ulong skipBytes = 0;
        var mipLevel = 0;
        while ((long)previewWidth * previewHeight > maxPreviewPixels && mipLevel + 1 < sourceMipCount)
        {
            var previousMipSize = GetTopMipByteCount(format, previewWidth, previewHeight);
            if (previousMipSize <= 0)
                return null;
            skipBytes += (ulong)previousMipSize;
            previewWidth = Math.Max(1, previewWidth / 2);
            previewHeight = Math.Max(1, previewHeight / 2);
            mipLevel++;
        }

        var byteCount = GetTopMipByteCount(format, previewWidth, previewHeight);
        return byteCount > 0
            ? new TextureMipPlan(previewWidth, previewHeight, mipLevel, skipBytes, byteCount)
            : null;
    }

    /// <summary>把从所选 mip 起点读出的负载解码为 BGRA 像素。</summary>
    internal static TexturePreviewData? DecodeMipPayload(
        byte[] mipPayload,
        TextureMipPlan plan,
        int dxgiFormat)
    {
        if (mipPayload.Length < plan.ByteCount)
            return null;

        var pixels = new BcDecoder().DecodeRaw(mipPayload, plan.Width, plan.Height, GetCompressionFormat(dxgiFormat));
        var bgra = new byte[pixels.Length * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            bgra[i * 4] = pixels[i].b;
            bgra[i * 4 + 1] = pixels[i].g;
            bgra[i * 4 + 2] = pixels[i].r;
            bgra[i * 4 + 3] = pixels[i].a;
        }

        return new TexturePreviewData
        {
            Width = plan.Width,
            Height = plan.Height,
            BgraPixels = bgra,
            Description = $"DXGI {dxgiFormat}, mip {plan.MipLevel}"
        };
    }

    private static async Task InspectPatchAsync(
        FileInfo patchFile,
        string displayName,
        int patchOrder,
        PatchResourceInspectionResult result,
        ModelPreviewResult? modelPreview = null,
        IReadOnlyDictionary<(string PatchPath, ulong UnitId), ModelPreviewMaterialLayout>? unitMaterialLayouts = null,
        CancellationToken cancellationToken = default,
        bool includeGpuStreams = true)
    {
        await using var patchStream = OpenRead(patchFile);
        if (patchStream.Length < HeaderSize)
            return;

        var header = new byte[HeaderSize];
        if (!await ReadAtAsync(patchStream, 0, header, cancellationToken) || MemoryMarshal.Read<int>(header) != PatchHeaderMagic)
            return;

        var numTypes = MemoryMarshal.Read<int>(header.AsSpan(4, 4));
        var numFiles = MemoryMarshal.Read<int>(header.AsSpan(8, 4));
        if (numTypes < 0 || numFiles < 0 || numTypes > 1000 || numFiles > 100000)
            return;

        var fileEntriesOffset = HeaderSize + (long)numTypes * TypeEntrySize;
        if (fileEntriesOffset + (long)numFiles * FileEntrySize > patchStream.Length)
            return;

        var gpuPath = patchFile.FullName + ".gpu_resources";
        await using FileStream? gpuStream = File.Exists(gpuPath) ? OpenRead(new FileInfo(gpuPath)) : null;
        var streamPath = patchFile.FullName + ".stream";
        await using FileStream? streamResource = File.Exists(streamPath) ? OpenRead(new FileInfo(streamPath)) : null;
        var entryBuffer = new byte[FileEntrySize];
        for (var index = 0; index < numFiles; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await ReadAtAsync(patchStream, fileEntriesOffset + (long)index * FileEntrySize, entryBuffer, cancellationToken))
                return;

            var fileId = MemoryMarshal.Read<ulong>(entryBuffer.AsSpan(0, 8));
            var typeId = MemoryMarshal.Read<ulong>(entryBuffer.AsSpan(8, 8));
            var mainOffset = MemoryMarshal.Read<ulong>(entryBuffer.AsSpan(16, 8));
            var streamOffset = MemoryMarshal.Read<ulong>(entryBuffer.AsSpan(24, 8));
            var gpuOffset = MemoryMarshal.Read<ulong>(entryBuffer.AsSpan(32, 8));
            var mainSize = MemoryMarshal.Read<uint>(entryBuffer.AsSpan(56, 4));
            var streamSize = MemoryMarshal.Read<uint>(entryBuffer.AsSpan(60, 4));
            var gpuSize = MemoryMarshal.Read<uint>(entryBuffer.AsSpan(64, 4));

            if (modelPreview is null)
            {
                result.TocEntries.Add(new PatchTocInspectionItem
                {
                    PatchFile = displayName,
                    PatchPath = patchFile.FullName,
                    PatchOrder = patchOrder,
                    EntryIndex = index + 1,
                    FileId = fileId,
                    TypeId = typeId,
                    MainOffset = mainOffset,
                    MainSize = mainSize,
                    GpuOffset = gpuOffset,
                    GpuSize = gpuSize,
                    StreamOffset = streamOffset,
                    StreamSize = streamSize
                });
            }

            if (typeId == UnitTypeId && IsRangeInBounds(mainOffset, mainSize, patchStream.Length))
            {
                if (modelPreview is not null || includeGpuStreams)
                {
                    await ReadUnitGpuStreamsAsync(
                        patchStream, gpuStream, displayName, index + 1, fileId, mainOffset, mainSize, gpuOffset, gpuSize,
                        result.GpuStreams, modelPreview,
                        unitMaterialLayouts is not null && unitMaterialLayouts.TryGetValue((patchFile.FullName, fileId), out var materialLayout)
                            ? materialLayout
                            : null,
                        cancellationToken);
                }
            }
            else if (modelPreview is null)
            {
                TextureInspectionItem? texture = null;
                if (typeId == TextureTypeId && IsRangeInBounds(mainOffset, mainSize, patchStream.Length))
                {
                    texture = await ReadTextureMetadataAsync(
                        patchStream, displayName, patchFile.FullName, patchOrder, index + 1, fileId,
                        mainOffset, mainSize, gpuOffset, gpuSize, streamOffset, streamSize, cancellationToken);
                }
                if (texture is not null)
                {
                    result.Textures.Add(texture);
                }
                else
                {
                    var pngTexture = await TryReadEmbeddedPngAsync(
                        gpuStream, streamResource, displayName, patchFile.FullName, patchOrder, index + 1, fileId,
                        mainOffset, mainSize, gpuOffset, gpuSize, streamOffset, streamSize, cancellationToken);
                    if (pngTexture is not null)
                        result.Textures.Add(pngTexture);
                }
            }
        }
    }

    private static async Task<TextureInspectionItem?> ReadTextureMetadataAsync(
        FileStream patchStream, string displayName, string patchPath, int patchOrder, int tocEntryIndex, ulong textureId,
        ulong mainOffset, uint mainSize, ulong gpuOffset, uint gpuSize, ulong streamOffset, uint streamSize,
        CancellationToken cancellationToken)
    {
        if (mainSize < TextureHeaderOffset + TextureHeaderSize)
            return null;

        var ddsHeader = new byte[TextureHeaderSize];
        if (!await ReadAtAsync(patchStream, (long)mainOffset + TextureHeaderOffset, ddsHeader, cancellationToken) ||
            !ddsHeader.AsSpan(0, 4).SequenceEqual("DDS "u8))
            return null;

        var height = MemoryMarshal.Read<int>(ddsHeader.AsSpan(12, 4));
        var width = MemoryMarshal.Read<int>(ddsHeader.AsSpan(16, 4));
        var mipCount = MemoryMarshal.Read<int>(ddsHeader.AsSpan(28, 4));
        var dxgiFormat = MemoryMarshal.Read<int>(ddsHeader.AsSpan(128, 4));
        var payloadSource = streamSize > 0 ? "stream" : "gpu_resources";
        return width > 0 && height > 0
            ? new TextureInspectionItem
            {
                PatchFile = displayName,
                PatchPath = patchPath,
                PatchOrder = patchOrder,
                TocEntryIndex = tocEntryIndex,
                TextureId = textureId,
                MainOffset = mainOffset,
                MainSize = mainSize,
                GpuOffset = gpuOffset,
                GpuSize = gpuSize,
                StreamOffset = streamOffset,
                StreamSize = streamSize,
                Width = width,
                Height = height,
                MipCount = Math.Max(mipCount, 1),
                DxgiFormat = dxgiFormat,
                PayloadKind = "DDS",
                PayloadSource = payloadSource
            }
            : null;
    }

    private static async Task<TextureInspectionItem?> TryReadEmbeddedPngAsync(
        FileStream? gpuStream, FileStream? streamResource, string displayName, string patchPath, int patchOrder, int tocEntryIndex, ulong textureId,
        ulong mainOffset, uint mainSize, ulong gpuOffset, uint gpuSize, ulong streamOffset, uint streamSize,
        CancellationToken cancellationToken)
    {
        var payloadStream = streamSize > 0 ? streamResource : gpuStream;
        var payloadOffset = streamSize > 0 ? streamOffset : gpuOffset;
        var payloadSize = streamSize > 0 ? streamSize : gpuSize;
        if (payloadStream is null || payloadSize < 24 || !IsRangeInBounds(payloadOffset, payloadSize, payloadStream.Length))
            return null;

        var signature = new byte[24];
        if (!await ReadAtAsync(payloadStream, (long)payloadOffset, signature, cancellationToken) ||
            !signature.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ||
            !signature.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            return null;

        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(signature.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(signature.AsSpan(20, 4));
        return width > 0 && height > 0
            ? new TextureInspectionItem
            {
                PatchFile = displayName,
                PatchPath = patchPath,
                PatchOrder = patchOrder,
                TocEntryIndex = tocEntryIndex,
                TextureId = textureId,
                MainOffset = mainOffset,
                MainSize = mainSize,
                GpuOffset = gpuOffset,
                GpuSize = gpuSize,
                StreamOffset = streamOffset,
                StreamSize = streamSize,
                Width = width,
                Height = height,
                MipCount = 1,
                DxgiFormat = -1,
                PayloadKind = "PNG",
                PayloadSource = streamSize > 0 ? "stream" : "gpu_resources"
            }
            : null;
    }

    /// <summary>
    /// Resolves materials in two stages: material resources reference texture resources,
    /// then each Unit's MeshInfo table assigns a material slot to each index range. This
    /// is deliberately based on the Unit structure instead of treating all 64-bit values
    /// in a Unit as a texture list.
    /// </summary>
    private static async Task<IReadOnlyDictionary<(string PatchPath, ulong UnitId), ModelPreviewMaterialLayout>>
        BuildUnitMaterialLayoutsAsync(PatchResourceInspectionResult inspection, CancellationToken cancellationToken)
    {
        var materialTextures = new Dictionary<ulong, ModelPreviewMaterialTextures>();
        var resourceEntries = inspection.TocEntries
            .Where(entry => entry.TypeId == MaterialTypeId)
            .OrderBy(static entry => entry.PatchOrder)
            .ToArray();
        foreach (var entry in resourceEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = await ReadMainResourceAsync(entry, cancellationToken);
            if (data is null)
                continue;

            var referencedTextures = TryReadMaterialTextures(data);
            if (referencedTextures is not null)
                materialTextures[entry.FileId] = referencedTextures;
        }

        var materialIds = materialTextures.Keys.ToHashSet();
        var unitMaterialLayouts = new Dictionary<(string PatchPath, ulong UnitId), ModelPreviewMaterialLayout>();
        foreach (var entry in inspection.TocEntries.Where(entry => entry.TypeId == UnitTypeId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = await ReadMainResourceAsync(entry, cancellationToken);
            if (data is null)
                continue;

            var fallbackTextures = new HashSet<ulong>();
            foreach (var materialId in ScanResourceIds(data, materialIds))
            {
                foreach (var textureId in materialTextures[materialId].TextureIds)
                    fallbackTextures.Add(textureId);
            }

            var fallbackColorTextureIds = ScanResourceIds(data, materialIds)
                .Select(materialId => materialTextures[materialId].ColorTextureId)
                .Where(static textureId => textureId.HasValue)
                .Select(static textureId => textureId!.Value)
                .Distinct()
                .Take(2)
                .ToArray();

            var customizationInfo = TryReadUnitCustomizationInfo(data);
            var sectionsByStream = TryReadUnitMaterialSections(data, materialTextures);
            var rig = TryReadUnitRig(data);
            if (sectionsByStream.Count > 0 || fallbackTextures.Count > 0 ||
                customizationInfo.BodyShape != ModelPreviewBodyShape.Unknown || rig is not null)
            {
                unitMaterialLayouts[(entry.PatchPath, entry.FileId)] = new ModelPreviewMaterialLayout(
                    sectionsByStream,
                    fallbackTextures.ToArray(),
                    fallbackColorTextureIds.Length == 1 ? fallbackColorTextureIds[0] : null,
                    customizationInfo.BodyShape,
                    customizationInfo.Slot,
                    rig);
            }
        }

        return unitMaterialLayouts;
    }

    /// <summary>
    /// CustomizationInfo is optional Unit metadata. The first string is the body type
    /// used by Helldivers 2 customization resources (Any, Slim, or Stocky). Keep this
    /// parser conservative: malformed or unknown values must remain visible instead of
    /// making a mesh disappear from the preview.
    /// </summary>
    internal static ModelPreviewBodyShape TryReadUnitBodyShape(byte[] data) =>
        TryReadUnitCustomizationInfo(data).BodyShape;

    internal static ModelPreviewCustomizationInfo TryReadUnitCustomizationInfo(byte[] data)
    {
        const int customizationInfoOffset = 0x4C;
        const int firstStringOffset = 24;
        const int maximumStringSize = 1024;

        if (data.Length < customizationInfoOffset + sizeof(uint))
            return new(ModelPreviewBodyShape.Unknown, ModelPreviewCustomizationSlot.Unknown);

        var customizationOffset = ReadUInt32(data, customizationInfoOffset);
        var maximumCustomizationOffset = data.Length - firstStringOffset - sizeof(uint);
        if (customizationOffset == 0 || customizationOffset > int.MaxValue || maximumCustomizationOffset < 0 ||
            customizationOffset > (uint)maximumCustomizationOffset)
            return new(ModelPreviewBodyShape.Unknown, ModelPreviewCustomizationSlot.Unknown);

        var cursor = checked((int)customizationOffset + firstStringOffset);
        if (!IsRangeInBounds(cursor, sizeof(uint), data.Length))
            return new(ModelPreviewBodyShape.Unknown, ModelPreviewCustomizationSlot.Unknown);

        var stringLength = ReadUInt32(data, cursor);
        cursor += sizeof(uint);
        if (stringLength > maximumStringSize || !IsRangeInBounds(cursor, stringLength, data.Length))
            return new(ModelPreviewBodyShape.Unknown, ModelPreviewCustomizationSlot.Unknown);

        var bodyType = Encoding.UTF8.GetString(data, cursor, (int)stringLength).TrimEnd('\0');
        var bodyShape = ModelPreviewBodyShapeParser.Parse(bodyType);
        var slot = TryReadCustomizationSlot(data, cursor + (int)stringLength);
        return new(bodyShape, slot);
    }

    private static ModelPreviewCustomizationSlot TryReadCustomizationSlot(byte[] data, int startOffset)
    {
        const string slotPrefix = "HelldiverCustomizationSlot_";
        const int maximumScanSize = 4096;
        if (startOffset < 0 || startOffset >= data.Length)
            return ModelPreviewCustomizationSlot.Unknown;

        var scanSize = Math.Min(maximumScanSize, data.Length - startOffset);
        var text = Encoding.UTF8.GetString(data, startOffset, scanSize);
        var prefixIndex = text.IndexOf(slotPrefix, StringComparison.OrdinalIgnoreCase);
        if (prefixIndex < 0)
            return ModelPreviewCustomizationSlot.Unknown;

        var endIndex = text.IndexOf('\0', prefixIndex);
        var slotText = endIndex >= 0
            ? text[prefixIndex..endIndex]
            : text[prefixIndex..];
        return ModelPreviewBodyShapeParser.ParseSlot(slotText);
    }

    /// <summary>
    /// Reads the Unit material and MeshInfo tables. Offsets are validated against the
    /// resource buffer at every stage because a number of mods contain partial Units.
    /// </summary>
    internal static IReadOnlyDictionary<int, IReadOnlyList<ModelPreviewMaterialSection>> TryReadUnitMaterialSections(
        byte[] data,
        IReadOnlyDictionary<ulong, ModelPreviewMaterialTextures> materialTextures)
    {
        const int unitMeshInfoOffset = 0x64;
        const int unitMaterialsOffset = 0x70;
        const int meshInfoSize = 128;
        const int meshInfoTransformIndexOffset = 48;
        const int meshInfoLodIndexOffset = 56;
        const int meshInfoStreamIndexOffset = 60;
        const int meshInfoMaterialCountOffset = 104;
        const int meshInfoMaterialOffset = 108;
        const int meshInfoSectionCountOffset = 120;
        const int meshInfoSectionsOffset = 124;
        const int meshSectionSize = 24;
        const int meshSectionMaterialIndexOffset = 0;
        const int meshSectionVertexOffset = 4;
        const int meshSectionVertexCountOffset = 8;
        const int meshSectionIndexOffset = 12;
        const int meshSectionIndexCountOffset = 16;

        if (data.Length < unitMaterialsOffset + sizeof(uint))
            return new Dictionary<int, IReadOnlyList<ModelPreviewMaterialSection>>();

        var meshInfoOffset = ReadInt32(data, unitMeshInfoOffset);
        var materialsOffset = ReadInt32(data, unitMaterialsOffset);
        if (!IsRangeInBounds(meshInfoOffset, sizeof(uint), data.Length) ||
            !IsRangeInBounds(materialsOffset, sizeof(uint), data.Length))
            return new Dictionary<int, IReadOnlyList<ModelPreviewMaterialSection>>();

        var materialCount = ReadInt32(data, materialsOffset);
        if (materialCount < 0 || materialCount > 4096 ||
            !IsRangeInBounds(materialsOffset + sizeof(uint), materialCount * sizeof(uint), data.Length) ||
            !IsRangeInBounds(materialsOffset + sizeof(uint) + materialCount * sizeof(uint), materialCount * sizeof(ulong), data.Length))
            return new Dictionary<int, IReadOnlyList<ModelPreviewMaterialSection>>();

        var materialBySlot = new Dictionary<uint, ulong>();
        for (var index = 0; index < materialCount; index++)
        {
            var slot = ReadUInt32(data, materialsOffset + sizeof(uint) + index * sizeof(uint));
            var materialId = ReadUInt64(data, materialsOffset + sizeof(uint) + materialCount * sizeof(uint) + index * sizeof(ulong));
            materialBySlot.TryAdd(slot, materialId);
        }

        var meshCount = ReadInt32(data, meshInfoOffset);
        var offsetsStart = meshInfoOffset + sizeof(uint);
        if (meshCount < 0 || meshCount > 4096 ||
            !IsRangeInBounds(offsetsStart, meshCount * sizeof(uint) * 2L, data.Length))
            return new Dictionary<int, IReadOnlyList<ModelPreviewMaterialSection>>();

        var transforms = TryReadUnitTransforms(data);
        var preferredLodIndex = int.MaxValue;
        for (var meshIndex = 0; meshIndex < meshCount; meshIndex++)
        {
            var relativeOffset = ReadInt32(data, offsetsStart + meshIndex * sizeof(uint));
            var meshOffset = meshInfoOffset + relativeOffset;
            if (relativeOffset < 0 || !IsRangeInBounds(meshOffset, meshInfoSize, data.Length))
                continue;

            var lodIndex = ReadInt32(data, meshOffset + meshInfoLodIndexOffset);
            if (lodIndex >= 0)
                preferredLodIndex = Math.Min(preferredLodIndex, lodIndex);
        }

        var sectionsByStream = new Dictionary<int, List<ModelPreviewMaterialSection>>();
        for (var meshIndex = 0; meshIndex < meshCount; meshIndex++)
        {
            var relativeOffset = ReadInt32(data, offsetsStart + meshIndex * sizeof(uint));
            var meshOffset = meshInfoOffset + relativeOffset;
            if (relativeOffset < 0 || !IsRangeInBounds(meshOffset, meshInfoSize, data.Length))
                continue;

            var streamIndex = ReadInt32(data, meshOffset + meshInfoStreamIndexOffset);
            var lodIndex = ReadInt32(data, meshOffset + meshInfoLodIndexOffset);
            var transformIndex = ReadInt32(data, meshOffset + meshInfoTransformIndexOffset);
            var meshMaterialCount = ReadInt32(data, meshOffset + meshInfoMaterialCountOffset);
            var meshMaterialRelativeOffset = ReadInt32(data, meshOffset + meshInfoMaterialOffset);
            var sectionCount = ReadInt32(data, meshOffset + meshInfoSectionCountOffset);
            var sectionsRelativeOffset = ReadInt32(data, meshOffset + meshInfoSectionsOffset);
            var meshMaterialOffset = (long)meshOffset + meshMaterialRelativeOffset;
            var sectionsOffset = (long)meshOffset + sectionsRelativeOffset;
            if (streamIndex < 0 || meshMaterialCount < 0 || meshMaterialCount > 4096 || sectionCount < 0 || sectionCount > 4096 ||
                meshMaterialRelativeOffset < 0 || sectionsRelativeOffset < 0 ||
                !IsRangeInBounds(meshMaterialOffset, meshMaterialCount * sizeof(uint), data.Length) ||
                !IsRangeInBounds(sectionsOffset, sectionCount * meshSectionSize, data.Length))
                continue;

            // A Unit can store the same visible part in several LODs, sometimes in one
            // combined GPU stream. Rendering every LOD both overlaps the geometry and
            // consumes the global preview budget before later body parts are reached.
            // Keep the highest-detail available LOD while retaining negative LOD proxy
            // records for the existing inspection/hiding controls.
            if (!sectionsByStream.ContainsKey(streamIndex))
                sectionsByStream.Add(streamIndex, []);
            if (lodIndex >= 0 && preferredLodIndex != int.MaxValue && lodIndex != preferredLodIndex)
                continue;

            var transform = transformIndex >= 0 && transformIndex < transforms.Count
                ? transforms[transformIndex]
                : ModelPreviewTransform.Identity;
            var rawSections = new List<(int SectionIndex, int MaterialIndex, uint Slot, uint VertexOffset, uint VertexCount, uint IndexOffset, uint IndexCount)>();
            for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
            {
                var sectionOffset = checked((int)(sectionsOffset + sectionIndex * meshSectionSize));
                var materialIndex = ReadInt32(data, sectionOffset + meshSectionMaterialIndexOffset);
                if (materialIndex < 0 || materialIndex >= meshMaterialCount)
                    continue;

                var slot = ReadUInt32(data, checked((int)(meshMaterialOffset + materialIndex * sizeof(uint))));
                rawSections.Add((
                    sectionIndex,
                    materialIndex,
                    slot,
                    ReadUInt32(data, sectionOffset + meshSectionVertexOffset),
                    ReadUInt32(data, sectionOffset + meshSectionVertexCountOffset),
                    ReadUInt32(data, sectionOffset + meshSectionIndexOffset),
                    ReadUInt32(data, sectionOffset + meshSectionIndexCountOffset)));
            }

            if (rawSections.Count == 0)
                continue;

            // HD2SDK identifies a culling body when every section falls back to
            // StingrayDefaultMaterial. A slot present in the Unit material table is an
            // explicit material even when that material resource is supplied by the base
            // game rather than this mod's selected patches.
            var isCullingBody = rawSections.All(section => !materialBySlot.ContainsKey(section.Slot));
            foreach (var rawSection in rawSections)
            {
                IReadOnlyList<ulong> textureIds = [];
                ulong? colorTextureId = null;
                ModelPreviewMaterialTextureSet? materialTextureSet = null;
                ulong? materialIdForSection = null;
                if (materialBySlot.TryGetValue(rawSection.Slot, out var materialId) &&
                    materialTextures.TryGetValue(materialId, out var resolvedTextures))
                {
                    textureIds = resolvedTextures.TextureIds;
                    colorTextureId = resolvedTextures.ColorTextureId;
                    materialTextureSet = resolvedTextures.ToTextureSet();
                    materialIdForSection = materialId;
                }

                var sections = sectionsByStream[streamIndex];

                sections.Add(new ModelPreviewMaterialSection(
                    meshIndex,
                    rawSection.SectionIndex,
                    rawSection.VertexOffset,
                    rawSection.VertexCount,
                    rawSection.IndexOffset,
                    rawSection.IndexCount,
                    textureIds,
                    colorTextureId,
                    isCullingBody,
                    transform,
                    materialTextureSet,
                    materialIdForSection,
                    lodIndex,
                    rawSection.MaterialIndex));
            }
        }

        return sectionsByStream.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<ModelPreviewMaterialSection>)pair.Value);
    }

    /// <summary>
    /// Material texture entries are stored as a parallel semantic-hash table followed
    /// by resource IDs. Keeping that pairing lets the preview choose the actual albedo
    /// input instead of guessing between same-sized normal, mask and color textures.
    /// </summary>
    /// <summary>
    /// 解析材质的语义贴图表。模组补丁里没有的贴图 ID（引用游戏原版资源）也必须保留：
    /// 它们是材质的权威输入，缺失时预览只能整段灰显或错拿 Normal/Mask 当 Albedo；
    /// 模型预览会再尝试从游戏包里解析这些外部引用。
    /// </summary>
    internal static ModelPreviewMaterialTextures? TryReadMaterialTextures(
        byte[] data)
    {
        const int textureCountOffset = 0x40;
        const int textureTableOffset = 0x88;
        if (data.Length < textureTableOffset)
            return null;

        var textureCount = ReadInt32(data, textureCountOffset);
        if (textureCount <= 0 || textureCount > 4096)
            return null;

        var semanticBytes = (long)textureCount * sizeof(uint);
        var textureIdsOffset = (long)textureTableOffset + semanticBytes;
        if (!IsRangeInBounds(textureTableOffset, semanticBytes, data.Length) ||
            !IsRangeInBounds(textureIdsOffset, (long)textureCount * sizeof(ulong), data.Length))
            return null;

        var textureIds = new List<ulong>();
        var texturesByRole = new Dictionary<ModelPreviewTextureRole, List<ulong>>();
        var inputs = new List<ModelPreviewMaterialInput>();
        ulong? colorTextureId = null;
        for (var index = 0; index < textureCount; index++)
        {
            var semanticId = ReadUInt32(data, textureTableOffset + index * sizeof(uint));
            var textureId = ReadUInt64(data, checked((int)(textureIdsOffset + index * sizeof(ulong))));
            if (textureId == 0)
                continue;

            textureIds.Add(textureId);
            var role = GetTextureRole(semanticId);
            inputs.Add(new ModelPreviewMaterialInput(semanticId, textureId, role));
            if (!texturesByRole.TryGetValue(role, out var roleIds))
                texturesByRole[role] = roleIds = [];
            roleIds.Add(textureId);
            // Iridescence 输入也是颜色贴图：它必须占据 ColorTextureId，保证既有消费者
            // （自动贴图选择、首选贴图解析）仍然把流光材质解析到正确的颜色输入。
            if (colorTextureId is null &&
                (role == ModelPreviewTextureRole.BaseColor || role == ModelPreviewTextureRole.Iridescence))
                colorTextureId = textureId;
        }

        return textureIds.Count > 0
            ? new ModelPreviewMaterialTextures(
                textureIds,
                colorTextureId,
                texturesByRole.ToDictionary(
                    static pair => pair.Key,
                    static pair => (IReadOnlyList<ulong>)pair.Value),
                inputs)
            : null;
    }

    private static ModelPreviewTextureRole GetTextureRole(uint semanticId) => semanticId switch
    {
        0xE67AC0C7 or // AlbedoEmissive (the common HD2 character material input)
        0xAC652E43 or // Albedo
        0xFAEE8CB2 or // AlbedoTex
        0x604318CD or // BaseColor
        0x608D8147 or // BaseColorEmissiveMap
        0x848BA63B or // BaseColorMetalMap
        0x3AA8B87E => ModelPreviewTextureRole.BaseColor,
        // Common semantic hashes from the Stingray material tables. Unknown semantic
        // values stay Unknown so forward-compatible materials are still visible through
        // the first readable input rather than being misclassified as normal maps.
        0x7668E94B or 0xF5C97D31 or 0x2B33D35F or 0x5A3BC7C0 or
        0xCAED6CD6 or // Normal
        0x1D57DCF3 => ModelPreviewTextureRole.Normal, // NormalXyAoRoughMap
        0xE97A4617 or 0x85C8629F or 0x204EB619 or 0xE6E80465 or 0xE58FF005 or
        0x756F6FA6 or // Mra
        0xCBDE381B => ModelPreviewTextureRole.Mask, // OpacityClipMap
        0x12A0F5C0 or 0x4DC19F08 or 0x3E6E30E7 or
        0xCA6F2CF1 => ModelPreviewTextureRole.Emissive, // EmissiveFStop10IntensityMap
        // AlbedoIridescence（流光/油光材质的颜色输入）。RGB 仍是颜色贴图，因此继续参与
        // BaseColor 回退链并写入 ColorTextureId；单独的 Iridescence 角色让预览给这些
        // 材质叠加流光高光层（实测其 Alpha 承载流光强度：未开启流光的同款贴图 Alpha≈0，
        // 开启后为 255，见"油光材质"选项实测）。
        0xFF2C91CC => ModelPreviewTextureRole.Iridescence,
        _ => ModelPreviewTextureRole.Unknown
    };

    private static IReadOnlyList<ModelPreviewTransform> TryReadUnitTransforms(byte[] data)
    {
        const int unitTransformInfoOffset = 0x34;
        const int transformInfoHeaderSize = 16;
        const int localTransformSize = 64;
        const int matrixSize = 64;

        if (data.Length < unitTransformInfoOffset + sizeof(uint))
            return [];

        var transformInfoOffset = ReadInt32(data, unitTransformInfoOffset);
        if (!IsRangeInBounds(transformInfoOffset, transformInfoHeaderSize, data.Length))
            return [];

        var transformCount = ReadInt32(data, transformInfoOffset);
        if (transformCount < 0 || transformCount > 65_536)
            return [];

        var matricesOffset = (long)transformInfoOffset + transformInfoHeaderSize + (long)transformCount * localTransformSize;
        if (!IsRangeInBounds(matricesOffset, (long)transformCount * matrixSize, data.Length))
            return [];

        var transforms = new ModelPreviewTransform[transformCount];
        for (var index = 0; index < transformCount; index++)
        {
            var offset = checked((int)(matricesOffset + index * matrixSize));
            var values = new float[16];
            var valid = true;
            for (var component = 0; component < values.Length; component++)
            {
                values[component] = BitConverter.ToSingle(data, offset + component * sizeof(float));
                valid &= float.IsFinite(values[component]);
            }

            transforms[index] = valid
                ? new ModelPreviewTransform(
                    values[0], values[4], values[8], values[12],
                    values[1], values[5], values[9], values[13],
                    values[2], values[6], values[10], values[14])
                : ModelPreviewTransform.Identity;
        }

        return transforms;
    }

    private static ModelPreviewUnitRig? TryReadUnitRig(byte[] data)
    {
        const int bonesReferenceOffset = 0x08;
        const int stateMachineReferenceOffset = 0x20;
        const int transformInfoPointerOffset = 0x34;
        const int boneInfoPointerOffset = 0x58;
        const int transformInfoHeaderSize = 16;
        const int localTransformSize = 64;
        const int matrixSize = 64;
        const int transformEntrySize = 4;

        if (data.Length < 0x68)
            return null;

        var transformInfoOffset = ReadInt32(data, transformInfoPointerOffset);
        if (!IsRangeInBounds(transformInfoOffset, transformInfoHeaderSize, data.Length))
            return null;

        var transformCount = ReadInt32(data, transformInfoOffset);
        if (transformCount <= 0 || transformCount > 4096)
            return null;

        var matricesOffset = (long)transformInfoOffset + transformInfoHeaderSize + (long)transformCount * localTransformSize;
        var entriesOffset = matricesOffset + (long)transformCount * matrixSize;
        var hashesOffset = entriesOffset + (long)transformCount * transformEntrySize;
        if (!IsRangeInBounds(matricesOffset, (long)transformCount * matrixSize, data.Length) ||
            !IsRangeInBounds(entriesOffset, (long)transformCount * transformEntrySize, data.Length) ||
            !IsRangeInBounds(hashesOffset, (long)transformCount * sizeof(uint), data.Length))
        {
            return null;
        }

        var bones = new ModelPreviewSkeletonBone[transformCount];
        for (var index = 0; index < transformCount; index++)
        {
            var matrixOffset = checked((int)(matricesOffset + index * matrixSize));
            var matrix = new Matrix4x4(
                BitConverter.ToSingle(data, matrixOffset),
                BitConverter.ToSingle(data, matrixOffset + 4),
                BitConverter.ToSingle(data, matrixOffset + 8),
                BitConverter.ToSingle(data, matrixOffset + 12),
                BitConverter.ToSingle(data, matrixOffset + 16),
                BitConverter.ToSingle(data, matrixOffset + 20),
                BitConverter.ToSingle(data, matrixOffset + 24),
                BitConverter.ToSingle(data, matrixOffset + 28),
                BitConverter.ToSingle(data, matrixOffset + 32),
                BitConverter.ToSingle(data, matrixOffset + 36),
                BitConverter.ToSingle(data, matrixOffset + 40),
                BitConverter.ToSingle(data, matrixOffset + 44),
                BitConverter.ToSingle(data, matrixOffset + 48),
                BitConverter.ToSingle(data, matrixOffset + 52),
                BitConverter.ToSingle(data, matrixOffset + 56),
                BitConverter.ToSingle(data, matrixOffset + 60));
            if (!IsFinite(matrix))
                return null;

            var entryOffset = checked((int)(entriesOffset + index * transformEntrySize));
            var parent = MemoryMarshal.Read<ushort>(data.AsSpan(entryOffset + sizeof(ushort), sizeof(ushort)));
            var parentIndex = parent < transformCount && parent != index ? parent : -1;
            var nameHash = ReadUInt32(data, checked((int)(hashesOffset + index * sizeof(uint))));
            bones[index] = new ModelPreviewSkeletonBone(parentIndex, nameHash, matrix);
        }

        var palettes = TryReadUnitBonePalettes(data, transformCount, boneInfoPointerOffset);
        if (palettes.Count == 0)
            return null;

        return new ModelPreviewUnitRig
        {
            Skeleton = new ModelPreviewSkeleton
            {
                BonesId = ReadUInt64(data, bonesReferenceOffset),
                StateMachineId = ReadUInt64(data, stateMachineReferenceOffset),
                Bones = bones
            },
            Palettes = palettes
        };
    }

    private static IReadOnlyList<ModelPreviewBonePalette> TryReadUnitBonePalettes(
        byte[] data,
        int transformCount,
        int boneInfoPointerOffset)
    {
        var boneInfoOffset = ReadInt32(data, boneInfoPointerOffset);
        if (!IsRangeInBounds(boneInfoOffset, sizeof(uint), data.Length))
            return [];

        var paletteCount = ReadInt32(data, boneInfoOffset);
        var paletteOffsetsStart = (long)boneInfoOffset + sizeof(uint);
        if (paletteCount <= 0 || paletteCount > 256 ||
            !IsRangeInBounds(paletteOffsetsStart, (long)paletteCount * sizeof(uint), data.Length))
        {
            return [];
        }

        var palettes = new ModelPreviewBonePalette[paletteCount];
        for (var paletteIndex = 0; paletteIndex < paletteCount; paletteIndex++)
        {
            var relativeOffset = ReadUInt32(data, checked((int)(paletteOffsetsStart + paletteIndex * sizeof(uint))));
            var paletteOffset = (long)boneInfoOffset + relativeOffset;
            if (!IsRangeInBounds(paletteOffset, 16, data.Length))
                return [];

            var boneCount = ReadInt32(data, checked((int)paletteOffset));
            var realIndicesRelativeOffset = ReadUInt32(data, checked((int)paletteOffset + 8));
            var remapsRelativeOffset = ReadUInt32(data, checked((int)paletteOffset + 12));
            if (boneCount <= 0 || boneCount > transformCount)
                return [];

            var realIndicesOffset = paletteOffset + realIndicesRelativeOffset;
            if (!IsRangeInBounds(realIndicesOffset, (long)boneCount * sizeof(uint), data.Length))
                return [];

            var transformIndices = new int[boneCount];
            for (var index = 0; index < boneCount; index++)
            {
                var transformIndex = ReadUInt32(data, checked((int)(realIndicesOffset + index * sizeof(uint))));
                transformIndices[index] = transformIndex < transformCount ? (int)transformIndex : -1;
            }

            var remapsOffset = paletteOffset + remapsRelativeOffset;
            if (!IsRangeInBounds(remapsOffset, sizeof(uint), data.Length))
                return [];

            var remapCount = ReadInt32(data, checked((int)remapsOffset));
            var remapHeadersOffset = remapsOffset + sizeof(uint);
            if (remapCount <= 0 || remapCount > 4096 ||
                !IsRangeInBounds(remapHeadersOffset, (long)remapCount * 8, data.Length))
            {
                return [];
            }

            var remaps = new IReadOnlyList<int>[remapCount];
            for (var remapIndex = 0; remapIndex < remapCount; remapIndex++)
            {
                var headerOffset = checked((int)(remapHeadersOffset + remapIndex * 8));
                var relativeRemapOffset = ReadUInt32(data, headerOffset);
                var remapBoneCount = ReadInt32(data, headerOffset + sizeof(uint));
                var remapOffset = remapsOffset + relativeRemapOffset;
                if (remapBoneCount < 0 || remapBoneCount > 4096 ||
                    !IsRangeInBounds(remapOffset, (long)remapBoneCount * sizeof(uint), data.Length))
                {
                    return [];
                }

                var remap = new int[remapBoneCount];
                for (var index = 0; index < remap.Length; index++)
                {
                    var realIndex = ReadUInt32(data, checked((int)(remapOffset + index * sizeof(uint))));
                    remap[index] = realIndex < boneCount ? (int)realIndex : -1;
                }
                remaps[remapIndex] = remap;
            }

            palettes[paletteIndex] = new ModelPreviewBonePalette
            {
                TransformIndices = transformIndices,
                Remaps = remaps
            };
        }

        return palettes;
    }

    private static bool IsFinite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);

    private static async Task<byte[]?> ReadMainResourceAsync(
        PatchTocInspectionItem entry,
        CancellationToken cancellationToken)
    {
        if (entry.MainSize == 0 || entry.MainSize > MaxReferenceScanBytes)
            return null;

        if (!File.Exists(entry.PatchPath))
            return null;

        await using var stream = OpenRead(new FileInfo(entry.PatchPath));
        if (!IsRangeInBounds(entry.MainOffset, entry.MainSize, stream.Length))
            return null;

        var data = new byte[(int)entry.MainSize];
        return await ReadAtAsync(stream, (long)entry.MainOffset, data, cancellationToken) ? data : null;
    }

    private static async Task<IReadOnlySet<ulong>> FindPureBlackBaseColorTextureIdsAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<TextureInspectionItem> textures,
        IEnumerable<ModelPreviewMaterialLayout> materialLayouts,
        CancellationToken cancellationToken)
    {
        var referencedColorTextureIds = materialLayouts
            .SelectMany(static layout => layout.SectionsByStream.Values)
            .SelectMany(static sections => sections)
            .Select(static section => section.ColorTextureId)
            .Where(static textureId => textureId.HasValue)
            .Select(static textureId => textureId!.Value)
            .ToHashSet();
        if (referencedColorTextureIds.Count == 0)
            return new HashSet<ulong>();

        var candidates = textures
            .Where(texture => referencedColorTextureIds.Contains(texture.TextureId) &&
                texture.PayloadKind == "DDS" && texture.DxgiFormat is 98 or 99)
            .GroupBy(static texture => texture.TextureId)
            .Select(static group => group.First())
            .ToArray();
        var pureBlackIds = new System.Collections.Concurrent.ConcurrentDictionary<ulong, byte>();
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 2,
                CancellationToken = cancellationToken
            },
            async (texture, token) =>
            {
                if (await IsBc7PureBlackPlaceholderAsync(modDirectory, texture, token))
                    pureBlackIds.TryAdd(texture.TextureId, 0);
            });
        return pureBlackIds.Keys.ToHashSet();
    }

    private static async Task<bool> IsBc7PureBlackPlaceholderAsync(
        DirectoryInfo modDirectory,
        TextureInspectionItem texture,
        CancellationToken cancellationToken)
    {
        const int blockSize = 16;
        const int blocksPerSample = 4;
        const int sampleByteCount = blockSize * blocksPerSample;
        var patchPath = Path.IsPathFullyQualified(texture.PatchPath)
            ? texture.PatchPath
            : Path.Combine(modDirectory.FullName, texture.PatchPath);
        var payloadPath = texture.PayloadSource.Equals("stream", StringComparison.OrdinalIgnoreCase)
            ? patchPath + ".stream"
            : patchPath + ".gpu_resources";
        if (!File.Exists(payloadPath))
            return false;

        var payloadOffset = texture.PayloadSource.Equals("stream", StringComparison.OrdinalIgnoreCase)
            ? texture.StreamOffset
            : texture.GpuOffset;
        var payloadSize = texture.PayloadSource.Equals("stream", StringComparison.OrdinalIgnoreCase)
            ? texture.StreamSize
            : texture.GpuSize;
        var blockCount = (long)Math.Max(1, (texture.Width + 3) / 4) * Math.Max(1, (texture.Height + 3) / 4);
        if (blockCount < blocksPerSample || payloadSize < sampleByteCount)
            return false;

        var lastSampleStart = blockCount - blocksPerSample;
        var sampleStarts = new[]
        {
            0L,
            lastSampleStart / 4,
            lastSampleStart / 2,
            lastSampleStart - lastSampleStart / 4,
            lastSampleStart
        }.Distinct().ToArray();
        await using var stream = OpenRead(new FileInfo(payloadPath));
        if (!IsRangeInBounds(payloadOffset, payloadSize, stream.Length))
            return false;

        var sampledBlocks = new byte[checked(sampleStarts.Length * sampleByteCount)];
        var sample = new byte[sampleByteCount];
        for (var sampleIndex = 0; sampleIndex < sampleStarts.Length; sampleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sampleOffset = sampleStarts[sampleIndex] * blockSize;
            if (sampleOffset > payloadSize - sampleByteCount ||
                payloadOffset > (ulong)long.MaxValue - (ulong)sampleOffset ||
                !await ReadAtAsync(
                    stream,
                    (long)(payloadOffset + (ulong)sampleOffset),
                    sample,
                    cancellationToken))
            {
                return false;
            }
            Buffer.BlockCopy(sample, 0, sampledBlocks, sampleIndex * sampleByteCount, sampleByteCount);
        }

        if (!ModelPreviewMaterialVariantSelector.IsBc7PureBlackPlaceholder(sampledBlocks))
            return false;

        // Some exports retain a sparse or empty highest-resolution mip while the
        // lower mip chain contains the actual albedo. Confirm with decoded pixels so
        // a real face/body texture is never removed based on compressed headers alone.
        var preview = await PreviewTextureCoreAsync(modDirectory, texture, 65_536, cancellationToken);
        return preview?.BgraPixels is { } pixels && ModelPreviewMaterialVariantSelector.IsOpaqueBgraPureBlack(pixels);
    }

    private static IReadOnlyList<ulong> ScanResourceIds(byte[] data, IReadOnlySet<ulong> ids)
    {
        if (data.Length < sizeof(ulong) || ids.Count == 0)
            return [];

        var matches = new HashSet<ulong>();
        for (var offset = 0; offset <= data.Length - sizeof(ulong); offset++)
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, sizeof(ulong)));
            if (ids.Contains(value))
                matches.Add(value);
        }

        return matches.ToArray();
    }

    private static async Task ReadUnitGpuStreamsAsync(
        Stream patchStream, Stream? gpuStream, string patchFile, int tocEntryIndex, ulong unitId,
        ulong mainOffset, uint mainSize, ulong gpuOffset, uint gpuSize, List<GpuStreamInspectionItem> output,
        ModelPreviewResult? modelPreview = null,
        ModelPreviewMaterialLayout? materialLayout = null,
        CancellationToken cancellationToken = default)
    {
        if (mainSize < 0x68)
            return;

        var unitHeader = new byte[0x68];
        if (!await ReadAtAsync(patchStream, (long)mainOffset, unitHeader, cancellationToken))
            return;

        var version = MemoryMarshal.Read<uint>(unitHeader.AsSpan(0x2C, 4));
        if (version is not (OriginalUnitVersion or LegacyVerifiedUnitVersion or CurrentVerifiedUnitVersion))
            return;

        var usesLegacyVertexFormats = version != CurrentVerifiedUnitVersion;

        var listOffset = MemoryMarshal.Read<int>(unitHeader.AsSpan(0x5C, 4));
        if (listOffset <= 0 || (long)listOffset + 4 > mainSize)
            return;

        var countBuffer = new byte[4];
        if (!await ReadAtAsync(patchStream, (long)mainOffset + listOffset, countBuffer, cancellationToken))
            return;

        var count = MemoryMarshal.Read<int>(countBuffer);
        if (count < 0 || count > 100 || (long)listOffset + 8L + count * 8L > mainSize)
            return;

        var offsets = new byte[count * 4];
        if (offsets.Length > 0 && !await ReadAtAsync(patchStream, (long)mainOffset + listOffset + 4, offsets, cancellationToken))
            return;

        var streamInfo = new byte[StreamInfoSize];
        for (var streamIndex = 0; streamIndex < count; streamIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativeOffset = MemoryMarshal.Read<int>(offsets.AsSpan(streamIndex * 4, 4));
            var streamStart = (long)listOffset + relativeOffset;
            if (relativeOffset < 0 || streamStart < 0 || streamStart + StreamInfoSize > mainSize ||
                !await ReadAtAsync(patchStream, (long)mainOffset + streamStart, streamInfo, cancellationToken))
                continue;

            var componentCount = MemoryMarshal.Read<ulong>(streamInfo.AsSpan(0x148, 8));
            if (componentCount > 16)
                continue;

            var components = new List<(uint Type, uint Format, int Offset)>();
            var componentText = modelPreview is null ? new StringBuilder() : null;
            var componentOffset = 0;
            var canSample = true;
            for (var componentIndex = 0; componentIndex < (int)componentCount; componentIndex++)
            {
                var baseOffset = 0x08 + componentIndex * 20;
                var type = MemoryMarshal.Read<uint>(streamInfo.AsSpan(baseOffset, 4));
                var format = MemoryMarshal.Read<uint>(streamInfo.AsSpan(baseOffset + 4, 4));
                if (componentText is not null)
                {
                    if (componentText.Length > 0)
                        componentText.Append(" | ");
                    componentText.Append(GetComponentName(type)).Append('[').Append(componentIndex).Append("]: ").Append(GetFormatName(format));
                }
                if (!TryGetFormatSize(format, usesLegacyVertexFormats, out var size))
                {
                    // Some base-game streams keep an optional semantic (most commonly
                    // BoneWeight) with format 0 and no bytes in the interleaved vertex.
                    // It must not make otherwise decodable position data disappear.
                    if (format != 0)
                        canSample = false;
                    continue;
                }
                components.Add((type, format, componentOffset));
                componentOffset += size;
            }

            var vertexCount = MemoryMarshal.Read<uint>(streamInfo.AsSpan(0x160, 4));
            var vertexStride = MemoryMarshal.Read<uint>(streamInfo.AsSpan(0x164, 4));
            var indexCount = MemoryMarshal.Read<uint>(streamInfo.AsSpan(0x188, 4));
            var indexType = MemoryMarshal.Read<uint>(streamInfo.AsSpan(0x18C, 4));
            var vertexOffset = MemoryMarshal.Read<uint>(streamInfo.AsSpan(0x1A0, 4));
            var vertexSize = MemoryMarshal.Read<uint>(streamInfo.AsSpan(0x1A4, 4));
            var indexOffset = MemoryMarshal.Read<uint>(streamInfo.AsSpan(0x1A8, 4));
            var indexSize = MemoryMarshal.Read<uint>(streamInfo.AsSpan(0x1AC, 4));

            if (modelPreview is null)
            {
                var vertexSample = await ReadVertexSampleAsync(
                    gpuStream, gpuOffset, gpuSize, vertexOffset, vertexCount, vertexStride, components, canSample, cancellationToken);
                output.Add(new GpuStreamInspectionItem
                {
                    PatchFile = patchFile,
                    TocEntryIndex = tocEntryIndex,
                    UnitId = unitId,
                    UnitVersion = version,
                    StreamIndex = streamIndex,
                    VertexCount = vertexCount,
                    VertexStride = vertexStride,
                    IndexCount = indexCount,
                    IndexFormat = indexType switch { 0 => "uint16", 1 => "uint32", _ => $"unknown ({indexType})" },
                    Components = componentText!.Length == 0 ? "-" : componentText.ToString(),
                    VertexBuffer = $"0x{vertexOffset:X} + {vertexSize:N0}",
                    IndexBuffer = $"0x{indexOffset:X} + {indexSize:N0}",
                    VertexSample = vertexSample
                });
            }

            if (modelPreview is not null && vertexCount > 0 && indexCount > 0)
            {
                IReadOnlyList<ModelPreviewMaterialSection> configuredSections = [];
                var hasConfiguredSections = false;
                if (materialLayout is not null &&
                    materialLayout.SectionsByStream.TryGetValue(streamIndex, out var sectionsForStream) &&
                    sectionsForStream is not null)
                {
                    configuredSections = sectionsForStream;
                    hasConfiguredSections = true;
                }
                if (hasConfiguredSections && configuredSections.Count == 0)
                    continue;

                if (modelPreview.IsAtCapacity)
                {
                    modelPreview.SkippedStreams++;
                    continue;
                }

                var sections = hasConfiguredSections
                    ? configuredSections
                    : [];
                if (sections.Count > 0)
                {
                    var addedSection = false;
                    foreach (var section in sections)
                    {
                        var sectionMesh = await ReadModelSectionMeshAsync(
                            gpuStream, gpuOffset, gpuSize, patchFile, unitId, streamIndex,
                            vertexCount, vertexStride, indexCount, indexType, vertexOffset, vertexSize,
                            indexOffset, indexSize, components, canSample,
                            materialLayout?.BodyShape ?? ModelPreviewBodyShape.Unknown,
                            materialLayout?.CustomizationSlot ?? ModelPreviewCustomizationSlot.Unknown,
                            materialLayout?.FallbackTextureIds ?? [], materialLayout?.FallbackColorTextureId,
                            materialLayout?.Rig, section, cancellationToken);
                        if (sectionMesh is null)
                            continue;

                        addedSection = true;
                        if (!modelPreview.TryAddMesh(sectionMesh))
                            modelPreview.SkippedStreams++;
                    }

                    if (!addedSection)
                        modelPreview.SkippedStreams++;
                    continue;
                }

                var mesh = await ReadModelMeshAsync(
                    gpuStream, gpuOffset, gpuSize, patchFile, unitId, streamIndex,
                    vertexCount, vertexStride, indexCount, indexType, vertexOffset, vertexSize,
                    indexOffset, indexSize, components, canSample,
                    materialLayout?.BodyShape ?? ModelPreviewBodyShape.Unknown,
                    materialLayout?.CustomizationSlot ?? ModelPreviewCustomizationSlot.Unknown,
                    materialLayout?.FallbackTextureIds ?? [], materialLayout?.FallbackColorTextureId, cancellationToken);
                if (mesh is null)
                {
                    modelPreview.SkippedStreams++;
                    continue;
                }

                if (!modelPreview.TryAddMesh(mesh))
                    modelPreview.SkippedStreams++;
            }
        }
    }

    /// <summary>
    /// Decodes one MeshInfo section without materializing the complete backing stream.
    /// Large character assets commonly pack several LOD windows into one GPU stream;
    /// the SDK seeks directly to each Section's vertex and index windows.
    /// </summary>
    private static async Task<ModelPreviewMesh?> ReadModelSectionMeshAsync(
        Stream? gpuStream,
        ulong gpuBaseOffset,
        uint gpuSize,
        string patchFile,
        ulong unitId,
        int streamIndex,
        uint streamVertexCount,
        uint vertexStride,
        uint streamIndexCount,
        uint indexType,
        uint vertexOffset,
        uint vertexSize,
        uint indexOffset,
        uint indexSize,
        IReadOnlyList<(uint Type, uint Format, int Offset)> components,
        bool canDecode,
        ModelPreviewBodyShape bodyShape,
        ModelPreviewCustomizationSlot customizationSlot,
        IReadOnlyList<ulong> fallbackTextureIds,
        ulong? fallbackColorTextureId,
        ModelPreviewUnitRig? rig,
        ModelPreviewMaterialSection section,
        CancellationToken cancellationToken)
    {
        if (!canDecode || gpuStream is null || vertexStride == 0 || vertexStride > 4096 || indexType is not (0 or 1) ||
            section.VertexOffset > streamVertexCount || section.VertexCount == 0 ||
            section.VertexCount > streamVertexCount - section.VertexOffset ||
            section.IndexOffset > streamIndexCount || section.IndexCount < 3 ||
            section.IndexCount > streamIndexCount - section.IndexOffset)
            return null;

        var position = components.FirstOrDefault(static component => component.Type == 0 && component.Format == 2);
        if (position.Format != 2)
            return null;

        var uv = components.FirstOrDefault(static component => component.Type == 4 && component.Format is 1 or 29 or 33);
        var boneIndex = components.FirstOrDefault(static component => component.Type == 6 && component.Format is 24 or 28);
        var boneWeight = components.FirstOrDefault(static component => component.Type == 7 && component.Format is 0 or 4 or 31 or 35);
        var palette = rig is not null && section.LodIndex >= 0 && section.LodIndex < rig.Palettes.Count
            ? rig.Palettes[section.LodIndex]
            : null;
        var canDecodeSkinning = palette is not null && boneIndex.Type == 6 && boneWeight.Type == 7;
        var indexElementSize = indexType == 0 ? 2u : 4u;
        var triangleIndexCount = section.IndexCount - section.IndexCount % 3;
        if (triangleIndexCount > MaxPreviewIndicesPerStream)
            return null;

        var indexByteOffset = (long)section.IndexOffset * indexElementSize;
        var indexByteCount = (long)triangleIndexCount * indexElementSize;
        if (indexByteCount <= 0 || indexByteCount > int.MaxValue ||
            indexByteOffset > indexSize || indexByteCount > indexSize - indexByteOffset ||
            indexOffset > (ulong)long.MaxValue - (ulong)indexByteOffset ||
            !IsRangeInBounds(indexOffset + (ulong)indexByteOffset, (uint)indexByteCount, gpuSize) ||
            gpuBaseOffset > (ulong)long.MaxValue - indexOffset - (ulong)indexByteOffset)
            return null;

        var indexBytes = new byte[(int)indexByteCount];
        if (!await ReadAtAsync(gpuStream, (long)(gpuBaseOffset + indexOffset + (ulong)indexByteOffset), indexBytes, cancellationToken))
            return null;

        var rawIndices = new uint[triangleIndexCount];
        var minimumIndex = uint.MaxValue;
        var maximumIndex = 0u;
        for (var index = 0; index < rawIndices.Length; index++)
        {
            if ((index & 0x3FFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var byteOffset = index * (int)indexElementSize;
            var value = indexType == 0
                ? MemoryMarshal.Read<ushort>(indexBytes.AsSpan(byteOffset, 2))
                : MemoryMarshal.Read<uint>(indexBytes.AsSpan(byteOffset, 4));
            rawIndices[index] = value;
            minimumIndex = Math.Min(minimumIndex, value);
            maximumIndex = Math.Max(maximumIndex, value);
        }

        // The common encoding is section-local, as documented by HD2SDK. Some older
        // exports retain stream-relative values, so recognize that unambiguously and
        // rebase only that window instead of discarding otherwise valid geometry.
        var indicesAreStreamRelative = minimumIndex >= section.VertexOffset &&
                                       maximumIndex - section.VertexOffset < section.VertexCount;
        var requiredVertexCount = indicesAreStreamRelative
            ? maximumIndex - section.VertexOffset + 1
            : maximumIndex + 1;
        if (requiredVertexCount == 0 || requiredVertexCount > MaxPreviewVerticesPerStream ||
            requiredVertexCount > streamVertexCount - section.VertexOffset)
            return null;

        var vertexByteOffset = (long)section.VertexOffset * vertexStride;
        var vertexByteCount = (long)requiredVertexCount * vertexStride;
        if (vertexByteCount <= 0 || vertexByteCount > MaxPreviewVertexBytes || vertexByteCount > int.MaxValue ||
            vertexByteOffset > vertexSize || vertexByteCount > vertexSize - vertexByteOffset ||
            vertexOffset > (ulong)long.MaxValue - (ulong)vertexByteOffset ||
            !IsRangeInBounds(vertexOffset + (ulong)vertexByteOffset, (uint)vertexByteCount, gpuSize) ||
            gpuBaseOffset > (ulong)long.MaxValue - vertexOffset - (ulong)vertexByteOffset)
            return null;

        var vertexBytes = new byte[(int)vertexByteCount];
        if (!await ReadAtAsync(gpuStream, (long)(gpuBaseOffset + vertexOffset + (ulong)vertexByteOffset), vertexBytes, cancellationToken))
            return null;

        var positions = new float[checked((int)requiredVertexCount * 3)];
        var textureCoordinates = uv.Format is 1 or 29 or 33
            ? new float[checked((int)requiredVertexCount * 2)]
            : null;
        var transformIndices = canDecodeSkinning
            ? new int[checked((int)requiredVertexCount * ModelPreviewSkinningData.InfluencesPerVertex)]
            : null;
        var weights = canDecodeSkinning
            ? new float[checked((int)requiredVertexCount * ModelPreviewSkinningData.InfluencesPerVertex)]
            : null;
        if (transformIndices is not null)
            Array.Fill(transformIndices, -1);
        var skinnedVertexCount = 0;
        var decodedWeights = canDecodeSkinning
            ? new float[ModelPreviewSkinningData.InfluencesPerVertex]
            : null;
        for (var vertexIndex = 0; vertexIndex < requiredVertexCount; vertexIndex++)
        {
            if ((vertexIndex & 0x3FFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var sourceOffset = checked((int)vertexIndex * (int)vertexStride + position.Offset);
            var targetOffset = (int)vertexIndex * 3;
            var transformed = section.Transform.TransformPoint(
                BitConverter.ToSingle(vertexBytes, sourceOffset),
                BitConverter.ToSingle(vertexBytes, sourceOffset + 4),
                BitConverter.ToSingle(vertexBytes, sourceOffset + 8));
            if (!float.IsFinite(transformed.X) || !float.IsFinite(transformed.Y) || !float.IsFinite(transformed.Z))
                return null;
            positions[targetOffset] = transformed.X;
            positions[targetOffset + 1] = transformed.Y;
            positions[targetOffset + 2] = transformed.Z;

            if (textureCoordinates is not null)
            {
                var uvSourceOffset = checked((int)vertexIndex * (int)vertexStride + uv.Offset);
                var uvTargetOffset = (int)vertexIndex * 2;
                if (uv.Format == 1)
                {
                    textureCoordinates[uvTargetOffset] = BitConverter.ToSingle(vertexBytes, uvSourceOffset);
                    textureCoordinates[uvTargetOffset + 1] = BitConverter.ToSingle(vertexBytes, uvSourceOffset + 4);
                }
                else
                {
                    textureCoordinates[uvTargetOffset] = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(vertexBytes, uvSourceOffset));
                    textureCoordinates[uvTargetOffset + 1] = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(vertexBytes, uvSourceOffset + 2));
                }

                if (!float.IsFinite(textureCoordinates[uvTargetOffset]) || !float.IsFinite(textureCoordinates[uvTargetOffset + 1]))
                    textureCoordinates = null;
            }

            if (transformIndices is not null && weights is not null && palette is not null)
            {
                var vertexSourceOffset = checked((int)vertexIndex * (int)vertexStride);
                var influenceOffset = checked((int)vertexIndex * ModelPreviewSkinningData.InfluencesPerVertex);
                if (decodedWeights is null ||
                    !TryDecodeBoneWeights(vertexBytes, vertexSourceOffset + boneWeight.Offset, boneWeight.Format, decodedWeights))
                    continue;

                var weightTotal = 0f;
                for (var influence = 0; influence < ModelPreviewSkinningData.InfluencesPerVertex; influence++)
                {
                    var rawBoneIndex = vertexBytes[vertexSourceOffset + boneIndex.Offset + influence];
                    var weight = decodedWeights[influence];
                    if (weight <= 0 || !palette.TryResolve(section.MaterialIndex, rawBoneIndex, out var transformIndex))
                        continue;

                    transformIndices[influenceOffset + influence] = transformIndex;
                    weights[influenceOffset + influence] = weight;
                    weightTotal += weight;
                }

                if (weightTotal <= 0.00001f)
                    continue;

                for (var influence = 0; influence < ModelPreviewSkinningData.InfluencesPerVertex; influence++)
                    weights[influenceOffset + influence] /= weightTotal;
                skinnedVertexCount++;
            }
        }

        var triangleIndices = new int[rawIndices.Length];
        for (var index = 0; index < rawIndices.Length; index++)
        {
            var localIndex = indicesAreStreamRelative
                ? rawIndices[index] - section.VertexOffset
                : rawIndices[index];
            if (localIndex >= requiredVertexCount)
                return null;
            triangleIndices[index] = (int)localIndex;
        }

        // Section index windows often reference a sparse subset of a large shared
        // vertex range. Keep only referenced vertices before the global preview budget
        // is applied; otherwise a few accessory triangles can consume hundreds of
        // thousands of unused vertices and hide later character details.
        var compactGeometry = CompactReferencedSectionGeometry(
            positions,
            textureCoordinates,
            skinnedVertexCount > 0 ? transformIndices : null,
            skinnedVertexCount > 0 ? weights : null,
            triangleIndices);

        return new ModelPreviewMesh
        {
            PatchFile = patchFile,
            UnitId = unitId,
            StreamIndex = streamIndex,
            MeshInfoIndex = section.MeshInfoIndex,
            SourceVertexOffset = section.VertexOffset,
            SourceVertexCount = requiredVertexCount,
            SourceIndexOffset = section.IndexOffset,
            SourceIndexCount = triangleIndexCount,
            BodyShape = bodyShape,
            CustomizationSlot = customizationSlot,
            Positions = compactGeometry.Positions,
            Normals = BuildSmoothedNormals(compactGeometry.Positions, compactGeometry.TriangleIndices),
            TextureCoordinates = compactGeometry.TextureCoordinates,
            Skinning = compactGeometry.TransformIndices is not null && compactGeometry.Weights is not null && rig is not null
                ? new ModelPreviewSkinningData
                {
                    Skeleton = rig.Skeleton,
                    TransformIndices = compactGeometry.TransformIndices,
                    Weights = compactGeometry.Weights
                }
                : null,
            TriangleIndices = compactGeometry.TriangleIndices,
            TextureIds = section.TextureIds.Count > 0 ? section.TextureIds : fallbackTextureIds,
            ColorTextureId = section.ColorTextureId ?? fallbackColorTextureId,
            MaterialId = section.MaterialId,
            MaterialTextures = section.MaterialTextures ?? (fallbackTextureIds.Count == 0
                ? ModelPreviewMaterialTextureSet.Empty
                : new ModelPreviewMaterialTextureSet(
                    new Dictionary<ModelPreviewTextureRole, IReadOnlyList<ulong>>
                    {
                        [ModelPreviewTextureRole.Unknown] = fallbackTextureIds
                    },
                    fallbackTextureIds,
                    fallbackColorTextureId)),
            IsCullingBody = section.IsCullingBody
        };
    }

    private static CompactSectionGeometry CompactReferencedSectionGeometry(
        float[] positions,
        float[]? textureCoordinates,
        int[]? transformIndices,
        float[]? weights,
        int[] triangleIndices)
    {
        var sourceVertexCount = positions.Length / 3;
        var remap = new int[sourceVertexCount];
        Array.Fill(remap, -1);
        var compactPositions = new List<float>(Math.Min(positions.Length, triangleIndices.Length * 3));
        var compactCoordinates = textureCoordinates is { Length: > 0 }
            ? new List<float>(Math.Min(textureCoordinates.Length, triangleIndices.Length * 2))
            : null;
        var compactTransformIndices = transformIndices is { Length: > 0 }
            ? new List<int>(Math.Min(transformIndices.Length, triangleIndices.Length * ModelPreviewSkinningData.InfluencesPerVertex))
            : null;
        var compactWeights = weights is { Length: > 0 }
            ? new List<float>(Math.Min(weights.Length, triangleIndices.Length * ModelPreviewSkinningData.InfluencesPerVertex))
            : null;
        var compactIndices = new int[triangleIndices.Length];
        for (var index = 0; index < triangleIndices.Length; index++)
        {
            var sourceVertex = triangleIndices[index];
            if (sourceVertex < 0 || sourceVertex >= sourceVertexCount)
                throw new InvalidDataException("Section index is outside its decoded vertex range.");

            var targetVertex = remap[sourceVertex];
            if (targetVertex < 0)
            {
                targetVertex = compactPositions.Count / 3;
                remap[sourceVertex] = targetVertex;
                var positionOffset = sourceVertex * 3;
                compactPositions.Add(positions[positionOffset]);
                compactPositions.Add(positions[positionOffset + 1]);
                compactPositions.Add(positions[positionOffset + 2]);
                if (compactCoordinates is not null && textureCoordinates is { } sourceCoordinates)
                {
                    var coordinateOffset = sourceVertex * 2;
                    compactCoordinates.Add(sourceCoordinates[coordinateOffset]);
                    compactCoordinates.Add(sourceCoordinates[coordinateOffset + 1]);
                }
                if (compactTransformIndices is not null && compactWeights is not null &&
                    transformIndices is { } sourceTransformIndices && weights is { } sourceWeights)
                {
                    var influenceOffset = sourceVertex * ModelPreviewSkinningData.InfluencesPerVertex;
                    for (var influence = 0; influence < ModelPreviewSkinningData.InfluencesPerVertex; influence++)
                    {
                        compactTransformIndices.Add(sourceTransformIndices[influenceOffset + influence]);
                        compactWeights.Add(sourceWeights[influenceOffset + influence]);
                    }
                }
            }

            compactIndices[index] = targetVertex;
        }

        return new CompactSectionGeometry(
            compactPositions.ToArray(),
            compactCoordinates?.ToArray(),
            compactTransformIndices?.ToArray(),
            compactWeights?.ToArray(),
            compactIndices);
    }

    private static bool TryDecodeBoneWeights(
        byte[] vertexBytes,
        int offset,
        uint format,
        float[] weights)
    {
        Array.Clear(weights);
        switch (format)
        {
            case 0:
                var scalar = BitConverter.ToSingle(vertexBytes, offset);
                if (!float.IsFinite(scalar))
                    return false;
                weights[0] = Math.Clamp(scalar, 0f, 1f);
                return true;
            case 4:
                for (var index = 0; index < weights.Length; index++)
                    weights[index] = vertexBytes[offset + index] / 255f;
                return true;
            case 31:
            case 35:
                for (var index = 0; index < weights.Length; index++)
                {
                    var value = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(vertexBytes, offset + index * sizeof(ushort)));
                    if (!float.IsFinite(value))
                        return false;
                    weights[index] = Math.Clamp(value, 0f, 1f);
                }
                return true;
            default:
                return false;
        }
    }

    private static async Task<ModelPreviewMesh?> ReadModelMeshAsync(
        Stream? gpuStream,
        ulong gpuBaseOffset,
        uint gpuSize,
        string patchFile,
        ulong unitId,
        int streamIndex,
        uint vertexCount,
        uint vertexStride,
        uint indexCount,
        uint indexType,
        uint vertexOffset,
        uint vertexSize,
        uint indexOffset,
        uint indexSize,
        IReadOnlyList<(uint Type, uint Format, int Offset)> components,
        bool canDecode,
        ModelPreviewBodyShape bodyShape,
        ModelPreviewCustomizationSlot customizationSlot,
        IReadOnlyList<ulong> textureIds,
        ulong? colorTextureId,
        CancellationToken cancellationToken)
    {
        if (!canDecode || gpuStream is null ||
            vertexCount > MaxPreviewVerticesPerStream || indexCount > MaxPreviewIndicesPerStream ||
            vertexStride == 0 || vertexStride > 4096 ||
            indexType is not (0 or 1))
            return null;

        var position = components.FirstOrDefault(static component => component.Type == 0 && component.Format == 2);
        if (position.Format != 2)
            return null;

        var uv = components.FirstOrDefault(static component => component.Type == 4 && component.Format is 1 or 29 or 33);

        var indexElementSize = indexType == 0 ? 2u : 4u;
        var vertexByteCount = (long)vertexCount * vertexStride;
        var indexByteCount = (long)indexCount * indexElementSize;
        if (vertexByteCount <= 0 || vertexByteCount > MaxPreviewVertexBytes ||
            indexByteCount <= 0 || indexByteCount > int.MaxValue ||
            vertexByteCount > vertexSize || indexByteCount != indexSize ||
            !IsRangeInBounds(vertexOffset, (uint)vertexByteCount, gpuSize) ||
            !IsRangeInBounds(indexOffset, (uint)indexByteCount, gpuSize) ||
            gpuBaseOffset > (ulong)long.MaxValue - vertexOffset ||
            gpuBaseOffset > (ulong)long.MaxValue - indexOffset)
            return null;

        var vertexBytes = new byte[(int)vertexByteCount];
        var indexBytes = new byte[(int)indexByteCount];
        if (!await ReadAtAsync(gpuStream, (long)(gpuBaseOffset + vertexOffset), vertexBytes, cancellationToken) ||
            !await ReadAtAsync(gpuStream, (long)(gpuBaseOffset + indexOffset), indexBytes, cancellationToken))
            return null;

        var positions = new float[checked((int)vertexCount * 3)];
        var textureCoordinates = uv.Format is 1 or 29 or 33
            ? new float[checked((int)vertexCount * 2)]
            : null;
        for (var vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            if ((vertexIndex & 0x3FFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var sourceOffset = checked((int)vertexIndex * (int)vertexStride + position.Offset);
            var targetOffset = vertexIndex * 3;
            var x = BitConverter.ToSingle(vertexBytes, sourceOffset);
            var y = BitConverter.ToSingle(vertexBytes, sourceOffset + 4);
            var z = BitConverter.ToSingle(vertexBytes, sourceOffset + 8);
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                return null;
            positions[targetOffset] = x;
            positions[targetOffset + 1] = y;
            positions[targetOffset + 2] = z;

            if (textureCoordinates is not null)
            {
                var uvSourceOffset = checked((int)vertexIndex * (int)vertexStride + uv.Offset);
                var uvTargetOffset = vertexIndex * 2;
                if (uv.Format == 1)
                {
                    textureCoordinates[uvTargetOffset] = BitConverter.ToSingle(vertexBytes, uvSourceOffset);
                    textureCoordinates[uvTargetOffset + 1] = BitConverter.ToSingle(vertexBytes, uvSourceOffset + 4);
                }
                else
                {
                    textureCoordinates[uvTargetOffset] = (float)BitConverter.UInt16BitsToHalf(
                        BitConverter.ToUInt16(vertexBytes, uvSourceOffset));
                    textureCoordinates[uvTargetOffset + 1] = (float)BitConverter.UInt16BitsToHalf(
                        BitConverter.ToUInt16(vertexBytes, uvSourceOffset + 2));
                }

                if (!float.IsFinite(textureCoordinates[uvTargetOffset]) ||
                    !float.IsFinite(textureCoordinates[uvTargetOffset + 1]))
                    textureCoordinates = null;
            }
        }

        var triangleIndexCount = (int)(indexCount / 3) * 3;
        var triangleIndices = new int[triangleIndexCount];
        for (var index = 0; index < triangleIndexCount; index++)
        {
            if ((index & 0x3FFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var sourceOffset = index * (int)indexElementSize;
            var value = indexType == 0
                ? MemoryMarshal.Read<ushort>(indexBytes.AsSpan(sourceOffset, 2))
                : MemoryMarshal.Read<uint>(indexBytes.AsSpan(sourceOffset, 4));
            if (value >= vertexCount)
                return null;
            triangleIndices[index] = (int)value;
        }

        return triangleIndices.Length >= 3
            ? new ModelPreviewMesh
            {
                PatchFile = patchFile,
                UnitId = unitId,
                StreamIndex = streamIndex,
                BodyShape = bodyShape,
                CustomizationSlot = customizationSlot,
                Positions = positions,
                Normals = BuildSmoothedNormals(positions, triangleIndices),
                TextureCoordinates = textureCoordinates,
                TriangleIndices = triangleIndices,
                TextureIds = textureIds,
                ColorTextureId = colorTextureId,
                MaterialTextures = textureIds.Count == 0
                    ? ModelPreviewMaterialTextureSet.Empty
                    : new ModelPreviewMaterialTextureSet(
                        new Dictionary<ModelPreviewTextureRole, IReadOnlyList<ulong>>
                        {
                            [ModelPreviewTextureRole.Unknown] = textureIds
                        },
                        textureIds,
                        colorTextureId)
            }
            : null;
    }

    /// <summary>
    /// Keeps only one MeshInfo section and remaps its vertices. WPF otherwise includes
    /// unused vertices in the bounds calculation, which makes a small garment section
    /// inherit the camera bounds of the complete source stream.
    /// </summary>
    internal static ModelPreviewMesh? CreateSectionMesh(ModelPreviewMesh source, ModelPreviewMaterialSection section)
    {
        if (section.VertexOffset > int.MaxValue || section.VertexCount > int.MaxValue ||
            section.IndexOffset > int.MaxValue || section.IndexCount > int.MaxValue)
            return null;

        var vertexStart = (int)section.VertexOffset;
        var vertexCount = (int)section.VertexCount;
        var start = (int)section.IndexOffset;
        var count = (int)section.IndexCount;
        count -= count % 3;
        if (vertexStart < 0 || vertexCount <= 0 || vertexStart > source.VertexCount || vertexCount > source.VertexCount - vertexStart ||
            start < 0 || start > source.TriangleIndices.Length || count < 3 || count > source.TriangleIndices.Length - start)
            return null;

        var remap = new Dictionary<int, int>();
        var positions = new List<float>();
        var coordinates = source.TextureCoordinates is { Length: > 0 } ? new List<float>() : null;
        var indices = new int[count];
        for (var index = 0; index < count; index++)
        {
            var localVertex = source.TriangleIndices[start + index];
            if (localVertex < 0 || localVertex >= vertexCount)
                return null;

            var sourceVertex = vertexStart + localVertex;
            if (!remap.TryGetValue(sourceVertex, out var targetVertex))
            {
                targetVertex = remap.Count;
                remap.Add(sourceVertex, targetVertex);
                var positionOffset = sourceVertex * 3;
                var transformed = section.Transform.TransformPoint(
                    source.Positions[positionOffset],
                    source.Positions[positionOffset + 1],
                    source.Positions[positionOffset + 2]);
                if (!float.IsFinite(transformed.X) || !float.IsFinite(transformed.Y) || !float.IsFinite(transformed.Z))
                    return null;
                positions.Add(transformed.X);
                positions.Add(transformed.Y);
                positions.Add(transformed.Z);
                if (coordinates is not null && source.TextureCoordinates is { } sourceCoordinates)
                {
                    var coordinateOffset = sourceVertex * 2;
                    coordinates.Add(sourceCoordinates[coordinateOffset]);
                    coordinates.Add(sourceCoordinates[coordinateOffset + 1]);
                }
            }

            indices[index] = targetVertex;
        }

        var compactPositions = positions.ToArray();
        return new ModelPreviewMesh
        {
            PatchFile = source.PatchFile,
            UnitId = source.UnitId,
            StreamIndex = source.StreamIndex,
            MeshInfoIndex = section.MeshInfoIndex,
            SourceVertexOffset = section.VertexOffset,
            SourceVertexCount = section.VertexCount,
            SourceIndexOffset = section.IndexOffset,
            SourceIndexCount = section.IndexCount,
            BodyShape = source.BodyShape,
            CustomizationSlot = source.CustomizationSlot,
            Positions = compactPositions,
            Normals = BuildSmoothedNormals(compactPositions, indices),
            TextureCoordinates = coordinates?.ToArray(),
            TriangleIndices = indices,
            TextureIds = section.TextureIds.Count > 0 ? section.TextureIds : source.TextureIds,
            ColorTextureId = section.ColorTextureId ?? source.ColorTextureId,
            MaterialId = section.MaterialId,
            MaterialTextures = section.MaterialTextures ?? source.MaterialTextures,
            IsCullingBody = section.IsCullingBody
        };
    }

    internal static float[] BuildSmoothedNormals(float[] positions, int[] triangleIndices)
    {
        var normals = new float[positions.Length];
        for (var index = 0; index + 2 < triangleIndices.Length; index += 3)
        {
            var first = triangleIndices[index] * 3;
            var second = triangleIndices[index + 1] * 3;
            var third = triangleIndices[index + 2] * 3;
            var abX = positions[second] - positions[first];
            var abY = positions[second + 1] - positions[first + 1];
            var abZ = positions[second + 2] - positions[first + 2];
            var acX = positions[third] - positions[first];
            var acY = positions[third + 1] - positions[first + 1];
            var acZ = positions[third + 2] - positions[first + 2];
            var normalX = abY * acZ - abZ * acY;
            var normalY = abZ * acX - abX * acZ;
            var normalZ = abX * acY - abY * acX;
            if (!float.IsFinite(normalX) || !float.IsFinite(normalY) || !float.IsFinite(normalZ))
                continue;

            AddNormal(normals, first, normalX, normalY, normalZ);
            AddNormal(normals, second, normalX, normalY, normalZ);
            AddNormal(normals, third, normalX, normalY, normalZ);
        }

        for (var index = 0; index < normals.Length; index += 3)
        {
            var x = normals[index];
            var y = normals[index + 1];
            var z = normals[index + 2];
            var length = MathF.Sqrt(x * x + y * y + z * z);
            if (length > 0 && float.IsFinite(length))
            {
                normals[index] = x / length;
                normals[index + 1] = y / length;
                normals[index + 2] = z / length;
            }
            else
            {
                normals[index + 1] = 1;
            }
        }

        return normals;
    }

    private static void AddNormal(float[] normals, int offset, float x, float y, float z)
    {
        normals[offset] += x;
        normals[offset + 1] += y;
        normals[offset + 2] += z;
    }

    private static async Task<string> ReadVertexSampleAsync(
        Stream? gpuStream, ulong gpuBaseOffset, uint gpuSize, uint vertexOffset, uint vertexCount, uint vertexStride,
        IReadOnlyList<(uint Type, uint Format, int Offset)> components, bool canSample,
        CancellationToken cancellationToken)
    {
        var position = components.FirstOrDefault(static component => component.Type == 0 && component.Format == 2);
        if (!canSample || gpuStream is null || vertexCount == 0 || vertexStride is 0 or > 4096 || position.Format != 2 ||
            (ulong)vertexOffset + vertexStride > gpuSize || gpuBaseOffset > (ulong)long.MaxValue - vertexOffset)
            return "Not sampled";

        var vertex = new byte[(int)vertexStride];
        if (!await ReadAtAsync(gpuStream, (long)gpuBaseOffset + vertexOffset, vertex, cancellationToken))
            return "Unavailable";

        var x = BitConverter.ToSingle(vertex, position.Offset);
        var y = BitConverter.ToSingle(vertex, position.Offset + 4);
        var z = BitConverter.ToSingle(vertex, position.Offset + 8);
        return float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)
            ? $"Position ({x:F3}, {y:F3}, {z:F3})"
            : "Position is non-finite";
    }

    private static string GetComponentName(uint type) => type switch
    {
        0 => "Position", 1 => "Normal", 2 => "Tangent", 3 => "Bitangent", 4 => "UV",
        5 => "Color", 6 => "BoneIndex", 7 => "BoneWeight", _ => $"Type 0x{type:X}"
    };

    private static string GetFormatName(uint format) => format switch
    {
        0 => "float", 1 => "float2", 2 => "float3", 4 => "RGBA8", 20 => "uint32x4 (legacy)",
        24 => "uint8x4 (legacy)", 26 => "oct-normal (legacy)", 28 => "uint8x4", 29 => "half2 (legacy)",
        30 => "oct-normal", 31 => "half4 (legacy)", 33 => "half2", 35 => "half4", _ => $"Format 0x{format:X}"
    };

    private static bool TryGetFormatSize(uint format, bool usesLegacyVertexFormats, out int size)
    {
        size = usesLegacyVertexFormats
            ? format switch { 0 => 4, 1 => 8, 2 => 12, 4 or 24 or 25 or 26 or 29 => 4, 20 => 16, 31 => 8, _ => 0 }
            : format switch { 0 => 4, 1 => 8, 2 => 12, 4 or 28 or 30 or 33 => 4, 35 => 8, _ => 0 };
        return size != 0;
    }

    private static bool IsRangeInBounds(ulong offset, uint size, long length) =>
        length >= 0 && offset <= (ulong)length && size <= (ulong)length - offset;

    private static bool IsRangeInBounds(long offset, long size, int length) =>
        offset >= 0 && size >= 0 && offset <= length && size <= length - offset;

    private static int ReadInt32(byte[] data, int offset) =>
        MemoryMarshal.Read<int>(data.AsSpan(offset, sizeof(int)));

    private static uint ReadUInt32(byte[] data, int offset) =>
        MemoryMarshal.Read<uint>(data.AsSpan(offset, sizeof(uint)));

    private static ulong ReadUInt64(byte[] data, int offset) =>
        MemoryMarshal.Read<ulong>(data.AsSpan(offset, sizeof(ulong)));

    private static FileInfo[] GetAllPatchFiles(DirectoryInfo modDirectory) =>
        modDirectory.GetFiles("*", SearchOption.AllDirectories)
            .Where(static file => file.Name.Contains(".patch_", StringComparison.OrdinalIgnoreCase) &&
                                  !file.Name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase) &&
                                  !file.Name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase) &&
                                  !file.Name.Contains(".hd2mm-", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>
    /// A Mod directory can be created during the current process after the same
    /// DirectoryInfo instance has already cached a negative Exists result. Always refresh
    /// before previewing so newly imported mods do not require an application restart.
    /// </summary>
    internal static bool RefreshDirectoryExists(DirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        directory.Refresh();
        return directory.Exists;
    }

    private static FileStream OpenRead(FileInfo file) => new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess);

    private sealed record CompactSectionGeometry(
        float[] Positions,
        float[]? TextureCoordinates,
        int[]? TransformIndices,
        float[]? Weights,
        int[] TriangleIndices);

    private static async Task<bool> ReadAtAsync(
        Stream stream,
        long offset,
        byte[] buffer,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
            return false;
        stream.Seek(offset, SeekOrigin.Begin);
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (count == 0)
                return false;
            read += count;
        }
        return true;
    }
}
