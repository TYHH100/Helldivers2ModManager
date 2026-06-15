using System.Collections.ObjectModel;
using System.IO;
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
	public override string Title => "创建";

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

	/// <summary>模组图标文件路径</summary>
	[ObservableProperty]
	private string _iconPath = string.Empty;

	/// <summary>是否正在创建中</summary>
	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(CreateCommand))]
	private bool _isCreating;

	/// <summary>模组选项集合</summary>
	public ObservableCollection<CreateModOptionViewModel> Options { get; } = [];

	/// <summary>图标预览</summary>
	public ImageSource? IconPreview
	{
		get
		{
			if (string.IsNullOrWhiteSpace(IconPath) || !File.Exists(IconPath))
				return null;
			try
			{
				var bmp = new BitmapImage();
				bmp.BeginInit();
				bmp.UriSource = new Uri(IconPath);
				bmp.CacheOption = BitmapCacheOption.None;
				bmp.EndInit();
				return bmp;
			}
			catch
			{
				return null;
			}
		}
	}

	private readonly ILogger<CreatePageViewModel> _logger;
	private readonly NavigationStore _navigationStore;
	private readonly ModService _modService;

	public CreatePageViewModel(ILogger<CreatePageViewModel> logger, NavigationStore navigationStore, ModService modService)
	{
		_logger = logger;
		_navigationStore = navigationStore;
		_modService = modService;
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
		return !string.IsNullOrWhiteSpace(ModName)
			&& !string.IsNullOrWhiteSpace(SourceDirectory)
			&& !IsCreating;
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
			var sourceDir = new DirectoryInfo(SourceDirectory);

			// 将 ViewModel 中的选项转换为 ModOption 模型列表
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
		// 复制模组图标
		if (!string.IsNullOrWhiteSpace(IconPath) && File.Exists(IconPath))
		{
			var destPath = Path.Combine(sourceDir.FullName, Path.GetFileName(IconPath));
			if (!File.Exists(destPath))
				File.Copy(IconPath, destPath);
		}

		// 复制选项图片和子选项图片
		foreach (var option in Options)
		{
			if (!string.IsNullOrWhiteSpace(option.ImagePath) && File.Exists(option.ImagePath))
			{
				var destPath = Path.Combine(sourceDir.FullName, Path.GetFileName(option.ImagePath));
				if (!File.Exists(destPath))
					File.Copy(option.ImagePath, destPath);
			}

			foreach (var subOption in option.SubOptions)
			{
				if (!string.IsNullOrWhiteSpace(subOption.ImagePath) && File.Exists(subOption.ImagePath))
				{
					var destPath = Path.Combine(sourceDir.FullName, Path.GetFileName(subOption.ImagePath));
					if (!File.Exists(destPath))
						File.Copy(subOption.ImagePath, destPath);
				}
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

	/// <summary>浏览选择模组图标</summary>
	[RelayCommand]
	void BrowseIcon()
	{
		var dialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = "选择模组图标",
			Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
		};

		if (dialog.ShowDialog() == true)
		{
			IconPath = dialog.FileName;
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

	/// <summary>
	/// 当源目录变化时，同步更新所有选项和子选项的 SourceDirectory，
	/// 以便 Include 浏览功能能正确定位根目录。
	/// </summary>
	partial void OnSourceDirectoryChanged(string value)
	{
		foreach (var option in Options)
		{
			option.SourceDirectory = value;
			foreach (var sub in option.SubOptions)
				sub.SourceDirectory = value;
		}
	}
}
