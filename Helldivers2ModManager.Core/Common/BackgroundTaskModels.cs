namespace Helldivers2ModManager.Core.Common;

public enum BackgroundTaskStatus
{
    Running,
    Succeeded,
    Failed,
    Canceled,
}

public enum BackgroundStepStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
}

public sealed record BackgroundTaskStep(string Id, string Name, BackgroundStepStatus Status);

public sealed record BackgroundTaskState(
    Guid Id,
    string Name,
    string Description,
    BackgroundTaskStatus Status,
    double? Progress,
    IReadOnlyList<BackgroundTaskStep> Steps);

public sealed record BackgroundTaskResult(
    Guid Id,
    BackgroundTaskStatus Status,
    Error? Error);

public interface IBackgroundTaskContext
{
    void ReportProgress(double? progress);

    string AddStep(string name, BackgroundStepStatus status = BackgroundStepStatus.Pending);

    void UpdateStep(string stepId, string name, BackgroundStepStatus status);
}

public interface IBackgroundTaskRunner
{
    Task<BackgroundTaskResult> RunAsync(
        string name,
        string description,
        Func<IBackgroundTaskContext, CancellationToken, Task> work,
        Action<BackgroundTaskState>? onChanged = null,
        CancellationToken cancellationToken = default);
}
