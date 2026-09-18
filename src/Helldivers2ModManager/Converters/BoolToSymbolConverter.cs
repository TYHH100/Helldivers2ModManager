using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Helldivers2ModManager.Converters;

/// <summary>
/// 根据布尔值返回不同的 Segoe Fluent Icons 符号
/// ConverterParameter 格式: "true符号,false符号"
/// </summary>
internal sealed class BoolToSymbolConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is not bool boolValue || parameter is not string param)
			return string.Empty;

		var symbols = param.Split(',');
		if (symbols.Length != 2)
			return string.Empty;

		return boolValue ? symbols[0] : symbols[1];
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
