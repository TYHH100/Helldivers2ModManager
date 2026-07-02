using System.Collections.ObjectModel;
using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Services;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class BackgroundTaskService
{
	public ObservableCollection<BackgroundTaskItem> Tasks { get; } = [];

	public BackgroundTaskItem Add(string name, string description = "")
	{
		var task = new BackgroundTaskItem
		{
			Name = name,
			Description = description,
			Status = BackgroundTaskStatus.Running
		};

		RunOnUiThread(() => Tasks.Insert(0, task));
		return task;
	}

	public void Update(BackgroundTaskItem task, string? description = null, double? progress = null, bool? isIndeterminate = null)
	{
		RunOnUiThread(() =>
		{
			if (description is not null)
				task.Description = description;
			if (progress.HasValue)
				task.Progress = Math.Clamp(progress.Value, 0, 1);
			if (isIndeterminate.HasValue)
				task.IsIndeterminate = isIndeterminate.Value;
		});
	}

	public void Complete(BackgroundTaskItem task, string? description = null)
	{
		RunOnUiThread(() =>
		{
			if (description is not null)
				task.Description = description;
			task.Progress = 1;
			task.IsIndeterminate = false;
			task.Status = BackgroundTaskStatus.Completed;
			task.CompletedAt = DateTime.Now;
		});
	}

	public void Fail(BackgroundTaskItem task, string errorMessage)
	{
		RunOnUiThread(() =>
		{
			task.ErrorMessage = errorMessage;
			task.IsIndeterminate = false;
			task.Status = BackgroundTaskStatus.Failed;
			task.CompletedAt = DateTime.Now;
		});
	}

	public void Cancel(BackgroundTaskItem task, string? description = null)
	{
		RunOnUiThread(() =>
		{
			if (description is not null)
				task.Description = description;
			task.IsIndeterminate = false;
			task.Status = BackgroundTaskStatus.Cancelled;
			task.CompletedAt = DateTime.Now;
		});
	}

	public void Remove(BackgroundTaskItem task)
	{
		RunOnUiThread(() => Tasks.Remove(task));
	}

	public void ClearCompleted()
	{
		RunOnUiThread(() =>
		{
			foreach (var task in Tasks.Where(static task => task.IsFinished).ToArray())
				Tasks.Remove(task);
		});
	}

	private static void RunOnUiThread(Action action)
	{
		var dispatcher = System.Windows.Application.Current?.Dispatcher;
		if (dispatcher is null || dispatcher.CheckAccess())
		{
			action();
			return;
		}

		dispatcher.Invoke(action);
	}
}
