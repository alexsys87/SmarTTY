using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;

namespace SmarTTY.GUI;

public class EnumValueFetcher : IValueConverter
{
    private Dictionary<Type, Array> _ValueDict = new Dictionary<Type, Array>();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null)
        {
            return new string[0];
        }
        Type type = value.GetType();
        if (!type.IsEnum)
        {
            return new string[0];
        }
        if (_ValueDict.TryGetValue(type, out var value2))
        {
            return value2;
        }
        return _ValueDict[type] = Enum.GetValues(type);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
