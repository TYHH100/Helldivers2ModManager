using System.Text.Json;
using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModManifestDescriptionFallbackTests
{
    private const string BaseFields =
        "\"Guid\": \"4c0dc24b-c7a0-46e1-b0c2-18e3dbb8d4e1\"," +
        "\"Name\": \"Test Mod\"";

    [TestMethod]
    public void V1_MissingDescription_FallsBackToEmptyString()
    {
        var manifest = ParseV1WithoutDescription();

        Assert.IsInstanceOfType<V1ModManifest>(manifest);
        Assert.AreEqual(string.Empty, manifest.Description);
    }

    [TestMethod]
    public void Legacy_MissingDescription_FallsBackToEmptyString()
    {
        var manifest = ParseLegacyWithoutDescription();

        Assert.IsInstanceOfType<LegacyModManifest>(manifest);
        Assert.AreEqual(string.Empty, manifest.Description);
    }

    [TestMethod]
    public void V1_NonStringDescription_FallsBackToEmptyString()
    {
        using var doc = JsonDocument.Parse($"{{\"Version\": 1, {BaseFields}, \"Description\": 123}}");
        var manifest = ModManifest.DeserializeFromDocument(doc);

        Assert.IsInstanceOfType<V1ModManifest>(manifest);
        Assert.AreEqual(string.Empty, manifest.Description);
    }

    [TestMethod]
    public void V1_PresentDescription_IsKept()
    {
        using var doc = JsonDocument.Parse($"{{\"Version\": 1, {BaseFields}, \"Description\": \"Hello\"}}");
        var manifest = ModManifest.DeserializeFromDocument(doc);

        Assert.IsInstanceOfType<V1ModManifest>(manifest);
        Assert.AreEqual("Hello", manifest.Description);
    }

    private static IModManifest ParseV1WithoutDescription()
    {
        using var doc = JsonDocument.Parse($"{{\"Version\": 1, {BaseFields}}}");
        return ModManifest.DeserializeFromDocument(doc);
    }

    private static IModManifest ParseLegacyWithoutDescription()
    {
        using var doc = JsonDocument.Parse($"{{ {BaseFields} }}");
        return ModManifest.DeserializeFromDocument(doc);
    }
}
