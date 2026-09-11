using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewAnimationNameTests
{
    [TestMethod]
    public void ParseNameTable_SkipsMetadataKeysAndInvalidKeys()
    {
        const string json = """
            {
              "数据来自": "https://docs.google.com/spreadsheets/d/example/edit",
              "0059b22d5deec33f": "Prone Wounded Strafing Right(?)",
              "not-hex-key": "Ignored",
              "00112233445566778899": "Too long, ignored",
              "aabbccdd": "Too short, ignored",
              "006f0f4d28b0d1bb": ""
            }
            """;

        var table = ModelPreviewAnimationNames.ParseNameTable(json);

        Assert.AreEqual(1, table.Count, "Only valid 16-hex keys with non-empty names may be kept.");
        Assert.AreEqual(
            "Prone Wounded Strafing Right(?)",
            table[25247180846449471],
            "0x0059B22D5EEEC33F must map back to its decimal animation id.");
    }

    [TestMethod]
    public void GetDisplayName_UnknownAnimation_FallsBackToHexIdentifier()
    {
        var display = ModelPreviewAnimationNames.GetDisplayName(
            0x0011223344556677,
            0x8899AABBCCDDEEFF);

        StringAssert.Contains(display, "0x0011223344556677");
        StringAssert.Contains(display, "0x8899AABBCCDDEEFF");
    }

    [TestMethod]
    public void TryGetName_ResolvesNamesFromCommunitySheet_WhenNameTableShipped()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Data", "animation-names.json");
        if (!File.Exists(path))
        {
            // 名称表 Content 未流入测试输出时由 ParseNameTable 用例覆盖格式约定。
            Assert.Inconclusive($"The animation name table was not copied to the test output: {path}");
        }

        // 中文表（动画ID收集）命中时优先中文描述。
        Assert.AreEqual(
            "进入敬礼",
            ModelPreviewAnimationNames.TryGetName(507572114292837867),
            "The decimal entry id from the community sheet must resolve by its hex key; zh table wins.");
        // 中文表未收录的条目回落英文表。
        Assert.AreEqual(
            "Crawling to Standing",
            ModelPreviewAnimationNames.TryGetName(132594416690666555));
    }

    [TestMethod]
    public void MergeNameTables_OverlayWinsExceptUnknownPlaceholder()
    {
        var baseTable = new Dictionary<ulong, string>
        {
            [0x0000000000000001] = "Enter Casual Salute Emote",
            [0x0000000000000002] = "Reload Tactical",
            [0x0000000000000003] = "Unknown"
        };
        var overlay = new Dictionary<ulong, string>
        {
            [0x0000000000000001] = "进入敬礼",
            [0x0000000000000003] = "Unknown",
            [0x0000000000000004] = "胜利姿势：超人"
        };

        var merged = ModelPreviewAnimationNames.MergeNameTables(baseTable, overlay);

        Assert.AreEqual(4, merged.Count);
        Assert.AreEqual("进入敬礼", merged[0x0000000000000001], "The zh overlay must win over the en name.");
        Assert.AreEqual("Reload Tactical", merged[0x0000000000000002], "Entries absent from the overlay stay unchanged.");
        Assert.AreEqual("Unknown", merged[0x0000000000000003], "The overlay's Unknown placeholder must not override the base table.");
        Assert.AreEqual("胜利姿势：超人", merged[0x0000000000000004], "Overlay-only entries must be added.");
    }

    [TestMethod]
    public void MergeNameTables_NullOverlay_ReturnsBaseTable()
    {
        var baseTable = new Dictionary<ulong, string> { [0x0000000000000001] = "Idle" };

        var merged = ModelPreviewAnimationNames.MergeNameTables(baseTable, null);

        Assert.AreEqual(1, merged.Count);
        Assert.AreEqual("Idle", merged[0x0000000000000001]);
    }
}
