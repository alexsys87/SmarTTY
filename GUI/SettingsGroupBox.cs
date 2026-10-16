using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SmarTTY.GUI;

public class SettingsGroupBox : ContentControl
{
    public static readonly DependencyProperty ImageSourceProperty = DependencyProperty.Register("ImageSource", typeof(ImageSource), typeof(SettingsGroupBox), new PropertyMetadata((PropertyChangedCallback)null));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register("Text", typeof(string), typeof(SettingsGroupBox), new PropertyMetadata((object)""));

    public static readonly DependencyProperty HeaderBackgroundProperty = DependencyProperty.Register("HeaderBackground", typeof(Brush), typeof(SettingsGroupBox), new PropertyMetadata((object)SystemColors.ControlBrush));

    public ImageSource ImageSource
    {
        get
        {
            return (ImageSource)((DependencyObject)this).GetValue(ImageSourceProperty);
        }
        set
        {
            ((DependencyObject)this).SetValue(ImageSourceProperty, (object)value);
        }
    }

    public string Text
    {
        get
        {
            return (string)((DependencyObject)this).GetValue(TextProperty);
        }
        set
        {
            ((DependencyObject)this).SetValue(TextProperty, (object)value);
        }
    }

    public Brush HeaderBackground
    {
        get
        {
            return (Brush)((DependencyObject)this).GetValue(HeaderBackgroundProperty);
        }
        set
        {
            ((DependencyObject)this).SetValue(HeaderBackgroundProperty, (object)value);
        }
    }

    static SettingsGroupBox()
    {
    }
}
