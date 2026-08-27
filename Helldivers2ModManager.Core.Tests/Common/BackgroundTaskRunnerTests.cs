using System.Collections.Concurrent;
using Helldivers2ModManager.Core.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Common;

[TestClass]
public sealed class BackgroundTaskRunnerTests
{
    [TestMethod]
    public async Task RunAsync_ShouldDeliverStepUpdatesBeforeFinalState()
    {
        var states = new ConcurrentQueue<BackgroundTaskState>();

        var result = await new BackgroundTaskRunner().RunAsync(
            "deploy",
            "Deploy enabled mods",
            (task, _) =>
            {
                var stepId = task.AddStep("copy files", BackgroundStepStatus.Running);
                task.ReportProgress(0.25);
                task.UpdateStep(stepId, "copy files", BackgroundStepStatus.Succeeded);
                task.ReportProgress(1);
                return Task.CompletedTask;
            },
            states.Enqueue).ConfigureAwait(false);

        Assert.AreEqual(BackgroundTaskStatus.Succeeded, result.Status);
        Assert.IsTrue(states.Count >= 4);
        Assert.AreEqual(BackgroundTaskStatus.Succeeded, states.Last().Status);
        Assert.AreEqual(BackgroundStepStatus.Succeeded, states.Last().Steps.Single().Status);
    }

    [TestMethod]
    public async Task RunAsync_ShouldReturnFailureForException()
    {
        var result = await new BackgroundTaskRunner().RunAsync(
            "import",
            "Import mod",
            (_, _) => throw new InvalidDataException("invalid manifest")).ConfigureAwait(false);

        Assert.AreEqual(BackgroundTaskStatus.Failed, result.Status);
        Assert.AreEqual(CoreErrorCode.Unknown, result.Error!.Value.Code);
        Assert.AreEqual("invalid manifest", result.Error!.Value.Message);
    }

    [TestMethod]
    public async Task RunAsync_ShouldHonorCooperativeCancellation()
    {
        var started = new TaskCompletionSource();
        using var cancellationTokenSource = new CancellationTokenSource();

        var runTask = new BackgroundTaskRunner().RunAsync(
            "hash",
            "Calculate hashes",
            async (_, cancellationToken) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken: cancellationTokenSource.Token);

        await started.Task.ConfigureAwait(false);
        cancellationTokenSource.Cancel();
        var result = await runTask.ConfigureAwait(false);

        Assert.AreEqual(BackgroundTaskStatus.Canceled, result.Status);
        Assert.IsNull(result.Error);
    }
}
