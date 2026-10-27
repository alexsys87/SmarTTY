using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SmarTTY.Utilities;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.GUI;

/// <summary>One colour of the basic grid: the swatch to draw and the "#RRGGBB" text behind it.</summary>
internal sealed class ColorSwatch
{
    public string Hex { get; }

    public Brush Brush { get; }

    public ColorSwatch(string hex)
    {
        Hex = hex;
        ColorUtils.TryParseHex(hex, out int red, out int green, out int blue);
        Brush = new SolidColorBrush(Color.FromRgb((byte)red, (byte)green, (byte)blue));
    }
}

/// <summary>
/// Themed replacement for the WinForms <c>ColorDialog</c>: a grid of basic colours plus red, green and blue sliders and
/// a "#RRGGBB" box, drawn with the application styles so it follows the light/dark theme like the other dialogs.
/// </summary>
/// <remarks>
/// The settings store a colour as separate channels (no alpha), so the picker works on the three channels and always
/// returns an opaque colour. Built in code apart from the layout, like <see cref="FontPickerWindow"/>.
/// </remarks>
public partial class ColorPickerWindow : Window
{
    private bool _ready;

    private ColorPickerWindow(int red, int green, int blue)
    {
        InitializeComponent();
        BasicColorList.ItemsSource = Array.ConvertAll(ColorUtils.BasicColors, hex => new ColorSwatch(hex));
        _ready = true;
        SetColor(red, green, blue);
    }

    /// <summary>
    /// Shows the picker over <paramref name="owner"/>. Returns the chosen colour, or null when the dialog was
    /// cancelled.
    /// </summary>
    public static DeviceTerminalSettings.ColorWrapper? Pick(Window owner, DeviceTerminalSettings.ColorWrapper color)
    {
        ColorPickerWindow window = new ColorPickerWindow(color.Red, color.Green, color.Blue);
        WindowOwnership.Attach(window, owner ?? WindowOwnership.ActiveWindow());
        return window.ShowDialog() == true ? window.SelectedColor : (DeviceTerminalSettings.ColorWrapper?)null;
    }

    private DeviceTerminalSettings.ColorWrapper SelectedColor
    {
        get
        {
            return new DeviceTerminalSettings.ColorWrapper
            {
                Alpha = 255,
                Red = Channel(RedSlider),
                Green = Channel(GreenSlider),
                Blue = Channel(BlueSlider)
            };
        }
    }

    // ------------------------------------------------------------------ keeping the controls in step

    /// <summary>Shows one colour in every control at once; the sliders, the boxes and the preview never disagree.</summary>
    private void SetColor(int red, int green, int blue)
    {
        red = ColorUtils.Clamp(red);
        green = ColorUtils.Clamp(green);
        blue = ColorUtils.Clamp(blue);
        // The flag stops the change notifications of the controls from calling back into this method.
        _ready = false;
        RedSlider.Value = red;
        GreenSlider.Value = green;
        BlueSlider.Value = blue;
        RedBox.Text = red.ToString(CultureInfo.CurrentCulture);
        GreenBox.Text = green.ToString(CultureInfo.CurrentCulture);
        BlueBox.Text = blue.ToString(CultureInfo.CurrentCulture);
        HexBox.Text = ColorUtils.FormatHex(red, green, blue);
        PreviewSwatch.Background = new SolidColorBrush(Color.FromRgb((byte)red, (byte)green, (byte)blue));
        _ready = true;
    }

    private static int Channel(Slider slider)
    {
        return ColorUtils.Clamp((int)Math.Round(slider.Value));
    }

    private void Channel_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_ready)
        {
            SetColor(Channel(RedSlider), Channel(GreenSlider), Channel(BlueSlider));
        }
    }

    private void ChannelBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyChannelBoxes();
        }
    }

    private void ChannelBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        ApplyChannelBoxes();
    }

    private void ApplyChannelBoxes()
    {
        SetColor(ParseBox(RedBox, Channel(RedSlider)), ParseBox(GreenBox, Channel(GreenSlider)), ParseBox(BlueBox, Channel(BlueSlider)));
    }

    // A box that does not hold a number keeps the channel as it is instead of failing the dialog.
    private static int ParseBox(TextBox box, int fallback)
    {
        return int.TryParse(box.Text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int value) ? ColorUtils.Clamp(value) : fallback;
    }

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyHexBox();
        }
    }

    private void HexBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        ApplyHexBox();
    }

    private void ApplyHexBox()
    {
        if (ColorUtils.TryParseHex(HexBox.Text, out int red, out int green, out int blue))
        {
            SetColor(red, green, blue);
        }
        else
        {
            // Restore the text the boxes still describe, so a typo does not stay on screen as a colour that is not set.
            SetColor(Channel(RedSlider), Channel(GreenSlider), Channel(BlueSlider));
        }
    }

    private void Swatch_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ColorSwatch swatch = (sender as FrameworkElement)?.DataContext as ColorSwatch;
        if (swatch != null && ColorUtils.TryParseHex(swatch.Hex, out int red, out int green, out int blue))
        {
            SetColor(red, green, blue);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
