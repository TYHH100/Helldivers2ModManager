namespace Helldivers2ModManager.Models;

/// <summary>
/// Read-only data shown by the Patch Resource Viewer. GPU payloads are represented by
/// their Unit stream metadata and a tiny vertex sample, never by loading the whole file.
/// </summary>
internal sealed class PatchResourceInspectionResult
{
    public List<PatchTocInspectionItem> TocEntries { get; } = [];
    public List<GpuStreamInspectionItem> GpuStreams { get; } = [];
    public List<TextureInspectionItem> Textures { get; } = [];
    public int PatchFileCount { get; set; }
    public string? Error { get; set; }
}

internal sealed class ModelPreviewResult
{
    public List<ModelPreviewMesh> Meshes { get; } = [];
    public List<TextureInspectionItem> Textures { get; } = [];
    /// <summary>
    /// Armor alternatives resolved from the game package index. The model preview keeps
    /// this separate from manifest options: manifest options choose patch files, while
    /// armor alternatives choose which Unit package identities are rendered.
    /// </summary>
    public List<ModelPreviewArmorOption> Armors { get; } = [];
    public List<ModelPreviewAnimationLibrary> AnimationLibraries { get; } = [];
    public int PatchFileCount { get; set; }
    public int SkippedStreams { get; set; }
    public string? Error { get; set; }

    internal int PreviewVertexCount { get; private set; }
    internal int PreviewIndexCount { get; private set; }
    internal bool IsAtCapacity => Meshes.Count >= 512 || PreviewVertexCount >= 1_000_000 || PreviewIndexCount >= 3_000_000;

    internal bool TryAddMesh(ModelPreviewMesh mesh)
    {
        const int maxMeshes = 512;
        const int maxVertices = 1_000_000;
        const int maxIndices = 3_000_000;
        if (Meshes.Count >= maxMeshes ||
            PreviewVertexCount > maxVertices - mesh.VertexCount ||
            PreviewIndexCount > maxIndices - mesh.TriangleIndices.Length)
            return false;

        Meshes.Add(mesh);
        PreviewVertexCount += mesh.VertexCount;
        PreviewIndexCount += mesh.TriangleIndices.Length;
        return true;
    }
}

/// <summary>
/// A decoded, untextured triangle mesh suitable for the WPF preview. The arrays are
/// deliberately kept independent from WPF's 3D types so patch decoding remains testable
/// and does not require a Dispatcher thread.
/// </summary>
internal sealed class ModelPreviewMesh : System.ComponentModel.INotifyPropertyChanged
{
    public required string PatchFile { get; init; }
    public required ulong UnitId { get; init; }
    public required int StreamIndex { get; init; }
    public int MeshInfoIndex { get; init; } = -1;
    public uint SourceVertexOffset { get; init; }
    public uint SourceVertexCount { get; init; }
    public uint SourceIndexOffset { get; init; }
    public uint SourceIndexCount { get; init; }
    public ModelPreviewBodyShape BodyShape { get; init; } = ModelPreviewBodyShape.Unknown;
    public ModelPreviewCustomizationSlot CustomizationSlot { get; init; } = ModelPreviewCustomizationSlot.Unknown;
    public required float[] Positions { get; init; }
    public float[]? Normals { get; init; }
    public float[]? TextureCoordinates { get; init; }
    public ModelPreviewSkinningData? Skinning { get; init; }
    public required int[] TriangleIndices { get; init; }
    public IReadOnlyList<ulong> TextureIds { get; init; } = [];
    public ulong? ColorTextureId { get; init; }
    public ulong? MaterialId { get; init; }
    /// <summary>
    /// All material inputs referenced by this section. ColorTextureId is retained as the
    /// preferred diffuse input for compatibility, while this collection lets the new
    /// material pipeline load normal/mask/emissive inputs without guessing globally.
    /// </summary>
    public ModelPreviewMaterialTextureSet MaterialTextures { get; init; } = ModelPreviewMaterialTextureSet.Empty;
    /// <summary>
    /// Normalized archive IDs of armor packages that reuse this Unit. Empty means shared
    /// or unresolved and is deliberately kept visible for every armor selection.
    /// </summary>
    public IReadOnlyList<string> ArmorIds { get; internal set; } = [];
    public bool IsCullingBody { get; init; }

    /// <summary>
    /// The automatic preview deliberately excludes only extreme size outliers. The source
    /// mesh remains selectable, so a heuristic can never make an otherwise valid stream
    /// inaccessible to the user.
    /// </summary>
    public ModelPreviewMeshRenderStatus RenderStatus { get; set; } = ModelPreviewMeshRenderStatus.Visible;
    public double BoundsDiagonal { get; set; }

    public string UnitIdText => $"0x{UnitId:X16}";
    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => TriangleIndices.Length / 3;
    public string DisplayName => MeshInfoIndex >= 0
        ? $"{PatchFile} / {UnitIdText} / Mesh {MeshInfoIndex} / Stream {StreamIndex}"
        : $"{PatchFile} / {UnitIdText} / Stream {StreamIndex}";
    public bool HasTextureCoordinates => TextureCoordinates?.Length == VertexCount * 2;
    private string _uvStatusText = string.Empty;
    public string UvStatusText
    {
        get => _uvStatusText;
        set
        {
            if (_uvStatusText == value)
                return;
            _uvStatusText = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(UvStatusText)));
        }
    }
    public string BoundsText => BoundsDiagonal > 0 && double.IsFinite(BoundsDiagonal)
        ? BoundsDiagonal.ToString("0.###")
        : "-";
    private string _previewStatusText = string.Empty;
    public string PreviewStatusText
    {
        get => _previewStatusText;
        set
        {
            if (_previewStatusText == value)
                return;
            _previewStatusText = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(PreviewStatusText)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class ModelPreviewArmorOption : System.ComponentModel.INotifyPropertyChanged
{
    public required string Id { get; init; }
    private string _name = string.Empty;
    public required string Name
    {
        get => _name;
        set
        {
            if (_name == value)
                return;
            _name = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Name)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayName)));
        }
    }
    public int MeshCount { get; init; }
    public bool IsAll { get; init; }
    public string DisplayName => IsAll ? Name : $"{Name} ({MeshCount})";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

internal enum ModelPreviewMeshRenderStatus
{
    Visible,
    HiddenCullingBody,
    HiddenLargeOutlier,
    HiddenProxyGeometry,
    HiddenCollisionSphere
}

/// <summary>
/// Produces the conservative default model-preview set. Unit resources contain more than
/// final visible geometry (for example proxy and collision streams), but this code does not
/// try to guess an undocumented LOD layout. It removes only low-complexity proxies,
/// regular collision spheres, and meshes whose bounds are extreme outliers relative to
/// meshes from the same Unit.
/// </summary>
internal static class ModelPreviewMeshSelector
{
    private const double LargeOutlierScale = 8.0;
    private const int MinimumPeerCount = 3;
    private const int MaxProxyVertices = 24;
    private const int MaxProxyTriangles = 12;
    private const int MinimumCollisionSphereVertices = 96;
    private const int MaximumCollisionSphereVertices = 4_096;
    private const double MaximumCollisionSphereAxisRatio = 1.05;
    private const double MaximumCollisionSphereRelativeRadiusDeviation = 0.03;

    public static ModelPreviewSelection Select(IReadOnlyList<ModelPreviewMesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);

        foreach (var mesh in meshes)
        {
            mesh.BoundsDiagonal = CalculateBoundsDiagonal(mesh.Positions);
            mesh.RenderStatus = mesh.IsCullingBody
                ? ModelPreviewMeshRenderStatus.HiddenCullingBody
                : IsLikelyProxyGeometry(mesh)
                    ? ModelPreviewMeshRenderStatus.HiddenProxyGeometry
                    : IsLikelyCollisionSphere(mesh)
                        ? ModelPreviewMeshRenderStatus.HiddenCollisionSphere
                        : ModelPreviewMeshRenderStatus.Visible;
        }

        foreach (var unitMeshes in meshes.GroupBy(static mesh => mesh.UnitId))
        {
            var measurable = unitMeshes
                .Where(static mesh => mesh.RenderStatus == ModelPreviewMeshRenderStatus.Visible &&
                                      mesh.BoundsDiagonal > 0 &&
                                      double.IsFinite(mesh.BoundsDiagonal))
                .OrderBy(static mesh => mesh.BoundsDiagonal)
                .ToArray();
            if (measurable.Length < MinimumPeerCount)
                continue;

            var median = measurable[measurable.Length / 2].BoundsDiagonal;
            if (median <= 0)
                continue;

            foreach (var mesh in measurable)
            {
                if (mesh.BoundsDiagonal > median * LargeOutlierScale)
                    mesh.RenderStatus = ModelPreviewMeshRenderStatus.HiddenLargeOutlier;
            }
        }

        var visible = meshes
            .Where(static mesh => mesh.RenderStatus == ModelPreviewMeshRenderStatus.Visible)
            .ToArray();
        return new ModelPreviewSelection(visible, meshes.Count - visible.Length);
    }

    /// <summary>
    /// Isolation is an inspection tool, not a way to accidentally re-enable a hidden
    /// collision mesh. A hidden selection becomes renderable only after the user has
    /// explicitly enabled the show-hidden control.
    /// </summary>
    public static IReadOnlyList<ModelPreviewMesh> GetRenderMeshes(
        ModelPreviewSelection selection,
        IReadOnlyList<ModelPreviewMesh> allMeshes,
        ModelPreviewMesh? selectedMesh,
        bool isolateSelectedMesh,
        bool showFilteredMeshes)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(allMeshes);

        if (isolateSelectedMesh && selectedMesh is not null &&
            (showFilteredMeshes || selectedMesh.RenderStatus == ModelPreviewMeshRenderStatus.Visible))
            return [selectedMesh];

        return showFilteredMeshes ? allMeshes : selection.VisibleMeshes;
    }

    private static bool IsLikelyProxyGeometry(ModelPreviewMesh mesh) =>
        mesh.VertexCount <= MaxProxyVertices && mesh.TriangleCount <= MaxProxyTriangles;

    /// <summary>
    /// Several armor Units include generated collision spheres at different tessellation
    /// levels alongside the visible mesh. The defining shape is equal axis extents and an
    /// almost constant radius measured from the bounding-box center, not a fixed count.
    /// A normal head or rounded clothing part has visible features and fails the tight
    /// radial-deviation test.
    /// </summary>
    private static bool IsLikelyCollisionSphere(ModelPreviewMesh mesh)
    {
        if (mesh.VertexCount is < MinimumCollisionSphereVertices or > MaximumCollisionSphereVertices ||
            mesh.TriangleCount < mesh.VertexCount ||
            mesh.Positions.Length != mesh.VertexCount * 3)
            return false;

        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;
        for (var index = 0; index < mesh.Positions.Length; index += 3)
        {
            var x = mesh.Positions[index];
            var y = mesh.Positions[index + 1];
            var z = mesh.Positions[index + 2];
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                return false;

            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            maxZ = Math.Max(maxZ, z);
        }

        var extentX = maxX - minX;
        var extentY = maxY - minY;
        var extentZ = maxZ - minZ;
        var minExtent = Math.Min(extentX, Math.Min(extentY, extentZ));
        var maxExtent = Math.Max(extentX, Math.Max(extentY, extentZ));
        if (minExtent <= 0 || maxExtent / minExtent > MaximumCollisionSphereAxisRatio)
            return false;

        // Vertex samples are not necessarily uniformly distributed around a collision
        // sphere, so their arithmetic mean can be off-center. The bounding-box center is
        // stable for the primitive and recognizes the 439-, 878- and other tessellations.
        var centerX = (minX + maxX) / 2;
        var centerY = (minY + maxY) / 2;
        var centerZ = (minZ + maxZ) / 2;
        var radii = new double[mesh.VertexCount];
        var meanRadius = 0d;
        for (var vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            var offset = vertex * 3;
            var radius = Math.Sqrt(
                Math.Pow(mesh.Positions[offset] - centerX, 2) +
                Math.Pow(mesh.Positions[offset + 1] - centerY, 2) +
                Math.Pow(mesh.Positions[offset + 2] - centerZ, 2));
            radii[vertex] = radius;
            meanRadius += radius;
        }

        meanRadius /= mesh.VertexCount;
        if (meanRadius <= 0 || !double.IsFinite(meanRadius))
            return false;

        var squaredDeviation = radii.Sum(radius => Math.Pow(radius - meanRadius, 2)) / mesh.VertexCount;
        return Math.Sqrt(squaredDeviation) / meanRadius <= MaximumCollisionSphereRelativeRadiusDeviation;
    }

    private static double CalculateBoundsDiagonal(float[] positions)
    {
        if (positions.Length < 3)
            return 0;

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
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                return 0;

            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            maxZ = Math.Max(maxZ, z);
        }

        return Math.Sqrt(
            Math.Pow(maxX - minX, 2) +
            Math.Pow(maxY - minY, 2) +
            Math.Pow(maxZ - minZ, 2));
    }
}

internal sealed record ModelPreviewSelection(IReadOnlyList<ModelPreviewMesh> VisibleMeshes, int HiddenMeshCount);

internal sealed class TextureInspectionItem : System.ComponentModel.INotifyPropertyChanged
{
    public required string PatchFile { get; init; }
    public required string PatchPath { get; init; }
    /// <summary>
    /// Deployment order of the source patch within the currently selected mod options.
    /// When a resource id is patched more than once, the later patch is the effective one.
    /// </summary>
    public required int PatchOrder { get; init; }
    public required int TocEntryIndex { get; init; }
    public required ulong TextureId { get; init; }
    public required ulong MainOffset { get; init; }
    public required uint MainSize { get; init; }
    public required ulong GpuOffset { get; init; }
    public required uint GpuSize { get; init; }
    public required ulong StreamOffset { get; init; }
    public required uint StreamSize { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int MipCount { get; init; }
    public required int DxgiFormat { get; init; }
    public required string PayloadKind { get; init; }
    public required string PayloadSource { get; init; }
    /// <summary>
    /// 该贴图解析自游戏归档（模组材质引用了模组未携带的原版资源），而不是模组补丁。
    /// 这类条目的偏移指向游戏包地址空间，预览读取走游戏归档链路而非补丁伴生文件。
    /// </summary>
    public bool IsFromGameArchive { get; init; }
    /// <summary>IsFromGameArchive 时为该贴图所在的游戏包名（模组补丁的 16 位前缀）。</summary>
    public string? GamePackageBaseName { get; init; }
    public string TextureIdText => $"0x{TextureId:X16}";
    public string SizeText => $"{Width:N0} × {Height:N0}";
    public string FormatText => PayloadKind == "PNG" ? "PNG" : $"DXGI {DxgiFormat}";
    public TexturePreviewRole PreviewRole { get; set; } = TexturePreviewRole.Unknown;
    private string _previewRoleText = string.Empty;
    public string PreviewRoleText
    {
        get => _previewRoleText;
        set
        {
            if (_previewRoleText == value)
                return;
            _previewRoleText = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(PreviewRoleText)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

internal enum TexturePreviewRole
{
    Unknown,
    ColorCandidate,
    LikelyNormalMap
}

internal sealed class TexturePreviewData
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public byte[]? BgraPixels { get; init; }
    public byte[]? EncodedImageBytes { get; init; }
    public required string Description { get; init; }
}

internal sealed class PatchTocInspectionItem
{
    public required string PatchFile { get; init; }
    public required string PatchPath { get; init; }
    public required int PatchOrder { get; init; }
    public required int EntryIndex { get; init; }
    public required ulong FileId { get; init; }
    public required ulong TypeId { get; init; }
    public required uint MainSize { get; init; }
    public required uint GpuSize { get; init; }
    public required uint StreamSize { get; init; }
    public required ulong MainOffset { get; init; }
    public required ulong GpuOffset { get; init; }
    public required ulong StreamOffset { get; init; }

    public string FileIdText => $"0x{FileId:X16}";
    public string TypeIdText => $"0x{TypeId:X16}";
    public string MainRangeText => $"0x{MainOffset:X} + {MainSize:N0}";
    public string GpuRangeText => GpuSize == 0 ? "-" : $"0x{GpuOffset:X} + {GpuSize:N0}";
    public string StreamRangeText => StreamSize == 0 ? "-" : $"0x{StreamOffset:X} + {StreamSize:N0}";
}

internal sealed class GpuStreamInspectionItem
{
    public required string PatchFile { get; init; }
    public required int TocEntryIndex { get; init; }
    public required ulong UnitId { get; init; }
    public required uint UnitVersion { get; init; }
    public required int StreamIndex { get; init; }
    public required uint VertexCount { get; init; }
    public required uint VertexStride { get; init; }
    public required uint IndexCount { get; init; }
    public required string IndexFormat { get; init; }
    public required string Components { get; init; }
    public required string VertexBuffer { get; init; }
    public required string IndexBuffer { get; init; }
    public required string VertexSample { get; init; }

    public string UnitIdText => $"0x{UnitId:X16}";
}
