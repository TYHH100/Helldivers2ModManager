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

	public ObservableCollection<BackgroundTaskItem> Tasks => _backgroundTaskService.Tasks;

	public bool HasCompletedTasks => Tasks.Any(static task => task.IsFinished);

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
			task.PropertyChanged += OnTaskPropertyChanged;
	}

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
		if (e.NewItems is not null)
			foreach (BackgroundTaskItem task in e.NewItems)
				task.PropertyChanged += OnTaskPropertyChanged;

		if (e.OldItems is not null)
			foreach (BackgroundTaskItem task in e.OldItems)
				task.PropertyChanged -= OnTaskPropertyChanged;

		OnPropertyChanged(nameof(HasCompletedTasks));
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
