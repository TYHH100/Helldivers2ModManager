using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Helldivers2ModManager.Services;

namespace Helldivers2ModManager;

internal sealed class StringToColorBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string colorString && !string.IsNullOrEmpty(colorString))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(colorString);
                return new SolidColorBrush(color);
            }
            catch
            {
                return new SolidColorBrush(Color.FromRgb(98, 32, 238));
            }
        }
        return new SolidColorBrush(Color.FromRgb(98, 32, 238));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return false;
    }
}

internal sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return false;
    }
}

internal sealed class TagsToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is IEnumerable<Models.ModTag> tags)
        {
            return string.Join(", ", tags.Select(t => t.Name));
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

internal sealed class ContainsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string str && parameter is string substring)
        {
            return str.Contains(substring, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

internal sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value != null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

internal sealed class InverseNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value == null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

internal sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value != null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

internal sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return false;
    }
}

internal sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string str && !string.IsNullOrEmpty(str))
        {
            return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

internal sealed class BytesToSizeConverter : IValueConverter
{
    private static readonly string[] SizeSuffixes = { "B", "KB", "MB", "GB", "TB" };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            int i = 0;
            double dblSByte = bytes;
            while (dblSByte >= 1024 && i < SizeSuffixes.Length - 1)
            {
                i++;
                dblSByte /= 1024;
            }
            return $"{dblSByte:0.##} {SizeSuffixes[i]}";
        }
        return "0 B";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

internal sealed class DownloadStatusToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Models.DownloadStatus status)
        {
            return status switch
            {
                Models.DownloadStatus.Pending => "等待中",
                Models.DownloadStatus.Downloading => "下载中",
                Models.DownloadStatus.Completed => "已完成",
                Models.DownloadStatus.Failed => "失败",
                Models.DownloadStatus.Cancelled => "已取消",
                _ => "未知"
            };
        }
        return "未知";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

internal sealed class DownloadStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Models.DownloadStatus status)
        {
            return status == Models.DownloadStatus.Downloading ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

internal sealed class ProgressWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2 && values[0] is double progress && values[1] is double maxWidth)
        {
            return Math.Max(0, progress * maxWidth);
        }
        return 0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 将下载速度（字节/秒）转换为可读文本（如 "1.5 MB/s"）
/// </summary>
internal sealed class SpeedToReadableConverter : IValueConverter
{
    private static readonly string[] SpeedSuffixes = { "B/s", "KB/s", "MB/s", "GB/s" };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double speed && speed > 0)
        {
            int i = 0;
            double readableSpeed = speed;
            while (readableSpeed >= 1024 && i < SpeedSuffixes.Length - 1)
            {
                i++;
                readableSpeed /= 1024;
            }
            return $"{readableSpeed:0.##} {SpeedSuffixes[i]}";
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 将 ModVersionStatus 枚举值转换为对应的颜色画刷
/// 兼容(绿色) / 不兼容(红色) / 未知(灰色) / 检查中(蓝色) / 错误(橙色)
/// </summary>
internal sealed class VersionStatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Models.ModVersionStatus status)
        {
            return status switch
            {
                Models.ModVersionStatus.Compatible => new SolidColorBrush(Color.FromRgb(76, 175, 80)),    // 绿色
                Models.ModVersionStatus.Incompatible => new SolidColorBrush(Color.FromRgb(244, 67, 54)), // 红色
                Models.ModVersionStatus.Unknown => new SolidColorBrush(Color.FromRgb(255, 193, 7)),     // 黄色（无法确认）
                Models.ModVersionStatus.Checking => new SolidColorBrush(Color.FromRgb(33, 150, 243)),    // 蓝色
                Models.ModVersionStatus.Error => new SolidColorBrush(Color.FromRgb(255, 152, 0)),        // 橙色
                _ => new SolidColorBrush(Color.FromRgb(158, 158, 158))
            };
        }
        return new SolidColorBrush(Color.FromRgb(158, 158, 158));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 将 ModVersionStatus 枚举值转换为本地化显示文本
/// </summary>
internal sealed class VersionStatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Models.ModVersionStatus status)
        {
            return status switch
            {
                Models.ModVersionStatus.Compatible => "兼容",
                Models.ModVersionStatus.Incompatible => "不兼容",
                Models.ModVersionStatus.Unknown => "无法确认",
                Models.ModVersionStatus.Checking => "检查中",
                Models.ModVersionStatus.Error => "检查失败",
                _ => "未知"
            };
        }
        return "未知";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 将 SortMode 枚举值转换为本地化显示文本
/// </summary>
internal sealed class SortModeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SortMode mode)
        {
            return mode switch
            {
                SortMode.Default => "默认顺序",
                SortMode.NameAsc => "名称 A-Z",
                SortMode.NameDesc => "名称 Z-A",
                SortMode.EnabledFirst => "已启用优先",
                SortMode.DisabledFirst => "已禁用优先",
                _ => "默认顺序",
            };
        }
        return "默认顺序";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}