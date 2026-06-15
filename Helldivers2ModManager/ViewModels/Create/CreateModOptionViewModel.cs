using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Helldivers2ModManager.ViewModels.Create;

/// <summary>
/// 创建页面中用于编辑模组选项的 ViewModel。
/// 每个选项包含名称、描述、Include 路径列表、可选图片和子选项集合。
/// </summary>
internal sealed partial class CreateModOptionViewModel : ObservableObject
{
	/// <summary>选项名称</summary>
	[ObservableProperty]
	private string _name = string.Empty;

	/// <summary>选项描述</summary>
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

	/// <summary>子选项集合</summary>
	public ObservableCollection<CreateModSubOptionViewModel> SubOptions { get; } = [];

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

	/// <summary>添加子选项</summary>
	[RelayCommand]
	void AddSubOption()
	{
		var sub = new CreateModSubOptionViewModel
		{
			SourceDirectory = SourceDirectory,
			ParentIncludePaths = IncludePaths,
		};
		SubOptions.Add(sub);
	}

	/// <summary>删除指定的子选项</summary>
	[RelayCommand]
	void RemoveSubOption(CreateModSubOptionViewModel subOption)
	{
		SubOptions.Remove(subOption);
	}

	/// <summary>浏览选择图片文件</summary>
	[RelayCommand]
	void BrowseImage()
	{
		var dialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = "选择选项图片",
			Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
		};

		if (dialog.ShowDialog() == true)
		{
			ImagePath = dialog.FileName;
			OnPropertyChanged(nameof(ImagePreview));
		}
	}

	/// <summary>
	/// 浏览选择 Include 目录。
	/// 弹出目录树选择对话框，展示源目录的子目录结构，
	/// 用户勾选后自动转为相对路径追加到 IncludePaths。
	/// 如果未设置源目录，弹窗警告提示。
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

		var picker = new Views.Create.IncludeDirectoryPicker(SourceDirectory, IncludePaths);
		picker.Owner = System.Windows.Application.Current.MainWindow;

		if (picker.ShowDialog() == true && picker.SelectedRelativePaths.Count > 0)
		{
			// 用对话框中勾选的结果替换当前值（而非追加，避免重复）
			IncludePaths = string.Join(";", picker.SelectedRelativePaths);
		}
	}

	/// <summary>将 ViewModel 数据转换为 ModOption 模型</summary>
	public Models.ModOption ToModOption()
	{
		var includes = IncludePaths.Split(';', StringSplitOptions.RemoveEmptyEntries)
			.Select(p => p.Trim())
			.Where(p => !string.IsNullOrWhiteSpace(p))
			.ToList();

		return new Models.ModOption
		{
			Name = !string.IsNullOrWhiteSpace(Name) ? Name : "未命名选项",
			Description = !string.IsNullOrWhiteSpace(Description) ? Description : string.Empty,
			Include = includes.Count > 0 ? includes : null,
			Image = !string.IsNullOrWhiteSpace(ImagePath) ? Path.GetFileName(ImagePath) : null,
			SubOptions = SubOptions.Count > 0 ? SubOptions.Select(s => s.ToModSubOption()).ToList() : null,
		};
	}

	partial void OnImagePathChanged(string value)
	{
		OnPropertyChanged(nameof(ImagePreview));
	}

	/// <summary>
	/// 当 IncludePaths 变化时，同步更新所有子选项的 ParentIncludePaths，
	/// 以便子选项浏览 Include 时能正确标记已被父选项占用的目录。
	/// </summary>
	partial void OnIncludePathsChanged(string value)
	{
		foreach (var sub in SubOptions)
			sub.ParentIncludePaths = value;
	}
}
