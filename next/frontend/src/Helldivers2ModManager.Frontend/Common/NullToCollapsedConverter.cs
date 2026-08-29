using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Helldivers2ModManager.Frontend.Common;

/// <summary>字符串非空时显示，空/空串时折叠（用于可选的分组名、标签摘要等）。</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
