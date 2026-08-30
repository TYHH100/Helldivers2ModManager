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
        var referencedIds = SelectAutomaticTextureIds(meshes, maxPreviewCount)
            .Where(textureMap.ContainsKey)
            .ToArray();

        // The gate is disposed explicitly after every decode task has finished. Task.WhenAll
        // returns early on cancellation while sibling tasks are still unwinding; disposing
        // the gate too early would make their Release() throw ObjectDisposedException and
        // leave an unobserved faulted task behind.
        var decodeGate = new SemaphoreSlim(concurrency, concurrency);
        var decodeTasks = referencedIds.Select(async textureId =>
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
                        (long)texture.Width * texture.Height);
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
                    (long)texture.Width * texture.Height);
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
    /// disappear, so the 3D preview deliberately renders the RGB channels as opaque.
    /// </summary>
    internal static ImageSource? CreateModelBitmapSource(
        TexturePreviewData? preview,
        bool useOriginalResolution = false)
    {
        if (preview is null)
            return null;

        if (preview.BgraPixels is not null)
        {
            var bitmap = BitmapSource.Create(
                preview.Width,
                preview.Height,
                96,
                96,
                PixelFormats.Bgr32,
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
        var opaque = new FormatConvertedBitmap(png, PixelFormats.Bgr32, null, 0);
        opaque.Freeze();
        return opaque;
    }

    internal static int? GetTextureDecodePixelWidth(int sourcePixelWidth, bool useOriginalResolution) =>
        useOriginalResolution ? null : Math.Min(Math.Max(sourcePixelWidth, 1), 2048);
}
