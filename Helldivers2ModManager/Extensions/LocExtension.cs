using System.ComponentModel;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Helldivers2ModManager.Services;

namespace Helldivers2ModManager.Extensions;

/// <summary>
/// 本地化标记扩展，允许在 XAML 中使用 <c>{loc:Loc Key}</c> 获取本地化字符串。
/// 当语言切换时自动更新绑定的目标属性。
/// 用法：
///   <TextBlock Text="{loc:Loc DashboardPage.Title}"/>
///   <Button Content="{loc:Loc MainWindow.ReportBug}"/>
///   <Button ToolTip="{loc:Loc MainWindow.Help}"/>
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
internal sealed class LocExtension : MarkupExtension
{
	/// <summary>
	/// 本地化键名，格式为 "页面/模块名.键名"（如 "DashboardPage.SearchWatermark"）。
	/// </summary>
	public string Key { get; set; } = string.Empty;

	/// <summary>
	/// 当键不存在时的备用文本（默认为空，显示 "[Key]" 占位符）。
	/// </summary>
	public string Fallback { get; set; } = string.Empty;

	public LocExtension() { }

	public LocExtension(string key)
	{
		Key = key;
	}

	public override object ProvideValue(IServiceProvider serviceProvider)
	{
		// 设计模式下返回键名
		if (DesignerProperties.GetIsInDesignMode(new DependencyObject()))
			return $"[{Key}]";

		if (string.IsNullOrEmpty(Key))
			return string.Empty;

		var service = GetLocalizationService();
		if (service is null)
			return $"[{Key}]";

		// 创建一个绑定到 LocalizationService 的索引器
		// 使用 Binding 的 Path 语法 "[Key]" 访问索引器
		var binding = new System.Windows.Data.Binding
		{
			Path = new PropertyPath($"Item[{Key}]"),
			Source = service,
			Mode = System.Windows.Data.BindingMode.OneWay,
			FallbackValue = $"[{Key}]",
			TargetNullValue = $"[{Key}]"
		};

		// 使用绑定机制返回，实现语言切换自动更新
		return binding.ProvideValue(serviceProvider);
	}

	/// <summary>
	/// 在运行时从 App.Host 获取本地化服务实例（缓存）。
	/// </summary>
	private static LocalizationService? s_service;
	private static readonly Lock s_lock = new();

	private static LocalizationService? GetLocalizationService()
	{
		if (s_service is not null)
			return s_service;

		lock (s_lock)
		{
			if (s_service is not null)
				return s_service;

			try
			{
				if (Application.Current is App app && app.Host is not null)
				{
					s_service = app.Host.Services.GetService(typeof(LocalizationService)) as LocalizationService;
				}
			}
			catch
			{
				// 初始化阶段服务可能尚未就绪
			}
		}

		return s_service;
	}
}
