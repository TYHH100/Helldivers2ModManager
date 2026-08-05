using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewTextureRequestStateTests
{
    [TestMethod]
    public void IsCurrent_ModelSwitchOrPageClose_PreventsLateOriginalTextureResultFromApplying()
    {
        Assert.IsTrue(ModelPreviewTextureRequestState.IsCurrent(4, 4, cancellationRequested: false));
        Assert.IsFalse(ModelPreviewTextureRequestState.IsCurrent(4, 5, cancellationRequested: false));
        Assert.IsFalse(ModelPreviewTextureRequestState.IsCurrent(4, 4, cancellationRequested: true));
    }
}
