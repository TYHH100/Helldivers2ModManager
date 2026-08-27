using Helldivers2ModManager.Core.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Common;

[TestClass]
public sealed class ResultTests
{
    [TestMethod]
    public void Success_ShouldExposeValue()
    {
        Result<string> result = Result.Success("ready");

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("ready", result.Value);
    }

    [TestMethod]
    public void Failure_ShouldExposeErrorCodeAndRejectValue()
    {
        Result<int> result = Result.Fail<int>(Error.Create(CoreErrorCode.InvalidFormat, "bad format"));

        Assert.IsTrue(result.Failed);
        Assert.AreEqual(CoreErrorCode.InvalidFormat, result.Error.Code);
        Assert.ThrowsException<InvalidOperationException>(() => result.Value);
    }
}
