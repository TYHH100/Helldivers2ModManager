using System.Text.Json;
using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModManifestVersionTests
{
    private const string V1RequiredFields =
        "\"Guid\": \"4c0dc24b-c7a0-46e1-b0c2-18e3dbb8d4e1\"," +
        "\"Name\": \"Test Mod\"," +
        "\"Description\": \"Test\"";

    [TestMethod]
    public void VersionOne_ParsedAsV1()
    {
        var manifest = ParseWithVersion("1");

        Assert.IsInstanceOfType<V1ModManifest>(manifest);
    }

    [TestMethod]
    public void NoVersionField_ParsedAsLegacy()
    {
        var manifest = ParseWithVersion(null);

        Assert.IsInstanceOfType<LegacyModManifest>(manifest);
    }

    [TestMethod]
    public void VersionTwo_MisusedAsModVersion_TreatedAsV1()
    {
        var manifest = ParseWithVersion("2");

        Assert.IsInstanceOfType<V1ModManifest>(manifest);
    }

    [TestMethod]
    public void VersionFive_MisusedAsModVersion_TreatedAsV1()
    {
        var manifest = ParseWithVersion("5");

        Assert.IsInstanceOfType<V1ModManifest>(manifest);
    }

    [TestMethod]
    public void VersionString_MisusedAsModVersion_TreatedAsV1()
    {
        var manifest = ParseWithVersion("\"2\"");

        Assert.IsInstanceOfType<V1ModManifest>(manifest);
    }

    [TestMethod]
    public void VersionDecimal_MisusedAsModVersion_TreatedAsV1()
    {
        var manifest = ParseWithVersion("1.5");

        Assert.IsInstanceOfType<V1ModManifest>(manifest);
    }

    private static IModManifest ParseWithVersion(string? versionJson)
    {
        var json = versionJson is null
            ? $"{{ {V1RequiredFields} }}"
            : $"{{ \"Version\": {versionJson}, {V1RequiredFields} }}";
        using var doc = JsonDocument.Parse(json);
        return ModManifest.DeserializeFromDocument(doc);
    }
}
