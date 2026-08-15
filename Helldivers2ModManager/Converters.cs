using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Helldivers2ModManager.Models;
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

/// <summary>
/// 根据背景色亮度返回合适的前景色（深色背景返回白色，浅色背景返回深灰色）
/// </summary>
internal sealed class ColorToForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string colorString && !string.IsNullOrEmpty(colorString))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(colorString);
                // 计算相对亮度: 0.2126*R + 0.7152*G + 0.0722*B (使用 0-255 值)
                double luminance = 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;
                return luminance > 128 ? new SolidColorBrush(Color.FromRgb(30, 30, 30)) : new SolidColorBrush(Colors.White);
            }
            catch
            {
                return new SolidColorBrush(Colors.White);
            }
        }
        return new SolidColorBrush(Colors.White);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
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

/// <summary>
/// 将 DeploymentItemType 转换为 Visibility
/// Mod 级别显示，其他级别折叠（用于展开/折叠按钮）
/// </summary>
internal sealed class ModLevelToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DeploymentItemType type)
        {
            return type == DeploymentItemType.Mod ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
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

internal sealed class BackgroundTaskStatusToStringConverter : IValueConverter
{
    private static LocalizationService? _localizationService;

    static BackgroundTaskStatusToStringConverter()
    {
        try { if (Application.Current is App app) _localizationService = app.Host.Services.GetService(typeof(LocalizationService)) as LocalizationService; } catch { }
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Models.BackgroundTaskStatus status)
        {
            return status switch
            {
                Models.BackgroundTaskStatus.Pending => _localizationService?["Converters.TaskStatusPending"] ?? "等待中",
                Models.BackgroundTaskStatus.Running => _localizationService?["Converters.TaskStatusRunning"] ?? "进行中",
                Models.BackgroundTaskStatus.Completed => _localizationService?["Converters.TaskStatusCompleted"] ?? "已完成",
                Models.BackgroundTaskStatus.Failed => _localizationService?["Converters.TaskStatusFailed"] ?? "失败",
                Models.BackgroundTaskStatus.Cancelled => _localizationService?["Converters.TaskStatusCancelled"] ?? "已取消",
                _ => _localizationService?["Converters.Unknown"] ?? "未知"
            };
        }
        return _localizationService?["Converters.Unknown"] ?? "未知";
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
    private static LocalizationService? _localizationService;
    static VersionStatusToTextConverter()
    {
        try { if (Application.Current is App app) _localizationService = app.Host.Services.GetService(typeof(LocalizationService)) as LocalizationService; } catch { }
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Models.ModVersionStatus status)
        {
            return status switch
            {
                Models.ModVersionStatus.Compatible => _localizationService?["Converters.Compatible"] ?? "兼容",
                Models.ModVersionStatus.Incompatible => _localizationService?["Converters.Incompatible"] ?? "不兼容",
                Models.ModVersionStatus.Unknown => _localizationService?["Converters.UnableToConfirm"] ?? "无法确认",
                Models.ModVersionStatus.Checking => _localizationService?["Converters.Checking"] ?? "检查中",
                Models.ModVersionStatus.Error => _localizationService?["VersionCheck.CheckFailed"] ?? "检查失败",
                _ => _localizationService?["Converters.Unknown"] ?? "未知"
            };
        }
        return _localizationService?["Converters.Unknown"] ?? "未知";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 将 ListBoxItem 的索引转换为显示序号（从 1 开始）
/// </summary>
internal sealed class IndexToNumberConverter : IValueConverter
{
    public static IndexToNumberConverter Default { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int index)
        {
            return index + 1; // 索引从 0 开始，显示从 1 开始
        }
        return 1;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
