using Helldivers2ModManager.Core.Common;

namespace Helldivers2ModManager.Frontend.Services;

public sealed class TaskExecutionService(IBackgroundTaskRunner runner)
{
    private readonly IBackgroundTaskRunner _runner = runner;

    public event EventHandler<BackgroundTaskState>? Changed;

    public Task<BackgroundTaskResult> RunAsync(
        string name,
        string description,
        Func<IBackgroundTaskContext, CancellationToken, Task> work,
        CancellationToken cancellationToken = default) =>
        _runner.RunAsync(name, description, work, state => Changed?.Invoke(this, state), cancellationToken);
}
