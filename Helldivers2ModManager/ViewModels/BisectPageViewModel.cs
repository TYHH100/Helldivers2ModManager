using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class BisectPageViewModel : PageViewModelBase
{
	private readonly ILogger<BisectPageViewModel> _logger;
	private readonly Lazy<NavigationStore> _navStore;
	private readonly ModService _modService;
	private readonly SettingsService _settingsService;
	private readonly ModGroupService _modGroupService;
	private readonly BisectService _bisectService;
	private readonly BackgroundTaskService _backgroundTaskService;
	private readonly LocalizationService _localizationService;

	private static readonly ProcessStartInfo s_gameStartInfo = new("steam://run/553850") { UseShellExecute = true };

	public override string Title => _localizationService["Bisect.Title"];

	public ObservableCollection<BisectRoundItem> Rounds { get; } = [];

	[ObservableProperty]
	private bool _isRunning;

	[ObservableProperty]
	private string _groupInfoText = string.Empty;

	[ObservableProperty]
	private string _sessionInfoText = string.Empty;

	[ObservableProperty]
	private string _suspectsText = string.Empty;

	public Visibility NoSessionVisibility => _bisectService.Current is null ? Visibility.Visible : Visibility.Collapsed;

	public Visibility ActiveSessionVisibility => _bisectService.Current is null ? Visibility.Collapsed : Visibility.Visible;

	public BisectPageViewModel(
		ILogger<BisectPageViewModel> logger,
		IServiceProvider provider,
		ModService modService,
		SettingsService settingsService,
		ModGroupService modGroupService,
		BisectService bisectService,
		BackgroundTaskService backgroundTaskService,
		LocalizationService localizationService)
	{
		_logger = logger;
		_navStore = new Lazy<NavigationStore>(provider.GetRequiredService<NavigationStore>);
		_modService = modService;
		_settingsService = settingsService;
		_modGroupService = modGroupService;
		_bisectService = bisectService;
		_backgroundTaskService = backgroundTaskService;
		_localizationService = localizationService;

		_localizationService.PropertyChanged += (_, _) =>
		{
			OnPropertyChanged(nameof(Title));
			UpdateSessionDisplay();
		};
		Rounds.CollectionChanged += (_, _) => NotifyVisibilityChanged();

		UpdateSessionDisplay();
	}

	[RelayCommand]
	private void GoBack() => _navStore.Value.Navigate<DashboardPageViewModel>();

	[RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanStart))]
	private async Task Start()
	{
		if (_settingsService.Initialized == false || string.IsNullOrEmpty(_settingsService.GameDirectory))
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
			{
				Message = _localizationService["Bisect.NoGameDir"]
			});
			return;
		}

		var enabledMods = _modGroupService.FilterMods(_modService.Mods)
			.Where(static mod => mod.Enabled)
			.ToList();
		if (enabledMods.Count < 2)
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxWarningMessage
			{
				Message = _localizationService["Bisect.NeedTwoEnabled"]
			});
			return;
		}

		var staleGroups = _bisectService.FindStaleTempGroups();
		if (staleGroups.Count > 0)
		{
			var staleNames = string.Join("\n", staleGroups.Select(static group => group.Name));
			var cleanupConfirmed = await AskConfirmAsync(
				_localizationService["Bisect.StaleTitle"],
				_localizationService["Bisect.StaleMessage"].Replace("{names}", staleNames));
			if (!cleanupConfirmed)
				return;

			foreach (var group in staleGroups)
			{
				try
				{
					await _modGroupService.DeleteGroupAsync(group.Id);
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Failed to delete stale bisect group {GroupName}", group.Name);
				}
			}
		}

		var startConfirmed = await AskConfirmAsync(
			_localizationService["Bisect.StartTitle"],
			_localizationService["Bisect.StartConfirmMessage"]
				.Replace("{name}", _bisectService.TempGroupName));
		if (!startConfirmed)
			return;

		try
		{
			var originalGroup = _modGroupService.SelectedGroup;
			await _bisectService.StartAsync(originalGroup, _modService.Mods);
			IsRunning = true;
			UpdateSessionDisplay();
			await RunBisectLoopAsync();
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to start bisect session");
			await RecoverAfterFailureAsync();
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = ex.Message });
		}
		finally
		{
			IsRunning = false;
			UpdateSessionDisplay();
		}
	}

	[RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanResume))]
	private async Task Resume()
	{
		try
		{
			IsRunning = true;
			UpdateSessionDisplay();
			await RunBisectLoopAsync();
		}
		finally
		{
			IsRunning = false;
			UpdateSessionDisplay();
		}
	}

	[RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanAbort))]
	private async Task Abort()
	{
		var confirmed = await AskConfirmAsync(
			_localizationService["Bisect.AbortTitle"],
			_localizationService["Bisect.AbortConfirm"]);
		if (!confirmed)
			return;

		await CancelAndNotifyAsync();
		UpdateSessionDisplay();
	}

	private bool CanStart() => _bisectService.Current is null;

	private bool CanResume() => _bisectService.Current is not null;

	private bool CanAbort() => _bisectService.Current is not null;

	private async Task RunBisectLoopAsync()
	{
		try
		{
			while (_bisectService.Current is not null)
			{
				var session = _bisectService.Current;
				// 所有轮次都报告未崩溃：无法定位嫌疑，直接结束
				if (session.Candidates.Count == 0)
					break;

				if (session.Candidates.Count <= 1)
				{
					if (session.Candidates.Count == 1)
					{
						// 收敛后单独验证：该候选可能是二分推断出来的，从未单独部署测试过。
						// 单独部署它再问一次：崩才标记嫌疑，没崩则不标记
						var sole = await _bisectService.PrepareSingleVerificationAsync();
						UpdateSessionDisplay();

						if (!await DeployWithProgressAsync())
						{
							await CancelAndNotifyAsync();
							break;
						}

						var singleVerifyReport = await AskReportAsync(
							_localizationService["Bisect.SingleVerifyMessage"]
								.Replace("{name}", sole.Manifest.Name));
						if (singleVerifyReport == _localizationService["Bisect.Cancel"])
						{
							await CancelAndNotifyAsync();
							break;
						}

						if (singleVerifyReport == _localizationService["Bisect.Crashed"])
						{
							await _bisectService.DisableSuspectAsync();
							UpdateSessionDisplay();
						}
					}

					var remaining = _bisectService.GetRemainingEnabledMods();
					// 剩余 0 个，或只剩单独验证过且未确认有问题的模组：结束
					if (remaining.Count <= 1)
						break;

					var continueConfirmed = await AskConfirmAsync(
						_localizationService["Bisect.ContinueTitle"],
						_localizationService["Bisect.ContinueQuestion"]
							.Replace("{name}", session.Candidates.Count == 1
								? session.AllMods.FirstOrDefault(mod => mod.Manifest.Guid == session.Candidates[0])?.Manifest.Name ?? string.Empty
								: string.Empty)
							.Replace("{count}", remaining.Count.ToString()));
					if (!continueConfirmed)
						break;

					// 先部署剩余全部模组验证是否仍崩溃，为下一轮二分建立前提
					if (!await DeployWithProgressAsync())
					{
						await CancelAndNotifyAsync();
						break;
					}
					var verifyReport = await AskReportAsync(
						_localizationService["Bisect.VerifyRemainingMessage"]
							.Replace("{count}", remaining.Count.ToString())
							.Replace("{names}", string.Join("\n", remaining.Select(static mod => mod.Manifest.Name))));
					if (verifyReport == _localizationService["Bisect.Cancel"])
					{
						await CancelAndNotifyAsync();
						break;
					}
					if (verifyReport == _localizationService["Bisect.NotCrashed"])
						break;

					_bisectService.ContinueWithRemaining(remaining);
					UpdateSessionDisplay();
					continue;
				}

				var round = await _bisectService.PrepareRoundAsync();
				UpdateSessionDisplay();

				if (!await DeployWithProgressAsync())
				{
					await CancelAndNotifyAsync();
					break;
				}

				var report = await AskReportAsync(
					_localizationService["Bisect.ReportMessage"]
						.Replace("{count}", round.TestedMods.Count.ToString())
						.Replace("{names}", string.Join("\n", round.TestedMods.Select(static mod => mod.Manifest.Name))));
				if (report == _localizationService["Bisect.Cancel"])
				{
					await CancelAndNotifyAsync();
					break;
				}

				_bisectService.ApplyResult(report == _localizationService["Bisect.Crashed"], round);
				UpdateSessionDisplay();
			}

			if (_bisectService.Current is not null)
			{
				var session = _bisectService.Current;
				await _bisectService.FinishAsync(true);
				await ShowSummaryAsync(session);
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Bisect loop failed");
			await RecoverAfterFailureAsync();
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = ex.Message });
		}
	}

	private async Task<bool> DeployWithProgressAsync()
	{
		// 游戏运行时部署不会生效：弹窗确认后自动关闭游戏再部署
		if (IsGameRunning())
		{
			var confirmed = await AskConfirmAsync(
				_localizationService["Bisect.GameRunningTitle"],
				_localizationService["Bisect.GameRunningMessage"]);
			if (!confirmed)
				return false;

			await CloseGameAsync();
		}

		WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
		{
			Title = _localizationService["Bisect.Deploying"],
			Message = _localizationService["SettingsPage.PleaseWait"]
		});

		try
		{
			await _backgroundTaskService.RunAsync(
				_localizationService["BackgroundTasksPage.TaskTypeDeploy"],
				_localizationService["SettingsPage.PleaseWait"],
				(_, _) => _bisectService.DeployAsync(),
				_localizationService["DashboardPage.DeploySuccess"]);

			LaunchGame();
		}
		finally
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
		}

		return true;
	}

	/// <summary>
	/// 每轮部署完成后通过 Steam 启动游戏，方便立即测试本轮组合。
	/// </summary>
	private void LaunchGame()
	{
		try
		{
			Process.Start(s_gameStartInfo);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to launch game via Steam after bisect deploy");
		}
	}

	private static bool IsGameRunning()
	{
		return Process.GetProcessesByName("helldivers2").Length > 0;
	}

	/// <summary>
	/// 关闭游戏进程：先尝试优雅关闭主窗口，超时后强制结束。
	/// </summary>
	private static async Task CloseGameAsync()
	{
		foreach (var process in Process.GetProcessesByName("helldivers2"))
		{
			try
			{
				if (process.CloseMainWindow())
				{
					if (!process.WaitForExit(5000))
						process.Kill();
				}
				else
				{
					process.Kill();
				}
			}
			catch
			{
				// 进程可能已经退出
			}
			finally
			{
				process.Dispose();
			}
		}

		// 留出进程完全退出的时间，避免文件仍被占用
		await Task.Delay(1000);
	}

	private Task<string> AskReportAsync(string message)
	{
		var tcs = new TaskCompletionSource<string>();
		WeakReferenceMessenger.Default.Send(new MessageBoxSelectionMessage
		{
			Title = _localizationService["Bisect.ReportTitle"],
			Message = message,
			Options =
			[
				_localizationService["Bisect.Crashed"],
				_localizationService["Bisect.NotCrashed"],
				_localizationService["Bisect.Cancel"],
			],
			Confirm = obj => tcs.TrySetResult(obj as string ?? string.Empty),
			Abort = () => tcs.TrySetResult(_localizationService["Bisect.Cancel"]),
		});
		return tcs.Task;
	}

	private Task<bool> AskConfirmAsync(string title, string message)
	{
		var tcs = new TaskCompletionSource<bool>();
		WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
		{
			Title = title,
			Message = message,
			Confirm = () => tcs.TrySetResult(true),
			Abort = () => tcs.TrySetResult(false),
		});
		return tcs.Task;
	}

	private async Task CancelAndNotifyAsync()
	{
		if (_bisectService.Current is null)
			return;

		try
		{
			await _bisectService.FinishAsync(false);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to cancel bisect session");
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = ex.Message });
			return;
		}

		WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
		{
			Message = _localizationService["Bisect.CanceledMessage"]
		});
	}

	private async Task RecoverAfterFailureAsync()
	{
		try
		{
			if (_bisectService.Current is not null)
				await _bisectService.FinishAsync(false);
		}
		catch (Exception inner)
		{
			_logger.LogError(inner, "Failed to recover original state after bisect failure");
		}
	}

	private async Task ShowSummaryAsync(BisectService.BisectSession session)
	{
		if (session.Suspects.Count == 0)
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
			{
				Message = _localizationService["Bisect.SummaryNone"]
			});
			return;
		}

		var suspectNames = string.Join("\n", session.Suspects
			.Select(guid => session.AllMods.FirstOrDefault(mod => mod.Manifest.Guid == guid)?.Manifest.Name ?? guid.ToString()));
		WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
		{
			Message = _localizationService["Bisect.SummaryMessage"]
				.Replace("{rounds}", session.Rounds.Count.ToString())
				.Replace("{count}", session.Suspects.Count.ToString())
				.Replace("{names}", suspectNames)
		});
	}

	private void UpdateSessionDisplay()
	{
		var session = _bisectService.Current;
		if (session is null)
		{
			var group = _modGroupService.SelectedGroup;
			var enabledCount = _modGroupService.FilterMods(_modService.Mods).Count(static mod => mod.Enabled);
			GroupInfoText = _localizationService["Bisect.GroupInfo"]
				.Replace("{name}", group.Name)
				.Replace("{count}", enabledCount.ToString());
			SessionInfoText = _localizationService["Bisect.SessionInactive"];
			SuspectsText = string.Empty;
		}
		else
		{
			GroupInfoText = _localizationService["Bisect.OriginalGroupInfo"]
				.Replace("{name}", session.OriginalGroupName);
			SessionInfoText = _localizationService["Bisect.CandidateCount"]
				.Replace("{count}", session.Candidates.Count.ToString());
			SuspectsText = session.Suspects.Count == 0
				? string.Empty
				: _localizationService["Bisect.SuspectsLabel"] + "\n" + string.Join("\n", session.Suspects
					.Select(guid => session.AllMods.FirstOrDefault(mod => mod.Manifest.Guid == guid)?.Manifest.Name ?? guid.ToString()));
		}

		Rounds.Clear();
		if (session is not null)
		{
			foreach (var round in session.Rounds)
			{
				Rounds.Add(new BisectRoundItem
				{
					RoundIndex = round.RoundIndex,
					ModsText = string.Join(", ", round.TestedModNames),
					ResultText = round.Crashed
						? _localizationService["Bisect.RoundCrashed"]
						: _localizationService["Bisect.RoundOk"],
				});
			}
		}

		OnPropertyChanged(nameof(GroupInfoText));
		OnPropertyChanged(nameof(SessionInfoText));
		OnPropertyChanged(nameof(SuspectsText));
		NotifyVisibilityChanged();
		StartCommand.NotifyCanExecuteChanged();
		ResumeCommand.NotifyCanExecuteChanged();
		AbortCommand.NotifyCanExecuteChanged();
	}

	private void NotifyVisibilityChanged()
	{
		OnPropertyChanged(nameof(NoSessionVisibility));
		OnPropertyChanged(nameof(ActiveSessionVisibility));
	}
}

internal sealed class BisectRoundItem
{
	public required int RoundIndex { get; init; }

	public required string ModsText { get; init; }

	public required string ResultText { get; init; }
}
