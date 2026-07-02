using CommunityToolkit.Mvvm.ComponentModel;

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
