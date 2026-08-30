using System.Text.Json;
using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModManifestRealWorldFixtureTests
{
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
                        {
                            "Name": "大余量条",
                            "Description": "位于武器右侧的大余量条，非常清楚",
                            "Include": [
                                "武器/大余量条"
                            ]
                        },
                        {
                            "Name": "小刻度盘",
                            "Description": "防炸mod",
                            "Include": [
                                "武器/小刻度盘"
                            ]
                        }
                    ],
                },
                {
                    "Name": "背包",
                    "Description": "多选一",
                    "SubOptions": [
                        {
                            "Name": "火焰纹理存量条",
                            "Description": "背包上的火焰纹里随着燃料减少退却",
                            "Include": [
                                "背包/火焰纹理存量条"
                            ]
                        },
                        {
                            "Name": "左侧刻度条",
                            "Description": "左侧刻度条随着燃料减少变短",
                            "Include": [
                                "背包/左侧刻度条"
                            ]
                        },
                        {
                            "Name": "右侧容量条",
                            "Description": "右侧容量条随着燃料减少变短，大多数情况下都可以看清楚",
                            "Include": [
                                "背包/右侧存量条"
                            ]
                        }
                    ]
                }
            ]
        }
        """;

    [TestMethod]
    public void RealWorldManifest_MisusedVersion_MissingDescription_TrailingComma_ImportsAsV1()
    {
        var options = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };
        using var doc = JsonDocument.Parse(MisusedVersionAndMissingDescriptionJson, options);
        var manifest = ModManifest.DeserializeFromDocument(doc);

        Assert.IsInstanceOfType<V1ModManifest>(manifest);
        var v1 = (V1ModManifest)manifest;
        Assert.AreEqual("惠专武 替换 B/FLAM-80焚燃者 5.1", v1.Name);
        Assert.AreEqual(string.Empty, v1.Description);
        Assert.IsNotNull(v1.Options);
        Assert.AreEqual(2, v1.Options.Count);
        Assert.AreEqual("武器", v1.Options[0].Name);
        Assert.AreEqual(2, v1.Options[0].SubOptions!.Count);
        Assert.AreEqual("背包", v1.Options[1].Name);
        Assert.AreEqual(3, v1.Options[1].SubOptions!.Count);
        Assert.AreEqual("大余量条", v1.Options[0].SubOptions![0].Name);
        Assert.AreEqual("右侧容量条", v1.Options[1].SubOptions![2].Name);
    }
}
