using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Helldivers2ModManager.Converters;

/// <summary>
/// 将布尔值反转后转换为 Visibility
/// </summary>
internal sealed class InvertedBoolToVisibilityConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is bool boolValue)
		{
			return boolValue ? Visibility.Collapsed : Visibility.Visible;
		}

		return Visibility.Collapsed;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is Visibility visibility)
		{
			return visibility != Visibility.Visible;
		}

		return false;
	}
}
