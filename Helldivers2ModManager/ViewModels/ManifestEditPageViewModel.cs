using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Helldivers2ModManager.ViewModels.Create;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.ViewModels;

/// <summary>
/// 清单编辑页面的视图模型（右键菜单"编辑模组"打开）。
/// 支持编辑模组基本信息（名称、描述、图标）和管理选项（添加/删除/编辑选项和子选项）。
/// </summary>
[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class ManifestEditPageViewModel : PageViewModelBase
{
	public override string Title => _localizationService["DashboardPage.EditManifest"];

	/// <summary>当前编辑的模组 ViewModel</summary>
	public ModViewModel? EditMod => _editModStore.CurrentMod;

	/// <summary>是否为 V1 格式清单</summary>
	public bool IsV1Manifest => (_draftManifest ?? EditMod?.Data.Manifest)?.Version == ManifestVersion.V1;

	/// <summary>是否为 Legacy 格式清单（旧版，无 Version 字段）</summary>
	public bool IsLegacyManifest => (_draftManifest ?? EditMod?.Data.Manifest)?.Version == ManifestVersion.Legacy;

	/// <summary>
	/// 是否显示选项编辑区域。
	/// V1 直接显示；Legacy 也显示（添加选项后会自动升级为 V1 格式）。
	/// </summary>
	public bool ShowOptionEditing => IsV1Manifest || IsLegacyManifest;

	/// <summary>模组名称（可编辑）</summary>
	[ObservableProperty]
	private string _modName = string.Empty;

	/// <summary>模组描述（可编辑）</summary>
	[ObservableProperty]
	private string _modDescription = string.Empty;

	/// <summary>浏览图标时的原始文件路径（用于复制）</summary>
	private string? _browsedIconSourcePath;

	/// <summary>模组图标路径（可编辑，显示相对路径）</summary>
	[ObservableProperty]
	private string _iconPath = string.Empty;

	/// <summary>图标预览</summary>
	public ImageSource? IconPreview
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(IconPath))
			{
				// 尝试从绝对路径加载（浏览选择的外部文件）
				if (Path.IsPathRooted(IconPath) && File.Exists(IconPath))
				{
					try
					{
						var bmp = new BitmapImage();
						bmp.BeginInit();
						bmp.UriSource = new Uri(IconPath);
						bmp.CacheOption = BitmapCacheOption.OnLoad;
						bmp.EndInit();
						return bmp;
					}
					catch { }
				}
				// 尝试从模组目录解析相对路径
				else if (EditMod?.Data?.Directory?.FullName is not null)
				{
					var fullPath = Path.Combine(EditMod.Data.Directory.FullName, IconPath);
					if (File.Exists(fullPath))
					{
						try
						{
							var bmp = new BitmapImage();
							bmp.BeginInit();
							bmp.UriSource = new Uri(fullPath);
							bmp.CacheOption = BitmapCacheOption.OnLoad;
							bmp.EndInit();
							return bmp;
						}
						catch { }
					}
				}
			}
			// 否则显示当前模组图标
			return EditMod?.Icon;
		}
	}

	/// <summary>是否为直接编辑 JSON 模式</summary>
	[ObservableProperty]
	private bool _isJsonMode;

	/// <summary>JSON 编辑器内容（直接编辑模式时使用）</summary>
	[ObservableProperty]
	private string _jsonContent = string.Empty;

	/// <summary>模组选项集合（可编辑）</summary>
	public ObservableCollection<CreateModOptionViewModel> EditOptions { get; } = [];

	/// <summary>原始 Legacy 选项的快照（用于判断选项是否被修改）</summary>
	private string[]? _originalLegacyOptions;

	/// <summary>当前表单正在编辑的清单版本及其不在表单中的元数据。</summary>
	private IModManifest? _draftManifest;

	private readonly ILogger<ManifestEditPageViewModel> _logger;
	private readonly NavigationStore _navStore;
	private readonly EditModStore _editModStore;
	private readonly ProfileSaveCoordinator _profileSaveCoordinator;
	private readonly ModService _modService;
	private readonly LocalizationService _localizationService;

	public ManifestEditPageViewModel(ILogger<ManifestEditPageViewModel> logger,
		NavigationStore navStore, EditModStore editModStore,
		ProfileSaveCoordinator profileSaveCoordinator, ModService modService,
		LocalizationService localizationService)
	{
		_logger = logger;
		_navStore = navStore;
		_editModStore = editModStore;
		_profileSaveCoordinator = profileSaveCoordinator;
		_modService = modService;
		_localizationService = localizationService;
	}

	/// <summary>初始化编辑页面，从当前模组加载信息</summary>
	public void InitializeFromMod()
	{
		if (EditMod is null)
			return;

		_draftManifest = EditMod.Data.Manifest;
		LoadVisualFields(_draftManifest);

		OnPropertyChanged(nameof(IconPreview));
		OnPropertyChanged(nameof(IsV1Manifest));
		OnPropertyChanged(nameof(IsLegacyManifest));
		OnPropertyChanged(nameof(ShowOptionEditing));
	}

	private void LoadVisualFields(IModManifest manifest)
	{
		ModName = manifest.Name;
		ModDescription = manifest.Description;

		// 图标路径：直接使用清单中的相对路径（如 icon.png）
		IconPath = manifest.IconPath ?? string.Empty;

		// 加载已有选项
		EditOptions.Clear();
		_originalLegacyOptions = null;
		if (manifest is V1ModManifest v1Manifest)
		{
			var sourceDir = EditMod?.Data.Directory.FullName ?? string.Empty;

			foreach (var opt in v1Manifest.Options ?? [])
			{
				var optVm = new CreateModOptionViewModel
				{
					Name = opt.Name,
					Description = opt.Description,
					IncludePaths = opt.Include is not null ? string.Join(";", opt.Include) : string.Empty,
					ImagePath = opt.Image ?? string.Empty,
					SourceDirectory = sourceDir,
				};

				foreach (var sub in opt.SubOptions ?? [])
				{
					var subVm = new CreateModSubOptionViewModel
					{
						Name = sub.Name,
						Description = sub.Description,
						IncludePaths = string.Join(";", sub.Include),
						ImagePath = sub.Image ?? string.Empty,
						SourceDirectory = sourceDir,
					};
					optVm.SubOptions.Add(subVm);
				}

				EditOptions.Add(optVm);
			}
		}
		else if (manifest is LegacyModManifest legacyManifest)
		{
			// Legacy 格式的选项只是简单的字符串数组，作为选项名称导入
			var legacyOptions = legacyManifest.Options;
			_originalLegacyOptions = legacyOptions?.ToArray();
			if (legacyOptions is not null)
			{
				foreach (var optName in legacyOptions)
				{
					EditOptions.Add(new CreateModOptionViewModel
					{
						Name = optName,
						SourceDirectory = EditMod?.Data.Directory.FullName ?? string.Empty,
					});
				}
			}
		}
	}

	/// <summary>保存并返回仪表板</summary>
	[RelayCommand]
	async Task Done()
	{
		if (EditMod is null)
			return;

		try
		{
			if (IsJsonMode)
			{
				// JSON 模式：直接解析 JSON 并保存
				using var doc = JsonDocument.Parse(JsonContent);
				var manifest = ModManifest.DeserializeFromDocument(doc, _logger);
				EditMod.Data.Manifest = manifest;
				ModManifest.SaveToFile(EditMod.Data.Manifest, EditMod.Data.Directory);
			}
			else
			{
				SaveVisualManifest();
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "保存模组清单失败");
			return;
		}

		// 保存配置
		try
		{
			await _profileSaveCoordinator.SaveCurrentAsync(_modService.Mods);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "保存 Mod 配置失败");
		}

		_editModStore.CurrentMod = null;
		_navStore.Navigate<DashboardPageViewModel>();
	}

	/// <summary>取消编辑，返回仪表板</summary>
	[RelayCommand]
	void Cancel()
	{
		_editModStore.CurrentMod = null;
		_navStore.Navigate<DashboardPageViewModel>();
	}

	/// <summary>显示图片预览</summary>
	[RelayCommand]
	void ShowImagePreview(ImageSource imageSource)
	{
		WeakReferenceMessenger.Default.Send(new ImagePreviewShowMessage { ImageSource = imageSource });
	}

	/// <summary>隐藏图片预览</summary>
	[RelayCommand]
	void HideImagePreview()
	{
		WeakReferenceMessenger.Default.Send(new ImagePreviewHideMessage());
	}

	/// <summary>浏览选择模组图标</summary>
	[RelayCommand]
	void BrowseIcon()
	{
		var dialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = _localizationService["ManifestEditPage.BrowseIconDialog"],
			Filter = _localizationService["Common.SelectImageFilter"],
			InitialDirectory = EditMod?.Data?.Directory?.FullName,
		};

		if (dialog.ShowDialog() == true)
		{
			_browsedIconSourcePath = dialog.FileName;
			// 只存文件名作为相对路径显示
			IconPath = Path.GetFileName(dialog.FileName);
			OnPropertyChanged(nameof(IconPreview));
		}
	}

	/// <summary>判断选项是否被修改（仅针对 Legacy 格式）</summary>
	/// <returns>如果选项被添加、删除或修改返回 true；否则返回 false</returns>
	private bool OptionsWereModified()
	{
		if (_originalLegacyOptions is null)
		{
			return EditOptions.Count > 0;
		}

		if (_originalLegacyOptions.Length != EditOptions.Count)
		{
			return true;
		}

		for (int i = 0; i < _originalLegacyOptions.Length; i++)
		{
			var option = EditOptions[i];
			if (_originalLegacyOptions[i] != option.Name
				|| !string.IsNullOrWhiteSpace(option.Description)
				|| !string.IsNullOrWhiteSpace(option.IncludePaths)
				|| !string.IsNullOrWhiteSpace(option.ImagePath)
				|| option.SubOptions.Count > 0)
			{
				return true;
			}
		}

		return false;
	}

	private void SaveVisualManifest()
	{
		if (EditMod is null)
			return;

		var currentManifest = _draftManifest ?? EditMod.Data.Manifest;
		var modDir = EditMod.Data.Directory.FullName;
		var iconPath = ResolveAndCopyIconPath(modDir);

		if (currentManifest is LegacyModManifest && !OptionsWereModified())
		{
			EditMod.Data.Manifest = new LegacyModManifest
			{
				Guid = currentManifest.Guid,
				Name = ModName,
				Description = ModDescription,
				IconPath = iconPath,
				Options = EditOptions.Count > 0 ? EditOptions.Select(o => o.Name).ToArray() : null,
			};
		}
		else
		{
			foreach (var opt in EditOptions)
			{
				CopyImageIfNeeded(opt.ResolveImageSourcePath(), modDir);
				opt.ResetBrowsedImageSource();
				foreach (var sub in opt.SubOptions)
				{
					CopyImageIfNeeded(sub.ResolveImageSourcePath(), modDir);
					sub.ResetBrowsedImageSource();
				}
			}

			EditMod.Data.Manifest = new V1ModManifest
			{
				Guid = currentManifest.Guid,
				Name = ModName,
				Description = ModDescription,
				IconPath = iconPath,
				Options = EditOptions.Count > 0 ? EditOptions.Select(o => o.ToModOption()).ToList() : null,
				NexusData = (currentManifest as V1ModManifest)?.NexusData,
			};
		}

		ModManifest.SaveToFile(EditMod.Data.Manifest, EditMod.Data.Directory);
		_draftManifest = EditMod.Data.Manifest;
	}

	private string? ResolveAndCopyIconPath(string modDir)
	{
		if (string.IsNullOrWhiteSpace(IconPath))
			return null;

		var sourcePath = _browsedIconSourcePath ?? (Path.IsPathRooted(IconPath) ? IconPath : Path.Combine(modDir, IconPath));
		CopyImageIfNeeded(sourcePath, modDir);
		_browsedIconSourcePath = null;
		return Path.IsPathRooted(IconPath) ? Path.GetFileName(IconPath) : IconPath;
	}

	/// <summary>添加新选项</summary>
	[RelayCommand]
	void AddOption()
	{
		var sourceDir = EditMod?.Data.Directory.FullName ?? string.Empty;
		EditOptions.Add(new CreateModOptionViewModel { SourceDirectory = sourceDir });
	}

	/// <summary>删除指定的选项</summary>
	[RelayCommand]
	void RemoveOption(CreateModOptionViewModel option)
	{
		EditOptions.Remove(option);
	}

	/// <summary>
	/// 将图片文件复制到模组目录（仅当图片不在模组目录中时才复制）。
	/// 处理逻辑与图标一致：如果图片已在模组目录中则跳过，否则复制过去。
	/// </summary>
	private static void CopyImageIfNeeded(string? imagePath, string modDir)
	{
		if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
			return;

		// 如果图片已经在模组目录中，无需复制
		var fullPath = Path.GetFullPath(imagePath);
		var fullModDir = Path.GetFullPath(modDir);
		if (fullPath.StartsWith(fullModDir, StringComparison.OrdinalIgnoreCase))
			return;

		// 复制到模组目录
		var fileName = Path.GetFileName(imagePath);
		var destPath = Path.Combine(modDir, fileName);
		if (!File.Exists(destPath))
			File.Copy(imagePath, destPath);
	}

	partial void OnIconPathChanged(string value)
	{
		OnPropertyChanged(nameof(IconPreview));
	}

	/// <summary>切换编辑模式时，序列化或反序列化 JSON</summary>
	partial void OnIsJsonModeChanged(bool value)
	{
		if (value)
			SwitchToJsonMode();
		else
			SwitchToVisualMode();
	}

	/// <summary>切换为 JSON 模式：将当前可视状态序列化为 JSON</summary>
	private void SwitchToJsonMode()
	{
		var newOptions = EditOptions.Select(o => o.ToModOption()).ToList();
		var currentManifest = _draftManifest ?? EditMod?.Data.Manifest;
		var guid = currentManifest?.Guid ?? Guid.NewGuid();
		IModManifest manifest;

		if (IsLegacyManifest && !OptionsWereModified())
		{
			manifest = new LegacyModManifest
			{
				Guid = guid,
				Name = ModName,
				Description = ModDescription,
				IconPath = !string.IsNullOrWhiteSpace(IconPath) ? IconPath : null,
				Options = EditOptions.Count > 0 ? EditOptions.Select(o => o.Name).ToArray() : null,
			};
		}
		else
		{
			manifest = new V1ModManifest
			{
				Guid = guid,
				Name = ModName,
				Description = ModDescription,
				IconPath = !string.IsNullOrWhiteSpace(IconPath) ? IconPath : null,
				Options = newOptions.Count > 0 ? newOptions : null,
				NexusData = (currentManifest as V1ModManifest)?.NexusData,
			};
		}

		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
		{
			manifest.Serialize(writer);
		}
		JsonContent = Encoding.UTF8.GetString(stream.ToArray());
	}

	/// <summary>切换为可视化模式：解析 JSON 并填充到表单</summary>
	private void SwitchToVisualMode()
	{
		if (string.IsNullOrWhiteSpace(JsonContent))
			return;

		try
		{
			using var doc = JsonDocument.Parse(JsonContent);
			_draftManifest = ModManifest.DeserializeFromDocument(doc, _logger);
			LoadVisualFields(_draftManifest);
			OnPropertyChanged(nameof(IsV1Manifest));
			OnPropertyChanged(nameof(IsLegacyManifest));
			OnPropertyChanged(nameof(ShowOptionEditing));
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "JSON 解析失败，切换到可视化编辑模式");
			// 不阻断切换，保留原有数据
		}
	}
}
