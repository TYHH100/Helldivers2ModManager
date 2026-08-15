using CommunityToolkit.Mvvm.ComponentModel;

namespace Helldivers2ModManager.Models;

internal enum TaskStepStatus
{
	/// <summary>当前正在执行的步骤（同一任务最多一行）。</summary>
	Running,

	/// <summary>已经完成的步骤。</summary>
	Completed,

	/// <summary>执行失败的步骤（如部署复制失败；UI 显示红色 ✗）。</summary>
	Failed
}

/// <summary>
/// 后台任务的一条步骤记录（如部署时的单个模组）。
/// <see cref="Text"/> 是步骤内容（如模组名），<see cref="Detail"/> 是动态副标题
/// （如"正在复制 3/12: xxx.gpu_resources"），状态决定 UI 前缀与配色。
/// 只在 UI 线程修改（通过 BackgroundTaskService 的线程切换）。
/// </summary>
internal sealed partial class TaskStepItem : ObservableObject
{
	public TaskStepItem(string text, TaskStepStatus status)
	{
		Text = text;
		_status = status;
	}

	public string Text { get; }

	[ObservableProperty]
	private TaskStepStatus _status;

	/// <summary>动态副标题（正在进行的详情，如文件复制进度）；完成时由服务清空。</summary>
	[ObservableProperty]
	private string? _detail;

	public bool IsRunning => Status == TaskStepStatus.Running;

	public bool IsCompleted => Status == TaskStepStatus.Completed;

	public bool IsFailed => Status == TaskStepStatus.Failed;

	partial void OnStatusChanged(TaskStepStatus value)
	{
		OnPropertyChanged(nameof(IsRunning));
		OnPropertyChanged(nameof(IsCompleted));
		OnPropertyChanged(nameof(IsFailed));
	}
}
