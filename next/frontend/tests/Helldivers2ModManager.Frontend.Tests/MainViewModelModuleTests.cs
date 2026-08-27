using System.IO;
using Helldivers2ModManager.Frontend;
using Helldivers2ModManager.Frontend.ViewModels;
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
public sealed class MainViewModelModuleTests
{
    [TestMethod]
    public void Modules_ContainFiveGroupsAndRouteDiagnosticsUnderAnalysis()
    {
        using var provider = CreateProvider();
        var viewModel = provider.GetRequiredService<MainViewModel>();

        CollectionAssert.AreEqual(
            new[] { "Library", "Deployment", "Tools", "Analysis", "System" },
            viewModel.Modules.Select(module => module.Key).ToArray());
        Assert.IsTrue(viewModel.Modules.Single(module => module.Key == "Analysis")
            .Pages.Any(page => page.RouteKey == "Diagnostics.BackendTestCenter"));
    }

    [TestMethod]
    public void SelectModule_ShowsFirstPageOfModule()
    {
        using var provider = CreateProvider();
        var viewModel = provider.GetRequiredService<MainViewModel>();

        viewModel.SelectModuleCommand.Execute("Tools");

        Assert.AreEqual("Tools", viewModel.CurrentModule.Key);
        Assert.AreEqual("Tools.Create", viewModel.CurrentRouteKey);
        Assert.IsTrue(viewModel.CurrentSubPages.Single(page => page.RouteKey == "Tools.Create").IsCurrent);
    }

    [TestMethod]
    public void Navigate_KeepsModuleSelectedAndUpdatesCurrentPage()
    {
        using var provider = CreateProvider();
        var viewModel = provider.GetRequiredService<MainViewModel>();

        viewModel.SelectModuleCommand.Execute("Tools");
        viewModel.NavigateCommand.Execute("Tools.Tags");

        Assert.AreEqual("Tools", viewModel.CurrentModule.Key);
        Assert.AreEqual("Tools.Tags", viewModel.CurrentRouteKey);
        Assert.IsTrue(viewModel.CurrentSubPages.Single(page => page.RouteKey == "Tools.Tags").IsCurrent);
        Assert.IsFalse(viewModel.CurrentSubPages.Single(page => page.RouteKey == "Tools.Create").IsCurrent);
    }

    private static ServiceProvider CreateProvider()
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
        return services.BuildServiceProvider();
    }
}
