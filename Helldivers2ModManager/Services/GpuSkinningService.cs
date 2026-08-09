using System.Numerics;
using System.Runtime.InteropServices;
using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.DXGI;

namespace Helldivers2ModManager.Services;

/// <summary>
/// Runs the vertex/bone weighted position calculation in a Direct3D 11 compute shader.
/// WPF still owns the final MeshGeometry3D upload for now; the expensive skinning loop
/// itself no longer runs once per vertex on the CPU.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class GpuSkinningService : IDisposable
{
    private const int ThreadsPerGroup = 64;
    private const int VertexStride = 48;
    private const string SkinningShader = """
        struct SkinVertex
        {
            float3 Position;
            uint Padding;
            uint4 BoneIndices;
            float4 BoneWeights;
        };

        struct BoneTransform
        {
            row_major float4x4 Value;
        };

        StructuredBuffer<SkinVertex> Vertices : register(t0);
        StructuredBuffer<BoneTransform> Bones : register(t1);
        RWStructuredBuffer<float3> Positions : register(u0);

        [numthreads(64, 1, 1)]
        void main(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            uint vertexIndex = dispatchThreadId.x;
            uint vertexCount;
            uint vertexStride;
            Vertices.GetDimensions(vertexCount, vertexStride);
            if (vertexIndex >= vertexCount)
                return;

            SkinVertex vertex = Vertices[vertexIndex];
            float4 source = float4(vertex.Position, 1.0f);
            float4 skinned = 0.0f;
            skinned += mul(source, Bones[vertex.BoneIndices.x].Value) * vertex.BoneWeights.x;
            skinned += mul(source, Bones[vertex.BoneIndices.y].Value) * vertex.BoneWeights.y;
            skinned += mul(source, Bones[vertex.BoneIndices.z].Value) * vertex.BoneWeights.z;
            skinned += mul(source, Bones[vertex.BoneIndices.w].Value) * vertex.BoneWeights.w;
            float totalWeight = dot(vertex.BoneWeights, float4(1.0f, 1.0f, 1.0f, 1.0f));
            if (totalWeight < 0.999f)
                skinned.xyz += source.xyz * saturate(1.0f - totalWeight);
            Positions[vertexIndex] = skinned.xyz;
        }
        """;

    private readonly object _gate = new();
    private readonly ILogger<GpuSkinningService> _logger;
    private readonly Dictionary<ModelPreviewMesh, GpuMeshResources> _meshResources = [];
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11ComputeShader? _shader;
    private bool _initializationAttempted;
    private bool _disposed;
    private long _successfulDispatchCount;

    public GpuSkinningService(ILogger<GpuSkinningService> logger)
    {
        _logger = logger;
    }

    public long SuccessfulDispatchCount => Interlocked.Read(ref _successfulDispatchCount);

    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                EnsureInitialized();
                return _device is not null && _context is not null && _shader is not null;
            }
        }
    }

    public bool TrySkinPositions(
        ModelPreviewMesh mesh,
        IReadOnlyList<Matrix4x4> skinningTransforms,
        CancellationToken cancellationToken,
        out float[] positions)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(skinningTransforms);
        positions = [];

        if (mesh.Skinning is not { } skinning ||
            !skinning.IsValidForVertexCount(mesh.VertexCount) ||
            skinningTransforms.Count == 0 ||
            skinningTransforms.Count != skinning.Skeleton.Bones.Count ||
            skinningTransforms.Count > 256)
            return false;

        lock (_gate)
        {
            if (_disposed)
                return false;

            EnsureInitialized();
            if (_device is null || _context is null || _shader is null)
                return false;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resources = GetOrCreateMeshResources(mesh);
                var bones = skinningTransforms.ToArray();
                _context.UpdateSubresource(bones, resources.BoneBuffer);
                _context.CSSetShaderResources(0, new[] { resources.VertexView, resources.BoneView });
                _context.CSSetUnorderedAccessViews(0, new[] { resources.OutputView });
                _context.CSSetShader(_shader);
                _context.Dispatch((uint)((mesh.VertexCount + ThreadsPerGroup - 1) / ThreadsPerGroup), 1, 1);
                _context.CopyResource(resources.ReadbackBuffer, resources.OutputBuffer);

                var mapped = _context.Map(resources.ReadbackBuffer, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    positions = new float[mesh.Positions.Length];
                    Marshal.Copy(mapped.DataPointer, positions, 0, positions.Length);
                }
                finally
                {
                    _context.Unmap(resources.ReadbackBuffer, 0);
                }

                _context.CSSetShader(null);
                _context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
                _context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
                var dispatchCount = Interlocked.Increment(ref _successfulDispatchCount);
                if (dispatchCount == 1)
                {
                    _logger.LogInformation(
                        "Model preview completed its first GPU skinning dispatch for {VertexCount} vertices",
                        mesh.VertexCount);
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                DisableGpu(ex);
                positions = [];
                return false;
            }
        }
    }

    public void ReleaseMeshes(IEnumerable<ModelPreviewMesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        lock (_gate)
        {
            foreach (var mesh in meshes)
            {
                if (_meshResources.Remove(mesh, out var resources))
                    resources.Dispose();
            }
        }
    }

    private void EnsureInitialized()
    {
        if (_initializationAttempted || _disposed)
            return;

        _initializationAttempted = true;
        try
        {
            var featureLevels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_0 };
            var device = D3D11.D3D11CreateDevice(
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                featureLevels);
            var shaderBytecode = CompileShader();
            _device = device;
            _context = device.ImmediateContext;
            _shader = _device.CreateComputeShader(shaderBytecode.Span);
            _logger.LogInformation("Model preview GPU skinning initialized with Direct3D 11 compute shaders");
        }
        catch (Exception ex)
        {
            DisableGpu(ex);
        }
    }

    private void DisableGpu(Exception exception)
    {
        foreach (var resources in _meshResources.Values)
            resources.Dispose();
        _meshResources.Clear();
        _shader?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _shader = null;
        _context = null;
        _device = null;
        _logger.LogWarning(exception, "Model preview GPU skinning is unavailable; using the CPU fallback");
    }

    private static ReadOnlyMemory<byte> CompileShader() => Compiler.Compile(
        SkinningShader,
        "main",
        "ModelPreviewGpuSkinning.hlsl",
        "cs_5_0",
        ShaderFlags.OptimizationLevel3,
        EffectFlags.None);

    private GpuMeshResources GetOrCreateMeshResources(ModelPreviewMesh mesh)
    {
        if (_meshResources.TryGetValue(mesh, out var existing))
            return existing;

        if (_device is null)
            throw new InvalidOperationException("The GPU device is unavailable.");

        var vertices = new GpuSkinVertex[mesh.VertexCount];
        for (var vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
        {
            var positionOffset = vertexIndex * 3;
            var influenceOffset = vertexIndex * ModelPreviewSkinningData.InfluencesPerVertex;
            var indices = new uint[ModelPreviewSkinningData.InfluencesPerVertex];
            var weights = new float[ModelPreviewSkinningData.InfluencesPerVertex];
            for (var influence = 0; influence < ModelPreviewSkinningData.InfluencesPerVertex; influence++)
            {
                var transformIndex = mesh.Skinning!.TransformIndices[influenceOffset + influence];
                var weight = mesh.Skinning.Weights[influenceOffset + influence];
                if (transformIndex < 0 ||
                    transformIndex >= mesh.Skinning.Skeleton.Bones.Count ||
                    weight <= 0)
                    continue;
                indices[influence] = (uint)transformIndex;
                weights[influence] = weight;
            }
            vertices[vertexIndex] = new GpuSkinVertex(
                new Vector3(mesh.Positions[positionOffset], mesh.Positions[positionOffset + 1], mesh.Positions[positionOffset + 2]),
                new UInt4(indices[0], indices[1], indices[2], indices[3]),
                new Vector4(weights[0], weights[1], weights[2], weights[3]));
        }

        var vertexBufferDescription = new BufferDescription(
            (uint)(vertices.Length * VertexStride),
            BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.BufferStructured,
            VertexStride);
        var vertexBuffer = _device.CreateBuffer<GpuSkinVertex>(vertices.AsSpan(), vertexBufferDescription);
        var vertexView = _device.CreateShaderResourceView(
            vertexBuffer,
            new ShaderResourceViewDescription(
                vertexBuffer,
                Format.Unknown,
                0,
                (uint)vertices.Length,
                BufferExtendedShaderResourceViewFlags.None));

        var boneCount = (uint)mesh.Skinning!.Skeleton.Bones.Count;
        var boneBuffer = _device.CreateBuffer(
            boneCount * 64,
            BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.BufferStructured,
            64);
        var boneView = _device.CreateShaderResourceView(
            boneBuffer,
            new ShaderResourceViewDescription(
                boneBuffer,
                Format.Unknown,
                0,
                boneCount,
                BufferExtendedShaderResourceViewFlags.None));

        var outputBuffer = _device.CreateBuffer(
            (uint)(mesh.VertexCount * sizeof(float) * 3),
            BindFlags.UnorderedAccess,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.BufferStructured,
            sizeof(float) * 3);
        var outputView = _device.CreateUnorderedAccessView(outputBuffer, new UnorderedAccessViewDescription(outputBuffer, Format.Unknown, 0, (uint)mesh.VertexCount, (uint)BufferUnorderedAccessViewFlags.None));

        var readbackBuffer = _device.CreateBuffer(
            (uint)(mesh.VertexCount * sizeof(float) * 3),
            BindFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read,
            ResourceOptionFlags.None,
            0);

        var resources = new GpuMeshResources(vertexBuffer, vertexView, boneBuffer, boneView, outputBuffer, outputView, readbackBuffer);
        _meshResources.Add(mesh, resources);
        return resources;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var resources in _meshResources.Values)
                resources.Dispose();
            _meshResources.Clear();
            _shader?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
            _shader = null;
            _context = null;
            _device = null;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct GpuSkinVertex(Vector3 position, UInt4 boneIndices, Vector4 boneWeights)
    {
        public readonly Vector3 Position = position;
        public readonly uint Padding = 0;
        public readonly UInt4 BoneIndices = boneIndices;
        public readonly Vector4 BoneWeights = boneWeights;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct UInt4(uint x, uint y, uint z, uint w)
    {
        public readonly uint X = x;
        public readonly uint Y = y;
        public readonly uint Z = z;
        public readonly uint W = w;
    }

    private sealed class GpuMeshResources(
        ID3D11Buffer vertexBuffer,
        ID3D11ShaderResourceView vertexView,
        ID3D11Buffer boneBuffer,
        ID3D11ShaderResourceView boneView,
        ID3D11Buffer outputBuffer,
        ID3D11UnorderedAccessView outputView,
        ID3D11Buffer readbackBuffer) : IDisposable
    {
        public ID3D11Buffer VertexBuffer { get; } = vertexBuffer;
        public ID3D11ShaderResourceView VertexView { get; } = vertexView;
        public ID3D11Buffer BoneBuffer { get; } = boneBuffer;
        public ID3D11ShaderResourceView BoneView { get; } = boneView;
        public ID3D11Buffer OutputBuffer { get; } = outputBuffer;
        public ID3D11UnorderedAccessView OutputView { get; } = outputView;
        public ID3D11Buffer ReadbackBuffer { get; } = readbackBuffer;

        public void Dispose()
        {
            ReadbackBuffer.Dispose();
            OutputView.Dispose();
            OutputBuffer.Dispose();
            BoneView.Dispose();
            BoneBuffer.Dispose();
            VertexView.Dispose();
            VertexBuffer.Dispose();
        }
    }
}
