using System.IO;
using Helldivers2ModManager.Frontend;
using Helldivers2ModManager.Frontend.Navigation;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Deployment;
using Helldivers2ModManager.Core.GameData;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Core.Profiles;
using Helldivers2ModManager.Frontend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Frontend.Tests;

[TestClass]
public sealed class NavigationStoreTests
{
    [TestMethod]
    public async Task Navigate_ReplacesCurrentPageInSeparateScope()
    {
        var root = Path.Combine(Path.GetTempPath(), "Helldivers2ModManagerFrontendTests", Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCommon();
        services.AddPersistence(Path.Combine(root, "data", "mod_manager.db"));
        services.AddMods();
        services.AddProfiles();
        services.AddDeployment();
        services.AddGameData();
        services.AddSingleton(new ApplicationPaths(root));
        services.AddFrontend();
        await using var provider = services.BuildServiceProvider();
        using var navigation = provider.GetRequiredService<INavigationStore>();

        var firstPage = navigation.CurrentPage;
        navigation.Navigate("System.Help");
        var secondPage = navigation.CurrentPage;

        Assert.AreNotSame(firstPage, secondPage);
        Assert.AreEqual("System.Help", navigation.CurrentRouteKey);
    }
}
