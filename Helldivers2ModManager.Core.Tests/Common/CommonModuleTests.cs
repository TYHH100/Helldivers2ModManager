using Helldivers2ModManager.Core.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Common;

[TestClass]
public sealed class CommonModuleTests
{
    [TestMethod]
    public void AddCommon_ShouldRegisterBackgroundTaskRunner()
    {
        var services = new ServiceCollection();
        services.AddCommon();

        using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<IBackgroundTaskRunner>();

        Assert.IsNotNull(runner);
    }
}
