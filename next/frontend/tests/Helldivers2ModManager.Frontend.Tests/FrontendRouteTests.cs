using System.Globalization;
using System.IO;
using Helldivers2ModManager.Frontend.ViewModels;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Frontend.Navigation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Frontend.Tests;

[TestClass]
public sealed class FrontendRouteTests
{
    [TestMethod]
    public void Routes_HaveUniqueKeysAndConstructibleViewModels()
    {
        var routes = FrontendRouteRegistry.All;

        Assert.AreEqual(18, routes.Count);
        CollectionAssert.AllItemsAreUnique(routes.Select(route => route.Key).ToArray());
        Assert.IsTrue(routes.All(route => !route.ViewModelType.IsAbstract));
        Assert.IsTrue(routes.All(route => typeof(FrontendPageViewModel).IsAssignableFrom(route.ViewModelType)));
    }

    [TestMethod]
    public void Routes_HaveLocalizedTitlesAndDescriptions()
    {
        var routes = FrontendRouteRegistry.All;
        var cultures = new[] { "en-US", "zh-CN" };

        foreach (var cultureName in cultures)
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            var localization = new LocalizationCatalog();
            foreach (var route in routes)
            {
                Assert.IsTrue(localization.Contains(route.TitleKey, culture), $"Missing title '{route.TitleKey}' for {cultureName}.");
                Assert.IsTrue(localization.Contains(route.DescriptionKey, culture), $"Missing description '{route.DescriptionKey}' for {cultureName}.");
            }
        }
    }
}
