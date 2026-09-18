using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Helldivers2ModManager.Converters;

/// <summary>
/// 将播放状态转换为播放/暂停图标的 Path 几何图形
/// </summary>
internal sealed class PlayPausePathConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is bool isPlaying)
		{
			if (isPlaying)
			{
				// 暂停图标：两个竖条
				return Geometry.Parse("M6,4 L10,4 L10,20 L6,20 Z M14,4 L18,4 L18,20 L14,20 Z");
			}
			else
			{
				// 播放图标：三角形
				return Geometry.Parse("M8,5 L8,19 L19,12 Z");
			}
		}

		return Geometry.Parse("M8,5 L8,19 L19,12 Z");
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}

/// <summary>
/// 将音量值转换为音量图标
/// </summary>
internal sealed class VolumeIconConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is double volume)
		{
			if (volume == 0)
			{
				return ""; // 静音
			}
			else if (volume < 33)
			{
				return ""; // 低音量
			}
			else if (volume < 66)
			{
				return ""; // 中音量
			}
			else
			{
				return ""; // 高音量
			}
		}

		return "";
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}

/// <summary>
/// 反转布尔值到可见性的转换器
/// </summary>
internal sealed class InverseBoolToVisibilityConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is bool boolValue)
		{
			return boolValue ? Visibility.Collapsed : Visibility.Visible;
		}

		return Visibility.Visible;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is Visibility visibility)
		{
			return visibility == Visibility.Collapsed;
		}

		return false;
	}
}
