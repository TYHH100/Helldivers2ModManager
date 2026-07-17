using Helldivers2ModManager.Core.Operations;

namespace Helldivers2ModManager.Core.UI;

public interface INavigationService
{
    void Navigate(Type destinationType, bool root = false);

    bool GoBack();
}

public sealed record DialogRequest(string Title, string Message, string? ConfirmText = null, string? CancelText = null);

public sealed record SelectionDialogRequest(
    string Title,
    string Message,
    IReadOnlyList<string> Options,
    int SelectedIndex = 0);

public enum MessageDialogSeverity
{
    Information,
    Warning,
    Error
}

public sealed record MessageDialogRequest(
    string Title,
    string Message,
    MessageDialogSeverity Severity = MessageDialogSeverity.Information);

public sealed record InputDialogRequest(
    string Title,
    string Message,
    string InitialText = "",
    int MaxLength = -1);

public sealed record ColorDialogRequest(
    string Title,
    string Message,
    string CurrentColor);

public sealed record ChecklistDialogOption(
    string Key,
    string Title,
    string Description,
    bool IsSelected = false);

public sealed record ChecklistDialogRequest(
    string Title,
    string Message,
    IReadOnlyList<ChecklistDialogOption> Options);

public sealed record ProgressDialogRequest(
    string Title,
    string Message,
    string? Step = null);

public interface IProgressDialogSession : IAsyncDisposable
{
    void Report(ProgressDialogRequest request);

    Task CloseAsync(CancellationToken cancellationToken);
}

public interface IDialogService
{
    Task<bool> ShowAsync(DialogRequest request, CancellationToken cancellationToken);

    Task<string?> SelectAsync(SelectionDialogRequest request, CancellationToken cancellationToken);

    Task ShowMessageAsync(MessageDialogRequest request, CancellationToken cancellationToken);

    Task<string?> PromptAsync(InputDialogRequest request, CancellationToken cancellationToken);

    Task<string?> PickColorAsync(ColorDialogRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>?> SelectManyAsync(ChecklistDialogRequest request, CancellationToken cancellationToken);

    Task<IProgressDialogSession> OpenProgressAsync(ProgressDialogRequest request, CancellationToken cancellationToken);
}

public interface IFilePickerService
{
    Task<IReadOnlyList<string>> PickFilesAsync(IReadOnlyList<string> extensions, bool allowMultiple, CancellationToken cancellationToken);

    Task<string?> PickFolderAsync(string title, string? initialDirectory, CancellationToken cancellationToken);
}

public interface IClipboardService
{
    Task SetTextAsync(string text, CancellationToken cancellationToken);
}

public interface IUiDispatcher
{
    Task InvokeAsync(Action action, CancellationToken cancellationToken);
}

public interface IBackgroundTaskRunner
{
    Task<OperationResult> RunAsync(
        string operationName,
        Func<IProgress<OperationProgress>, CancellationToken, Task<OperationResult>> operation,
        CancellationToken cancellationToken);
}
