using SmarTTY.Utilities;
using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Data;

namespace SmarTTY.GUI;

public class EnumValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null)
        {
            return null;
        }
        if (value.GetType().IsEnum)
        {
            try
            {
                FieldInfo field = value.GetType().GetField(value.ToString());
                if (field != null && field.GetCustomAttributes(typeof(EnumDisplayText), inherit: false)?.FirstOrDefault() is EnumDisplayText enumDisplayText)
                {
                    return enumDisplayText.Text;
                }
            }
            catch
            {
            }
        }
        return value.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return null;
    }
}
