using System.Reflection;
using Helldivers2ModManager.Frontend.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Frontend.Tests;

[TestClass]
public sealed class TagManagementPageViewTests
{
    [TestMethod]
    public void TagManagementPageView_InitializesSharedResources()
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = new TagManagementPageView();
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
