using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.D3DCompiler;
using Vortice.DXGI;

namespace Helldivers2ModManager.Services;

/// <summary>
/// Runs the vertex/bone weighted position calculation in a Direct3D 12 compute shader,
/// matching the API the game itself renders with. WPF still owns the final MeshGeometry3D
/// upload for now; the expensive skinning loop itself no longer runs once per vertex on the CPU.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class GpuSkinningService : IDisposable
{
    private const int ThreadsPerGroup = 64;
    private const int VertexStride = 48;
    private const int BoneStride = 64;
    // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING; required (not just ignored) for buffer SRVs.
    private const uint DefaultShader4ComponentMapping = 0x1688;
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

    private const int DescriptorSlotVertex = 0;
    private const int DescriptorSlotBone = 1;
    private const int DescriptorSlotOutput = 2;

    private readonly Lock _gate = new();
    private readonly ILogger<GpuSkinningService> _logger;
    private readonly Dictionary<ModelPreviewMesh, GpuMeshResources> _meshResources = [];
    private ID3D12Device? _device;
    private ID3D12CommandQueue? _commandQueue;
    private ID3D12CommandAllocator? _commandAllocator;
    private ID3D12GraphicsCommandList? _commandList;
    private ID3D12RootSignature? _rootSignature;
    private ID3D12PipelineState? _pipelineState;
    private ID3D12DescriptorHeap? _descriptorHeap;
    private CpuDescriptorHandle _descriptorHeapCpuStart;
    private GpuDescriptorHandle _descriptorHeapGpuStart;
    private uint _descriptorHandleIncrementSize;
    private ID3D12Fence? _fence;
    private readonly ManualResetEvent _fenceEvent = new(false);
    private ulong _fenceValue;
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
                return _device is not null && _commandQueue is not null && _pipelineState is not null;
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
            if (_device is null || _commandQueue is null || _commandAllocator is null ||
                _commandList is null || _rootSignature is null || _pipelineState is null ||
                _descriptorHeap is null || _fence is null)
                return false;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resources = GetOrCreateMeshResources(mesh);
                var outputSizeInBytes = (ulong)mesh.VertexCount * sizeof(float) * 3;

                // Upload-heap buffers stay persistently mapped; the bone transforms change every
                // dispatch, the vertex data is written once at resource creation.
                var bones = skinningTransforms.ToArray();
                resources.BoneUploadBuffer.SetData(bones.AsSpan(), 0);

                WriteMeshDescriptors(resources);

                _commandAllocator.Reset();
                _commandList.Reset(_commandAllocator, _pipelineState);
                _commandList.SetComputeRootSignature(_rootSignature);
                _commandList.SetDescriptorHeaps(_descriptorHeap);
                _commandList.SetComputeRootDescriptorTable(0, _descriptorHeapGpuStart);
                // The output buffer decays back to Common after every ExecuteCommandLists, so each
                // dispatch runs with fully explicit transitions; relying on implicit promotion in
                // combination with a UAV->CopySource barrier is rejected by the runtime.
                _commandList.ResourceBarrierTransition(resources.OutputBuffer, ResourceStates.Common, ResourceStates.UnorderedAccess);
                _commandList.Dispatch((uint)((mesh.VertexCount + ThreadsPerGroup - 1) / ThreadsPerGroup), 1, 1);
                _commandList.ResourceBarrierTransition(resources.OutputBuffer, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
                _commandList.CopyBufferRegion(resources.ReadbackBuffer, 0, resources.OutputBuffer, 0, outputSizeInBytes);
                _commandList.Close();

                _commandQueue.ExecuteCommandList(_commandList);
                var fenceValue = ++_fenceValue;
                _fenceEvent.Reset();
                _fence.SetEventOnCompletion(fenceValue, _fenceEvent);
                _commandQueue.Signal(_fence, fenceValue);
                _fenceEvent.WaitOne();

                var mapped = resources.ReadbackBuffer.Map<byte>(0, (int)outputSizeInBytes);
                try
                {
                    positions = new float[mesh.Positions.Length];
                    MemoryMarshal.Cast<byte, float>(mapped).CopyTo(positions);
                }
                finally
                {
                    resources.ReadbackBuffer.Unmap(0, null);
                }

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
            _device = D3D12.D3D12CreateDevice<ID3D12Device>((IDXGIAdapter?)null, FeatureLevel.Level_11_0);
            _commandQueue = _device.CreateCommandQueue(new CommandQueueDescription(
                CommandListType.Compute, 0, CommandQueueFlags.None, 0));
            _commandAllocator = _device.CreateCommandAllocator(CommandListType.Compute);
            _rootSignature = CreateSkinningRootSignature();
            var shaderBytecode = CompileShader();
            var pipelineStateDescription = new ComputePipelineStateDescription
            {
                RootSignature = _rootSignature,
                ComputeShader = shaderBytecode,
            };
            _pipelineState = _device.CreateComputePipelineState(pipelineStateDescription);
            _commandList = _device.CreateCommandList<ID3D12GraphicsCommandList>(
                CommandListType.Compute, _commandAllocator, _pipelineState);
            _commandList.Close();
            _descriptorHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                4,
                DescriptorHeapFlags.ShaderVisible,
                0));
            _descriptorHeapCpuStart = _descriptorHeap.GetCPUDescriptorHandleForHeapStart();
            _descriptorHeapGpuStart = _descriptorHeap.GetGPUDescriptorHandleForHeapStart();
            _descriptorHandleIncrementSize = _device.GetDescriptorHandleIncrementSize(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            _fence = _device.CreateFence(0, FenceFlags.None);
            _fenceValue = 0;
            _logger.LogInformation("Model preview GPU skinning initialized with Direct3D 12 compute shaders");
        }
        catch (Exception ex)
        {
            DisableGpu(ex);
        }
    }

    private ID3D12RootSignature CreateSkinningRootSignature()
    {
        var ranges = new[]
        {
            new DescriptorRange(DescriptorRangeType.ShaderResourceView, 2, 0, 0, DescriptorSlotVertex),
            new DescriptorRange(DescriptorRangeType.UnorderedAccessView, 1, 0, 0, DescriptorSlotOutput),
        };
        var parameters = new[]
        {
            new RootParameter(new RootDescriptorTable(ranges), ShaderVisibility.All),
        };
        var description = new RootSignatureDescription(RootSignatureFlags.None, parameters, []);
        return _device!.CreateRootSignature(description, RootSignatureVersion.Version1);
    }

    private void WriteMeshDescriptors(GpuMeshResources resources)
    {
        if (_device is null)
            throw new InvalidOperationException("The GPU device is unavailable.");

        var vertexHandle = DescriptorSlotHandle(DescriptorSlotVertex);
        var boneHandle = DescriptorSlotHandle(DescriptorSlotBone);
        var outputHandle = DescriptorSlotHandle(DescriptorSlotOutput);

        _device.CreateShaderResourceView(resources.VertexUploadBuffer, new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = DefaultShader4ComponentMapping,
            Buffer = new BufferShaderResourceView
            {
                FirstElement = 0,
                NumElements = resources.VertexCount,
                StructureByteStride = VertexStride,
                Flags = BufferShaderResourceViewFlags.None,
            },
        }, vertexHandle);

        _device.CreateShaderResourceView(resources.BoneUploadBuffer, new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = DefaultShader4ComponentMapping,
            Buffer = new BufferShaderResourceView
            {
                FirstElement = 0,
                NumElements = resources.BoneCount,
                StructureByteStride = BoneStride,
                Flags = BufferShaderResourceViewFlags.None,
            },
        }, boneHandle);

        _device.CreateUnorderedAccessView(resources.OutputBuffer, null, new UnorderedAccessViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView
            {
                FirstElement = 0,
                NumElements = resources.VertexCount,
                StructureByteStride = sizeof(float) * 3,
                Flags = BufferUnorderedAccessViewFlags.None,
            },
        }, outputHandle);
    }

    private CpuDescriptorHandle DescriptorSlotHandle(int slot) => new()
    {
        Ptr = _descriptorHeapCpuStart.Ptr + (nuint)(_descriptorHandleIncrementSize * slot),
    };

    private void DisableGpu(Exception exception)
    {
        foreach (var resources in _meshResources.Values)
            resources.Dispose();
        _meshResources.Clear();
        _fence?.Dispose();
        _fenceEvent.Dispose();
        _descriptorHeap?.Dispose();
        _pipelineState?.Dispose();
        _rootSignature?.Dispose();
        _commandList?.Dispose();
        _commandAllocator?.Dispose();
        _commandQueue?.Dispose();
        _device?.Dispose();
        _fence = null;
        _descriptorHeap = null;
        _pipelineState = null;
        _rootSignature = null;
        _commandList = null;
        _commandAllocator = null;
        _commandQueue = null;
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

        var vertexUploadBuffer = CreateUploadBuffer((ulong)vertices.Length * VertexStride);
        vertexUploadBuffer.SetData(vertices.AsSpan(), 0);

        var boneCount = (uint)mesh.Skinning!.Skeleton.Bones.Count;
        var boneUploadBuffer = CreateUploadBuffer(boneCount * BoneStride);

        var outputBuffer = _device.CreateCommittedResource(
            HeapType.Default,
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)mesh.VertexCount * sizeof(float) * 3, ResourceFlags.AllowUnorderedAccess, 0),
            ResourceStates.Common,
            null);

        var readbackBuffer = _device.CreateCommittedResource(
            HeapType.Readback,
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)mesh.VertexCount * sizeof(float) * 3, ResourceFlags.None, 0),
            ResourceStates.CopyDest,
            null);

        var resources = new GpuMeshResources(
            vertexUploadBuffer,
            boneUploadBuffer,
            outputBuffer,
            readbackBuffer,
            (uint)vertices.Length,
            boneCount);
        _meshResources.Add(mesh, resources);
        return resources;
    }

    private ID3D12Resource CreateUploadBuffer(ulong sizeInBytes)
    {
        if (_device is null)
            throw new InvalidOperationException("The GPU device is unavailable.");
        return _device.CreateCommittedResource(
            HeapType.Upload,
            HeapFlags.None,
            ResourceDescription.Buffer(sizeInBytes, ResourceFlags.None, 0),
            ResourceStates.GenericRead,
            null);
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
            _fence?.Dispose();
            _fenceEvent.Dispose();
            _descriptorHeap?.Dispose();
            _pipelineState?.Dispose();
            _rootSignature?.Dispose();
            _commandList?.Dispose();
            _commandAllocator?.Dispose();
            _commandQueue?.Dispose();
            _device?.Dispose();
            _fence = null;
            _descriptorHeap = null;
            _pipelineState = null;
            _rootSignature = null;
            _commandList = null;
            _commandAllocator = null;
            _commandQueue = null;
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
        ID3D12Resource vertexUploadBuffer,
        ID3D12Resource boneUploadBuffer,
        ID3D12Resource outputBuffer,
        ID3D12Resource readbackBuffer,
        uint vertexCount,
        uint boneCount) : IDisposable
    {
        public ID3D12Resource VertexUploadBuffer { get; } = vertexUploadBuffer;
        public ID3D12Resource BoneUploadBuffer { get; } = boneUploadBuffer;
        public ID3D12Resource OutputBuffer { get; } = outputBuffer;
        public ID3D12Resource ReadbackBuffer { get; } = readbackBuffer;
        public uint VertexCount { get; } = vertexCount;
        public uint BoneCount { get; } = boneCount;

        public void Dispose()
        {
            ReadbackBuffer.Dispose();
            OutputBuffer.Dispose();
            BoneUploadBuffer.Dispose();
            VertexUploadBuffer.Dispose();
        }
    }
}
