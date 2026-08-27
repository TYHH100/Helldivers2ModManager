using System.Threading.Channels;

namespace Helldivers2ModManager.Core.Common;

public sealed class BackgroundTaskRunner : IBackgroundTaskRunner
{
    private abstract record PipelineEvent;

    private sealed record ProgressChanged(double? Progress) : PipelineEvent;

    private sealed record StepAdded(BackgroundTaskStep Step) : PipelineEvent;

    private sealed record StepChanged(string StepId, string Name, BackgroundStepStatus Status) : PipelineEvent;

    private sealed record TaskFinished(BackgroundTaskStatus Status, Error? Error) : PipelineEvent;

    public async Task<BackgroundTaskResult> RunAsync(
        string name,
        string description,
        Func<IBackgroundTaskContext, CancellationToken, Task> work,
        Action<BackgroundTaskState>? onChanged = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(description);
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();

        return await Operation.RunAsync(name, description, work, onChanged, cancellationToken).ConfigureAwait(false);
    }

    private sealed class Operation : IBackgroundTaskContext
    {
        private readonly object _stateLock = new();
        private readonly List<BackgroundTaskStep> _steps = [];
        private readonly Channel<PipelineEvent> _events;
        private readonly Guid _id = Guid.NewGuid();

        private BackgroundTaskStatus _status = BackgroundTaskStatus.Running;
        private double? _progress;
        private BackgroundTaskResult? _result;

        private Operation(string name, string description)
        {
            _events = Channel.CreateUnbounded<PipelineEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
            State = new BackgroundTaskState(_id, name, description, _status, null, []);
        }

        public BackgroundTaskState State { get; }

        public static async Task<BackgroundTaskResult> RunAsync(
            string name,
            string description,
            Func<IBackgroundTaskContext, CancellationToken, Task> work,
            Action<BackgroundTaskState>? onChanged,
            CancellationToken externalToken)
        {
            var operation = new Operation(name, description);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

            if (externalToken.IsCancellationRequested)
            {
                operation.Finish(BackgroundTaskStatus.Canceled, null);
                await operation.ConsumeAsync(onChanged).ConfigureAwait(false);
                return operation._result!;
            }

            var execution = Task.Run(
                async () =>
                {
                    try
                    {
                        await work(operation, linkedSource.Token).ConfigureAwait(false);
                        operation.Finish(BackgroundTaskStatus.Succeeded, null);
                    }
                    catch (OperationCanceledException)
                    {
                        operation.Finish(BackgroundTaskStatus.Canceled, null);
                    }
                    catch (Exception exception)
                    {
                        operation.Finish(
                            BackgroundTaskStatus.Failed,
                            Error.Create(CoreErrorCode.Unknown, exception.Message));
                    }
                },
                CancellationToken.None);

            await operation.ConsumeAsync(onChanged).ConfigureAwait(false);
            await execution.ConfigureAwait(false);
            return operation._result!;
        }

        public void ReportProgress(double? progress)
        {
            if (progress is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(progress), progress, "Progress must be between 0 and 1.");
            }

            _events.Writer.TryWrite(new ProgressChanged(progress));
        }

        public string AddStep(string name, BackgroundStepStatus status = BackgroundStepStatus.Pending)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            var step = new BackgroundTaskStep(Guid.NewGuid().ToString("N"), name, status);
            _events.Writer.TryWrite(new StepAdded(step));
            return step.Id;
        }

        public void UpdateStep(string stepId, string name, BackgroundStepStatus status)
        {
            ArgumentException.ThrowIfNullOrEmpty(stepId);
            ArgumentException.ThrowIfNullOrEmpty(name);
            _events.Writer.TryWrite(new StepChanged(stepId, name, status));
        }

        private void Finish(BackgroundTaskStatus status, Error? error)
        {
            if (Interlocked.CompareExchange(ref _status, status, BackgroundTaskStatus.Running) != BackgroundTaskStatus.Running)
            {
                return;
            }

            _result = new BackgroundTaskResult(_id, status, error);
            _events.Writer.TryWrite(new TaskFinished(status, error));
            _events.Writer.Complete();
        }

        private async Task ConsumeAsync(Action<BackgroundTaskState>? onChanged)
        {
            await foreach (var pipelineEvent in _events.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                switch (pipelineEvent)
                {
                    case ProgressChanged changed:
                        _progress = changed.Progress;
                        break;

                    case StepAdded added:
                        lock (_stateLock)
                        {
                            _steps.Add(added.Step);
                        }
                        break;

                    case StepChanged changed:
                        lock (_stateLock)
                        {
                            var index = _steps.FindIndex(step => step.Id == changed.StepId);
                            if (index < 0)
                            {
                                throw new InvalidOperationException($"Unknown background task step '{changed.StepId}'.");
                            }

                            _steps[index] = new BackgroundTaskStep(changed.StepId, changed.Name, changed.Status);
                        }
                        break;

                    case TaskFinished finished:
                        _status = finished.Status;
                        break;
                }

                onChanged?.Invoke(CreateState());
            }
        }

        private BackgroundTaskState CreateState()
        {
            lock (_stateLock)
            {
                return new BackgroundTaskState(_id, State.Name, State.Description, _status, _progress, [.. _steps]);
            }
        }
    }
}
