using Helldivers2ModManager.Models;
using System.Globalization;
using System.Windows.Data;

namespace Helldivers2ModManager;

internal sealed class GroupItemConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null)
        {
            return "无";
        }
        else if (value is string str && str == "无")
        {
            return "无";
        }
        else if (value is ModGroup group)
        {
            return group.Name;
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string str)
        {
            if (str == "无")
            {
                return "无";
            }
        }
        return value;
    }
}