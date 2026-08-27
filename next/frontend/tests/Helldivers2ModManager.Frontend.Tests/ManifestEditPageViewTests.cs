using System.Reflection;
using Helldivers2ModManager.Frontend.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Frontend.Tests;

[TestClass]
public sealed class ManifestEditPageViewTests
{
    [TestMethod]
    public void ManifestEditPageView_InitializesSharedResources()
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = new ManifestEditPageView();
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
