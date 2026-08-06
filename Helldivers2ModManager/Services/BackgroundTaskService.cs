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

	/// <summary>
	/// 在后台线程执行耗时工作，并统一管理任务状态生命周期
	/// （Running → Completed / Failed / Cancelled）。
	/// work 在后台线程运行；通过 <see cref="BackgroundTaskContext"/> 更新描述与进度
	/// （内部自动切回 UI 线程）。不要在 work 内直接操作 WPF 集合或绑定属性——
	/// 计算完成后把结果返回，由调用方在 UI 线程应用。
	/// 成功后自动 <see cref="Complete"/>，取消时 <see cref="Cancel"/>，异常时 <see cref="Fail"/> 并重新抛出。
	/// </summary>
	public async Task RunAsync(
		string name,
		string description,
		Func<BackgroundTaskContext, CancellationToken, Task> work,
		string? completedDescription = null,
		CancellationToken cancellationToken = default)
	{
		var task = Add(name, description);
		var context = new BackgroundTaskContext(this, task);
		try
		{
			await Task.Run(() => work(context, cancellationToken), cancellationToken);
			Complete(task, completedDescription);
		}
		catch (OperationCanceledException)
		{
			Cancel(task);
			throw;
		}
		catch (Exception ex)
		{
			Fail(task, ex.Message);
			throw;
		}
	}

	/// <summary>
	/// 在后台线程执行耗时工作并返回结果，统一管理任务状态生命周期
	/// （Running → Completed / Failed / Cancelled）。
	/// work 在后台线程运行；通过 <see cref="BackgroundTaskContext"/> 更新描述与进度
	/// （内部自动切回 UI 线程）。不要在 work 内直接操作 WPF 集合或绑定属性——
	/// 计算完成后把结果返回，由调用方在 UI 线程应用。
	/// 成功后自动 <see cref="Complete"/>，取消时 <see cref="Cancel"/>，异常时 <see cref="Fail"/> 并重新抛出。
	/// </summary>
	/// <param name="name">任务名称（任务页显示）</param>
	/// <param name="description">任务描述</param>
	/// <param name="work">后台工作委托；返回结果供调用方在 UI 线程应用</param>
	/// <param name="completedDescription">完成时覆盖的描述；为 null 时保留 work 最后上报的描述</param>
	/// <param name="cancellationToken">取消令牌（work 内部需自行响应）</param>
	public async Task<T> RunAsync<T>(
		string name,
		string description,
		Func<BackgroundTaskContext, CancellationToken, Task<T>> work,
		string? completedDescription = null,
		CancellationToken cancellationToken = default)
	{
		var task = Add(name, description);
		var context = new BackgroundTaskContext(this, task);
		try
		{
			var result = await Task.Run(() => work(context, cancellationToken), cancellationToken);
			Complete(task, completedDescription);
			return result;
		}
		catch (OperationCanceledException)
		{
			Cancel(task);
			throw;
		}
		catch (Exception ex)
		{
			Fail(task, ex.Message);
			throw;
		}
	}

	/// <summary>
	/// 后台任务执行上下文：供 work 在后台线程更新任务描述与进度（线程安全，自动切回 UI 线程）。
	/// </summary>
	internal sealed class BackgroundTaskContext
	{
		private readonly BackgroundTaskService _service;
		private readonly BackgroundTaskItem _task;

		internal BackgroundTaskContext(BackgroundTaskService service, BackgroundTaskItem task)
		{
			_service = service;
			_task = task;
		}

		/// <summary>当前任务条目（一般不需要直接使用）。</summary>
		public BackgroundTaskItem Task => _task;

		/// <summary>更新任务页的描述与进度（progress 为 0..1；isIndeterminate 为 true 时进度不确定）。</summary>
		public void Report(string? description = null, double? progress = null, bool? isIndeterminate = null)
			=> _service.Update(_task, description, progress, isIndeterminate);
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
