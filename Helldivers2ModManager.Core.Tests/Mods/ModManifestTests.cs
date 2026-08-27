using System.Text.Json;
using Helldivers2ModManager.Core.Mods;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Mods;

[TestClass]
public sealed class ModManifestTests
{
    private const string ValidGuid = "4c0dc24b-c7a0-46e1-b0c2-18e3dbb8d4e1";
    private const string MisusedVersionAndMissingDescriptionJson = """
        {
            "Version": 260501,
            "Guid": "9535145d-1db9-40c5-85e5-9ce36b8301e8",
            "Name": "惠专武 替换 B/FLAM-80焚燃者 5.1",
            "Options": [
                {
                    "Name": "武器",
                    "Description": "多选一",
                    "SubOptions": [
                        { "Name": "大余量条", "Include": ["武器/大余量条"] },
                        { "Name": "小刻度盘", "Include": ["武器/小刻度盘"] }
                    ]
                },
                {
                    "Name": "背包",
                    "SubOptions": [
                        { "Name": "火焰纹理存量条", "Include": ["背包/火焰纹理存量条"] },
                        { "Name": "左侧刻度条", "Include": ["背包/左侧刻度条"] },
                        { "Name": "右侧容量条", "Include": ["背包/右侧存量条"] }
                    ]
                }
            ]
        }
        """;

    [TestMethod]
    public void RealWorldManifest_MisusedVersion_MissingDescription_TrailingComma_ImportsAsV1()
    {
        var manifest = ModManifest.DeserializeFromJson(MisusedVersionAndMissingDescriptionJson);
        Assert.IsInstanceOfType(manifest, typeof(V1ModManifest));
        var v1 = (V1ModManifest)manifest;

        Assert.AreEqual("惠专武 替换 B/FLAM-80焚燃者 5.1", v1.Name);
        Assert.AreEqual(string.Empty, v1.Description);
        Assert.IsNotNull(v1.Options);
        Assert.AreEqual(2, v1.Options.Count);
        Assert.AreEqual(2, v1.Options[0].SubOptions!.Count);
        Assert.AreEqual("大余量条", v1.Options[0].SubOptions![0].Name);
        Assert.AreEqual(3, v1.Options[1].SubOptions!.Count);
        Assert.AreEqual("右侧容量条", v1.Options[1].SubOptions![2].Name);
    }

    [TestMethod]
    public void VersionOne_ParsedAsV1()
    {
        var manifest = ModManifest.DeserializeFromJson($$"""{"Version":1,"Guid":"{{ValidGuid}}","Name":"Test Mod","Description":"Test"}""");
        Assert.IsInstanceOfType<V1ModManifest>(manifest);
    }

    [TestMethod]
    public void NoVersionField_ParsedAsLegacy()
    {
        var manifest = ModManifest.DeserializeFromJson($$"""{"Guid":"{{ValidGuid}}","Name":"Test Mod","Description":"Test"}""");
        Assert.IsInstanceOfType<LegacyModManifest>(manifest);
    }

    [DataTestMethod]
    [DataRow("2")]
    [DataRow("5")]
    [DataRow("\"2\"")]
    [DataRow("1.5")]
    public void MisusedVersionValues_AreTreatedAsV1(string version)
    {
        var json = $$"""{"Version":{{version}},"Guid":"{{ValidGuid}}","Name":"Test Mod","Description":"Test"}""";
        Assert.IsInstanceOfType<V1ModManifest>(ModManifest.DeserializeFromJson(json));
    }

    [TestMethod]
    public void V1_NonHexCharacterGuid_FallsBackToNewGuid()
    {
        var manifest = ModManifest.DeserializeFromJson("{\"Version\": 1, \"Guid\": \"i6d7e8f9-0a1b-2c3d-4e5f-6a7b8c9d0e1f\", \"Name\": \"Test\"}");
        Assert.IsInstanceOfType<V1ModManifest>(manifest);
        Assert.AreNotEqual(Guid.Empty, manifest.Guid);
    }

    [TestMethod]
    public void Legacy_NonHexCharacterGuid_FallsBackToNewGuid()
    {
        var manifest = ModManifest.DeserializeFromJson("{\"Guid\": \"i6d7e8f9-0a1b-2c3d-4e5f-6a7b8c9d0e1f\", \"Name\": \"Test\"}");
        Assert.IsInstanceOfType<LegacyModManifest>(manifest);
        Assert.AreNotEqual(Guid.Empty, manifest.Guid);
    }

    [TestMethod]
    public void MissingGuid_FallsBackToNewGuid()
    {
        Assert.AreNotEqual(Guid.Empty, ModManifest.DeserializeFromJson("{\"Version\": 1, \"Name\": \"Test\"}").Guid);
        Assert.AreNotEqual(Guid.Empty, ModManifest.DeserializeFromJson("{\"Name\": \"Test\"}").Guid);
    }

    [TestMethod]
    public void ValidGuid_IsKept()
    {
        var manifest = ModManifest.DeserializeFromJson($"{{\"Version\": 1, \"Guid\": \"{ValidGuid}\", \"Name\": \"Test\"}}");
        Assert.AreEqual(Guid.Parse(ValidGuid), manifest.Guid);
    }

    [DataTestMethod]
    public async Task Description_MissingOrNonString_FallsBackToEmptyString()
    {
        await Task.CompletedTask;
        foreach (var suffix in new[] { string.Empty, ",\"Description\":123" })
        {
            var json = $"{{\"Version\":1,\"Guid\":\"{ValidGuid}\",\"Name\":\"Test\"{suffix}}}";
            var manifest = (V1ModManifest)ModManifest.DeserializeFromJson(json);
            Assert.AreEqual(string.Empty, manifest.Description);

            var legacy = (LegacyModManifest)ModManifest.DeserializeFromJson($"{{\"Guid\":\"{ValidGuid}\",\"Name\":\"Test\"{suffix}}}");
            Assert.AreEqual(string.Empty, legacy.Description);
        }
    }

    [TestMethod]
    public void InvalidImagePaths_AreCleared_ValidOnesKept()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2mm-core-manifest-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "mod"));
            File.WriteAllText(Path.Combine(directory.FullName, "ok.png"), "x");
            const string json = """
                {
                  "Version": 1,
                  "Guid": "4c0dc24b-c7a0-46e1-b0c2-18e3dbb8d4e1",
                  "Name": "Test",
                  "IconPath": "",
                  "Options": [
                    {
                      "Name": "B",
                      "Image": "missing-b.png",
                      "Include": ["B"],
                      "SubOptions": [{ "Name": "B1", "Image": "missing-b1.png", "Include": ["B/B1"] }]
                    },
                    { "Name": "A", "Image": "ok.png", "Include": ["A"] }
                  ]
                }
                """;
            var manifest = (V1ModManifest)ModManifest.DeserializeFromJson(json);
            var sanitized = (V1ModManifest)ModManifestSanitizer.SanitizeImagePaths(manifest, directory, NullLogger.Instance);

            Assert.IsNull(sanitized.IconPath);
            Assert.IsNull(sanitized.Options![0].Image);
            Assert.IsNull(sanitized.Options![0].SubOptions![0].Image);
            Assert.AreEqual("ok.png", sanitized.Options![1].Image);
            Assert.AreSame(manifest.Options![1], sanitized.Options![1]);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Serialization_PreservesLegacyAndV1Shapes()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2mm-core-manifest-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var directory = Directory.CreateDirectory(root);
            const string guid = ValidGuid;
            var legacyJson = ModManifest.Serialize(new LegacyModManifest
            {
                Guid = Guid.Parse(guid),
                Name = "Legacy",
                Description = "",
                Options = ["A"],
            });
            StringAssert.Contains(legacyJson, "\"Guid\"");
            StringAssert.DoesNotMatch(legacyJson, new System.Text.RegularExpressions.Regex("\"Version\""));

            var v1 = new V1ModManifest
            {
                Guid = Guid.Parse(guid),
                Name = "V1",
                Description = "",
                Options =
                [
                    new(
                        "Option",
                        "",
                        ["A"],
                        null,
                        [new("Sub", "", ["A/B"], null)]),
                ],
                NexusData = new NexusManifestData(42, "1.0"),
            };
            var v1Json = ModManifest.Serialize(v1);
            StringAssert.Contains(v1Json, "\"Version\": 1");
            StringAssert.Contains(v1Json, "\"Image\": null");
            StringAssert.Contains(v1Json, "\"NexusData\"");

            using var document = JsonDocument.Parse(v1Json);
            var parsed = (V1ModManifest)ModManifest.DeserializeFromDocument(document);
            Assert.AreEqual(42, parsed.NexusData!.ModId);
            Assert.IsNull(parsed.Options![0].Image);
            Assert.IsNull(parsed.Options[0].SubOptions![0].Image);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
