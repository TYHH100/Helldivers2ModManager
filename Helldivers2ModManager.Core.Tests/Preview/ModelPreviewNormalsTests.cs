using Helldivers2ModManager.Core.Preview;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Preview;

[TestClass]
public sealed class ModelPreviewNormalsTests
{
    [TestMethod]
    public void BuildSmoothedNormals_NormalizesTriangleVertexNormals()
    {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        var normals = ModelPreviewNormals.BuildSmoothedNormals(positions, [0, 1, 2]);
        Assert.AreEqual(9, normals.Length);
        for (var index = 0; index < normals.Length; index += 3)
        {
            Assert.AreEqual(0f, normals[index], 0.0001f);
            Assert.AreEqual(0f, normals[index + 1], 0.0001f);
            Assert.AreEqual(1f, normals[index + 2], 0.0001f);
        }
    }
}
