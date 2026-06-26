using Helldivers2ModManager.Models;
using Helldivers2ModManager.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace Helldivers2ModManager;

/// <summary>
/// 模板选择器，用于区分 ModViewModel 和 ModSeparator 的渲染模板
/// </summary>
internal sealed class DashboardItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ModTemplate { get; set; }
    public DataTemplate? SeparatorTemplate { get; set; }

    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
        if (item is ModSeparator)
            return SeparatorTemplate!;
        return ModTemplate!;
    }
}
