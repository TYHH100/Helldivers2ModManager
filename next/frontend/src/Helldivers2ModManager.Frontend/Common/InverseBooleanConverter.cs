using System.Globalization;
using System.Windows.Data;

namespace Helldivers2ModManager.Frontend.Common;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool boolean ? !boolean : false;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool boolean ? !boolean : false;
}
