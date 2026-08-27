using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Mods;

[TestClass]
public sealed class AutoTaggingServiceTests
{
    [TestMethod]
    public void Apply_ShouldReuseAliasKeepUserTagsAndReplaceStaleBuiltIns()
    {
        var service = new AutoTaggingService();
        var userId = Guid.NewGuid();
        var audioTagId = Guid.NewGuid();
        var staleArmorId = BuiltInId(ModType.Armor);
        var result = service.Apply(
        [
            new(@"C:\mod", [userId, staleArmorId], [ModType.Audio]),
        ],
        [
            new(userId, "收藏", "#FF0000"),
            new(audioTagId, "音效", "#00FF00"),
        ],
        [],
        Localize,
        createMissingTags: false);

        Assert.AreEqual(1, result.ChangedCount);
        CollectionAssert.AreEqual(new[] { userId, audioTagId }, result.TagIdsByPath[@"C:\mod"].ToArray());
    }

    [TestMethod]
    public void Apply_ShouldHonorManualMappingAndOnlyCreateMissingTagsWhenAllowed()
    {
        var service = new AutoTaggingService();
        var mappedTagId = Guid.NewGuid();
        var result = service.Apply(
        [
            new(@"C:\mapped", [], [ModType.Audio]),
            new(@"C:\missing", [], [ModType.Ui]),
        ],
        [new(mappedTagId, "自定义音频", "#0000FF")],
        [new((int)ModType.Audio, mappedTagId)],
        Localize,
        createMissingTags: false);

        CollectionAssert.AreEqual(new[] { mappedTagId }, result.TagIdsByPath[@"C:\mapped"].ToArray());
        Assert.AreEqual(0, result.TagIdsByPath[@"C:\missing"].Count);
        Assert.AreEqual(1, result.ChangedCount);

        var createdResult = service.Apply(
        [
            new(@"C:\missing", [], [ModType.Ui]),
        ],
        [],
        [],
        Localize,
        createMissingTags: true);

        var uiDefinition = Core.Mods.ModTypeDetectionService.BuiltInTags.Single(static definition => definition.Type == ModType.Ui);
        CollectionAssert.AreEqual(new[] { uiDefinition.Id }, createdResult.TagIdsByPath[@"C:\missing"].ToArray());
        Assert.AreEqual(uiDefinition.Id, createdResult.Tags.Single().Id);
        Assert.AreEqual("name:Ui", createdResult.Tags.Single().Name);
    }

    private static Guid BuiltInId(ModType type) =>
        Core.Mods.ModTypeDetectionService.BuiltInTags.First(definition => definition.Type == type).Id;

    private static string Localize(ModType type) => $"name:{type}";
}
