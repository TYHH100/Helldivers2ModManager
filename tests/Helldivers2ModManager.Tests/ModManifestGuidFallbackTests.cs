using System.Text.Json;
using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModManifestGuidFallbackTests
{
    [TestMethod]
    public void V1_NonHexCharacterGuid_FallsBackToNewGuid()
    {
        using var doc = JsonDocument.Parse("{\"Version\": 1, \"Guid\": \"i6d7e8f9-0a1b-2c3d-4e5f-6a7b8c9d0e1f\", \"Name\": \"Test\"}");
        var manifest = ModManifest.DeserializeFromDocument(doc);

        Assert.IsInstanceOfType<V1ModManifest>(manifest);
        Assert.AreNotEqual(Guid.Empty, manifest.Guid);
    }

    [TestMethod]
    public void Legacy_NonHexCharacterGuid_FallsBackToNewGuid()
    {
        using var doc = JsonDocument.Parse("{\"Guid\": \"i6d7e8f9-0a1b-2c3d-4e5f-6a7b8c9d0e1f\", \"Name\": \"Test\"}");
        var manifest = ModManifest.DeserializeFromDocument(doc);

        Assert.IsInstanceOfType<LegacyModManifest>(manifest);
        Assert.AreNotEqual(Guid.Empty, manifest.Guid);
    }

    [TestMethod]
    public void V1_MissingGuid_FallsBackToNewGuid()
    {
        using var doc = JsonDocument.Parse("{\"Version\": 1, \"Name\": \"Test\"}");
        var manifest = ModManifest.DeserializeFromDocument(doc);

        Assert.IsInstanceOfType<V1ModManifest>(manifest);
        Assert.AreNotEqual(Guid.Empty, manifest.Guid);
    }

    [TestMethod]
    public void ValidGuid_IsKept()
    {
        using var doc = JsonDocument.Parse("{\"Version\": 1, \"Guid\": \"4c0dc24b-c7a0-46e1-b0c2-18e3dbb8d4e1\", \"Name\": \"Test\"}");
        var manifest = ModManifest.DeserializeFromDocument(doc);

        Assert.AreEqual(Guid.Parse("4c0dc24b-c7a0-46e1-b0c2-18e3dbb8d4e1"), manifest.Guid);
    }
}
