using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.AI;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Media;

namespace Helldivers2ModManager.ViewModels;

/// <summary>
/// 模组选项编辑页面（点击首页"编辑"按钮打开）。
/// 显示模组作者定义的自定义选项，支持切换启用/禁用和选择子选项。
/// </summary>
[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class EditPageViewModel : PageViewModelBase
{
	public override string Title => _localizationService["EditPage.Title"];

	public ModViewModel? EditMod => _editModStore.CurrentMod;

	private readonly NavigationStore _navStore;
	private readonly EditModStore _editModStore;
	private readonly ProfileSaveCoordinator _profileSaveCoordinator;
	private readonly ModService _modService;
	private readonly LocalizationService _localizationService;
	private readonly AiTranslationService _aiTranslationService;

	public EditPageViewModel(NavigationStore navStore, EditModStore editModStore,
		ProfileSaveCoordinator profileSaveCoordinator, ModService modService,
		LocalizationService localizationService, AiTranslationService aiTranslationService)
	{
		_navStore = navStore;
		_editModStore = editModStore;
		_profileSaveCoordinator = profileSaveCoordinator;
		_modService = modService;
		_localizationService = localizationService;
		_aiTranslationService = aiTranslationService;

		_localizationService.PropertyChanged += (_, _) =>
		{
			OnPropertyChanged(nameof(Title));
		};

	}

	[ObservableProperty]
	private bool _isTranslating;

	[ObservableProperty]
	private string _translationStatus = string.Empty;

	[RelayCommand]
	private async Task TranslateOptions()
	{
		var options = EditMod?.Options;
		if (options is null || options.Length == 0)
		{
			TranslationStatus = _localizationService["EditPage.NoOptionsToTranslate"];
			return;
		}

		if (IsTranslating)
			return;

		IsTranslating = true;
		TranslationStatus = _localizationService["EditPage.Translating"];
		try
		{
			var progress = new Progress<AiTranslationProgress>(item =>
			{
				ApplyTranslation(options, item.Source, item.Translation);
				if (IsTranslating)
					TranslationStatus = _localizationService["EditPage.TranslatingProgress"]
						.Replace("{completed}", item.Completed.ToString())
						.Replace("{total}", item.Total.ToString());
			});
			var result = await _aiTranslationService.TranslateAsync(GetOptionTexts(options), progress: progress);
			ApplyTranslations(options, result.Translations);
			TranslationStatus = result.ApiTextCount == 0
				? _localizationService["EditPage.TranslationCompletedFromCache"]
					.Replace("{local}", result.LocalCacheHits.ToString())
				: _localizationService["EditPage.TranslationCompletedWithUsage"]
					.Replace("{local}", result.LocalCacheHits.ToString())
					.Replace("{api}", result.ApiTextCount.ToString())
					.Replace("{hit}", result.PromptCacheHitTokens.ToString())
					.Replace("{miss}", result.PromptCacheMissTokens.ToString())
					.Replace("{output}", result.CompletionTokens.ToString());
		}
		catch (OperationCanceledException)
		{
			TranslationStatus = _localizationService["EditPage.TranslationCanceled"];
		}
		catch (Exception ex)
		{
			TranslationStatus = _localizationService["EditPage.TranslationFailed"];
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = ex.Message });
		}
		finally
		{
			IsTranslating = false;
		}
	}

	private static void ApplyTranslation(
		IEnumerable<ModOptionViewModel> options,
		string source,
		string translation)
	{
		foreach (var option in options)
		{
			if (string.Equals(option.Name, source, StringComparison.Ordinal))
				option.SetTranslation(translation, option.TranslatedDescription);
			if (string.Equals(option.Description, source, StringComparison.Ordinal))
				option.SetTranslation(option.TranslatedName, translation);

			if (option.SubOptions is null)
				continue;
			foreach (var sub in option.SubOptions)
			{
				if (string.Equals(sub.Name, source, StringComparison.Ordinal))
					sub.SetTranslation(translation, sub.TranslatedDescription);
				if (string.Equals(sub.Description, source, StringComparison.Ordinal))
					sub.SetTranslation(sub.TranslatedName, translation);
			}
		}
	}

	internal async Task LoadCachedTranslationsAsync()
	{
		var options = EditMod?.Options;
		if (options is null || options.Length == 0)
			return;

		try
		{
			var translations = await _aiTranslationService.GetCachedTranslationsAsync(GetOptionTexts(options));
			ApplyTranslations(options, translations);
		}
		catch (Exception)
		{
			// 缓存读取失败不应阻止用户打开选项页面；手动翻译时会显示具体错误。
		}
	}

	private static IEnumerable<string> GetOptionTexts(IEnumerable<ModOptionViewModel> options) =>
		options.SelectMany(option =>
			new[] { option.Name, option.Description }
			.Concat(option.SubOptions?.SelectMany(sub => new[] { sub.Name, sub.Description }) ?? []));

	private static void ApplyTranslations(
		IEnumerable<ModOptionViewModel> options,
		IReadOnlyDictionary<string, string> translations)
	{
		foreach (var option in options)
		{
			translations.TryGetValue(option.Name, out var name);
			translations.TryGetValue(option.Description, out var description);
			option.SetTranslation(name, description);
			if (option.SubOptions is null)
				continue;

			foreach (var sub in option.SubOptions)
			{
				translations.TryGetValue(sub.Name, out var subName);
				translations.TryGetValue(sub.Description, out var subDescription);
				sub.SetTranslation(subName, subDescription);
			}
		}
	}

	[RelayCommand]
	async Task Done()
	{
		// 在退出编辑前保存当前 Mod 配置到数据库，避免导航回 Dashboard 时数据丢失
		try
		{
			await _profileSaveCoordinator.SaveCurrentAsync(_modService.Mods);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"{_localizationService["EditPage.SaveFailed"]}{ex.Message}");
		}

		_editModStore.CurrentMod = null;
		_navStore.Navigate<DashboardPageViewModel>();
	}

	[RelayCommand]
	void Cancel()
	{
		_editModStore.CurrentMod = null;
		_navStore.Navigate<DashboardPageViewModel>();
	}

	[RelayCommand]
	void ShowImagePreview(ImageSource imageSource)
	{
		WeakReferenceMessenger.Default.Send(new ImagePreviewShowMessage { ImageSource = imageSource });
	}

	[RelayCommand]
	void HideImagePreview()
	{
		WeakReferenceMessenger.Default.Send(new ImagePreviewHideMessage());
	}
}
