using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Helldivers2ModManager.ViewModels.Create;

/// <summary>
/// 创建页面中用于编辑模组子选项的 ViewModel。
/// 每个子选项包含名称、描述、Include 路径列表和可选图片。
/// </summary>
internal sealed partial class CreateModSubOptionViewModel : ObservableObject
{
	/// <summary>子选项名称</summary>
	[ObservableProperty]
	private string _name = string.Empty;

	/// <summary>子选项描述</summary>
	[ObservableProperty]
	private string _description = string.Empty;

	/// <summary>Include 路径，以分号分隔（相对于源目录的路径）</summary>
	[ObservableProperty]
	private string _includePaths = string.Empty;

	/// <summary>图片文件路径</summary>
	[ObservableProperty]
	private string _imagePath = string.Empty;

	/// <summary>源目录路径，用于浏览 Include 路径时定位根目录</summary>
	public string SourceDirectory { get; set; } = string.Empty;

	/// <summary>父选项的 Include 路径（分号分隔），这些目录在子选项中不可勾选</summary>
	public string ParentIncludePaths { get; set; } = string.Empty;

	/// <summary>图片预览</summary>
	public ImageSource? ImagePreview
	{
		get
		{
			if (string.IsNullOrWhiteSpace(ImagePath) || !File.Exists(ImagePath))
				return null;
			try
			{
				var bmp = new BitmapImage();
				bmp.BeginInit();
				bmp.UriSource = new Uri(ImagePath);
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

	/// <summary>浏览选择图片文件</summary>
	[RelayCommand]
	void BrowseImage()
	{
		var dialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = "选择子选项图片",
			Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
		};

		if (dialog.ShowDialog() == true)
		{
			ImagePath = dialog.FileName;
			OnPropertyChanged(nameof(ImagePreview));
		}
	}

	/// <summary>
	/// 浏览选择 Include 目录（子选项模式）。
	/// 弹出目录树选择对话框，只展示父选项 Include 目录内的子目录结构，
	/// 用户勾选后自动转为相对路径。
	/// 如果未设置源目录或父选项未设置 Include 路径，弹窗警告提示。
	/// </summary>
	[RelayCommand]
	void BrowseInclude()
	{
		if (string.IsNullOrWhiteSpace(SourceDirectory) || !Directory.Exists(SourceDirectory))
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxWarningMessage
			{
				Message = "请先设置源目录后再选择 Include 路径。"
			});
			return;
		}

		// 解析父选项的 Include 路径作为浏览范围
		var scopePaths = ParentIncludePaths
			.Split(';', StringSplitOptions.RemoveEmptyEntries)
			.Select(p => p.Trim())
			.Where(p => !string.IsNullOrWhiteSpace(p))
			.ToList();

		if (scopePaths.Count == 0)
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxWarningMessage
			{
				Message = "请先在选项中设置 Include 路径，子选项只能选择选项目录内的内容。"
			});
			return;
		}

		var picker = new Views.Create.IncludeDirectoryPicker(SourceDirectory, IncludePaths, scopePaths);
		picker.Owner = System.Windows.Application.Current.MainWindow;

		if (picker.ShowDialog() == true && picker.SelectedRelativePaths.Count > 0)
		{
			// 用对话框中勾选的结果替换当前值（而非追加，避免重复）
			IncludePaths = string.Join(";", picker.SelectedRelativePaths);
		}
	}

	/// <summary>将 ViewModel 数据转换为 ModSubOption 模型</summary>
	public Models.ModSubOption ToModSubOption()
	{
		var includes = IncludePaths.Split(';', StringSplitOptions.RemoveEmptyEntries)
			.Select(p => p.Trim())
			.Where(p => !string.IsNullOrWhiteSpace(p))
			.ToList();

		return new Models.ModSubOption
		{
			Name = !string.IsNullOrWhiteSpace(Name) ? Name : "未命名子选项",
			Description = !string.IsNullOrWhiteSpace(Description) ? Description : string.Empty,
			Include = includes,
			Image = !string.IsNullOrWhiteSpace(ImagePath) ? Path.GetFileName(ImagePath) : null,
		};
	}

	partial void OnImagePathChanged(string value)
	{
		OnPropertyChanged(nameof(ImagePreview));
	}
}
