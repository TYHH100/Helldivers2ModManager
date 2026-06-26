using CommunityToolkit.Mvvm.ComponentModel;

namespace Helldivers2ModManager.Models;

/// <summary>
/// 部署顺序列表项的类型
/// </summary>
public enum DeploymentItemType
{
	/// <summary>模组级别</summary>
	Mod,
	/// <summary>选项级别</summary>
	Option,
	/// <summary>子选项级别</summary>
	SubOption
}

/// <summary>
/// 自定义部署顺序中的单个 Mod 项
/// </summary>
public sealed partial class DeploymentOrderItem : ObservableObject
{
	public Guid Guid { get; }

	private string _name = string.Empty;
	public string Name
	{
		get => _name;
		set
		{
			if (_name != value)
			{
				_name = value;
				OnPropertyChanged(nameof(Name));
			}
		}
	}

	/// <summary>
	/// 是否被选中（用于多选拖拽）
	/// </summary>
	[ObservableProperty]
	private bool _isSelected;

	/// <summary>
	/// 列表项层级类型
	/// </summary>
	public DeploymentItemType ItemType { get; set; } = DeploymentItemType.Mod;

	/// <summary>
	/// 所属 Mod 的 GUID
	/// </summary>
	public Guid ParentModGuid { get; set; }

	/// <summary>
	/// 对于 SubOption，所属 Option 的索引；对于 Mod/Option，值为 -1
	/// </summary>
	public int ParentOptionIndex { get; set; } = -1;

	/// <summary>
	/// 在原始列表中的索引
	/// </summary>
	public int OriginalIndex { get; set; }

	/// <summary>
	/// 是否展开（仅对 Mod 类型有效）
	/// </summary>
	[ObservableProperty]
	private bool _isExpanded;

	public DeploymentOrderItem(Guid guid, string name)
	{
		Guid = guid;
		Name = name;
	}
}
