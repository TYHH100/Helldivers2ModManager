using System.Reflection;
using Helldivers2ModManager.Frontend.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Frontend.Tests;

[TestClass]
public sealed class NexusDownloadPageViewTests
{
    [TestMethod]
    public void NexusDownloadPageView_InitializesSharedResources()
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = new NexusDownloadPageView();
            }
            catch (TargetInvocationException ex)
            {
                exception = ex.InnerException;
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.IsNull(exception, exception?.Message);
    }
}
