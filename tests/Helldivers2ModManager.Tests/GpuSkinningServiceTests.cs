using System.Numerics;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class GpuSkinningServiceTests
{
    [TestMethod]
    public void TrySkinPositions_TranslatesVerticesThroughDirect3D12Pipeline()
    {
        var service = new GpuSkinningService(new ConsoleLogger());
        try
        {
            if (!service.IsAvailable)
            {
                Assert.Inconclusive("No Direct3D 12 device is available in this environment.");
                return;
            }

            var mesh = CreateTestMesh(4);
            var transforms = new[] { Matrix4x4.CreateTranslation(0f, 5f, 0f) };

            Assert.IsTrue(
                service.TrySkinPositions(mesh, transforms, CancellationToken.None, out var positions),
                "The first dispatch should succeed once the device is available.");
            Assert.AreEqual(mesh.Positions.Length, positions.Length);
            for (var vertex = 0; vertex < mesh.VertexCount; vertex++)
            {
                Assert.AreEqual(mesh.Positions[vertex * 3], positions[vertex * 3], 1e-4f, "X should be unchanged.");
                Assert.AreEqual(mesh.Positions[vertex * 3 + 1] + 5f, positions[vertex * 3 + 1], 1e-4f, "Y should be translated by 5.");
                Assert.AreEqual(mesh.Positions[vertex * 3 + 2], positions[vertex * 3 + 2], 1e-4f, "Z should be unchanged.");
            }

            // The second dispatch reuses the command allocator, fence and mesh buffers.
            var secondTransforms = new[] { Matrix4x4.CreateTranslation(1f, 0f, 2f) };
            Assert.IsTrue(
                service.TrySkinPositions(mesh, secondTransforms, CancellationToken.None, out positions),
                "A repeated dispatch should succeed with recycled GPU resources.");
            Assert.AreEqual(mesh.Positions[0] + 1f, positions[0], 1e-4f);
            Assert.AreEqual(mesh.Positions[1] + 0f, positions[1], 1e-4f);
            Assert.AreEqual(mesh.Positions[2] + 2f, positions[2], 1e-4f);
            Assert.AreEqual(2, service.SuccessfulDispatchCount);
        }
        finally
        {
            service.Dispose();
        }
    }

    [TestMethod]
    public void TrySkinPositions_RejectsMeshWithoutSkinningData()
    {
        var service = new GpuSkinningService(NullLogger<GpuSkinningService>.Instance);
        try
        {
            var mesh = CreateTestMesh(4, withSkinning: false);
            var transforms = new[] { Matrix4x4.Identity };
            Assert.IsFalse(service.TrySkinPositions(mesh, transforms, CancellationToken.None, out var positions));
            Assert.AreEqual(0, positions.Length);
        }
        finally
        {
            service.Dispose();
        }
    }

    private sealed class ConsoleLogger : ILogger<GpuSkinningService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            Console.WriteLine($"[{logLevel}] {formatter(state, exception)}");
            if (exception is not null) Console.WriteLine(exception);
        }
    }

    private static ModelPreviewMesh CreateTestMesh(int vertexCount, bool withSkinning = true)
    {
        var positions = new float[vertexCount * 3];
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            positions[vertex * 3] = vertex;
            positions[vertex * 3 + 1] = 10f + vertex;
            positions[vertex * 3 + 2] = 20f + vertex;
        }

        ModelPreviewSkinningData? skinning = null;
        if (withSkinning)
        {
            var skeleton = new ModelPreviewSkeleton
            {
                BonesId = 1,
                StateMachineId = 1,
                Bones = [new ModelPreviewSkeletonBone(-1, 0, Matrix4x4.Identity)],
            };
            var transformIndices = new int[vertexCount * ModelPreviewSkinningData.InfluencesPerVertex];
            var weights = new float[vertexCount * ModelPreviewSkinningData.InfluencesPerVertex];
            for (var vertex = 0; vertex < vertexCount; vertex++)
            {
                transformIndices[vertex * ModelPreviewSkinningData.InfluencesPerVertex] = 0;
                weights[vertex * ModelPreviewSkinningData.InfluencesPerVertex] = 1f;
            }
            skinning = new ModelPreviewSkinningData
            {
                Skeleton = skeleton,
                TransformIndices = transformIndices,
                Weights = weights,
            };
        }

        return new ModelPreviewMesh
        {
            PatchFile = "test.patch_0",
            UnitId = 1,
            StreamIndex = 0,
            Positions = positions,
            TriangleIndices = [],
            Skinning = skinning,
        };
    }
}
