using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.GUI;

public class ColorWrapperConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DeviceTerminalSettings.ColorWrapper colorWrapper)
        {
            return (object)new SolidColorBrush(Color.FromArgb((byte)colorWrapper.Alpha, (byte)colorWrapper.Red, (byte)colorWrapper.Green, (byte)colorWrapper.Blue));
        }
        return Brushes.White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
