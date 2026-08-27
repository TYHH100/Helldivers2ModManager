using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Localization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Globalization;
using System.Resources;

namespace Helldivers2ModManager.Core.Tests.Localization;

[TestClass]
public sealed class LocalizationCatalogTests
{
    [TestMethod]
    public void GetString_ReadsChineseAndEnglishResources()
    {
        var catalog = new LocalizationCatalog();
        var key = GetFirstKey(new CultureInfo("zh-CN"));

        Assert.IsTrue(catalog.Contains(key, new CultureInfo("zh-CN")));
        Assert.IsTrue(catalog.Contains(key, new CultureInfo("en-US")));
        Assert.AreNotEqual(string.Empty, catalog.GetString(key, new CultureInfo("zh-CN")));
        Assert.AreNotEqual(string.Empty, catalog.GetString(key, new CultureInfo("en-US")));
    }

    [TestMethod]
    public void ResourceSets_HaveSameCompleteKeys()
    {
        var neutral = ReadKeys(CultureInfo.InvariantCulture);
        var chinese = ReadKeys(new CultureInfo("zh-CN"));
        var english = ReadKeys(new CultureInfo("en-US"));

        Assert.IsTrue(neutral.Count > 500);
        CollectionAssert.AreEquivalent(neutral.ToArray(), chinese.ToArray());
        CollectionAssert.AreEquivalent(neutral.ToArray(), english.ToArray());
    }

    [TestMethod]
    public void EveryCoreErrorCode_HasLocalizationInBothLanguages()
    {
        foreach (Enum code in Enum.GetValues(typeof(CoreErrorCode)))
        {
            var key = $"ErrorCode.{code}";
            Assert.IsTrue(ReadKeys(new CultureInfo("zh-CN")).Contains(key), key);
            Assert.IsTrue(ReadKeys(new CultureInfo("en-US")).Contains(key), key);
        }
    }

    private static string GetFirstKey(CultureInfo culture)
    {
        var keys = ReadKeys(culture).Where(key => !key.StartsWith("ErrorCode.", StringComparison.Ordinal)).ToArray();
        Assert.IsTrue(keys.Length > 0);
        return keys[0];
    }

    private static HashSet<string> ReadKeys(CultureInfo culture)
    {
        var manager = new ResourceManager(
            "Helldivers2ModManager.Core.Localization.StringResources",
            typeof(LocalizationCatalog).Assembly);
        using var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!;
        var keys = set.Cast<System.Collections.DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(keys.Count > 0);
        return keys;
    }
}

