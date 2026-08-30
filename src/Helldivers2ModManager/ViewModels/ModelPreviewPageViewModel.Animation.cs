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
    [RelayCommand]
    private void ToggleAnimationPlayback()
    {
        if (SelectedAnimation is null || SelectedAnimationDuration <= 0)
            return;

        if (IsAnimationPlaying)
        {
            StopAnimationPlayback();
            return;
        }

        IsAnimationPlaying = true;
        _isAnimationApplied = true;
        QueueAnimationFrame();
        _animationClock.Restart();
        _animationTimer.Start();
    }

    [RelayCommand]
    private void ResetAnimation()
    {
        StopAnimationPlayback();
        _isAnimationApplied = false;
        _suppressAnimationTimeApplication = true;
        try
        {
            AnimationTimeSeconds = 0;
        }
        finally
        {
            _suppressAnimationTimeApplication = false;
        }
        QueueRebuild(resetCamera: false);
    }

    private void AnimationTimerOnTick(object? sender, EventArgs e)
    {
        if (!IsAnimationPlaying || SelectedAnimationDuration <= 0 || Volatile.Read(ref _isDisposed) != 0)
            return;

        var elapsed = _animationClock.Elapsed.TotalSeconds;
        _animationClock.Restart();
        var next = AnimationTimeSeconds + elapsed;
        AnimationTimeSeconds = next >= SelectedAnimationDuration
            ? next % SelectedAnimationDuration
            : next;
    }

    private void StopAnimationPlayback()
    {
        _animationTimer.Stop();
        _animationClock.Reset();
        IsAnimationPlaying = false;
    }

    partial void OnUseAutomaticMaterialsChanged(bool value)
    {
        if (!value && SelectedTexture is not null)
            _ = LoadSelectedTextureAsync(SelectedTexture);
        else
            QueueRebuild();
    }

    partial void OnUseOriginalTextureResolutionChanged(bool value)
    {
        // 切换原始分辨率时，如果当前处于自动材质模式，需要重新加载所有自动匹配的多张贴图
        // （而不是只重新加载手动选中的单张贴图），因为多贴图模型的 BaseColor/Emissive
        // 等多张自动匹配图都要改用原分辨率解码，否则整个合成材质的清晰度不会提升。
        // 单张手动贴图预览仍然按旧逻辑重载以保留 SelectedTexturePreview 大图显示。
        SelectedTexturePreview = null;

        var mod = SelectedMod;
        if (mod is not null && UseAutomaticMaterials)
        {
            // 只清纹理预览缓存，不清几何/网格结果；重新加载自动匹配的多张贴图后重建模型组。
            Interlocked.Increment(ref _textureLoadGeneration);
            CancelActiveTextureLoad();
            _texturePreviews.Clear();
            _automaticTexturePreviewIds.Clear();
            _selectedOriginalTextureId = null;
            _selectedOriginalTexturePreview = null;
            _ = ReloadAutomaticTexturesAfterResolutionSwitchAsync(mod);
        }
        else if (SelectedTexture is not null)
        {
            _ = LoadSelectedTextureAsync(SelectedTexture);
            QueueRebuild();
        }
        else
        {
            QueueRebuild();
        }
    }

    private void QueueAnimationFrame()
    {
        if (Volatile.Read(ref _isDisposed) != 0 || ModelGroup is null)
            return;

        Interlocked.Exchange(ref _animationFrameRequested, 1);
        if (Interlocked.CompareExchange(ref _animationFrameWorkerRunning, 1, 0) == 0)
            _ = RunQueuedAnimationFramesAsync();
    }

    private async Task RunQueuedAnimationFramesAsync()
    {
        try
        {
            do
            {
                Interlocked.Exchange(ref _animationFrameRequested, 0);
                await ApplyAnimationFrameAsync(_pageLifetimeCancellation.Token);
                if (IsAnimationPlaying && Volatile.Read(ref _animationFrameRequested) != 0)
                    await Task.Delay(10, _pageLifetimeCancellation.Token);
            }
            while (Volatile.Read(ref _animationFrameRequested) != 0);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to update the model preview animation frame");
        }
        finally
        {
            Interlocked.Exchange(ref _animationFrameWorkerRunning, 0);
            if (Volatile.Read(ref _animationFrameRequested) != 0 &&
                Volatile.Read(ref _isDisposed) == 0 &&
                Interlocked.CompareExchange(ref _animationFrameWorkerRunning, 1, 0) == 0)
            {
                _ = RunQueuedAnimationFramesAsync();
            }
        }
    }

    private async Task ApplyAnimationFrameAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _isDisposed) != 0 ||
            !_isAnimationApplied ||
            SelectedAnimation is not { } selectedAnimation)
        {
            return;
        }

        await _rebuildGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _isDisposed) != 0 ||
                !_isAnimationApplied ||
                !ReferenceEquals(SelectedAnimation, selectedAnimation))
            {
                return;
            }

            var renderGeneration = _renderGeneration;
            var animationTimeSeconds = (float)AnimationTimeSeconds;
            var meshes = GetVisibleMeshes()
                .Where(mesh =>
                    _liveMeshGeometries.ContainsKey(mesh) &&
                    mesh.Skinning is { } skinning &&
                    ModelPreviewAnimationCompatibility.IsCompatible(
                        skinning.Skeleton,
                        selectedAnimation.Library))
                .ToArray();
            if (meshes.Length == 0)
                return;

            if (!ReferenceEquals(_animationFrameCacheChoice, selectedAnimation) ||
                _animationFrameCacheRenderGeneration != renderGeneration)
            {
                ClearAnimationFrameCache();
                _animationFrameCacheChoice = selectedAnimation;
                _animationFrameCacheRenderGeneration = renderGeneration;
            }

            var sample = GetAnimationFrameSample(
                selectedAnimation.Option.Clip.LengthSeconds,
                animationTimeSeconds,
                meshes);
            if (!_animationFrameCache.TryGetValue(sample.FrameIndex, out var updates))
            {
                updates = await Task.Run(
                    () => BuildAnimationGeometryUpdates(
                        meshes,
                        selectedAnimation,
                        sample.TimeSeconds,
                        _animationBindings,
                        _gpuSkinningService,
                        cancellationToken),
                    cancellationToken);
                if (renderGeneration == _renderGeneration &&
                    ReferenceEquals(_animationFrameCacheChoice, selectedAnimation))
                {
                    _animationFrameCache[sample.FrameIndex] = updates;
                }
            }
            if (Volatile.Read(ref _isDisposed) != 0 ||
                renderGeneration != _renderGeneration ||
                !ReferenceEquals(SelectedAnimation, selectedAnimation))
            {
                return;
            }

            foreach (var update in updates)
            {
                if (!_liveMeshGeometries.TryGetValue(update.Mesh, out var geometry))
                    continue;
                geometry.Positions = update.Positions;
                if (update.Normals is not null)
                    geometry.Normals = update.Normals;
            }
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    private static AnimationGeometryUpdate[] BuildAnimationGeometryUpdates(
        IReadOnlyList<ModelPreviewMesh> meshes,
        ModelPreviewAnimationChoice selectedAnimation,
        float animationTimeSeconds,
        ConcurrentDictionary<AnimationBindingCacheKey, ModelPreviewAnimationBinding> animationBindings,
        GpuSkinningService gpuSkinningService,
        CancellationToken cancellationToken)
    {
        var transformsBySkeleton = new Dictionary<ModelPreviewSkeleton, IReadOnlyList<System.Numerics.Matrix4x4>>();
        var updates = new AnimationGeometryUpdate[meshes.Count];
        for (var meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mesh = meshes[meshIndex];
            var skinning = mesh.Skinning!;
            if (!transformsBySkeleton.TryGetValue(skinning.Skeleton, out var transforms))
            {
                var bindingKey = new AnimationBindingCacheKey(
                    skinning.Skeleton,
                    selectedAnimation.Library,
                    selectedAnimation.Option.Clip);
                transforms = animationBindings.GetOrAdd(
                        bindingKey,
                        static key => new ModelPreviewAnimationBinding(
                            key.Skeleton,
                            key.Library.BoneHashes,
                            key.Clip,
                            key.Library.BonesId))
                    .SampleSkinningTransforms(animationTimeSeconds);
                transformsBySkeleton[skinning.Skeleton] = transforms;
            }

            var skinnedPositions = gpuSkinningService.TrySkinPositions(
                mesh,
                transforms,
                cancellationToken,
                out var gpuPositions)
                ? gpuPositions
                : ModelPreviewCpuSkinner.Skin(mesh, transforms, skinNormals: false).Positions;
            var positions = CreatePoint3DCollection(skinnedPositions);
            Vector3DCollection? normals = null;
            updates[meshIndex] = new AnimationGeometryUpdate(mesh, positions, normals);
        }
        return updates;
    }

    private static AnimationFrameSample GetAnimationFrameSample(
        float durationSeconds,
        float timeSeconds,
        IReadOnlyList<ModelPreviewMesh> meshes)
    {
        var bytesPerFrame = meshes.Sum(static mesh => (long)mesh.VertexCount * 3 * sizeof(double));
        var memoryBound = bytesPerFrame <= 0
            ? 1
            : (int)Math.Clamp(MaxAnimationFrameCacheBytes / bytesPerFrame, 1, MaxCachedAnimationFrames);
        var desiredFrames = durationSeconds > 0
            ? Math.Max(1, (int)Math.Ceiling(durationSeconds * AnimationFramesPerSecond))
            : 1;
        var frameCount = Math.Min(desiredFrames, memoryBound);
        var normalizedTime = durationSeconds > 0
            ? Math.Clamp(timeSeconds % durationSeconds, 0, durationSeconds)
            : 0;
        var frameIndex = durationSeconds > 0
            ? Math.Min((int)(normalizedTime / durationSeconds * frameCount), frameCount - 1)
            : 0;
        var sampleTime = durationSeconds > 0
            ? frameIndex * durationSeconds / frameCount
            : 0;
        return new AnimationFrameSample(frameIndex, sampleTime);
    }

    private void ClearAnimationFrameCache()
    {
        _animationFrameCache.Clear();
        _animationFrameCacheChoice = null;
        _animationFrameCacheRenderGeneration = -1;
    }

    private static Point3DCollection CreatePoint3DCollection(IReadOnlyList<float> values)
    {
        var collection = new Point3DCollection(values.Count / 3);
        for (var index = 0; index < values.Count; index += 3)
            collection.Add(new Point3D(values[index], values[index + 1], values[index + 2]));
        collection.Freeze();
        return collection;
    }

    private static Vector3DCollection CreateVector3DCollection(IReadOnlyList<float> values)
    {
        var collection = new Vector3DCollection(values.Count / 3);
        for (var index = 0; index < values.Count; index += 3)
            collection.Add(new Vector3D(values[index], values[index + 1], values[index + 2]));
        collection.Freeze();
        return collection;
    }
}
