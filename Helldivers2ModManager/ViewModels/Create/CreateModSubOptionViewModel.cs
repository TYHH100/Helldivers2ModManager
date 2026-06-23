using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;

namespace Helldivers2ModManager.ViewModels.Create;

/// <summary>
/// 创建页面中用于编辑模组子选项的 ViewModel。
/// 每个子选项包含名称、描述、Include 路径列表和可选图片。
/// 图片相关逻辑（ImagePath、ImagePreview、BrowseImage）继承自 <see cref="ModImageViewModelBase"/>。
/// </summary>
internal sealed partial class CreateModSubOptionViewModel : ModImageViewModelBase
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

	/// <summary>浏览对话框标题</summary>
	protected override string BrowseImageDialogTitle => "选择子选项图标";

	/// <summary>
	/// 浏览选择 Include 目录（子选项模式）。
	/// 弹出目录树选择对话框，展示源目录的子目录结构，
	/// 用户勾选后自动转为相对路径。
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
			Image = !string.IsNullOrWhiteSpace(ImagePath) ? ImagePath : null,
		};
	}
}
