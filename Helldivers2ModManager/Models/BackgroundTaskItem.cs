using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace Helldivers2ModManager.Models;

internal enum BackgroundTaskStatus
{
	Pending,
	Running,
	Completed,
	Failed,
	Cancelled
}

internal sealed partial class BackgroundTaskItem : ObservableObject
{
	public Guid Id { get; } = Guid.NewGuid();

	/// <summary>
	/// 是否为前台任务：前台任务有自己的进度弹窗/对话框（部署、删除、导入、更新等），
	/// 任务页不显示，进入终态后由 BackgroundTaskService 自动从集合移除；
	/// 后台任务（哈希计算、版本检查、扫描等）在任务页显示。
	/// </summary>
	public bool IsForeground { get; init; }

	/// <summary>
	/// 任务过程步骤列表（如部署时每个模组一条：当前正在部署的在顶部，已完成的沉在下面）。
	/// 只在 UI 线程修改（通过 BackgroundTaskService.AddStep 的线程切换），
	/// 供进度弹窗/任务页绑定。
	/// </summary>
	public ObservableCollection<TaskStepItem> Steps { get; } = [];

	[ObservableProperty]
	private string _name = string.Empty;

	[ObservableProperty]
	private string _description = string.Empty;

	[ObservableProperty]
	private BackgroundTaskStatus _status = BackgroundTaskStatus.Pending;

	[ObservableProperty]
	private double _progress;

	[ObservableProperty]
	private bool _isIndeterminate = true;

	[ObservableProperty]
	private string? _errorMessage;

	public DateTime StartedAt { get; } = DateTime.Now;

	[ObservableProperty]
	private DateTime? _completedAt;

	public bool IsFinished => Status is BackgroundTaskStatus.Completed or BackgroundTaskStatus.Failed or BackgroundTaskStatus.Cancelled;

	public string ProgressText => IsIndeterminate ? string.Empty : $"{Progress * 100:F0}%";

	partial void OnStatusChanged(BackgroundTaskStatus value)
	{
		OnPropertyChanged(nameof(IsFinished));
	}

	partial void OnProgressChanged(double value)
	{
		OnPropertyChanged(nameof(ProgressText));
	}

	partial void OnIsIndeterminateChanged(bool value)
	{
		OnPropertyChanged(nameof(ProgressText));
	}
}
