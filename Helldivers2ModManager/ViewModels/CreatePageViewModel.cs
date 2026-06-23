using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Helldivers2ModManager.ViewModels.Create;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Helldivers2ModManager.ViewModels;

/// <summary>
/// 模组创建页面的视图模型，提供表单式的模组创建功能。
/// 用户可以输入模组名称、描述、图标，选择源目录，添加选项和子选项，然后创建模组。
/// </summary>
[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class CreatePageViewModel : PageViewModelBase
{
	public override string Title => "Crete Mods";

	/// <summary>模组显示名称</summary>
	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(CreateCommand))]
	private string _modName = string.Empty;

	/// <summary>模组描述</summary>
	[ObservableProperty]
	private string _modDescription = string.Empty;

	/// <summary>源目录路径，包含模组文件内容</summary>
	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(CreateCommand))]
	private string _sourceDirectory = string.Empty;

	/// <summary>浏览图标时的原始文件路径（用于复制到模组目录）</summary>
	private string? _browsedIconSourcePath;

	/// <summary>模组图标文件路径（显示相对路径，如 icon.png）</summary>
	[ObservableProperty]
	private string _iconPath = string.Empty;

	/// <summary>是否正在创建中</summary>
	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(CreateCommand))]
	private bool _isCreating;

	/// <summary>是否为直接编辑 JSON 模式</summary>
	[ObservableProperty]
	private bool _isJsonMode;

	/// <summary>JSON 编辑器内容（直接编辑模式时使用）</summary>
	[ObservableProperty]
	private string _jsonContent = string.Empty;

	/// <summary>模组选项集合</summary>
	public ObservableCollection<CreateModOptionViewModel> Options { get; } = [];

	/// <summary>图标预览，支持绝对路径和相对路径（从 SourceDirectory 解析）</summary>
	public ImageSource? IconPreview
	{
		get
		{
			if (string.IsNullOrWhiteSpace(IconPath))
				return null;

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
				catch { return null; }
			}

			// 尝试从 SourceDirectory 解析相对路径
			if (!string.IsNullOrWhiteSpace(SourceDirectory))
			{
				var fullPath = Path.Combine(SourceDirectory, IconPath);
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

			return null;
		}
	}

	private readonly ILogger<CreatePageViewModel> _logger;
	private readonly NavigationStore _navigationStore;
	private readonly ModService _modService;
	private readonly SettingsService _settingsService;

	public CreatePageViewModel(ILogger<CreatePageViewModel> logger, NavigationStore navigationStore, ModService modService, SettingsService settingsService)
	{
		_logger = logger;
		_navigationStore = navigationStore;
		_modService = modService;
		_settingsService = settingsService;
	}

	/// <summary>取消创建，返回仪表板</summary>
	[RelayCommand]
	void Cancel()
	{
		_navigationStore.Navigate<DashboardPageViewModel>();
	}

	/// <summary>判断是否可以执行创建操作（名称和源目录不为空，且不在创建中）</summary>
	bool CanCreate()
	{
		if (IsCreating)
			return false;
		if (string.IsNullOrWhiteSpace(SourceDirectory))
			return false;
		if (IsJsonMode)
			return !string.IsNullOrWhiteSpace(JsonContent);
		return !string.IsNullOrWhiteSpace(ModName);
	}

	/// <summary>
	/// 执行模组创建操作。
	/// 调用 ModService.TryAddModFromDirectoryAsync 将源目录的模组文件
	/// 复制到存储目录，并根据用户配置的选项生成 V1 格式清单文件。
	/// </summary>
	[RelayCommand(CanExecute = nameof(CanCreate))]
	async Task Create()
	{
		IsCreating = true;

		try
		{
			if (IsJsonMode)
			{
				// JSON 模式：直接解析 JSON 并创建模组
				using var doc = JsonDocument.Parse(JsonContent);
				var manifest = (V1ModManifest)V1ModManifest.Deserialize(doc.RootElement);
				var sourceDir = new DirectoryInfo(SourceDirectory);
				var modOptions = manifest.Options?.ToList() ?? [];
				var iconPath = !string.IsNullOrWhiteSpace(manifest.IconPath)
					? Path.Combine(SourceDirectory, manifest.IconPath)
					: null;

				var problems = await _modService.TryAddModFromDirectoryAsync(
					sourceDir, manifest.Name, manifest.Description, modOptions, iconPath);

				if (problems.Length > 0)
				{
					foreach (var problem in problems)
						_logger.LogWarning("创建模组时遇到问题: {Kind}", problem.Kind);
					return;
				}

				_logger.LogInformation("Mod created successfully: {}", manifest.Name);
			}
			else
			{
				// 可视化模式：使用表单数据创建模组
				var sourceDir = new DirectoryInfo(SourceDirectory);
				var modOptions = Options.Select(o => o.ToModOption()).ToList();

				// 复制图片文件到源目录（在复制到存储目录之前）
				CopyImageFiles(sourceDir);

				var problems = await _modService.TryAddModFromDirectoryAsync(
					sourceDir, ModName, ModDescription, modOptions, IconPath);

				if (problems.Length > 0)
				{
					foreach (var problem in problems)
						_logger.LogWarning("创建模组时遇到问题: {Kind}", problem.Kind);
					return;
				}

				_logger.LogInformation("Mod created successfully: {}", ModName);
			}

			_navigationStore.Navigate<DashboardPageViewModel>();
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "创建模组失败");
		}
		finally
		{
			IsCreating = false;
		}
	}

	/// <summary>
	/// 将用户选择的图片文件复制到源目录中，
	/// 包括模组图标、选项图片和子选项图片。
	/// </summary>
	private void CopyImageFiles(DirectoryInfo sourceDir)
	{
		// 复制模组图标（使用浏览时的完整路径）
		if (!string.IsNullOrWhiteSpace(_browsedIconSourcePath) && File.Exists(_browsedIconSourcePath))
		{
			var destPath = Path.Combine(sourceDir.FullName, Path.GetFileName(_browsedIconSourcePath));
			if (!File.Exists(destPath))
				File.Copy(_browsedIconSourcePath, destPath);
		}

		// 复制选项图片和子选项图片
		foreach (var option in Options)
		{
			var optSourcePath = option.ResolveImageSourcePath();
			if (!string.IsNullOrWhiteSpace(optSourcePath) && File.Exists(optSourcePath))
			{
				var destPath = Path.Combine(sourceDir.FullName, Path.GetFileName(optSourcePath));
				if (!File.Exists(destPath))
					File.Copy(optSourcePath, destPath);
			}
			option.ResetBrowsedImageSource();

			foreach (var subOption in option.SubOptions)
			{
				var subSourcePath = subOption.ResolveImageSourcePath();
				if (!string.IsNullOrWhiteSpace(subSourcePath) && File.Exists(subSourcePath))
				{
					var destPath = Path.Combine(sourceDir.FullName, Path.GetFileName(subSourcePath));
					if (!File.Exists(destPath))
						File.Copy(subSourcePath, destPath);
				}
				subOption.ResetBrowsedImageSource();
			}
		}
	}

	/// <summary>浏览选择源目录</summary>
	[RelayCommand]
	void BrowseSource()
	{
		using var dialog = new System.Windows.Forms.FolderBrowserDialog
		{
			Description = "选择模组文件所在的目录",
			UseDescriptionForTitle = true,
		};

		if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
		{
			SourceDirectory = dialog.SelectedPath;
		}
	}

	/// <summary>浏览选择模组图标，存储相对路径</summary>
	[RelayCommand]
	void BrowseIcon()
	{
		var dialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = "选择模组图标",
			Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
			InitialDirectory = !string.IsNullOrWhiteSpace(SourceDirectory) ? SourceDirectory : null,
		};

		if (dialog.ShowDialog() == true)
		{
			_browsedIconSourcePath = dialog.FileName;
			IconPath = Path.GetFileName(dialog.FileName);
			OnPropertyChanged(nameof(IconPreview));
		}
	}

	/// <summary>添加新选项</summary>
	[RelayCommand]
	void AddOption()
	{
		Options.Add(new CreateModOptionViewModel { SourceDirectory = SourceDirectory });
	}

	/// <summary>删除指定的选项</summary>
	[RelayCommand]
	void RemoveOption(CreateModOptionViewModel option)
	{
		Options.Remove(option);
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
		CreateCommand.NotifyCanExecuteChanged();
	}

	/// <summary>切换为 JSON 模式：将当前表单状态序列化为 JSON</summary>
	private void SwitchToJsonMode()
	{
		var options = Options.Select(o => o.ToModOption()).ToList();
		var manifest = new V1ModManifest
		{
			Guid = Guid.NewGuid(),
			Name = ModName,
			Description = ModDescription,
			IconPath = !string.IsNullOrWhiteSpace(IconPath) ? Path.GetFileName(IconPath) : null,
			Options = options.Count > 0 ? options : null,
		};

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
			var manifest = (V1ModManifest)V1ModManifest.Deserialize(doc.RootElement);

			ModName = manifest.Name;
			ModDescription = manifest.Description;
			IconPath = manifest.IconPath ?? string.Empty;

			Options.Clear();
			foreach (var opt in manifest.Options ?? [])
			{
				var optVm = new CreateModOptionViewModel
				{
					Name = opt.Name,
					Description = opt.Description,
					IncludePaths = opt.Include is not null ? string.Join(";", opt.Include) : string.Empty,
					ImagePath = opt.Image ?? string.Empty,
					SourceDirectory = SourceDirectory,
				};
				foreach (var sub in opt.SubOptions ?? [])
				{
					optVm.SubOptions.Add(new CreateModSubOptionViewModel
					{
						Name = sub.Name,
						Description = sub.Description,
						IncludePaths = string.Join(";", sub.Include),
						ImagePath = sub.Image ?? string.Empty,
						SourceDirectory = SourceDirectory,
					});
				}
				Options.Add(optVm);
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "JSON 解析失败，切换到可视化编辑模式");
		}
	}

	partial void OnSourceDirectoryChanged(string value)
	{
		foreach (var option in Options)
		{
			option.SourceDirectory = value;
			foreach (var sub in option.SubOptions)
				sub.SourceDirectory = value;
		}

		if (string.IsNullOrWhiteSpace(value) || !Directory.Exists(value))
			return;

		// 自动设置模组名称为根目录文件夹名（仅当名称尚未手动填写时）
		if (string.IsNullOrWhiteSpace(ModName))
		{
			ModName = new DirectoryInfo(value).Name;
		}

		// 自动检测模组图标（仅当图标尚未手动设置时）
		if (string.IsNullOrWhiteSpace(IconPath))
		{
			var iconFile = FindFirstImageFile(value);
			if (iconFile != null)
			{
				IconPath = Path.Combine(value, iconFile);
				OnPropertyChanged(nameof(IconPreview));
			}
		}

		// 如果选项列表为空，自动从目录结构生成选项和子选项
		if (Options.Count == 0)
		{
			AutoGenerateOptionsFromDirectory(value);
		}
	}

	/// <summary>
	/// 获取归类文件夹名集合（大小写不敏感）。
	/// 始终包含默认的 "Models" 和 "Model"，设置项中的名称作为补充。
	/// </summary>
	private HashSet<string> GetOrganizationalFolderNames()
	{
		var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"Models", "Model"
		};

		// 从设置中补充用户自定义的归类文件夹名
		if (_settingsService.Initialized)
		{
			foreach (var name in _settingsService.OrganizationalFolderNames)
				set.Add(name);
		}

		return set;
	}

	/// <summary>
	/// 根据源目录的子目录结构自动生成选项和子选项。
	/// 普通一级子目录成为选项（Include 为该目录名），
	/// 归类文件夹（如 Models）的子目录成为选项（Include 为 "Models\子目录名"），
	/// 选项下的二级子目录成为子选项。
	/// 同时自动检测各目录中的第一张图片作为对应选项/子选项的图片。
	/// 路径格式与 IncludeDirectoryPicker 保持一致，使用 \ 分隔符。
	/// </summary>
	private void AutoGenerateOptionsFromDirectory(string sourceDir)
	{
		try
		{
			var dirInfo = new DirectoryInfo(sourceDir);
			var subDirs = dirInfo.GetDirectories();
			if (subDirs.Length == 0)
				return;

			var orgFolderNames = GetOrganizationalFolderNames();

			foreach (var dir in subDirs)
			{
				if (orgFolderNames.Contains(dir.Name))
				{
					// 归类文件夹（如 Models）：其子目录提升为选项，Include 路径带归类文件夹前缀
					foreach (var innerDir in dir.GetDirectories())
					{
						CreateOptionFromDirectory(innerDir, sourceDir, dir.Name + "\\");
					}
				}
				else
				{
					// 普通目录：直接作为选项
					CreateOptionFromDirectory(dir, sourceDir, "");
				}
			}

			_logger.LogInformation("已从目录结构自动生成 {Count} 个选项", Options.Count);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "自动生成选项结构时出错");
		}
	}

	/// <summary>
	/// 将指定目录创建为一个选项 ViewModel，包含子选项和图片检测。
	/// </summary>
	/// <param name="optionDir">选项目录</param>
	/// <param name="sourceDir">源目录根路径</param>
	/// <param name="includePrefix">Include 路径前缀（归类文件夹名 + \，普通目录传空字符串）</param>
	private void CreateOptionFromDirectory(DirectoryInfo optionDir, string sourceDir, string includePrefix)
	{
		var relativeBase = includePrefix + optionDir.Name;

		var optionVm = new CreateModOptionViewModel
		{
			SourceDirectory = sourceDir,
			Name = optionDir.Name,
			IncludePaths = relativeBase,
		};

		// 自动检测选项图片
		TrySetImage(optionVm, optionDir.FullName, relativeBase);

		// 扫描该选项目录下的子目录，生成子选项
		try
		{
			foreach (var subDir in optionDir.GetDirectories())
			{
				var subRelativeBase = relativeBase + "\\" + subDir.Name;

				var subOptionVm = new CreateModSubOptionViewModel
				{
					SourceDirectory = sourceDir,
					Name = subDir.Name,
					IncludePaths = subRelativeBase,
				};

				// 自动检测子选项图片
				TrySetImage(subOptionVm, subDir.FullName, subRelativeBase);

				optionVm.SubOptions.Add(subOptionVm);
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "扫描选项 '{OptionName}' 的子目录时出错", optionDir.Name);
		}

		Options.Add(optionVm);
	}

	/// <summary>
	/// 检测目录中的第一张图片，设置到 ViewModel 的 ImagePath（相对于源目录的路径）。
	/// </summary>
	private static void TrySetImage(ModImageViewModelBase vm, string directoryPath, string relativeBase)
	{
		var image = FindFirstImageFile(directoryPath);
		if (image != null)
		{
			vm.ImagePath = relativeBase + "\\" + image;
		}
	}

	/// <summary>
	/// 在指定目录中查找第一张图片文件，返回文件名（不含路径）。
	/// 支持的格式：.png、.jpg、.jpeg、.bmp、.gif。
	/// </summary>
	/// <returns>图片文件名，未找到时返回 null</returns>
	private static string? FindFirstImageFile(string directoryPath)
	{
		try
		{
			var imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
				{ ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

			foreach (var file in Directory.EnumerateFiles(directoryPath))
			{
				if (imageExtensions.Contains(Path.GetExtension(file)))
					return Path.GetFileName(file);
			}
		}
		catch
		{
			// 访问权限等异常静默跳过
		}
		return null;
	}
}
