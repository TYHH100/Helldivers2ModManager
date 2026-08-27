using System.Collections.ObjectModel;
using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Services;

internal sealed class BackgroundTaskService
{
	private readonly Core.Common.IBackgroundTaskRunner _runner;

	public ObservableCollection<BackgroundTaskItem> Tasks { get; } = [];

	public BackgroundTaskService(Core.Common.IBackgroundTaskRunner runner)
	{
		_runner = runner;
	}

	/// <summary>
	/// 添加一个任务条目。
	/// <paramref name="isForeground"/> 为 true 时表示前台任务（有专属进度弹窗/对话框），
	/// 任务页不显示，进入终态后自动从集合移除；false（默认）为后台任务，在任务页显示。
	/// </summary>
	public BackgroundTaskItem Add(string name, string description = "", bool isForeground = false)
	{
		var task = new BackgroundTaskItem
		{
			Name = name,
			Description = description,
			Status = BackgroundTaskStatus.Running,
			IsForeground = isForeground
		};

		RunOnUiThread(() => Tasks.Insert(0, task));
		return task;
	}

	public void Update(BackgroundTaskItem task, string? description = null, double? progress = null, bool? isIndeterminate = null)
	{
		RunOnUiThread(() =>
		{
			// 终态守卫：Complete/Fail/Cancel 可能已先于排队的 Update 执行（BeginInvoke
			// 异步排队不保证顺序），此时不再应用描述/进度，避免已完成的任务被
			// 过期的进度更新覆盖回进行中状态。
			if (task.Status != BackgroundTaskStatus.Running)
				return;

			if (description is not null)
				task.Description = description;
			if (progress.HasValue)
				task.Progress = Math.Clamp(progress.Value, 0, 1);
			if (isIndeterminate.HasValue)
				task.IsIndeterminate = isIndeterminate.Value;
		});
	}

	/// <summary>
	/// 向任务追加一条步骤（如部署时每个模组一条：文本为模组名）。在 UI 线程执行，
	/// 绑定 <see cref="BackgroundTaskItem.Steps"/> 的控件可安全收到集合变更通知。
	/// 语义：新步骤插入顶部（上 = 最新，下 = 旧），上一行自动标记为已完成，
	/// 保证同一任务任意时刻只有一行处于 Running。
	/// </summary>
	public void AddStep(BackgroundTaskItem task, string text)
	{
		RunOnUiThread(() =>
		{
			// 与 Update 相同的终态守卫：终态后不再追加步骤
			if (task.Status != BackgroundTaskStatus.Running)
				return;

			foreach (var step in task.Steps)
			{
				if (step.Status == TaskStepStatus.Running)
					step.Status = TaskStepStatus.Completed;
			}

			task.Steps.Insert(0, new TaskStepItem(text, TaskStepStatus.Running));
		});
	}

	/// <summary>
	/// 更新顶部正在进行的步骤的副标题（如"正在复制 3/12: xxx.gpu_resources"）。
	/// 没有 Running 步骤时忽略（终态或步骤为空）。
	/// </summary>
	public void UpdateStep(BackgroundTaskItem task, string detail)
	{
		RunOnUiThread(() =>
		{
			if (task.Status != BackgroundTaskStatus.Running)
				return;

			foreach (var step in task.Steps)
			{
				if (step.Status == TaskStepStatus.Running)
				{
					step.Detail = detail;
					return;
				}
			}
		});
	}

	/// <summary>
	/// 完成顶部正在进行的步骤（→ Completed 并清空副标题），不插入新行。
	/// 没有 Running 步骤时忽略。
	/// </summary>
	public void CompleteStep(BackgroundTaskItem task)
	{
		RunOnUiThread(() =>
		{
			if (task.Status != BackgroundTaskStatus.Running)
				return;

			foreach (var step in task.Steps)
			{
				if (step.Status == TaskStepStatus.Running)
				{
					step.Status = TaskStepStatus.Completed;
					step.Detail = null;
					return;
				}
			}
		});
	}

	/// <summary>
	/// 把顶部正在进行的步骤标记为失败（→ ✗，保留副标题便于查看卡在哪个文件），
	/// 用于部署等流程失败时定位出问题的步骤。没有 Running 步骤时忽略。
	/// </summary>
	public void FailStep(BackgroundTaskItem task)
	{
		RunOnUiThread(() =>
		{
			if (task.Status != BackgroundTaskStatus.Running)
				return;

			foreach (var step in task.Steps)
			{
				if (step.Status == TaskStepStatus.Running)
				{
					step.Status = TaskStepStatus.Failed;
					return;
				}
			}
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

			// 前台任务有自己的进度弹窗，任务页不显示：终态后立即从集合移除
			// （弹窗通过 task.Steps 集合引用保持步骤显示，移除条目不影响）。
			if (task.IsForeground)
				Tasks.Remove(task);
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

			if (task.IsForeground)
				Tasks.Remove(task);
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

			if (task.IsForeground)
				Tasks.Remove(task);
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
	/// <param name="taskCreated">任务创建后立即回调（同步、在调用线程），用于把任务条目
	/// （如 <see cref="BackgroundTaskItem.Steps"/>）暴露给进度弹窗等 UI；默认 null。</param>
	/// <param name="isForeground">true 表示前台任务（有专属进度弹窗），任务页不显示，终态后自动移除；默认 false。</param>
	public async Task RunAsync(
		string name,
		string description,
		Func<BackgroundTaskContext, CancellationToken, Task> work,
		string? completedDescription = null,
		CancellationToken cancellationToken = default,
		Action<BackgroundTaskItem>? taskCreated = null,
		bool isForeground = false)
	{
		var task = Add(name, description, isForeground);
		taskCreated?.Invoke(task);
		var context = new BackgroundTaskContext(this, task);
		Exception? workException = null;
		var result = await _runner.RunAsync(
			name,
			description,
			async (_, cancellationToken) =>
			{
				try
				{
					await work(context, cancellationToken);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					workException = ex;
					throw;
				}
			},
			cancellationToken: cancellationToken);

		await FinishAsync(task, result, completedDescription, workException);
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
	/// <param name="taskCreated">任务创建后立即回调（同步、在调用线程），用于把任务条目
	/// （如 <see cref="BackgroundTaskItem.Steps"/>）暴露给进度弹窗等 UI；默认 null。</param>
	/// <param name="isForeground">true 表示前台任务（有专属进度弹窗），任务页不显示，终态后自动移除；默认 false。</param>
	public async Task<T> RunAsync<T>(
		string name,
		string description,
		Func<BackgroundTaskContext, CancellationToken, Task<T>> work,
		string? completedDescription = null,
		CancellationToken cancellationToken = default,
		Action<BackgroundTaskItem>? taskCreated = null,
		bool isForeground = false)
	{
		var task = Add(name, description, isForeground);
		taskCreated?.Invoke(task);
		var context = new BackgroundTaskContext(this, task);
		T? workResult = default;
		Exception? workException = null;
		var taskResult = await _runner.RunAsync(
			name,
			description,
			async (_, cancellationToken) =>
			{
				try
				{
					workResult = await work(context, cancellationToken);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					workException = ex;
					throw;
				}
			},
			cancellationToken: cancellationToken);

		await FinishAsync(task, taskResult, completedDescription, workException);
		return workResult!;
	}

	private async Task FinishAsync(
		BackgroundTaskItem task,
		Core.Common.BackgroundTaskResult result,
		string? completedDescription,
		Exception? workException)
	{
		switch (result.Status)
		{
			case Core.Common.BackgroundTaskStatus.Succeeded:
				QueueOnUiThread(() => Complete(task, completedDescription));
				break;
			case Core.Common.BackgroundTaskStatus.Canceled:
				QueueOnUiThread(() => Cancel(task));
				throw new OperationCanceledException();
			case Core.Common.BackgroundTaskStatus.Failed:
				var message = result.Error?.Message ?? workException?.Message ?? "Background task failed.";
				QueueOnUiThread(() => Fail(task, message));
				if (workException is not null)
					throw workException;

				throw new InvalidOperationException(result.Error?.Message ?? "Background task failed.");
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

		/// <summary>追加一条步骤（如"正在部署: 某模组"），自动切回 UI 线程更新。</summary>
		public void ReportStep(string step)
			=> _service.AddStep(_task, step);

		/// <summary>更新顶部正在进行的步骤的副标题（如"正在复制 3/12: xxx"）。</summary>
		public void ReportStepDetail(string detail)
			=> _service.UpdateStep(_task, detail);

		/// <summary>完成顶部正在进行的步骤（→ ✓），不插入新行。</summary>
		public void CompleteStep()
			=> _service.CompleteStep(_task);

		/// <summary>把顶部正在进行的步骤标记为失败（→ ✗），用于失败时定位问题步骤。</summary>
		public void FailStep()
			=> _service.FailStep(_task);
	}

	private static void RunOnUiThread(Action action)
	{
		var dispatcher = System.Windows.Application.Current?.Dispatcher;
		if (dispatcher is null || dispatcher.CheckAccess())
		{
			action();
			return;
		}

		// BeginInvoke 异步排队：后台 worker 的进度上报不再阻塞等待 UI 处理完毕，
		// 避免高频 Update（哈希迁移逐文件、部署逐文件等）把 CPU 密集任务拖慢到
		// UI 渲染速度；排队的更新乱序到达由 Update 内的 Running 状态守卫兜底。
		dispatcher.BeginInvoke(action);
	}

	/// <summary>
	/// 把操作排到 UI 队列末尾执行（无论调用线程是否就是 UI 线程）。
	/// RunAsync 的终态操作（Complete/Fail/Cancel）必须用它：work 期间排队的
	/// 步骤更新（AddStep/UpdateStep/CompleteStep 等）先于终态应用，否则快速任务
	/// （如符号链接部署，所有步骤操作瞬间入队）会在终态同步执行时被守卫拦截，
	/// 步骤永远停留在 Running。
	/// </summary>
	private static void QueueOnUiThread(Action action)
	{
		var dispatcher = System.Windows.Application.Current?.Dispatcher;
		if (dispatcher is null)
		{
			action();
			return;
		}

		dispatcher.BeginInvoke(action);
	}
}
