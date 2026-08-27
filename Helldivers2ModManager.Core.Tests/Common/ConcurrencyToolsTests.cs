using Helldivers2ModManager.Core.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Common;

[TestClass]
public sealed class ConcurrencyToolsTests
{
    [TestMethod]
    public async Task ConcurrencyLimiter_ShouldEnforceMaximumConcurrency()
    {
        using var limiter = new ConcurrencyLimiter(2);
        var current = 0;
        var maximum = 0;

        var tasks = Enumerable.Range(0, 8).Select(_ => limiter.RunAsync(async _ =>
        {
            var now = Interlocked.Increment(ref current);
            maximum = Math.Max(maximum, Volatile.Read(ref now));
            await Task.Delay(20).ConfigureAwait(false);
            Interlocked.Decrement(ref current);
        })).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        Assert.IsTrue(maximum <= 2);
    }

    [TestMethod]
    public async Task AsyncKeyedLock_ShouldSerializeSameKeyOnly()
    {
        var locker = new AsyncKeyedLock<string>();
        var first = await locker.LockAsync("mod").ConfigureAwait(false);

        var secondTask = locker.LockAsync("mod");
        await Task.Delay(50).ConfigureAwait(false);
        Assert.IsFalse(secondTask.IsCompleted);

        await first.DisposeAsync().ConfigureAwait(false);
        await using var second = await secondTask.ConfigureAwait(false);
        Assert.IsTrue(secondTask.IsCompletedSuccessfully);
    }

    [DataTestMethod]
    [DataRow(-4, 2)]
    [DataRow(0, 2)]
    [DataRow(3, 2)]
    [DataRow(5, 2)]
    [DataRow(7, 3)]
    [DataRow(16, 4)]
    public void GetIoParallelism_ShouldUseCentralClamp(int processors, int expected)
    {
        Assert.AreEqual(expected, ConcurrencyPolicy.GetIoParallelism(processors));
    }
}
