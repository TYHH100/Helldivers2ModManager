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
    private void QueueRebuild(bool resetCamera = true)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
            return;

        if (resetCamera)
            Interlocked.Exchange(ref _cameraResetRequested, 1);
        if (Interlocked.Exchange(ref _rebuildRequested, 1) == 0)
            _ = RunQueuedRebuildsAsync();
    }

    private async Task RunQueuedRebuildsAsync()
    {
        try
        {
            do
            {
                Interlocked.Exchange(ref _rebuildRequested, 0);
                var resetCamera = Interlocked.Exchange(ref _cameraResetRequested, 0) != 0;
                await RebuildModelGroupAsync(resetCamera: resetCamera);
            }
            while (Volatile.Read(ref _rebuildRequested) != 0);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RebuildModelGroupAsync(
        CancellationToken cancellationToken = default,
        bool resetCamera = true)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
            return;

        var renderGeneration = Interlocked.Increment(ref _renderGeneration);
        ClearAnimationFrameCache();
        var meshes = GetVisibleMeshes().ToArray();
        if (meshes.Length == 0)
        {
            _liveMeshGeometries.Clear();
            ModelGroup = null;
            SuggestedCameraDistance = 5;
            SuggestedCameraYaw = 0;
            if (resetCamera)
                CameraResetVersion++;
            return;
        }

        var previews = ModelPreviewTextureResolutionState.GetMaterialPreviews(
            _texturePreviews,
            UseOriginalTextureResolution,
            SelectedTexture?.TextureId,
            _selectedOriginalTextureId,
            _selectedOriginalTexturePreview);
        var useAutomaticMaterials = UseAutomaticMaterials;
        var selectedTextureId = SelectedTexturePreview is not null ? SelectedTexture?.TextureId : null;
        var selectedAnimation = _isAnimationApplied ? SelectedAnimation : null;
        var animationTimeSeconds = (float)AnimationTimeSeconds;
        try
        {
            await _rebuildGate.WaitAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            // The page is being disposed and has closed the rebuild gate; drop the request.
            return;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _isDisposed) != 0)
                return;
            var build = await Task.Run(
                () => BuildModelGroup(
                    meshes,
                    previews,
                    useAutomaticMaterials,
                    selectedTextureId,
                     selectedAnimation,
                     animationTimeSeconds,
                     _animationBindings,
                     _geometryCache,
                     _gpuSkinningService,
                     cancellationToken),
                cancellationToken);
            if (Volatile.Read(ref _isDisposed) != 0 || renderGeneration != _renderGeneration)
                return;

            ModelGroup = CreateLiveModelGroup(build.Group, meshes, _liveMeshGeometries);
            if (resetCamera)
            {
                if (selectedAnimation is null)
                {
                    SuggestedCameraDistance = Math.Max(build.Radius * 3.0, 1.0);
                    SuggestedCameraYaw = build.FrontYaw;
                }
                CameraResetVersion++;
            }
        }
        finally
        {
            try
            {
                _rebuildGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Page disposal may close the gate while a rebuild still holds it.
            }
        }
    }

    private static ModelPreviewBuildResult BuildModelGroup(
        IReadOnlyList<ModelPreviewMesh> meshes,
        IReadOnlyDictionary<ulong, LoadedTexturePreview> texturePreviews,
        bool useAutomaticMaterials,
        ulong? selectedTextureId,
        ModelPreviewAnimationChoice? selectedAnimation,
        float animationTimeSeconds,
        ConcurrentDictionary<AnimationBindingCacheKey, ModelPreviewAnimationBinding> animationBindings,
        ConditionalWeakTable<ModelPreviewMesh, CachedMeshGeometry> geometryCache,
        GpuSkinningService gpuSkinningService,
        CancellationToken cancellationToken)
    {
        var group = new Model3DGroup();
        var materials = new Dictionary<string, Material>(StringComparer.Ordinal);

        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;
        var skinningTransforms = new Dictionary<ModelPreviewSkeleton, IReadOnlyList<System.Numerics.Matrix4x4>>();

        foreach (var source in meshes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CachedMeshGeometry cachedGeometry;
            if (selectedAnimation is not null && source.Skinning is { } skinning &&
                ModelPreviewAnimationCompatibility.IsCompatible(
                    skinning.Skeleton,
                    selectedAnimation.Library))
            {
                if (!skinningTransforms.TryGetValue(skinning.Skeleton, out var transforms))
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
                    skinningTransforms[skinning.Skeleton] = transforms;
                }

                var animatedPositions = gpuSkinningService.TrySkinPositions(
                    source,
                    transforms,
                    cancellationToken,
                    out var gpuPositions)
                    ? gpuPositions
                    : ModelPreviewCpuSkinner.Skin(source, transforms, skinNormals: false).Positions;
                cachedGeometry = CreateCachedMeshGeometry(
                    source,
                    animatedPositions,
                    source.Normals);
            }
            else
            {
                cachedGeometry = geometryCache.GetValue(source, static mesh => CreateCachedMeshGeometry(mesh));
            }
            var geometry = cachedGeometry.Geometry;
            minX = Math.Min(minX, cachedGeometry.MinX);
            minY = Math.Min(minY, cachedGeometry.MinY);
            minZ = Math.Min(minZ, cachedGeometry.MinZ);
            maxX = Math.Max(maxX, cachedGeometry.MaxX);
            maxY = Math.Max(maxY, cachedGeometry.MaxY);
            maxZ = Math.Max(maxZ, cachedGeometry.MaxZ);
            var materialKey = CreateMaterialKey(source, useAutomaticMaterials, selectedTextureId, texturePreviews);
            if (!materials.TryGetValue(materialKey, out var material))
            {
                material = CreateMaterial(source, texturePreviews, useAutomaticMaterials, selectedTextureId);
                materials[materialKey] = material;
            }

            var model = new GeometryModel3D(geometry, material) { BackMaterial = material };
            model.Freeze();
            group.Children.Add(model);
        }

        var center = new Vector3D(
            (minX + maxX) / 2,
            (minY + maxY) / 2,
            (minZ + maxZ) / 2);
        var radius = Math.Max(
            Math.Sqrt(
                Math.Pow(maxX - minX, 2) +
                Math.Pow(maxY - minY, 2) +
                Math.Pow(maxZ - minZ, 2)) / 2,
            0.5);
        var presentationRotation = ModelPreviewCharacterOrientation.GetRequiredRotation(meshes);
        group.Transform = CreatePresentationTransform(center, presentationRotation);
        group.Transform.Freeze();
        group.Freeze();
        return new ModelPreviewBuildResult(
            group,
            radius,
            ModelPreviewCharacterOrientation.GetSuggestedFrontYaw(presentationRotation));
    }

    private static Model3DGroup CreateLiveModelGroup(
        Model3DGroup frozenGroup,
        IReadOnlyList<ModelPreviewMesh> meshes,
        Dictionary<ModelPreviewMesh, MeshGeometry3D> liveGeometries)
    {
        liveGeometries.Clear();
        var group = new Model3DGroup { Transform = frozenGroup.Transform };
        for (var index = 0; index < frozenGroup.Children.Count; index++)
        {
            if (index >= meshes.Count ||
                frozenGroup.Children[index] is not GeometryModel3D sourceModel ||
                sourceModel.Geometry is not MeshGeometry3D sourceGeometry)
            {
                group.Children.Add(frozenGroup.Children[index]);
                continue;
            }

            var liveGeometry = new MeshGeometry3D
            {
                Positions = sourceGeometry.Positions,
                Normals = sourceGeometry.Normals,
                TextureCoordinates = sourceGeometry.TextureCoordinates,
                TriangleIndices = sourceGeometry.TriangleIndices
            };
            var liveModel = new GeometryModel3D(liveGeometry, sourceModel.Material)
            {
                BackMaterial = sourceModel.BackMaterial,
                Transform = sourceModel.Transform
            };
            group.Children.Add(liveModel);
            liveGeometries[meshes[index]] = liveGeometry;
        }
        return group;
    }

    internal static Transform3D CreatePresentationTransform(
        Vector3D center,
        ModelPreviewPresentationRotation rotation)
    {
        if (rotation == ModelPreviewPresentationRotation.None)
            return new TranslateTransform3D(-center.X, -center.Y, -center.Z);

        var (axis, angle) = rotation switch
        {
            ModelPreviewPresentationRotation.PositiveXToPositiveY => (new Vector3D(0, 0, 1), 90d),
            ModelPreviewPresentationRotation.NegativeXToPositiveY => (new Vector3D(0, 0, 1), -90d),
            ModelPreviewPresentationRotation.PositiveZToPositiveY => (new Vector3D(1, 0, 0), -90d),
            ModelPreviewPresentationRotation.NegativeZToPositiveY => (new Vector3D(1, 0, 0), 90d),
            _ => throw new ArgumentOutOfRangeException(nameof(rotation), rotation, null)
        };
        var transform = new Transform3DGroup();
        transform.Children.Add(new TranslateTransform3D(-center.X, -center.Y, -center.Z));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(axis, angle)));
        return transform;
    }

    private static CachedMeshGeometry CreateCachedMeshGeometry(
        ModelPreviewMesh source,
        float[]? positionOverride = null,
        float[]? normalOverride = null)
    {
        var positions = positionOverride is { Length: > 0 } && positionOverride.Length == source.Positions.Length
            ? positionOverride
            : source.Positions;
        var normals = normalOverride is { Length: > 0 } && normalOverride.Length == source.Positions.Length
            ? normalOverride
            : source.Normals;
        var hasNormals = normals is { Length: > 0 } && normals.Length == source.Positions.Length;
        var hasCoordinates = source.TextureCoordinates is { Length: > 0 } &&
                             source.TextureCoordinates.Length == source.VertexCount * 2;
        var geometry = new MeshGeometry3D
        {
            Positions = new Point3DCollection(source.VertexCount),
            Normals = new Vector3DCollection(hasNormals ? source.VertexCount : 0),
            TextureCoordinates = new PointCollection(hasCoordinates ? source.VertexCount : 0),
            TriangleIndices = new Int32Collection(source.TriangleIndices.Length)
        };

        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;
        for (var index = 0; index < positions.Length; index += 3)
        {
            var x = positions[index];
            var y = positions[index + 1];
            var z = positions[index + 2];
            geometry.Positions.Add(new Point3D(x, y, z));
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            maxZ = Math.Max(maxZ, z);
        }

        if (hasNormals && normals is not null)
            for (var index = 0; index < normals.Length; index += 3)
                geometry.Normals.Add(new Vector3D(normals[index], normals[index + 1], normals[index + 2]));

        if (hasCoordinates && source.TextureCoordinates is { } coordinates)
            for (var index = 0; index < coordinates.Length; index += 2)
                geometry.TextureCoordinates.Add(new System.Windows.Point(coordinates[index], coordinates[index + 1]));

        foreach (var index in source.TriangleIndices)
            geometry.TriangleIndices.Add(index);

        geometry.Freeze();
        return new CachedMeshGeometry(geometry, minX, minY, minZ, maxX, maxY, maxZ);
    }

    private static TexturePreviewCacheKey CreateTextureCacheKey(
        TextureInspectionItem texture,
        bool useOriginalResolution) => new(
        texture.PatchPath,
        texture.TextureId,
        texture.PayloadSource,
        texture.PayloadKind,
        texture.MainOffset,
        texture.GpuOffset,
        texture.StreamOffset,
        texture.GpuSize,
        texture.StreamSize,
        texture.Width,
        texture.Height,
        texture.MipCount,
        texture.DxgiFormat,
        useOriginalResolution);

    private static string CreatePatchSetCacheKey(IReadOnlyList<FileInfo> patchFiles)
    {
        var key = new System.Text.StringBuilder(patchFiles.Count * 160);
        foreach (var patchFile in patchFiles)
        {
            AppendFileStamp(key, patchFile.FullName);
            AppendFileStamp(key, patchFile.FullName + ".gpu_resources");
            AppendFileStamp(key, patchFile.FullName + ".stream");
        }
        return key.ToString();
    }

    private static void AppendFileStamp(System.Text.StringBuilder key, string path)
    {
        key.Append(path).Append('|');
        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            key.Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks);
        }
        key.AppendLine();
    }

    private void CacheModelResult(string key, ModelPreviewResult result)
    {
        if (_modelResultCache.ContainsKey(key))
            return;
        while (_modelResultCache.Count >= MaxModelResultCacheEntries && _modelResultOrder.TryDequeue(out var oldest))
            _modelResultCache.Remove(oldest);
        _modelResultCache[key] = result;
        _modelResultOrder.Enqueue(key);
    }

    private void ClearActiveTexturePreviews()
    {
        Interlocked.Increment(ref _textureLoadGeneration);
        CancelActiveTextureLoad();
        _texturePreviews.Clear();
        _automaticTexturePreviewIds.Clear();
        _selectedOriginalTextureId = null;
        _selectedOriginalTexturePreview = null;
    }

    private CancellationTokenSource BeginTextureLoad()
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _pageLifetimeCancellation.Token,
            _loadCancellation?.Token ?? CancellationToken.None);
        Interlocked.Exchange(ref _textureLoadCancellation, cancellation)?.Cancel();
        return cancellation;
    }

    private void CancelActiveTextureLoad() =>
        Interlocked.Exchange(ref _textureLoadCancellation, null)?.Cancel();

    private bool IsCurrentTextureRequest(
        TextureInspectionItem texture,
        ModData mod,
        int textureGeneration,
        CancellationTokenSource cancellation) =>
        ModelPreviewTextureRequestState.IsCurrent(
            textureGeneration,
            Volatile.Read(ref _textureLoadGeneration),
            cancellation.IsCancellationRequested) &&
        Volatile.Read(ref _isDisposed) == 0 &&
        ReferenceEquals(cancellation, _textureLoadCancellation) &&
        ReferenceEquals(texture, SelectedTexture) &&
        ReferenceEquals(mod, SelectedMod);

    private void ClearRetainedPreviewCaches()
    {
        _decodedTexturePreviews.Clear();
        _decodedTextureOrder.Clear();
        _modelResultCache.Clear();
        _modelResultOrder.Clear();
        // ConditionalWeakTable has no Clear API. Replacing it removes this page's
        // strong cache root so old WPF MeshGeometry3D instances can be collected.
        _geometryCache = new ConditionalWeakTable<ModelPreviewMesh, CachedMeshGeometry>();
        _animationBindings.Clear();
        _gpuSkinningService.ReleaseMeshes(Meshes);
        _liveMeshGeometries.Clear();
        ClearAnimationFrameCache();
    }

    private void StoreManualTexturePreview(ulong textureId, LoadedTexturePreview preview)
    {
        _texturePreviews[textureId] = preview;
        while (_texturePreviews.Count > MaxActiveTexturePreviewEntries)
        {
            var evictionCandidate = _texturePreviews.Keys
                .Where(id => !_automaticTexturePreviewIds.Contains(id) && id != SelectedTexture?.TextureId)
                .Select(static id => (ulong?)id)
                .FirstOrDefault();
            if (evictionCandidate is null)
                return;
            _texturePreviews.Remove(evictionCandidate.Value);
        }
    }

    private bool TryGetDecodedTexture(
        TextureInspectionItem texture,
        bool useOriginalResolution,
        out LoadedTexturePreview preview)
        => _decodedTexturePreviews.TryGetValue(
            CreateTextureCacheKey(texture, useOriginalResolution),
            out preview!);

    private void CacheDecodedTexture(TexturePreviewCacheKey key, LoadedTexturePreview preview)
    {
        if (_decodedTexturePreviews.ContainsKey(key))
            return;
        while (_decodedTexturePreviews.Count >= MaxDecodedTextureCacheEntries && _decodedTextureOrder.TryDequeue(out var oldest))
            _decodedTexturePreviews.Remove(oldest);
        _decodedTexturePreviews[key] = preview;
        _decodedTextureOrder.Enqueue(key);
    }

    internal static ulong? FindPreferredTextureId(
        ModelPreviewMesh mesh,
        IReadOnlyDictionary<ulong, LoadedTexturePreview> texturePreviews)
    {
        var semanticColorId = mesh.MaterialTextures
            .Get(ModelPreviewTextureRole.BaseColor)
            .FirstOrDefault(texturePreviews.ContainsKey);
        if (semanticColorId != 0)
            return semanticColorId;

        if (mesh.ColorTextureId is ulong colorTextureId &&
            texturePreviews.ContainsKey(colorTextureId))
            return colorTextureId;

        return mesh.TextureIds
            .Where(texturePreviews.ContainsKey)
            .OrderBy(id => texturePreviews[id].Role == TexturePreviewRole.ColorCandidate ? 0 :
                texturePreviews[id].Role == TexturePreviewRole.Unknown ? 1 : 2)
            .ThenByDescending(id => texturePreviews[id].SourcePixelCount)
            .Cast<ulong?>()
            .FirstOrDefault();
    }

    internal static ulong? GetSelectedTextureIdForMesh(
        ModelPreviewMesh mesh,
        ulong? selectedTextureId)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return selectedTextureId is ulong textureId && mesh.TextureIds.Contains(textureId)
            ? textureId
            : null;
    }

    internal static Material CreateMaterial(ImageSource? image)
    {
        Brush brush = image is null
            ? new SolidColorBrush(Color.FromRgb(184, 193, 202))
            : new ImageBrush(image)
            {
                Stretch = Stretch.Fill,
                Viewbox = new Rect(0, 0, 1, 1),
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewport = new Rect(0, 0, 1, 1),
                ViewportUnits = BrushMappingMode.Absolute,
                TileMode = TileMode.Tile
            };

        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }

    internal static Material CreateMaterial(
        ModelPreviewMesh mesh,
        IReadOnlyDictionary<ulong, LoadedTexturePreview> texturePreviews,
        bool useAutomaticMaterials,
        ulong? selectedTextureId)
    {
        var textureIds = useAutomaticMaterials
            ? GetAutomaticMaterialTextureIds(mesh)
            : (GetSelectedTextureIdForMesh(mesh, selectedTextureId) is ulong selected
                ? [selected]
                : []);

        var semanticBaseColorIds = mesh.MaterialTextures.Get(ModelPreviewTextureRole.BaseColor)
            .Concat(mesh.ColorTextureId is ulong color ? [color] : [])
            .Distinct()
            .ToArray();
        var baseColor = !useAutomaticMaterials
            ? textureIds.FirstOrDefault(texturePreviews.ContainsKey)
            : semanticBaseColorIds.Length > 0
                ? semanticBaseColorIds.FirstOrDefault(texturePreviews.ContainsKey)
                : textureIds.FirstOrDefault(texturePreviews.ContainsKey);
        var baseImage = baseColor != 0 && texturePreviews.TryGetValue(baseColor, out var basePreview)
            ? basePreview.Image
            : null;

        if (baseImage is null)
            return CreateMaterial(null);

        var emissiveImages = useAutomaticMaterials
            ? mesh.MaterialTextures.Get(ModelPreviewTextureRole.Emissive)
                .Where(texturePreviews.ContainsKey)
                .Select(textureId => texturePreviews[textureId].Image)
                .ToArray()
            : [];
        if (emissiveImages.Length == 0)
            return CreateMaterial(baseImage);

        // WPF's fixed-function 3D material has one Brush slot. Compose the material's
        // base color and emissive inputs into one DrawingBrush so every referenced
        // texture remains visible in the preview without pretending a normal map is an
        // albedo. The normal and mask inputs are still loaded and exposed in the asset
        // graph for future shader-backed rendering.
        var drawingGroup = new DrawingGroup();
        drawingGroup.Children.Add(new ImageDrawing(baseImage, new Rect(0, 0, 1, 1)));
        foreach (var emissiveImage in emissiveImages)
        {
            var emissiveGroup = new DrawingGroup { Opacity = 0.22 };
            emissiveGroup.Children.Add(new ImageDrawing(emissiveImage, new Rect(0, 0, 1, 1)));
            drawingGroup.Children.Add(emissiveGroup);
        }

        drawingGroup.Freeze();
        var brush = new DrawingBrush(drawingGroup)
        {
            Stretch = Stretch.Fill,
            Viewbox = new Rect(0, 0, 1, 1),
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport = new Rect(0, 0, 1, 1),
            ViewportUnits = BrushMappingMode.Absolute,
            TileMode = TileMode.Tile
        };
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }

    private static string CreateMaterialKey(
        ModelPreviewMesh mesh,
        bool useAutomaticMaterials,
        ulong? selectedTextureId,
        IReadOnlyDictionary<ulong, LoadedTexturePreview> texturePreviews)
    {
        var ids = useAutomaticMaterials
            ? GetAutomaticMaterialTextureIds(mesh)
                .Where(texturePreviews.ContainsKey)
            : (GetSelectedTextureIdForMesh(mesh, selectedTextureId) is ulong selected
                ? [selected]
                : Enumerable.Empty<ulong>());
        return string.Join(",", ids.OrderBy(static id => id));
    }

    private static IReadOnlyList<ulong> GetAutomaticMaterialTextureIds(ModelPreviewMesh mesh)
    {
        var semanticBaseColorIds = mesh.MaterialTextures.Get(ModelPreviewTextureRole.BaseColor)
            .Concat(mesh.ColorTextureId is ulong color ? [color] : [])
            .Distinct()
            .ToArray();

        return semanticBaseColorIds.Length > 0
            ? semanticBaseColorIds
                .Concat(mesh.MaterialTextures.Get(ModelPreviewTextureRole.Emissive))
                .Distinct()
                .ToArray()
            : mesh.MaterialTextures.Get(ModelPreviewTextureRole.Emissive)
                .Concat(mesh.TextureIds)
                .Distinct()
                .ToArray();
    }

    private void UpdateLocalizedPreviewLabels()
    {
        foreach (var mesh in Meshes)
        {
            mesh.PreviewStatusText = mesh.RenderStatus switch
            {
                ModelPreviewMeshRenderStatus.HiddenCullingBody => _localizationService["ModelPreviewPage.HiddenCullingBody"],
                ModelPreviewMeshRenderStatus.HiddenLargeOutlier => _localizationService["ModelPreviewPage.HiddenLargeOutlier"],
                ModelPreviewMeshRenderStatus.HiddenProxyGeometry => _localizationService["ModelPreviewPage.HiddenProxyGeometry"],
                ModelPreviewMeshRenderStatus.HiddenCollisionSphere => _localizationService["ModelPreviewPage.HiddenCollisionSphere"],
                _ => _localizationService["ModelPreviewPage.PreviewVisible"]
            };
            mesh.UvStatusText = mesh.HasTextureCoordinates
                ? _localizationService["ModelPreviewPage.HasUv"]
                : _localizationService["ModelPreviewPage.NoUv"];
        }

        foreach (var texture in Textures)
            texture.PreviewRoleText = GetTexturePreviewRoleText(texture.PreviewRole);

        foreach (var armor in Armors.Where(static armor => armor.IsAll))
            armor.Name = _localizationService["ModelPreviewPage.AllArmors"];
    }

    private string GetTexturePreviewRoleText(TexturePreviewRole role) => role switch
    {
        TexturePreviewRole.ColorCandidate => _localizationService["ModelPreviewPage.ColorCandidate"],
        TexturePreviewRole.LikelyNormalMap => _localizationService["ModelPreviewPage.LikelyNormalMap"],
        _ => _localizationService["ModelPreviewPage.UnclassifiedTexture"]
    };

    private void LocalizationServiceOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(AutomaticallyHiddenMeshSummary));
        OnPropertyChanged(nameof(AnimationPlaybackToolTip));
        UpdateLocalizedPreviewLabels();
        UpdateCameraOrientationText(_cameraDirection);
    }

    internal void UpdateCameraOrientation(double yaw, double pitch)
    {
        _cameraDirection = ModelPreviewViewportGuides.GetCameraDirection(yaw, SuggestedCameraYaw, pitch);
        UpdateCameraOrientationText(_cameraDirection);
    }

    private void UpdateCameraOrientationText(ModelPreviewCameraDirection direction)
    {
        var directionKey = direction switch
        {
            ModelPreviewCameraDirection.Front => "ModelPreviewPage.OrientationFront",
            ModelPreviewCameraDirection.Right => "ModelPreviewPage.OrientationRight",
            ModelPreviewCameraDirection.Back => "ModelPreviewPage.OrientationBack",
            ModelPreviewCameraDirection.Left => "ModelPreviewPage.OrientationLeft",
            ModelPreviewCameraDirection.Top => "ModelPreviewPage.OrientationTop",
            ModelPreviewCameraDirection.Bottom => "ModelPreviewPage.OrientationBottom",
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null)
        };
        CameraOrientationText = _localizationService["ModelPreviewPage.CameraOrientation"]
            .Replace("{direction}", _localizationService[directionKey]);
    }

    protected override void OnDispose()
    {
        Volatile.Write(ref _isDisposed, 1);
        StopAnimationPlayback();
        _animationTimer.Tick -= AnimationTimerOnTick;
        _pageLifetimeCancellation.Cancel();
        _loadCancellation?.Cancel();
        Interlocked.Increment(ref _renderGeneration);
        ModelGroup = null;
        _gpuSkinningService.ReleaseMeshes(Meshes);
        _liveMeshGeometries.Clear();
        ClearAnimationFrameCache();
        SelectedTexturePreview = null;
        ClearActiveTexturePreviews();
        ClearRetainedPreviewCaches();
        SelectedTexture = null;
        SelectedMesh = null;
        SelectedArmor = null;
        SelectedMod = null;
        Meshes.Clear();
        Textures.Clear();
        Armors.Clear();
        Animations.Clear();
        // A queued rebuild can still be unwinding after navigation. It observes
        // _isDisposed/_renderGeneration above; the rebuild gate and page-lifetime
        // cancellation are guarded/verified against post-dispose access, so both are
        // released here without surfacing a fault.
        _localizationService.PropertyChanged -= LocalizationServiceOnPropertyChanged;
        _pageLifetimeCancellation.Dispose();
        _rebuildGate.Dispose();
    }
}
