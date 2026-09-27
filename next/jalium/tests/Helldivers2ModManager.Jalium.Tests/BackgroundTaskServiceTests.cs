using System.Collections.Concurrent;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Tests;

[TestClass]
public sealed class BackgroundTaskServiceTests
{
    [TestMethod]
    public async Task WorkerStepsCompleteInOrderAndForegroundTaskIsRemoved()
    {
        var queued = new ConcurrentQueue<Action>();
        var onUi = false;
        var service = new BackgroundTaskService(queued.Enqueue, () => onUi);
        BackgroundTaskItem? background = null;

        await service.RunAsync("scan", "running", (context, _) =>
        {
            context.ReportStep("first");
            context.ReportStepDetail("file");
            context.CompleteStep();
            context.ReportStep("second");
            context.Report(progress: 0.8, isIndeterminate: false);
            return Task.CompletedTask;
        }, taskCreated: task => background = task);

        Assert.AreEqual(0, service.Tasks.Count);
        onUi = true;
        while (queued.TryDequeue(out var action)) action();
        Assert.IsNotNull(background);
        Assert.AreEqual(BackgroundTaskStatus.Completed, background.Status);
        Assert.AreEqual(2, background.Steps.Count);
        Assert.AreEqual("second", background.Steps[0].Text);
        Assert.AreEqual(TaskStepStatus.Completed, background.Steps[1].Status);
        Assert.AreEqual(1, background.Progress);
        Assert.AreEqual(1, service.Tasks.Count);

        onUi = false;
        await service.RunAsync("deploy", "foreground", (_, _) => Task.CompletedTask,
            isForeground: true);
        onUi = true;
        while (queued.TryDequeue(out var action)) action();
        Assert.AreEqual(1, service.Tasks.Count);
        Assert.AreSame(background, service.Tasks[0]);
    }
}
