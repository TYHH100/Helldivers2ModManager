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
                // 播放中不做额外延时：请求由 15ms 时间轴定时器节流，循环在无待处理
                // 帧时自然退出；逐帧按当前时钟采样，抖动只影响节奏不影响动作速度。
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

            // clip 尚未解码时不动画：此时时长为 0，任何采样都只等价于站立姿势。
            // 解码完成由 PreloadSelectedAnimationClipAsync 触发一次重建。
            if (!selectedAnimation.Option.IsClipReady)
                return;

            if (!ReferenceEquals(_animationFrameCacheChoice, selectedAnimation) ||
                _animationFrameCacheRenderGeneration != renderGeneration)
            {
                ClearAnimationFrameCache();
                _animationFrameCacheChoice = selectedAnimation;
                _animationFrameCacheRenderGeneration = renderGeneration;
            }

            var sample = GetAnimationFrameSample(
                (float)selectedAnimation.Option.CachedLengthSeconds,
                animationTimeSeconds);
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
                    AddAnimationFrameCacheEntry(sample.FrameIndex, updates);
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
        // 本方法运行在 Task.Run 内（后台线程），此处解析 clip 命中预热缓存；游戏库的
        // clip 只在后台解码，解析失败（null）时按“无动画”处理，保持静态姿势。
        if (selectedAnimation.Option.ResolveClip() is not { } clip)
            return [];

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
                    clip);
                transforms = animationBindings.GetOrAdd(
                        bindingKey,
                        static key => new ModelPreviewAnimationBinding(
                            key.Skeleton,
                            key.Library.BoneHashes,
                            key.Clip,
                            key.Library.SourceSkeleton))
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
        float timeSeconds)
    {
        if (durationSeconds <= 0)
            return new AnimationFrameSample(0, 0);

        // 帧索引空间覆盖整段 clip 的 1/60s 步进；缓存内存不在此处压缩 clip——
        // 那会把长动画压到每秒几个离散姿势。内存有界性由缓存插入时的淘汰保证。
        var frameCount = Math.Max(1, (int)Math.Ceiling(durationSeconds * AnimationFramesPerSecond));
        var normalizedTime = Math.Clamp(timeSeconds % durationSeconds, 0, durationSeconds);
        var frameIndex = Math.Min((int)(normalizedTime / durationSeconds * frameCount), frameCount - 1);
        return new AnimationFrameSample(frameIndex, frameIndex * durationSeconds / frameCount);
    }

    /// <summary>
    /// 缓存只服务拖动回放与循环回绕的帧复用；顺序播放每帧都是新时间点。写入超出字节
    /// 预算或条目上限时整体清空，保证缓存内存有界，而不限制可播放的动画长度。
    /// </summary>
    private void AddAnimationFrameCacheEntry(int frameIndex, AnimationGeometryUpdate[] updates)
    {
        var bytes = 0L;
        foreach (var update in updates)
            bytes += update.Positions.Count * sizeof(double) * 3 +
                     (update.Normals?.Count ?? 0) * sizeof(double) * 3;
        if (_animationFrameCache.Count >= MaxCachedAnimationFrames ||
            _animationFrameCacheBytes + bytes > MaxAnimationFrameCacheBytes)
        {
            ClearAnimationFrameCache();
        }

        _animationFrameCache[frameIndex] = updates;
        _animationFrameCacheBytes += bytes;
    }

    private void ClearAnimationFrameCache()
    {
        _animationFrameCache.Clear();
        _animationFrameCacheBytes = 0;
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
