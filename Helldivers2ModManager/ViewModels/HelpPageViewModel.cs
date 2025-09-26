﻿using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class HelpPageViewModel(NavigationStore navigationStore) : PageViewModelBase
{
	public override string Title => "Help";

	private readonly NavigationStore _navigationStore = navigationStore;

	[RelayCommand]
	void Back()
	{
		_navigationStore.Navigate<DashboardPageViewModel>();
	}
}
