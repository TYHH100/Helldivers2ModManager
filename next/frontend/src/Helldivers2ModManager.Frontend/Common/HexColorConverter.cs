using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Helldivers2ModManager.Frontend.Common;

public sealed class HexColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is string text && !string.IsNullOrWhiteSpace(text))
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text));
                brush.Freeze();
                return brush;
            }
        }
        catch (FormatException)
        {
        }

        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SolidColorBrush brush ? brush.Color.ToString() : "#FF60CDFF";
}
