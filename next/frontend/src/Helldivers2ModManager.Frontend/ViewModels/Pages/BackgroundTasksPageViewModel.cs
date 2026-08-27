using System.Collections.ObjectModel;
using System.Windows;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Services;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed record BackgroundTaskItem(Guid Id, string Name, string Description, BackgroundTaskStatus Status, double? Progress, string Steps);

public sealed class BackgroundTasksPageViewModel : FrontendPageViewModel, IDisposable
{
    private readonly TaskExecutionService _tasks;
    private readonly LocalizationCatalog _localization;

    public ObservableCollection<BackgroundTaskItem> Tasks { get; } = [];

    public override string Title => _localization.GetString("Nav.BackgroundTasks");

    public BackgroundTasksPageViewModel(TaskExecutionService tasks, LocalizationCatalog localization)
    {
        _tasks = tasks;
        _localization = localization;
        _tasks.Changed += OnTaskChanged;
    }

    private void OnTaskChanged(object? sender, BackgroundTaskState state)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Update(state);
            return;
        }

        dispatcher.Invoke(() => Update(state));
    }

    private void Update(BackgroundTaskState state)
    {
        var item = new BackgroundTaskItem(
            state.Id,
            state.Name,
            state.Description,
            state.Status,
            state.Progress,
            string.Join(" → ", state.Steps.Select(step => step.Name)));
        var existing = Tasks.FirstOrDefault(task => task.Id == state.Id);
        var index = existing is null ? 0 : Tasks.IndexOf(existing);
        if (existing is not null)
        {
            Tasks.RemoveAt(index);
        }

        Tasks.Insert(index, item);
    }

    public void Dispose() => _tasks.Changed -= OnTaskChanged;
}
