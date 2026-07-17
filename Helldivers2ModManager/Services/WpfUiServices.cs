using Helldivers2ModManager.Components;
using Helldivers2ModManager.Core.Operations;
using Helldivers2ModManager.Core.UI;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DialogHost = Helldivers2ModManager.Components.MessageBox;

namespace Helldivers2ModManager.Services;

internal sealed class WpfUiDispatcher : IUiDispatcher
{
    public async Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        await dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken);
    }
}

internal sealed class WpfClipboardService(IUiDispatcher dispatcher) : IClipboardService
{
    public Task SetTextAsync(string text, CancellationToken cancellationToken) =>
        dispatcher.InvokeAsync(() => Clipboard.SetText(text ?? string.Empty), cancellationToken);
}

internal sealed class WpfFilePickerService(IUiDispatcher dispatcher) : IFilePickerService
{
    public async Task<IReadOnlyList<string>> PickFilesAsync(
        IReadOnlyList<string> extensions,
        bool allowMultiple,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> selected = [];
        await dispatcher.InvokeAsync(() =>
        {
            var normalized = extensions
                .Select(static extension => extension.Trim().TrimStart('.'))
                .Where(static extension => extension.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var patterns = normalized.Length == 0
                ? "*.*"
                : string.Join(';', normalized.Select(static extension => $"*.{extension}"));
            var dialog = new OpenFileDialog
            {
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = allowMultiple,
                Filter = $"Supported files ({patterns})|{patterns}|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true)
                selected = dialog.FileNames;
        }, cancellationToken);
        return selected;
    }

    public async Task<string?> PickFolderAsync(
        string title,
        string? initialDirectory,
        CancellationToken cancellationToken)
    {
        string? selected = null;
        await dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFolderDialog
            {
                Title = title,
                InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty,
                Multiselect = false
            };
            if (dialog.ShowDialog() == true)
                selected = dialog.FolderName;
        }, cancellationToken);
        return selected;
    }
}

/// <summary>
/// Central dialog adapter. New callers invoke the visual host directly and never publish
/// application-wide messenger messages.
/// </summary>
internal sealed class WpfDialogService(IUiDispatcher dispatcher) : IDialogService
{
    public async Task<bool> ShowAsync(DialogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Task<bool>? dialogTask = null;
        await dispatcher.InvokeAsync(() =>
        {
            var host = DialogHost.Current
                ?? throw new InvalidOperationException("The application dialog host is not available.");
            dialogTask = host.ShowConfirmationAsync(request);
        }, cancellationToken);

        try
        {
            return await dialogTask!.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await dispatcher.InvokeAsync(() => DialogHost.Current?.HideDialog(), CancellationToken.None);
            throw;
        }
    }

    public async Task<string?> SelectAsync(SelectionDialogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Task<string?>? dialogTask = null;
        await dispatcher.InvokeAsync(() =>
        {
            var host = DialogHost.Current
                ?? throw new InvalidOperationException("The application dialog host is not available.");
            dialogTask = host.ShowSelectionAsync(request);
        }, cancellationToken);

        try
        {
            return await dialogTask!.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await dispatcher.InvokeAsync(() => DialogHost.Current?.HideDialog(), CancellationToken.None);
            throw;
        }
    }

    public async Task ShowMessageAsync(MessageDialogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Task? dialogTask = null;
        await dispatcher.InvokeAsync(() =>
        {
            var host = DialogHost.Current
                ?? throw new InvalidOperationException("The application dialog host is not available.");
            dialogTask = host.ShowMessageAsync(request);
        }, cancellationToken);

        try
        {
            await dialogTask!.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await dispatcher.InvokeAsync(() => DialogHost.Current?.HideDialog(), CancellationToken.None);
            throw;
        }
    }

    public async Task<string?> PromptAsync(InputDialogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Task<string?>? dialogTask = null;
        await dispatcher.InvokeAsync(() =>
        {
            var host = DialogHost.Current
                ?? throw new InvalidOperationException("The application dialog host is not available.");
            dialogTask = host.ShowInputAsync(request);
        }, cancellationToken);

        try
        {
            return await dialogTask!.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await dispatcher.InvokeAsync(() => DialogHost.Current?.HideDialog(), CancellationToken.None);
            throw;
        }
    }

    public async Task<string?> PickColorAsync(ColorDialogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Task<string?>? dialogTask = null;
        await dispatcher.InvokeAsync(() =>
        {
            var host = DialogHost.Current
                ?? throw new InvalidOperationException("The application dialog host is not available.");
            dialogTask = host.ShowColorPickerAsync(request);
        }, cancellationToken);

        try
        {
            return await dialogTask!.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await dispatcher.InvokeAsync(() => DialogHost.Current?.HideDialog(), CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<string>?> SelectManyAsync(ChecklistDialogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Task<IReadOnlyList<string>?>? dialogTask = null;
        await dispatcher.InvokeAsync(() =>
        {
            var host = DialogHost.Current
                ?? throw new InvalidOperationException("The application dialog host is not available.");
            dialogTask = host.ShowChecklistAsync(request);
        }, cancellationToken);

        try
        {
            return await dialogTask!.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await dispatcher.InvokeAsync(() => DialogHost.Current?.HideDialog(), CancellationToken.None);
            throw;
        }
    }

    public async Task<IProgressDialogSession> OpenProgressAsync(
        ProgressDialogRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await dispatcher.InvokeAsync(() =>
        {
            var host = DialogHost.Current
                ?? throw new InvalidOperationException("The application dialog host is not available.");
            host.ShowProgress(request);
        }, cancellationToken);
        return new WpfProgressDialogSession();
    }

    private sealed class WpfProgressDialogSession : IProgressDialogSession
    {
        private int _closed;

        public void Report(ProgressDialogRequest request)
        {
            if (Volatile.Read(ref _closed) != 0)
                return;
            ArgumentNullException.ThrowIfNull(request);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                DialogHost.Current?.ShowProgress(request);
                return;
            }

            dispatcher.Invoke(() => DialogHost.Current?.ShowProgress(request));
        }

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                DialogHost.Current?.HideDialog();
                return;
            }

            await dispatcher.InvokeAsync(
                () => DialogHost.Current?.HideDialog(),
                DispatcherPriority.Normal,
                cancellationToken);
        }

        public async ValueTask DisposeAsync() => await CloseAsync(CancellationToken.None);
    }
}

internal sealed class WpfBackgroundTaskRunner(BackgroundTaskService tasks) : IBackgroundTaskRunner
{
    public async Task<OperationResult> RunAsync(
        string operationName,
        Func<IProgress<OperationProgress>, CancellationToken, Task<OperationResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(operation);
        var item = tasks.Add(operationName);
        var progress = new Progress<OperationProgress>(value =>
        {
            double? fraction = value.Total > 0 ? Math.Clamp((double)value.Completed / value.Total, 0, 1) : null;
            tasks.Update(item, value.Message ?? value.CurrentItem ?? value.Stage, fraction, fraction is null);
        });

        try
        {
            var result = await operation(progress, cancellationToken);
            if (result.IsSuccess)
                tasks.Complete(item);
            else
                tasks.Fail(item, result.ErrorMessage ?? result.ErrorCode ?? operationName);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            tasks.Cancel(item);
            return OperationResult.Failure("Operation.Cancelled");
        }
        catch (Exception exception)
        {
            tasks.Fail(item, exception.Message);
            return OperationResult.Failure("Operation.UnexpectedFailure", exception.Message);
        }
    }
}
