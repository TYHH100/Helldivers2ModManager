using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class ModelPreviewPageViewModel
{
    internal static IReadOnlyList<ulong> SelectAutomaticTextureIds(
        IReadOnlyList<ModelPreviewMesh> meshes,
        int maximumCount)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);

        var candidates = new List<ulong>();
        var priorities = new Dictionary<ulong, int>();

        static void AddCandidate(
            ICollection<ulong> target,
            IDictionary<ulong, int> priorities,
            ulong textureId,
            int priority)
        {
            if (textureId == 0)
                return;

            if (priorities.TryGetValue(textureId, out var existingPriority))
            {
                priorities[textureId] = Math.Min(existingPriority, priority);
                return;
            }

            priorities[textureId] = priority;
            target.Add(textureId);
        }

        foreach (var mesh in meshes)
        {
            var hasColorBinding = false;
            foreach (var textureId in mesh.MaterialTextures.Get(ModelPreviewTextureRole.BaseColor))
            {
                AddCandidate(candidates, priorities, textureId, priority: 0);
                hasColorBinding = true;
            }

            foreach (var textureId in mesh.MaterialTextures.Get(ModelPreviewTextureRole.Iridescence))
            {
                // 流光输入也是颜色贴图（AlbedoIridescence）：必须参与自动解码，
                // 否则流光材质会退回灰显或错拿其它输入当颜色。
                AddCandidate(candidates, priorities, textureId, priority: 0);
                hasColorBinding = true;
            }

            if (mesh.ColorTextureId is ulong colorTextureId)
            {
                AddCandidate(candidates, priorities, colorTextureId, priority: 0);
                hasColorBinding = true;
            }

            foreach (var textureId in mesh.MaterialTextures.Get(ModelPreviewTextureRole.Emissive))
                AddCandidate(candidates, priorities, textureId, priority: 1);

            // Older material layouts do not expose semantic bindings. Keep their
            // texture list as a bounded fallback, but do not preload known normal/mask maps.
            if (!hasColorBinding)
            {
                foreach (var textureId in mesh.TextureIds)
                    AddCandidate(candidates, priorities, textureId, priority: 2);
            }
        }

        return candidates
            .OrderBy(textureId => priorities[textureId])
            .Take(maximumCount)
            .ToArray();
    }

    private bool IsCurrentLoad(ModData mod, int generation) =>
        ReferenceEquals(mod, SelectedMod) && generation == _loadGeneration;

    private TextureInspectionItem? ChoosePreferredTexture()
    {
        var preferredColorTextureIds = Meshes
            .Select(static mesh => mesh.ColorTextureId)
            .Where(static textureId => textureId.HasValue)
            .Select(static textureId => textureId!.Value)
            .ToHashSet();
        var referencedTextureIds = Meshes
            .SelectMany(static mesh => mesh.MaterialTextures.AllTextureIds.Concat(mesh.TextureIds))
            .ToHashSet();
        return Textures
            .Where(texture => referencedTextureIds.Contains(texture.TextureId))
            .OrderBy(texture => preferredColorTextureIds.Contains(texture.TextureId) ? 0 :
                texture.PreviewRole == TexturePreviewRole.ColorCandidate ? 1 :
                texture.PreviewRole == TexturePreviewRole.Unknown ? 2 : 3)
            .ThenByDescending(static texture => (long)texture.Width * texture.Height)
            .FirstOrDefault()
            ?? Textures.FirstOrDefault();
    }

    private IReadOnlyList<ModelPreviewMesh> GetVisibleMeshes()
    {
        var bodyShapeMeshes = GetBodyShapeMeshes();
        var bodyShapeMeshSet = bodyShapeMeshes.ToHashSet();
        var selection = new ModelPreviewSelection(
            _selection.VisibleMeshes.Where(bodyShapeMeshSet.Contains).ToArray(),
            _selection.HiddenMeshCount);
        var selectedMesh = SelectedMesh is not null && bodyShapeMeshSet.Contains(SelectedMesh)
            ? SelectedMesh
            : null;
        return ModelPreviewMeshSelector.GetRenderMeshes(
            selection,
            bodyShapeMeshes,
            selectedMesh,
            IsolateSelectedMesh,
            ShowFilteredMeshes);
    }

    private IReadOnlyList<ModelPreviewMesh> GetBodyShapeMeshes()
    {
        var armorMeshes = GetArmorMeshes();
        var renderableArmorMeshes = _selection.VisibleMeshes.Where(armorMeshes.Contains).ToArray();
        return ModelPreviewBodyShapeSelection.Filter(armorMeshes, renderableArmorMeshes, ShowStockyBody);
    }

    private IReadOnlyList<ModelPreviewMesh> GetArmorMeshes() =>
        ModelPreviewBackend.FilterByArmor(Meshes, SelectedArmor?.Id);

    private async Task LoadAutomaticTexturePreviewsAsync(
        ModData mod,
        IReadOnlyList<ModelPreviewMesh> meshes,
        IReadOnlyList<TextureInspectionItem> textures,
        int loadGeneration,
        CancellationToken cancellationToken)
    {
        // 原始分辨率模式下，自动匹配的多张贴图也使用原分辨率解码，不再只对手动选中的单张贴图生效。
        // 原始分辨率上限（4K）远高于普通预览（1K），为避免同时解码 16 张大图把 LOH 撑爆，
        // 原始分辨率模式下降低自动并发解码上限，并减小单批次的最大数量。
        bool useOriginalResolution = UseOriginalTextureResolution;
        int maxPixelCount = useOriginalResolution
            ? ModelPreviewOriginalTexturePixels
            : ModelPreviewMaxTexturePixels;
        int maxPreviewCount = useOriginalResolution
            ? Math.Min(MaxAutomaticTexturePreviews, 8) // 4K × 8 张 ≈ 512 MiB 峰值 BGRA，保持可接受
            : MaxAutomaticTexturePreviews;
        int concurrency = useOriginalResolution ? 1 : 2;

        var textureMap = ModelPreviewTextureIndex.Create(textures);
        // WPF composes BaseColor and Emissive only. Normal and mask maps stay available
        // through the texture list, but pre-decoding them consumes memory without
        // affecting the rendered model.
        // 材质语义引用的贴图 ID 可能不在模组补丁里（原版资源）；这些 ID 不参与模组
        // 解码，改走游戏归档按需解析（见 LoadOriginalTexturePreviewsAsync）。
        var referencedIds = SelectAutomaticTextureIds(meshes, maxPreviewCount);
        var modTextureIds = referencedIds.Where(textureMap.ContainsKey).ToArray();

        // The gate is disposed explicitly after every decode task has finished. Task.WhenAll
        // returns early on cancellation while sibling tasks are still unwinding; disposing
        // the gate too early would make their Release() throw ObjectDisposedException and
        // leave an unobserved faulted task behind.
        var decodeGate = new SemaphoreSlim(concurrency, concurrency);
        var decodeTasks = modTextureIds.Select(async textureId =>
        {
            if (!textureMap.TryGetValue(textureId, out var texture))
                return null;

            cancellationToken.ThrowIfCancellationRequested();
            var cacheKey = CreateTextureCacheKey(texture, useOriginalResolution);
            if (_decodedTexturePreviews.TryGetValue(cacheKey, out var cached))
                return new LoadedTextureResult(textureId, texture, cached);

            try
            {
                await decodeGate.WaitAsync(cancellationToken);
                try
                {
                    var preview = await _inspectionService.PreviewTextureAsync(
                        mod.Directory, texture, maxPixelCount, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentLoad(mod, loadGeneration))
                        return null;

                    var bitmap = CreateModelBitmapSource(preview, useOriginalResolution);
                    if (bitmap is null || preview is null)
                        return null;

                    var role = ModelPreviewTextureAnalysis.Classify(preview);
                    var loaded = new LoadedTexturePreview(
                        bitmap,
                        role,
                        (long)texture.Width * texture.Height,
                        ModelPreviewTextureAnalysis.MeasureIridescenceStrength(preview));
                    return new LoadedTextureResult(textureId, texture, loaded);
                }
                finally
                {
                    decodeGate.Release();
                }
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                    throw;
                _logger.LogDebug(ex, "Texture {TextureId} could not be loaded for model preview", texture.TextureIdText);
                return null;
            }
        }).ToArray();

        try
        {
            foreach (var loaded in await Task.WhenAll(decodeTasks))
            {
                if (loaded is null)
                    continue;
                loaded.Texture.PreviewRole = loaded.Preview.Role;
                loaded.Texture.PreviewRoleText = GetTexturePreviewRoleText(loaded.Preview.Role);
                _texturePreviews[loaded.TextureId] = loaded.Preview;
                _automaticTexturePreviewIds.Add(loaded.TextureId);
                CacheDecodedTexture(CreateTextureCacheKey(loaded.Texture, useOriginalResolution), loaded.Preview);
            }
        }
        catch
        {
            // WhenAll returns early on cancellation, but sibling tasks may still be running.
            // Wait for all of them to settle before disposing the gate so a late Release()
            // cannot throw ObjectDisposedException and surface as an unobserved fault.
            try
            {
                await Task.WhenAll(decodeTasks);
            }
            catch
            {
                // The original exception is rethrown below; this wait only drains the tasks.
            }
            throw;
        }
        finally
        {
            decodeGate.Dispose();
        }

        // 模组材质引用、但模组未携带的贴图（原版资源）：按需从游戏归档解析。
        // 候选预算与模组贴图共享（SelectAutomaticTextureIds 已按优先级截断），
        // 游戏未配置或包内找不到时静默降级，不影响模组自身贴图的展示。
        var externalTextureIds = referencedIds
            .Where(textureId => !textureMap.ContainsKey(textureId))
            .ToArray();
        if (externalTextureIds.Length > 0)
        {
            await LoadOriginalTexturePreviewsAsync(
                mod,
                externalTextureIds,
                maxPixelCount,
                useOriginalResolution,
                loadGeneration,
                cancellationToken);
        }
    }

    private async Task LoadOriginalTexturePreviewsAsync(
        ModData mod,
        IReadOnlyList<ulong> externalTextureIds,
        int maxPixelCount,
        bool useOriginalResolution,
        int loadGeneration,
        CancellationToken cancellationToken)
    {
        // 解码缓存的键来自条目元数据（包名+偏移+尺寸），元数据可跨选项切换复用；
        // 复用元数据就能直接命中解码缓存，避免同一贴图反复读归档。
        var missingRecordIds = externalTextureIds
            .Where(textureId => !_vanillaTextureRecords.ContainsKey(textureId))
            .ToList();
        if (missingRecordIds.Count > 0)
        {
            var originals = await _previewBackend.ReadOriginalTexturesAsync(
                missingRecordIds,
                maxPixelCount,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentLoad(mod, loadGeneration))
                return;

            foreach (var (textureId, original) in originals)
            {
                var record = CreateGameTextureInspectionItem(textureId, original);
                _vanillaTextureRecords[textureId] = record;
                EnsureGameTextureListed(record);
                // 批次结果已带解码像素，直接入库；后面的逐张回退只服务
                // "元数据已复用但解码缓存未命中"（如分辨率切换）的少数情况。
                StoreLoadedGameTexture(record, originals, maxPixelCount, useOriginalResolution);
            }
        }

        foreach (var textureId in externalTextureIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_vanillaTextureRecords.TryGetValue(textureId, out var record))
                continue; // 游戏归档里找不到：按"无法解析"降级，材质回退到灰显/语义回退链
            EnsureGameTextureListed(record);
            if (_texturePreviews.ContainsKey(textureId) && _automaticTexturePreviewIds.Contains(textureId))
                continue;
            if (TryGetDecodedTexture(record, useOriginalResolution, out var cached))
            {
                _texturePreviews[textureId] = cached;
                _automaticTexturePreviewIds.Add(textureId);
                record.PreviewRole = cached.Role;
                record.PreviewRoleText = GetTexturePreviewRoleText(cached.Role);
                continue;
            }

            var single = await _previewBackend.ReadOriginalTexturesAsync(
                [textureId],
                maxPixelCount,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentLoad(mod, loadGeneration))
                return;

            StoreLoadedGameTexture(record, single, maxPixelCount, useOriginalResolution);
        }
    }

    private void StoreLoadedGameTexture(
        TextureInspectionItem record,
        IReadOnlyDictionary<ulong, GameUnitReferenceReader.GameOriginalTexture> originals,
        int maxPixelCount,
        bool useOriginalResolution)
    {
        if (!originals.TryGetValue(record.TextureId, out var original))
            return;
        var bitmap = CreateModelBitmapSource(original.Preview, useOriginalResolution);
        if (bitmap is null)
            return;

        var role = ModelPreviewTextureAnalysis.Classify(original.Preview);
        var loaded = new LoadedTexturePreview(
            bitmap,
            role,
            (long)original.Width * original.Height,
            ModelPreviewTextureAnalysis.MeasureIridescenceStrength(original.Preview));
        record.PreviewRole = role;
        record.PreviewRoleText = GetTexturePreviewRoleText(role);
        _texturePreviews[record.TextureId] = loaded;
        _automaticTexturePreviewIds.Add(record.TextureId);
        if (useOriginalResolution)
        {
            // 与模组贴图同策略：原始分辨率解码结果单独保留一份，避免大缓冲常驻自动缓存。
            _selectedOriginalTextureId = record.TextureId;
            _selectedOriginalTexturePreview = loaded;
        }
        else
        {
            CacheDecodedTexture(CreateTextureCacheKey(record, useOriginalResolution), loaded);
        }
    }

    private TextureInspectionItem CreateGameTextureInspectionItem(
        ulong textureId,
        GameUnitReferenceReader.GameOriginalTexture original)
    {
        var locator = original.Locator;
        return new TextureInspectionItem
        {
            PatchFile = _localizationService["ModelPreviewPage.GameTextureSource"],
            PatchPath = $"game://{locator.PackageName}",
            PatchOrder = 0,
            TocEntryIndex = locator.TocEntryIndex,
            TextureId = textureId,
            MainOffset = locator.MainOffset,
            MainSize = locator.MainSize,
            GpuOffset = locator.GpuOffset,
            GpuSize = locator.GpuSize,
            StreamOffset = locator.StreamOffset,
            StreamSize = locator.StreamSize,
            Width = original.Width,
            Height = original.Height,
            MipCount = original.MipCount,
            DxgiFormat = original.DxgiFormat,
            PayloadKind = "DDS",
            PayloadSource = "bundle",
            IsFromGameArchive = true,
            GamePackageBaseName = locator.PackageName
        };
    }

    private void EnsureGameTextureListed(TextureInspectionItem record)
    {
        foreach (var texture in Textures)
        {
            if (texture.IsFromGameArchive && texture.TextureId == record.TextureId)
                return;
        }

        Textures.Add(record);
    }

    private async Task LoadSelectedTextureAsync(TextureInspectionItem? texture)
    {
        SelectedTexturePreview = null;
        var mod = SelectedMod;
        if (texture is null || mod is null)
            return;

        var useOriginalResolution = UseOriginalTextureResolution;
        var textureGeneration = Volatile.Read(ref _textureLoadGeneration);
        var cancellation = BeginTextureLoad();
        var cancellationToken = cancellation.Token;
        try
        {
            if (!IsCurrentTextureRequest(texture, mod, textureGeneration, cancellation))
                return;

            if (ModelPreviewTextureResolutionState.IsCurrentOriginalPreview(
                    useOriginalResolution,
                    texture.TextureId,
                    _selectedOriginalTextureId) &&
                _selectedOriginalTexturePreview is not null)
            {
                SelectedTexturePreview = _selectedOriginalTexturePreview.Image;
                await RebuildModelGroupAsync();
                return;
            }

            if (!useOriginalResolution &&
                (_texturePreviews.TryGetValue(texture.TextureId, out var cachedPreview) ||
                 TryGetDecodedTexture(texture, useOriginalResolution, out cachedPreview)))
            {
                SelectedTexturePreview = cachedPreview.Image;
                StoreManualTexturePreview(texture.TextureId, cachedPreview);
                await RebuildModelGroupAsync();
                return;
            }

            // 游戏归档贴图没有补丁伴生文件，按定位表走归档有界读取；缓存语义与补丁贴图一致。
            if (texture.IsFromGameArchive)
            {
                var originals = await _previewBackend.ReadOriginalTexturesAsync(
                    [texture.TextureId],
                    useOriginalResolution ? ModelPreviewOriginalTexturePixels : ModelPreviewMaxTexturePixels,
                    cancellationToken);
                if (!IsCurrentTextureRequest(texture, mod, textureGeneration, cancellation) ||
                    useOriginalResolution != UseOriginalTextureResolution)
                    return;
                if (originals.TryGetValue(texture.TextureId, out var gameTexture) &&
                    CreateModelBitmapSource(gameTexture.Preview, useOriginalResolution) is { } gameBitmap)
                {
                    var gameRole = ModelPreviewTextureAnalysis.Classify(gameTexture.Preview);
                    texture.PreviewRole = gameRole;
                    texture.PreviewRoleText = GetTexturePreviewRoleText(gameRole);
                    SelectedTexturePreview = gameBitmap;
                    var gameLoaded = new LoadedTexturePreview(
                        gameBitmap,
                        gameRole,
                        (long)gameTexture.Width * gameTexture.Height,
                        ModelPreviewTextureAnalysis.MeasureIridescenceStrength(gameTexture.Preview));
                    if (useOriginalResolution)
                    {
                        _selectedOriginalTextureId = texture.TextureId;
                        _selectedOriginalTexturePreview = gameLoaded;
                    }
                    else
                    {
                        StoreManualTexturePreview(texture.TextureId, gameLoaded);
                        CacheDecodedTexture(CreateTextureCacheKey(texture, useOriginalResolution), gameLoaded);
                    }
                    await RebuildModelGroupAsync();
                }
                return;
            }

            var preview = await _inspectionService.PreviewTextureAsync(
                mod.Directory,
                texture,
                useOriginalResolution ? ModelPreviewOriginalTexturePixels : ModelPreviewMaxTexturePixels,
                cancellationToken);
            if (!IsCurrentTextureRequest(texture, mod, textureGeneration, cancellation) ||
                useOriginalResolution != UseOriginalTextureResolution)
                return;

            if (preview is null)
                return;

            var role = ModelPreviewTextureAnalysis.Classify(preview);
            var bitmap = CreateModelBitmapSource(preview, useOriginalResolution);
            // Alpha 强度必须在解码结果释放前统计：AlbedoIridescence 的 Alpha 承载流光强度。
            var iridescenceStrength = ModelPreviewTextureAnalysis.MeasureIridescenceStrength(preview);
            // The source-resolution decoder can have a 256 MiB managed BGRA buffer.
            // The frozen BitmapSource owns the pixel content needed by WPF; do not let
            // the async state machine retain the decoder result while rebuilding.
            preview = null;
            if (!IsCurrentTextureRequest(texture, mod, textureGeneration, cancellation))
                return;
            if (bitmap is not null)
            {
                texture.PreviewRole = role;
                texture.PreviewRoleText = GetTexturePreviewRoleText(role);
                SelectedTexturePreview = bitmap;
                var loaded = new LoadedTexturePreview(
                    bitmap,
                    role,
                    (long)texture.Width * texture.Height,
                    iridescenceStrength);
                if (useOriginalResolution)
                {
                    // A source mip may be very large. Retain exactly one separately
                    // from the bounded normal-resolution automatic preview cache.
                    _selectedOriginalTextureId = texture.TextureId;
                    _selectedOriginalTexturePreview = loaded;
                }
                else
                {
                    StoreManualTexturePreview(texture.TextureId, loaded);
                    CacheDecodedTexture(CreateTextureCacheKey(texture, useOriginalResolution), loaded);
                }
                await RebuildModelGroupAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The model, selected texture, or page lifetime moved on while decoding.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Texture {TextureId} could not be loaded for manual model preview", texture.TextureIdText);
        }
        finally
        {
            Interlocked.CompareExchange(ref _textureLoadCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Model albedo inputs often pack unrelated data into alpha. WPF's ImageBrush would
    /// otherwise interpret that channel as opacity and make an otherwise valid model
    /// disappear, so packed or uniform alpha keeps rendering as opaque RGB. A near-binary
    /// alpha distribution (hair, veils, cutout geometry) is a real opacity mask and is
    /// preserved so transparent parts no longer render as solid panels.
    /// </summary>
    internal static ImageSource? CreateModelBitmapSource(
        TexturePreviewData? preview,
        bool useOriginalResolution = false)
    {
        if (preview is null)
            return null;

        if (preview.BgraPixels is not null)
        {
            // 遮罩样 Alpha 才保留透明通道；打包数据/全值 Alpha 仍按不透明 RGB 渲染。
            var hasOpacityMask = ModelPreviewTextureAnalysis.IsOpacityMask(preview.BgraPixels);
            var bitmap = BitmapSource.Create(
                preview.Width,
                preview.Height,
                96,
                96,
                hasOpacityMask ? PixelFormats.Bgra32 : PixelFormats.Bgr32,
                null,
                preview.BgraPixels,
                preview.Width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        if (preview.EncodedImageBytes is null)
            return null;

        using var stream = new MemoryStream(preview.EncodedImageBytes, writable: false);
        var png = new BitmapImage();
        png.BeginInit();
        png.CacheOption = BitmapCacheOption.OnLoad;
        if (GetTextureDecodePixelWidth(preview.Width, useOriginalResolution) is int decodePixelWidth)
            png.DecodePixelWidth = decodePixelWidth;
        png.StreamSource = stream;
        png.EndInit();
        png.Freeze();
        var pixels = new byte[png.PixelWidth * png.PixelHeight * 4];
        // 先统一转换到 Bgra32 再按 Alpha 分布决定最终格式；PNG 是内嵌图标/贴图的少见路径，
        // 解码像素宽度已被限制在 2048 内，缓冲规模可控。
        new FormatConvertedBitmap(png, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, png.PixelWidth * 4, 0);
        var pngHasOpacityMask = ModelPreviewTextureAnalysis.IsOpacityMask(pixels);
        var opaque = BitmapSource.Create(
            png.PixelWidth,
            png.PixelHeight,
            96,
            96,
            pngHasOpacityMask ? PixelFormats.Bgra32 : PixelFormats.Bgr32,
            null,
            pixels,
            png.PixelWidth * 4);
        opaque.Freeze();
        return opaque;
    }

    internal static int? GetTextureDecodePixelWidth(int sourcePixelWidth, bool useOriginalResolution) =>
        useOriginalResolution ? null : Math.Min(Math.Max(sourcePixelWidth, 1), 2048);
}
