using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class BackgroundTasksPageViewModel : PageViewModelBase
{
	private readonly Lazy<NavigationStore> _navStore;
	private readonly BackgroundTaskService _backgroundTaskService;
	private readonly LocalizationService _localizationService;

	public override string Title => _localizationService["DashboardPage.BackgroundTasks"];

	/// <summary>
	/// 任务页只显示后台任务（无专属进度弹窗的操作：哈希计算、版本检查、扫描等）。
	/// 前台任务（部署、删除、导入、更新等）有自己的弹窗，进入终态后即被服务自动移除，
	/// 这里再按 <see cref="BackgroundTaskItem.IsForeground"/> 过滤进行中任务，保证页面语义统一。
	/// </summary>
	public ObservableCollection<BackgroundTaskItem> VisibleTasks { get; } = [];

	public bool HasCompletedTasks => VisibleTasks.Any(static task => task.IsFinished);

	public BackgroundTasksPageViewModel(
		IServiceProvider provider,
		BackgroundTaskService backgroundTaskService,
		LocalizationService localizationService)
	{
		_navStore = new Lazy<NavigationStore>(provider.GetRequiredService<NavigationStore>);
		_backgroundTaskService = backgroundTaskService;
		_localizationService = localizationService;

		_localizationService.PropertyChanged += OnLocalizationChanged;
		Tasks.CollectionChanged += OnTasksChanged;
		foreach (var task in Tasks)
		{
			task.PropertyChanged += OnTaskPropertyChanged;
			if (!task.IsForeground)
				VisibleTasks.Add(task);
		}
	}

	private ObservableCollection<BackgroundTaskItem> Tasks => _backgroundTaskService.Tasks;

	[RelayCommand]
	private void GoBack()
	{
		_navStore.Value.Navigate<DashboardPageViewModel>();
	}

	[RelayCommand]
	private void ClearCompleted()
	{
		_backgroundTaskService.ClearCompleted();
	}

	[RelayCommand]
	private void RemoveTask(BackgroundTaskItem task)
	{
		if (task.IsFinished)
			_backgroundTaskService.Remove(task);
	}

	private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		OnPropertyChanged(nameof(Title));
	}

	private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		// Tasks 只在 UI 线程变更（BackgroundTaskService 统一切换），此处同步可见集合安全。
		if (e.Action == NotifyCollectionChangedAction.Reset)
		{
			foreach (var task in VisibleTasks)
				task.PropertyChanged -= OnTaskPropertyChanged;
			VisibleTasks.Clear();
			foreach (var task in Tasks)
			{
				task.PropertyChanged += OnTaskPropertyChanged;
				if (!task.IsForeground)
					VisibleTasks.Add(task);
			}

			OnPropertyChanged(nameof(HasCompletedTasks));
			return;
		}

		if (e.OldItems is not null)
		{
			foreach (BackgroundTaskItem task in e.OldItems)
			{
				task.PropertyChanged -= OnTaskPropertyChanged;
				VisibleTasks.Remove(task);
			}
		}

		if (e.NewItems is not null)
		{
			foreach (BackgroundTaskItem task in e.NewItems)
			{
				task.PropertyChanged += OnTaskPropertyChanged;
				if (!task.IsForeground)
					VisibleTasks.Insert(GetVisibleIndex(task), task);
			}
		}

		OnPropertyChanged(nameof(HasCompletedTasks));
	}

	/// <summary>
	/// 计算任务在可见集合中的插入位置：Tasks 中该任务之前的非前台任务数量。
	/// </summary>
	private int GetVisibleIndex(BackgroundTaskItem task)
	{
		var index = Tasks.IndexOf(task);
		if (index < 0)
			return VisibleTasks.Count;

		var visibleIndex = 0;
		for (var i = 0; i < index; i++)
		{
			if (!Tasks[i].IsForeground)
				visibleIndex++;
		}

		return visibleIndex;
	}

	private void OnTaskPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(BackgroundTaskItem.IsFinished) || e.PropertyName == nameof(BackgroundTaskItem.Status))
			OnPropertyChanged(nameof(HasCompletedTasks));
	}

	protected override void OnDispose()
	{
		_localizationService.PropertyChanged -= OnLocalizationChanged;
		Tasks.CollectionChanged -= OnTasksChanged;
		foreach (var task in Tasks)
			task.PropertyChanged -= OnTaskPropertyChanged;
	}
}
