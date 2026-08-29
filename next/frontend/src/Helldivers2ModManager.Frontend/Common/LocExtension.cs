using System.Windows.Markup;
using Helldivers2ModManager.Core.Localization;

namespace Helldivers2ModManager.Frontend.Common;

/// <summary>
/// 静态定位点：组合根在构建服务前赋值，XAML 的 <see cref="LocExtension"/> 由此取文案。
/// 页面在导航时重建模板，取值即随当前 UI 文化刷新；常驻主壳文本在语言保存后由
/// MainViewModel 主动刷新。
/// </summary>
public static class LocalizationSource
{
    public static LocalizationCatalog? Catalog { get; set; }
}

/// <summary>
/// 本地化标记扩展：<c>{common:Loc Library.Refresh}</c>。
/// 标记编译器要求提供无参构造与可设置的 Key 属性，两者都必须保留。
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        LocalizationSource.Catalog?.GetString(Key) ?? $"[{Key}]";
}
